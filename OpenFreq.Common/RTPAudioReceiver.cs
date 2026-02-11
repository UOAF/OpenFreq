using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using Concentus.Structs;
using Microsoft.Extensions.Logging;
using OpenFreq.Common.Rtp;

namespace OpenFreq.Common;

/// <summary>
/// RTP audio receiver with adaptive jitter buffering.
/// Handles network layer: RTP parsing, jitter buffer, packet reordering, Opus decoding.
/// Outputs clean, ordered PCM audio ready for RadioPlayback.
/// </summary>
public class RtpAudioReceiver : IDisposable
{
    public class AudioReceivedEventArgs : EventArgs
    {
        public byte[] AudioData { get; set; } = Array.Empty<byte>();
        public required AudioPacketMetadata Metadata { get; set; }
    }

    public event EventHandler<AudioReceivedEventArgs>? AudioReceived;
    public event EventHandler<string>? ErrorOccurred;

    private readonly ILogger<RtpAudioReceiver> _logger;
    private readonly UdpClient _udpClient;
    private readonly RtpJitterBuffer _jitterBuffer;
    private readonly OpusDecoder? _opusDecoder;
    private readonly bool _opusEnabled;
    private readonly CancellationTokenSource _cts = new();
    private readonly Timer _playoutTimer;
    private readonly ILoggerFactory _loggerFactory;

    private const int PLAYOUT_INTERVAL_MS = 20; // Check for ready packets every 20ms
        
    /// <summary>
    /// Create RTP audio receiver with adaptive jitter buffer
    /// </summary>
    /// <param name="logger">Logger instance</param>
    /// <param name="udpClient">Existing UDP client</param>
    /// <param name="opusEnabled">Whether to decode Opus (true) or expect raw PCM (false)</param>
    /// <param name="initialBufferMs">Initial jitter buffer size in milliseconds (will adapt)</param>
    public RtpAudioReceiver(ILoggerFactory loggerFactory, UdpClient udpClient, bool opusEnabled = true, int initialBufferMs = 150)
    {
        _loggerFactory = loggerFactory;
        _logger = loggerFactory.CreateLogger<RtpAudioReceiver>();
        _opusEnabled = opusEnabled;

        if (_opusEnabled)
        {
            #pragma warning disable CS0618 // Using the new factory method will not work in Linux!
            _opusDecoder =  new OpusDecoder(OpenFreqRtcClient.SAMPLE_RATE, OpenFreqRtcClient.CHANNELS) as OpusDecoder;
            #pragma warning restore CS0618 // Type or member is obsolete
        }

        // Create jitter buffer
        _jitterBuffer = new RtpJitterBuffer(loggerFactory.CreateLogger<RtpJitterBuffer>());
        _jitterBuffer.SetTargetBufferSize(initialBufferMs);

        // Reuse UDP Client
        _udpClient = udpClient;
        var port = (_udpClient.Client.LocalEndPoint as IPEndPoint).Port;
        _logger.LogInformation("Started on port {Port}", port);
        _logger.LogInformation("  Opus: {OpusEnabled}", _opusEnabled);
        _logger.LogInformation("  Initial buffer: {BufferMs}ms (adaptive)", initialBufferMs);

        // Start receiving task
        Task.Run(() => ReceiveLoop(), _cts.Token);
            
        // Start playout timer (pulls packets from jitter buffer)
        _playoutTimer = new Timer(PlayoutTimerCallback, null, 0, PLAYOUT_INTERVAL_MS);

        if (!_logger.IsEnabled(LogLevel.Debug))
        {
            Task.Run(() =>
            {
                while (true)
                {
                    var stats = GetStatistics();
                    _logger.LogDebug("Network Stats");
                    _logger.LogDebug("  Packets: received={Received}, lost={Lost} ({LossPercent:F1}%)",
                        stats.received, stats.lost, stats.lossPercent);
                    _logger.LogDebug("  Jitter: {JitterMs:F1}ms", stats.jitterMs);
                    _logger.LogDebug("  Buffer size: {BufferMs:F0}ms (adaptive)", stats.bufferMs);
                    _logger.LogDebug("  Buffered packets: {Buffered}", stats.buffered);
                    Task.Delay(5000).Wait();
                }
            });
        }
    }

    /// <summary>
    /// UDP receive loop - receives packets and adds to jitter buffer
    /// </summary>
    private async Task ReceiveLoop()
    {
        _logger.LogInformation("Receive loop started");
            
        while (!_cts.Token.IsCancellationRequested)
        {
            try
            {
                var result = await _udpClient.ReceiveAsync(_cts.Token);
                ProcessIncomingPacket(result.Buffer);
            }
            catch (OperationCanceledException)
            {
                break; // Expected during shutdown
            }
            catch (Exception ex)
            {
                ErrorOccurred?.Invoke(this, $"Receive error: {ex.Message}");
            }
        }
            
        _logger.LogInformation("Receive loop stopped");
    }

    /// <summary>
    /// Process incoming UDP packet
    /// </summary>
    private void ProcessIncomingPacket(byte[] data)
    {
        try
        {
            // Parse RTP packet
            var rtpPacket = RtpPacket.Parse(data);
            if (rtpPacket == null)
            {
                _logger.LogWarning("Invalid RTP packet");
                return;
            }

            // Add to jitter buffer (handles reordering, timing)
            _jitterBuffer.AddPacket(rtpPacket);
        }
        catch (Exception ex)
        {
            ErrorOccurred?.Invoke(this, $"Packet processing error: {ex.Message}");
        }
    }

