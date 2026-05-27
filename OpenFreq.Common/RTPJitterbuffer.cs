using System.Diagnostics;
using System.Security.Cryptography.X509Certificates;
using Microsoft.Extensions.Logging;
using OpenFreq.Common.Rtp;

namespace OpenFreq.Common;

public record SequencedPacket
{
    public required uint Ssrc { get; set; }
    public required long SequenceNumber { get; set; }
    public required long Timestamp { get; set; }
    public required byte[] Payload { get; set; }
    public required byte[]? Metadata { get; set; }
    /// <summary>
    /// True when injected by the drain thread as a proactive-PLC sentinel.
    /// No Payload/Metadata; the decoder generates concealment for this slot.
    /// </summary>
    public bool IsConcealment { get; set; } = false;
}

/// <summary>
/// Adaptive RTP jitter buffer with timestamp-based playout scheduling.
/// Handles packet reordering, jitter smoothing, and adaptive buffer sizing.
/// </summary>
public class RtpJitterBuffer
{
    private readonly ILogger<RtpJitterBuffer> _logger;

    /// <summary>
    /// Sorts packets based on their (extended) sequence number
    /// </summary>
    private readonly SortedDictionary<long, SequencedPacket> _buffer = new();
    private readonly Queue<double> _jitterSamples = new(50);
    // Per-packet playout margin (intrinsic — buffer backed out). Drives AdaptBufferSize.
    private readonly Queue<double> _marginSamples = new(50);
    private readonly int _maxBufferPackets;

    /// <summary>
    /// Starting (extended) timestamp from the first packet
    /// </summary>
    private uint _baseTimestamp;
    /// <summary>
    /// Local monotonic time when the first packet arrived.
    /// Jitter is a comparison to the base timestamp
    /// </summary>
    private long _baseTimeTicks;
    // rollover logic - see AddPacket()
    private int _timestampEpoch = 0;
    private int _sequenceEpoch = 0;
    private uint _highestTimestamp = 0;
    private ushort _highestSequence = 0;
    /// <summary>
    /// Local monotonic time when the last packet arrived.
    /// </summary>
    private long _lastPacketReceivedTicks;
    /// <summary>
    /// Timestamp (in extended samples) of the last packet
    /// </summary>
    private long _lastPacketTimestamp;

    // Adaptive jitter buffer parameters
    private double _targetBufferMs = 60; // Start with 60ms
    // Active delay used by the playout clock — frozen when someone is transmitting.
    private double _activeBufferMs = 60;
    private double _measuredJitterMs;
    private const double MIN_BUFFER_MS = 20;
    private const double MAX_BUFFER_MS = 500;
    // Most a single late-arrival event may grow the buffer, so one freak packet
    // can't balloon latency. Realistic latency steps fit well under this.
    private const double LATE_GROW_CAP_MS = 250;
    // Steady-state sizing aims to leave the worst recent packet this much spare
    // time before its playout slot.
    private const double DESIRED_MARGIN_MS = 30;

    // Concealment is paced one frame (~20ms) per playout slot. These bound a run:
    //  - BLIND: frames to conceal when nothing past the gap has been received yet.
    //    Covers a late successor / the leading edge of a burst. The cost is a short
    //    PLC tail (~3 frames = 60ms) after a talkspurt genuinely ends.
    //  - MAX: absolute cap once the gap is proven (a later packet was received).
    //    Opus PLC degrades to noise well before this; past it we resync.
    private const int MAX_BLIND_CONCEAL_FRAMES = 3;
    private const int MAX_CONCEAL_FRAMES = 15;

    // _lastReleasedPt: relative timestamp (ts - _baseTimestamp) of the last slot we delivered (real packet or concealment)
    // _lastReceivedPt: relative timestamp of the latest packet we have ever received.
    private long? _lastReleasedPt;
    private long _lastReceivedPt = -1;
    // Consecutive concealment frames emitted since the last real packet was released.
    private int _concealmentRunLength;

    // Statistics
    private int _packetsReceived;
    private int _packetsLost;
    private int _packetsLate;
    private int _packetsDuplicate;
    private int _packetsPlayed;

    public RtpJitterBuffer(ILogger<RtpJitterBuffer> logger, int sampleRate = OpenFreqRtcClient.SAMPLE_RATE, int maxBufferPackets = 200)
    {
        _logger = logger;
        _packetsLate = 0;
        _maxBufferPackets = maxBufferPackets;
    }

