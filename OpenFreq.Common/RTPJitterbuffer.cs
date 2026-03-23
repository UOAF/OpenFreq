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
    
    private class BufferedPacket
    {
        public required RtpPacket Packet { get; set; }
        public long ReceivedTicks { get; set; }
    }
    /// <summary>
    /// Sorts packets based on their (extended) sequence number
    /// </summary>
    private readonly SortedDictionary<ushort, BufferedPacket> _buffer = new();
    private readonly Queue<double> _jitterSamples = new(50);
    private readonly int _maxBufferPackets;
        
    /// <summary>
    /// Once we've played a packet, the next expected one. Used to detect missing ones.
    /// </summary>
    private ushort? _nextExpectedSequence;
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
    
        // Initialize on first packet
        if (_packetsReceived == 0)
        {
            _baseTimestamp = packet.Timestamp;
            _baseTimeTicks = Stopwatch.GetTimestamp();

            _logger.LogInformation("Initialized: buffer={BufferMs}ms", _targetBufferMs);
        }
        _packetsReceived++;

        lock (_buffer)
        {


            // Check for duplicate
            if (_buffer.ContainsKey(packet.SequenceNumber))
            {
                _packetsDuplicate++;
                return;
            }

            // Calculate playout time
            long timestampDiff = RtpPacket.TimestampDifference(packet.Timestamp, _baseTimestamp);

            var bufferedPacket = new BufferedPacket
            {
                Packet = packet,
                ReceivedTicks = Stopwatch.GetTimestamp()
            };

            // Add to buffer
            _buffer[packet.SequenceNumber] = bufferedPacket;

            MeasureJitter(bufferedPacket.ReceivedTicks, timestampDiff);

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
    }
        
    /// <summary>
    /// Get next packet ready for playout
    /// </summary>
    public RtpPacket? GetNextPacket()
    {
        if (_packetsReceived == 0 || _buffer.Count == 0)
            return null;

        var nowTicks = Stopwatch.GetTimestamp();
        var elapsedTicks = nowTicks - _baseTimeTicks;

        // The playout position is "where the sender was _targetBufferMs ago".
        // When _targetBufferMs changes (via AdaptBufferSize), this adjusts immediately.
        long elapsedSamples = (long)((double)elapsedTicks * OpenFreqRtcClient.SAMPLE_RATE / Stopwatch.Frequency);
        long bufferDelaySamples = (long)(_targetBufferMs / 1000.0 * OpenFreqRtcClient.SAMPLE_RATE);
        uint playoutTimestamp = _baseTimestamp + (uint)(elapsedSamples - bufferDelaySamples);

        KeyValuePair<ushort, BufferedPacket> nextPacket;
        lock (_buffer)
        {


            // Find packets ready for playout
            var readyPackets = _buffer
                .Where(kvp => RtpPacket.TimestampDifference(playoutTimestamp, kvp.Value.Packet.Timestamp) >= 0)
                .ToList();

            if (readyPackets.Count == 0)
            {
                return null;
            }

            // Get the packet with the lowest sequence number
            nextPacket = readyPackets.First();
            _buffer.Remove(nextPacket.Key);

            // Check if this is the expected sequence (loss detection).
            // Skip the check on the first packet
            if (_nextExpectedSequence.HasValue)
            {
                var nextExpected = _nextExpectedSequence.Value;
                var gap = RtpPacket.SequenceDifference(nextPacket.Key, nextExpected);
                if (gap > 0)
                {
                    // Skipped packets (loss)
                    _packetsLost += gap;
                    _logger.LogWarning("Packet loss: {Gap} packets (seq {ExpectedSeq} to {LastSeq})", 
                        gap, nextExpected, nextPacket.Key - 1);
                }
                else
                {
                    // Late packet
                    _packetsLate++;
                    _logger.LogWarning("Late packet seq {SequenceNumber} (expected {ExpectedSequence})", 
                        nextPacket.Key, nextExpected);
                }
            }

            _nextExpectedSequence = (ushort)(nextPacket.Key + 1);
            _packetsPlayed++;

        }

        return nextPacket.Value.Packet;
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
        if (_jitterSamples.Count > 50)
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

        int bufferedCount;
        lock (_buffer)
        {
            bufferedCount = _buffer.Count;
        }

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