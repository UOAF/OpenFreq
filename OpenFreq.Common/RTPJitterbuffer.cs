using System.Diagnostics;
using Microsoft.Extensions.Logging;
using OpenFreq.Common.Rtp;

namespace OpenFreq.Common;

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
    private readonly SortedDictionary<ushort, RtpPacket> _buffer = new();
    private readonly Queue<double> _jitterSamples = new(50);
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
    /// <summary>
    /// Local monotonic time when the last packet arrived.
    /// </summary>
    private long _lastPacketReceivedTicks;
    private long _lastPacketTimestamp;

    // Adaptive jitter buffer parameters
    private double _targetBufferMs = 60; // Start with 60ms
    private double _measuredJitterMs;
    private const double MIN_BUFFER_MS = 20;
    private const double MAX_BUFFER_MS = 500;
        
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
            _logger.LogInformation("Initialized: buffer={BufferMs}ms", _targetBufferMs);
        }
        _packetsReceived++;

        // Check for duplicate
        if (_buffer.ContainsKey(packet.SequenceNumber))
        {
            _packetsDuplicate++;
            return;
        }

        // Calculate playout time
        long timestampDiff = RtpPacket.TimestampDifference(packet.Timestamp, _baseTimestamp);

        // Add to buffer
        _buffer[packet.SequenceNumber] = packet;

        var ticksDiff = now - _baseTimeTicks;
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
    public record PacketsReady(List<RtpPacket> Packets) : PacketsReadyResult;
    public record WaitFor(long Ticks) : PacketsReadyResult;


    public PacketsReadyResult GetReadyPackets(long now)
    {
        // Bail if we've got nothing to play
        if (_buffer.Count == 0)return new NoPackets();

        long elapsedTicks = now - _baseTimeTicks;
        // The current jitter buffer length, in ticks.
        long bufferDelayTicks = (long)(_targetBufferMs / 1000.0 * Stopwatch.Frequency);
        // How many samples have elapsed, after applying the buffer delay?
        long jitterAdjustedElapsedSamples = (long)(
            (double)(elapsedTicks - bufferDelayTicks) /
                Stopwatch.Frequency * OpenFreqRtcClient.SAMPLE_RATE);

        List<RtpPacket> readies = [];
        while (_buffer.Count > 0)
        {
            var first = _buffer.First();
            var packet = first.Value;
            var pt = packet.Timestamp - _baseTimestamp;
            var samplesUntilReady = (long)pt - jitterAdjustedElapsedSamples;
            if (samplesUntilReady > 0)
            {
                // If no packets are ready,
                // return the amount of time to wait until the first is.
                if (readies.Count == 0)
                {
                    // Back to ticks
                    var ticksUntilReady = (long)(
                        (double)samplesUntilReady /
                            OpenFreqRtcClient.SAMPLE_RATE * Stopwatch.Frequency);
                    return new WaitFor(ticksUntilReady);
                }
                else
                {
                    break;
                }
            }
            else
            {
                _buffer.Remove(first.Key);
                readies.Add(packet);
            }
        }
        _packetsPlayed += readies.Count;
        // TODO other stats aggregation
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
    /// Adapt target buffer size based on observed jitter.
    /// Uses asymmetric convergence: fast increase to protect against bursts,
    /// slow decrease to avoid oscillation.
    /// </summary>
    private void AdaptBufferSize()
    {
        if (_jitterSamples.Count < 10)
            return;

        var sortedJitter = _jitterSamples.OrderBy(x => x).ToList();
        int p95Index = (int)(sortedJitter.Count * 0.95);
        double jitter95 = sortedJitter[p95Index];

        // 2.5x p95 jitter is enough headroom for most conditions
        double targetBuffer = Math.Max(MIN_BUFFER_MS, jitter95 * 2.5);

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
    /// Set target buffer size (for manual override)
    /// </summary>
    public void SetTargetBufferSize(double milliseconds)
    {
        _targetBufferMs = Math.Clamp(milliseconds, MIN_BUFFER_MS, MAX_BUFFER_MS);
        _logger.LogInformation("Manual buffer size: {BufferMs:F0}ms", _targetBufferMs);
    }
}