    /// <summary>
    /// Add packet to jitter buffer
    /// </summary>
    public void AddPacket(RtpPacket packet)
    {
        var now = Stopwatch.GetTimestamp();

        // Initialize on first packet
        if (_packetsReceived == 0)
        {
            _baseTimestamp = packet.Timestamp;
            _baseTimeTicks = now;
            _highestSequence = packet.SequenceNumber;
            _highestTimestamp = packet.Timestamp;
            _logger.LogInformation("Initialized: buffer={BufferMs}ms", _targetBufferMs);
        }
        _packetsReceived++;

        // Rollover handling (from RTP's RFC 3711, seciton 3.3.1):
        // we can extend both the 16-bit sequence number _and_ the 32-bit timestamp
        // to 64-bit values, in a way that handles rollover *and* out-of-order packets!
        // This is very nice because it lets everything downstream never worry about
        // rollover ever again. (2^63 samples @ 48kHz is 6 million years.)
        var seqDelta = (short)(ushort)(packet.SequenceNumber - _highestSequence);
        int seqEpoch;
        if (seqDelta >= 0)
        {
            // If the delta is positive but the sequence is < highest,
            // we've rolled over
            if (packet.SequenceNumber < _highestSequence) ++_sequenceEpoch;
            seqEpoch = _sequenceEpoch;
            _highestSequence = packet.SequenceNumber;
        }
        else
        {
            // If the delta is <= 0 but sequence is > highest,
            // it's a late packet from before the last rollover.
            if (packet.SequenceNumber > _highestSequence)
            {
                seqEpoch = _sequenceEpoch - 1;
            }
            // Late packet without any rollover shenanigans:
            else
            {
                seqEpoch = _sequenceEpoch;
            }
        }
        // Our result is a 48-bit int.
        long seq = (long)seqEpoch << 16 | (long)packet.SequenceNumber;

        // Give timestamps the same treatment.
        int tsDelta = (int)(packet.Timestamp - _highestTimestamp);
        int tsEpoch;
        if (tsDelta >= 0)
        {
            if (packet.Timestamp < _highestTimestamp) ++_timestampEpoch;
            tsEpoch = _timestampEpoch;
            _highestTimestamp = packet.Timestamp;
        }
        else
        {
            if (packet.Timestamp > _highestTimestamp)
            {
                tsEpoch = _timestampEpoch - 1;
            }
            else
            {
                tsEpoch = _timestampEpoch;
            }
        }
        long ts = (long)tsEpoch << 32 | (long)packet.Timestamp;

        // Track the highest relative timestamp we've seen.
        // Only update for non-late packets (tsDelta >= 0 means new/current).
        if (tsDelta >= 0)
        {
            long pt = ts - _baseTimestamp;
            if (pt > _lastReceivedPt) _lastReceivedPt = pt;

            // Playout-margin tracking. A packet's margin — the spare time
            // between when it arrived and its scheduled playout slot — folds
            // together both network jitter and the baseline delay. Inter-arrival
            // variance (MeasureJitter) sees only the jitter, so a constant delay
            // step is invisible to it; margin catches it. Two uses:
            //  - Record the *intrinsic* margin (the buffer's own contribution
            //    backed out) so AdaptBufferSize can size from spare time rather
            //    than variance.
            //  - If a packet already arrived past its slot (negative margin),
            //    grow the buffer now — AdaptBufferSize converges far too slowly
            //    to stop GetReadyPackets discarding packets in the meantime.
            if (_packetsReceived > 1)
            {
                long activeBufferTicks = (long)(_activeBufferMs / 1000.0 * Stopwatch.Frequency);
                long clockSamples = (long)(
                    (double)(now - _baseTimeTicks - activeBufferTicks)
                        / Stopwatch.Frequency * OpenFreqRtcClient.SAMPLE_RATE);
                double marginMs = (double)(pt - clockSamples)
                    / OpenFreqRtcClient.SAMPLE_RATE * 1000.0;

                // Intrinsic margin: margin with the current buffer removed, so
                // samples stay comparable even as _activeBufferMs changes.
                _marginSamples.Enqueue(marginMs - _activeBufferMs);
                while (_marginSamples.Count > 50)
                    _marginSamples.Dequeue();

                if (marginMs < 0)
                {
                    // Cover the lateness plus the desired steady-state margin,
                    // capped so one freak packet can't balloon latency.
                    double grow = Math.Min(
                        -marginMs + DESIRED_MARGIN_MS, LATE_GROW_CAP_MS);
                    double grown = Math.Clamp(
                        _activeBufferMs + grow, MIN_BUFFER_MS, MAX_BUFFER_MS);
                    if (grown > _activeBufferMs)
                    {
                        _logger.LogInformation(
                            "Late arrival ({LateMs:F0}ms past slot) — buffer {Old:F0}→{New:F0}ms",
                            -marginMs, _activeBufferMs, grown);
                        _activeBufferMs = grown;
                        // Persist so the next talkspurt doesn't snap back and re-glitch.
                        _targetBufferMs = Math.Max(_targetBufferMs, grown);
                    }
                }
            }
        }

        var sp = new SequencedPacket
        {
            Ssrc = packet.Ssrc,
            Payload = packet.Payload,
            Metadata = packet.ExtensionData,
            SequenceNumber = seq,
            Timestamp = ts,
        };

        // Check for duplicate
        if (_buffer.ContainsKey(sp.SequenceNumber))
        {
            _packetsDuplicate++;
#if DEBUG
            _logger.LogDebug(
                "[RTPTRACE 2/6] jitter-in  SSRC={Ssrc:X8} seq16={Seq16} seq64={Seq64} → duplicate, dropped",
                packet.Ssrc, packet.SequenceNumber, sp.SequenceNumber);
#endif
            return;
        }

        // Add to buffer
        _buffer[sp.SequenceNumber] = sp;
#if DEBUG
        _logger.LogDebug(
            "[RTPTRACE 2/6] jitter-in  SSRC={Ssrc:X8} seq16={Seq16} seq64={Seq64} ts64={Ts64} buf={Buf}",
            packet.Ssrc, packet.SequenceNumber, sp.SequenceNumber, sp.Timestamp, _buffer.Count);
#endif

        var ticksDiff = now - _baseTimeTicks;
        var timestampDiff = sp.Timestamp - _baseTimestamp;
        MeasureJitter(ticksDiff, timestampDiff);

        // Adapt buffer size
        AdaptBufferSize();

        // Limit buffer size
        while (_buffer.Count > _maxBufferPackets)
        {
            var oldest = _buffer.Keys.First();
            _buffer.Remove(oldest);
            _logger.LogWarning("Buffer overflow, dropped seq {SequenceNumber}", oldest);
        }
    }