    /// <summary>
    /// Playout timer callback - pulls ready packets from jitter buffer
    /// </summary>
    private void PlayoutTimerCallback(object? state)
    {
        try
        {
            // Pull all ready packets
            while (_jitterBuffer.GetNextPacket() is { } packet)
            {
                ProcessReadyPacket(packet);
            }
        }
        catch (Exception ex)
        {
            ErrorOccurred?.Invoke(this, $"Playout error: {ex.Message}");
        }
    }

    /// <summary>
    /// Process packet that's ready for playout
    /// </summary>
    private void ProcessReadyPacket(RtpPacket packet)
    {
        try
        {
            // Parse metadata from payload
            // Format: [2 bytes metadata len][JSON metadata][audio data]
            if (packet.Payload.Length < 2)
            {
                _logger.LogWarning("Payload too small");
                return;
            }

            var metadataLength = (ushort)((packet.Payload[0] << 8) | packet.Payload[1]);
                
            if (metadataLength + 2 > packet.Payload.Length)
            {
                _logger.LogWarning("Invalid metadata length: {MetadataLength}", metadataLength);
                return;
            }

            // Extract metadata JSON
            var metadataJson = Encoding.UTF8.GetString(packet.Payload, 2, metadataLength);
            var metadata = JsonSerializer.Deserialize(metadataJson, OpenFreqJsonContext.Default.AudioPacketMetadata);

            if (metadata == null)
            {
                _logger.LogWarning("Failed to parse metadata");
                return;
            }
            
            if (metadata.HasAnyBeginMarker)
            {
                //_jitterBuffer.Reset();
            }

            // Extract audio data
            var audioDataStart = 2 + metadataLength;
            var audioDataLength = packet.Payload.Length - audioDataStart;
            var audioData = new byte[audioDataLength];
            Array.Copy(packet.Payload, audioDataStart, audioData, 0, audioDataLength);

            // Decode audio if Opus is enabled
            byte[] decodedAudio;
            if (_opusEnabled && _opusDecoder != null)
            {
                decodedAudio = DecodeOpus(audioData);
                if (decodedAudio.Length == 0)
                    return; // Decode failed
            }
            else
            {
                // Raw PCM - use as-is
                decodedAudio = audioData;
            }

            // Fire event with clean audio
            AudioReceived?.Invoke(this, new AudioReceivedEventArgs
            {
                AudioData = decodedAudio,
                Metadata = metadata
            });
        }
        catch (Exception ex)
        {
            ErrorOccurred?.Invoke(this, $"Ready packet processing error: {ex.Message}");
        }
    }

    /// <summary>
    /// Decode Opus audio
    /// </summary>
    private byte[] DecodeOpus(byte[] opusData)
    {
        if (_opusDecoder == null)
            return [];

        try
        {
            const int MAX_OPUS_FRAME_SAMPLES = 5760; // 120ms at 48kHz
            short[] pcmSamples = new short[MAX_OPUS_FRAME_SAMPLES];
                
            int samplesDecoded = _opusDecoder.Decode(
                opusData, 
                0, 
                opusData.Length, 
                pcmSamples, 
                0, 
                MAX_OPUS_FRAME_SAMPLES, 
                false
            );

            if (samplesDecoded <= 0)
            {
                _logger.LogWarning("Opus decode failed for {ByteCount} bytes", opusData.Length);
                return [];
            }

            // Convert to byte array (16-bit PCM)
            var decodedAudio = new byte[samplesDecoded * 2];
            Buffer.BlockCopy(pcmSamples, 0, decodedAudio, 0, decodedAudio.Length);
                
            return decodedAudio;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Opus decode exception");
            return [];
        }
    }

    /// <summary>
    /// Get receiver statistics
    /// </summary>
    public (int received, int lost, int late, int duplicate, int played, 
        double lossPercent, double jitterMs, double bufferMs, int buffered) GetStatistics()
    {
        return _jitterBuffer.GetStatistics();
    }

    /// <summary>
    /// Print detailed statistics
    /// </summary>
    public void PrintStatistics()
    {
        var stats = GetStatistics();
        _logger.LogInformation("=== RTP Receiver Statistics ===");
        _logger.LogInformation("  Packets received: {Received}", stats.received);
        _logger.LogInformation("  Packets lost: {Lost} ({LossPercent:F2}%)", stats.lost, stats.lossPercent);
        _logger.LogInformation("  Packets late: {Late}", stats.late);
        _logger.LogInformation("  Packets duplicate: {Duplicate}", stats.duplicate);
        _logger.LogInformation("  Packets played: {Played}", stats.played);
        _logger.LogInformation("  Measured jitter: {JitterMs:F1}ms", stats.jitterMs);
        _logger.LogInformation("  Buffer size: {BufferMs:F0}ms (adaptive)", stats.bufferMs);
        _logger.LogInformation("  Currently buffered: {Buffered} packets", stats.buffered);
        _logger.LogInformation("================================");
    }

    /// <summary>
    /// Set jitter buffer size manually (overrides adaptation temporarily)
    /// </summary>
    public void SetJitterBufferSize(int milliseconds)
    {
        _jitterBuffer.SetTargetBufferSize(milliseconds);
    }

    public void Dispose()
    {
        _cts.Cancel();
        _playoutTimer?.Dispose();
        _opusDecoder?.Dispose();
        PrintStatistics();
        _logger.LogInformation("Disposed");
    }
}