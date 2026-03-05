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
        public uint PlayoutTimestamp { get; set; }
    }
        
    private readonly SortedDictionary<ushort, BufferedPacket> _buffer = new();
    private readonly Queue<double> _jitterSamples = new(50);
    private readonly int _sampleRate;
    private readonly int _maxBufferPackets;
        
    // State
    private ushort _nextExpectedSequence;
    private uint _baseTimestamp;
    private long _baseTimeTicks;
    private long _playoutStartTicks;
    private long _lastPacketReceivedTicks;
    private double _lastPacketTimestamp;
    private bool _initialized;
        
    // Adaptive jitter buffer parameters
    private double _targetBufferMs = 60; // Start with 60ms
    private double _measuredJitterMs;
    private const double MIN_BUFFER_MS = 30;
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
        _sampleRate = sampleRate;
        _packetsLate = 0;
        _maxBufferPackets = maxBufferPackets;
    }
        
    /// <summary>
    /// Add packet to jitter buffer
    /// </summary>
    public void AddPacket(RtpPacket packet)
    {
        _packetsReceived++;
    
        // Initialize on first packet
        if (!_initialized)
        {
            _baseTimestamp = packet.Timestamp;
            _baseTimeTicks = Stopwatch.GetTimestamp();
            _playoutStartTicks = _baseTimeTicks + (long)(_targetBufferMs / 1000.0 * Stopwatch.Frequency);
            _initialized = true;

            _logger.LogInformation("Initialized: buffer={BufferMs}ms", _targetBufferMs);
        }

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
            double timestampMs = (timestampDiff * 1000.0) / _sampleRate;

            var bufferedPacket = new BufferedPacket
            {
                Packet = packet,
                ReceivedTicks = Stopwatch.GetTimestamp(),
                PlayoutTimestamp = packet.Timestamp
            };

            // Add to buffer
            _buffer[packet.SequenceNumber] = bufferedPacket;

            MeasureJitter(bufferedPacket, timestampMs);

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
        if (!_initialized || _buffer.Count == 0)
            return null;
        
        var nowTicks = Stopwatch.GetTimestamp();

        if (nowTicks < _playoutStartTicks)
            return null;

        var elapsedTicks = nowTicks - _playoutStartTicks;

        // Calculate which timestamp we should be playing now
        uint playoutTimestamp = _baseTimestamp + (uint)((double)elapsedTicks * _sampleRate / Stopwatch.Frequency);

        KeyValuePair<ushort, BufferedPacket> nextPacket;
        lock (_buffer)
        {


            foreach (var kvp in _buffer.OrderBy(k => k.Key).Take(3))
            {
                var valuePlayoutTimestamp = kvp.Value.PlayoutTimestamp;
                RtpPacket.TimestampDifference(playoutTimestamp, valuePlayoutTimestamp);
            }

            // Find packets ready for playout
            var readyPackets = _buffer
                .Where(kvp => RtpPacket.TimestampDifference(playoutTimestamp, kvp.Value.PlayoutTimestamp) >= 0)
                .OrderBy(kvp => kvp.Key)
                .ToList();

            if (readyPackets.Count == 0)
            {
                return null;
            }

            // Get the packet with the lowest sequence number
            nextPacket = readyPackets.First();
            _buffer.Remove(nextPacket.Key);

            // Check if this is the expected sequence (loss detection)
            if (nextPacket.Key != _nextExpectedSequence)
            {
                var gap = RtpPacket.SequenceDifference(nextPacket.Key, _nextExpectedSequence);
                if (gap > 0)
                {
                    // Skipped packets (loss)
                    _packetsLost += gap;
                    _logger.LogWarning("Packet loss: {Gap} packets (seq {ExpectedSeq} to {LastSeq})", 
                        gap, _nextExpectedSequence, nextPacket.Key - 1);
                }
                else
                {
                    // Late packet
                    _packetsLate++;
                    _logger.LogWarning("Late packet seq {SequenceNumber} (expected {ExpectedSequence})", 
                        nextPacket.Key, _nextExpectedSequence);
                }
            }

            _nextExpectedSequence = (ushort)(nextPacket.Key + 1);
            _packetsPlayed++;
        }

        return nextPacket.Value.Packet;
    }
    
    /// <summary>
    /// Peek at the next packet that would be returned, without removing it
    /// Used for FEC decoding when checking if packet N+1 exists
    /// </summary>
    public RtpPacket? PeekNextPacket()
    {
        if (!_initialized || _buffer.Count == 0)
            return null;
        
        var nowTicks = Stopwatch.GetTimestamp();

        if (nowTicks < _playoutStartTicks)
        {
            return null;
        }

        var elapsedTicks = nowTicks - _playoutStartTicks;

        // Calculate which timestamp we should be playing now
        uint playoutTimestamp = _baseTimestamp + (uint)((double)elapsedTicks * _sampleRate / Stopwatch.Frequency);

        lock (_buffer)
        {
            // Find packets ready for playout
            var readyPackets = _buffer
                .Where(kvp => RtpPacket.TimestampDifference(playoutTimestamp, kvp.Value.PlayoutTimestamp) >= 0)
                .OrderBy(kvp => kvp.Key)
                .ToList();

            if (readyPackets.Count == 0)
            {
                return null;
            }

            // Return the packet without removing it
            return readyPackets.First().Value.Packet;
        }
    }
    
    /// <summary>
    /// Try to get a specific packet by sequence number (for FEC)
    /// </summary>
    public RtpPacket? GetPacketBySequence(ushort sequenceNumber)
    {
        lock (_buffer)
        {
            if (_buffer.TryGetValue(sequenceNumber, out var bufferedPacket))
            {
                return bufferedPacket.Packet;
            }
            return null;
        }
    }
        
    /// <summary>
    /// Measure packet arrival jitter
    /// </summary>
    private void MeasureJitter(BufferedPacket packet, double expectedTimestampMs)
    {
        if (_lastPacketReceivedTicks == 0)
        {
            // First packet - just record baseline
            _lastPacketReceivedTicks = packet.ReceivedTicks;
            _lastPacketTimestamp = expectedTimestampMs;
            return;
        }

        double actualIntervalMs = (packet.ReceivedTicks - _lastPacketReceivedTicks) * 1000.0 / Stopwatch.Frequency;
        double expectedIntervalMs = expectedTimestampMs - _lastPacketTimestamp;
    
        // Ignore abnormal timestamp deltas (first packet often has accumulated frames)
        if (expectedIntervalMs > 150)
        {
            _logger.LogWarning("Ignoring abnormal timestamp delta: {DeltaMs:F0}ms", expectedIntervalMs);
            _lastPacketReceivedTicks = packet.ReceivedTicks;
            _lastPacketTimestamp = expectedTimestampMs;
            return;
        }
    
        // Detect transmission gap (PTT released)
        if (actualIntervalMs > 500 || expectedIntervalMs > 500)
        {
            _logger.LogInformation("Transmission gap detected ({IntervalMs:F0}ms), resetting jitter measurement", actualIntervalMs);
            _jitterSamples.Clear();
            _measuredJitterMs = 0;
            _lastPacketReceivedTicks = packet.ReceivedTicks;
            _lastPacketTimestamp = expectedTimestampMs;
            return;
        }
    
        // Normal jitter calculation
        double jitter = Math.Abs(actualIntervalMs - expectedIntervalMs);
    
        _jitterSamples.Enqueue(jitter);
        if (_jitterSamples.Count > 50)
            _jitterSamples.Dequeue();
    
        if (_jitterSamples.Count >= 10)
        {
            _measuredJitterMs = _jitterSamples.Average();
        }
    
        _lastPacketReceivedTicks = packet.ReceivedTicks;
        _lastPacketTimestamp = expectedTimestampMs;
    }
        
    /// <summary>
    /// Adapt buffer size based on observed jitter
    /// </summary>
    private void AdaptBufferSize()
    {
        if (_jitterSamples.Count < 10)
            return;
    
        // Use 95th percentile instead of max (handles occasional spikes)
        var sortedJitter = _jitterSamples.OrderBy(x => x).ToList();
        int p95Index = (int)(sortedJitter.Count * 0.95);
        double jitter95 = sortedJitter[p95Index];
    
        // Target buffer = 4x p95 jitter, minimum 60ms
        double targetBuffer = Math.Max(60, jitter95 * 4.0);
    
        // Adapt gradually (5% per adjustment = ~20 steps to converge)
        double delta = targetBuffer - _targetBufferMs;
        _targetBufferMs += delta * 0.05;
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
    /// Reset jitter buffer
    /// </summary>
    public void Reset()
    {
        lock (_buffer)
        {
            _buffer.Clear();
            _jitterSamples.Clear();
            _initialized = false;
            _baseTimeTicks = 0;
            _playoutStartTicks = 0;
            _lastPacketReceivedTicks = 0;
            _lastPacketTimestamp = 0;
            _packetsReceived = 0;
            _packetsLost = 0;
            _packetsLate = 0;
            _packetsDuplicate = 0;
            _packetsPlayed = 0;
        }
    }
        
    /// <summary>
    /// Get current buffer size in milliseconds
    /// </summary>
    public double GetBufferSizeMs() => _targetBufferMs;
        
    /// <summary>
    /// Set target buffer size (for manual override)
    /// </summary>
    public void SetTargetBufferSize(double milliseconds)
    {
        _targetBufferMs = Math.Clamp(milliseconds, MIN_BUFFER_MS, MAX_BUFFER_MS);
        _logger.LogInformation("Manual buffer size: {BufferMs:F0}ms", _targetBufferMs);
    }
}