    // Toy language doesn't have sum types/tagged unions,
    // but apparently this is the closest we get since C# 9.
    public abstract record PacketsReadyResult;
    public record NoPackets : PacketsReadyResult;
    public record PacketsReady(List<SequencedPacket> Packets) : PacketsReadyResult;
    public record WaitFor(long Ticks) : PacketsReadyResult;
    /// <summary>
    /// A playout slot is overdue with no real packet in the buffer.
    /// The drain thread must inject a PLC/FEC sentinel into the decode channel
    /// so the decoder fills the hole at the correct clock position.
    /// <para>
    /// <paramref name="FecPayload"/> is set when the packet immediately after
    /// the missing one (N+1) is already buffered: its LBRR bits can recover N
    /// without falling back to pure PLC.  Null means pure PLC.
    /// </para>
    /// </summary>
    public record ConcealmentNeeded(byte[]? FecPayload) : PacketsReadyResult;


    public PacketsReadyResult GetReadyPackets(long now)
    {
        // Discard packets that arrived after their playout slot has already passed.
        if (_lastReleasedPt.HasValue)
        {
            while (_buffer.Count > 0)
            {
                var front = _buffer.First();
                long frontPt = front.Value.Timestamp - _baseTimestamp;
                if (frontPt <= _lastReleasedPt.Value)
                {
                    _buffer.Remove(front.Key);
                    _packetsLate++;
                }
                else break;
            }
        }

        // Truly empty and no cursor — nothing to do.
        if (_buffer.Count == 0 && !_lastReleasedPt.HasValue)
        {
            _activeBufferMs = _targetBufferMs;
            return new NoPackets();
        }

        long elapsedTicks = now - _baseTimeTicks;
        // Use the frozen active delay, not the (possibly mid-talkspurt adapted) target.
        long bufferDelayTicks = (long)(_activeBufferMs / 1000.0 * Stopwatch.Frequency);
        long jitterAdjustedElapsedSamples = (long)(
            (double)(elapsedTicks - bufferDelayTicks) /
                Stopwatch.Frequency * OpenFreqRtcClient.SAMPLE_RATE);

        // When the playout clock passes a slot and no real packet is in the buffer,
        // signal the drain thread to inject PLC now rather than waiting for the
        // next real packet to arrive (which would be a full frame too late).
        if (_lastReleasedPt.HasValue)
        {
            long nextExpectedPt = _lastReleasedPt.Value + OpenFreqRtcClient.OPUS_SAMPLES_PER_FRAME;

            if (jitterAdjustedElapsedSamples >= nextExpectedPt)
            {
                bool nextPacketMissing = _buffer.Count == 0 ||
                    (_buffer.First().Value.Timestamp - _baseTimestamp) > nextExpectedPt;

                if (nextPacketMissing)
                {
                    // We can't directly tell "next packet still in flight / burst in
                    // progress" from "talkspurt ended", so conceal for a bounded run:
                    //  - provenGap: a packet at/after this slot was already received,
                    //    so the gap is real loss — conceal up to MAX_CONCEAL_FRAMES.
                    //  - otherwise conceal "blind" up to MAX_BLIND_CONCEAL_FRAMES,
                    //    enough to ride out a late successor or a short burst.
                    // Past the cap, give up: drop the cursor and fall through so the
                    // release loop skips ahead to whatever is buffered (or NoPackets).
                    bool provenGap = _lastReceivedPt >= nextExpectedPt;
                    int cap = provenGap ? MAX_CONCEAL_FRAMES : MAX_BLIND_CONCEAL_FRAMES;

                    if (_concealmentRunLength >= cap)
                    {
                        _lastReleasedPt = null;
                        _concealmentRunLength = 0;
                        _activeBufferMs = _targetBufferMs;
                        // fall through to the buffer-empty check / release loop
                    }
                    else
                    {
                        // If N+1 is already in the buffer, pass its payload so the
                        // decoder can use LBRR FEC to recover N instead of pure PLC.
                        byte[]? fecPayload = null;
                        if (_buffer.Count > 0)
                        {
                            var candidate = _buffer.First().Value;
                            long candidatePt = candidate.Timestamp - _baseTimestamp;
                            if (candidatePt == nextExpectedPt + OpenFreqRtcClient.OPUS_SAMPLES_PER_FRAME)
                                fecPayload = candidate.Payload;
                        }

                        // Advance cursor and tell the drain thread to generate FEC/PLC.
                        _lastReleasedPt = nextExpectedPt;
                        _concealmentRunLength++;
                        _packetsLost++;
                        return new ConcealmentNeeded(fecPayload);
                    }
                }
                // Real packet IS at the expected slot and is due — fall through to release it.
            }
            else if (_buffer.Count == 0)
            {
                // Cursor set, next slot not yet due, buffer empty — wait.
                long ticksUntilNext = (long)(
                    (double)(nextExpectedPt - jitterAdjustedElapsedSamples) /
                        OpenFreqRtcClient.SAMPLE_RATE * Stopwatch.Frequency);
                return new WaitFor(ticksUntilNext);
            }
            // Cursor set, next slot not yet due, buffer has packets:
            // fall through to the release loop which will produce a WaitFor.
        }

        if (_buffer.Count == 0)
        {
            _activeBufferMs = _targetBufferMs;
            return new NoPackets();
        }

        // Release any packets whose playout time has arrived.
        List<SequencedPacket> readies = [];
        while (_buffer.Count > 0)
        {
            var first = _buffer.First();
            var packet = first.Value;
            var pt = packet.Timestamp - _baseTimestamp;
            var samplesUntilReady = pt - jitterAdjustedElapsedSamples;
            if (samplesUntilReady > 0)
            {
                if (readies.Count == 0)
                {
                    var ticksUntilReady = (long)(
                        (double)samplesUntilReady /
                            OpenFreqRtcClient.SAMPLE_RATE * Stopwatch.Frequency);
                    return new WaitFor(ticksUntilReady);
                }
                else break;
            }
            else
            {
                _buffer.Remove(first.Key);
                readies.Add(packet);
            }
        }

        _packetsPlayed += readies.Count;
        foreach (var p in readies)
        {
            _lastReleasedPt = p.Timestamp - _baseTimestamp; // advance playout cursor
        }
        _concealmentRunLength = 0; // real audio resumed — reset the concealment budget
        return new PacketsReady(readies);
    }

    /// <summary>
    /// Measure packet arrival jitter
    /// </summary>
    private void MeasureJitter(long ticksElapsed, long samplesElapsed)
    {
        if (_lastPacketReceivedTicks == 0)
        {
            // First packet - just record baseline
            _lastPacketReceivedTicks = ticksElapsed;
            _lastPacketTimestamp = samplesElapsed;
            return;
        }
        if (samplesElapsed < _lastPacketTimestamp) ++_packetsLate;

        double actualInterval = (double)(ticksElapsed - _lastPacketReceivedTicks) / Stopwatch.Frequency;
        double expectedInterval = (double)(samplesElapsed - _lastPacketTimestamp) / OpenFreqRtcClient.SAMPLE_RATE;

        // Detect transmission gap (PTT released).
        if (expectedInterval > 0.5)
        {
            _logger.LogInformation("Transmission gap detected ({IntervalMs:F0}ms RTP delta), resetting jitter measurement",
                expectedInterval * 1000.0);
            _jitterSamples.Clear();
            _measuredJitterMs = 0;
            _lastPacketReceivedTicks = ticksElapsed;
            _lastPacketTimestamp = samplesElapsed;
            // New talkspurt starting — safe to apply pending adaptation now.
            _activeBufferMs = _targetBufferMs;
            return;
        }

        // Normal jitter calculation
        double jitterMs = Math.Abs(actualInterval - expectedInterval) * 1000;

        _jitterSamples.Enqueue(jitterMs);
        while (_jitterSamples.Count > 50)
            _jitterSamples.Dequeue();

        if (_jitterSamples.Count >= 10)
        {
            _measuredJitterMs = _jitterSamples.Average();
        }

        _lastPacketReceivedTicks = ticksElapsed;
        _lastPacketTimestamp = samplesElapsed;
    }

    /// <summary>
    /// Adapt target buffer size from observed playout margin.
    /// Sizes the buffer so the worst recent packet would still have had
    /// DESIRED_MARGIN_MS of spare time. Margin folds together jitter (its
    /// variance) and a constant delay step (its level), so this reacts to
    /// both — unlike a pure inter-arrival jitter metric, which is blind to a
    /// constant delay. Asymmetric convergence: fast increase, slow decrease.
    /// </summary>
    private void AdaptBufferSize()
    {
        if (_marginSamples.Count < 10)
            return;

        // Protect the worst packet, not the average: take a low percentile of
        // intrinsic margin (most negative = latest arrival), ignoring a couple
        // of extreme outliers — mirrors the old jitter-p95 logic.
        var sorted = _marginSamples.OrderBy(x => x).ToList();
        int p5Index = (int)(sorted.Count * 0.05);
        double worstMarginMs = sorted[p5Index];

        // The buffer that would lift that worst packet up to DESIRED_MARGIN_MS.
        double targetBuffer = Math.Clamp(
            DESIRED_MARGIN_MS - worstMarginMs, MIN_BUFFER_MS, MAX_BUFFER_MS);

        double delta = targetBuffer - _targetBufferMs;
        // Converge up quickly (protect against bursts), down slowly (avoid churn)
        double rate = delta > 0 ? 0.15 : 0.03;
        _targetBufferMs += delta * rate;
        _targetBufferMs = Math.Clamp(_targetBufferMs, MIN_BUFFER_MS, MAX_BUFFER_MS);
    }


    /// <summary>
    /// Get buffer statistics
    /// </summary>
    public (int received, int lost, int late, int duplicate, int played,
        double lossPercent, double jitterMs, double bufferMs, int buffered) GetStatistics()
    {
        double lossPercent = (_packetsReceived + _packetsLost) > 0
            ? (100.0 * _packetsLost) / (_packetsReceived + _packetsLost)
            : 0.0;

        int bufferedCount = _buffer.Count;

        return (
            _packetsReceived,
            _packetsLost,
            _packetsLate,
            _packetsDuplicate,
            _packetsPlayed,
            lossPercent,
            _measuredJitterMs,
            _targetBufferMs,
            bufferedCount
        );
    }

    /// <summary>
    /// Set target buffer size (for manual override).
    /// Applied to both target and active delay — safe to call before any talkspurt.
    /// </summary>
    public void SetTargetBufferSize(double milliseconds)
    {
        _targetBufferMs = Math.Clamp(milliseconds, MIN_BUFFER_MS, MAX_BUFFER_MS);
        _activeBufferMs = _targetBufferMs;
        _logger.LogInformation("Manual buffer size: {BufferMs:F0}ms", _targetBufferMs);
    }
}
