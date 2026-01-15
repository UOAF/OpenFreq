using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using Concentus;
using Concentus.Structs;
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

    private readonly UdpClient _udpClient;
    private readonly RtpJitterBuffer _jitterBuffer;
    private readonly OpusDecoder? _opusDecoder;
    private readonly bool _opusEnabled;
    private readonly CancellationTokenSource _cts = new();
    private readonly Timer _playoutTimer;
        
    private const int PLAYOUT_INTERVAL_MS = 20; // Check for ready packets every 20ms
        
    /// <summary>
    /// Create RTP audio receiver with adaptive jitter buffer
    /// </summary>
    /// <param name="udpClient">Existing UDP client</param>
    /// <param name="opusEnabled">Whether to decode Opus (true) or expect raw PCM (false)</param>
    /// <param name="initialBufferMs">Initial jitter buffer size in milliseconds (will adapt)</param>
    public RtpAudioReceiver(UdpClient udpClient, bool opusEnabled = true, int initialBufferMs = 150)
    {
        _opusEnabled = opusEnabled;

        if (_opusEnabled)
        {
            _opusDecoder =  new OpusDecoder(OpenFreqRtcClient.SAMPLE_RATE, OpenFreqRtcClient.CHANNELS) as OpusDecoder; // OpusCodecFactory.CreateDecoder(OpenFreqRtcClient.SAMPLE_RATE, OpenFreqRtcClient.CHANNELS) as OpusDecoder;
        }

        // Create jitter buffer
        _jitterBuffer = new RtpJitterBuffer(sampleRate: 48000);
        _jitterBuffer.SetTargetBufferSize(initialBufferMs);

        // Reuse UDP Client
        _udpClient = udpClient;
        var port = (_udpClient.Client.LocalEndPoint as IPEndPoint).Port;
        Console.WriteLine($"[RtpAudioReceiver] Started on port {port}");
        Console.WriteLine($"[RtpAudioReceiver]   Opus: {_opusEnabled}");
        Console.WriteLine($"[RtpAudioReceiver]   Initial buffer: {initialBufferMs}ms (adaptive)");

        // Start receiving task
        Task.Run(() => ReceiveLoop(), _cts.Token);
            
        // Start playout timer (pulls packets from jitter buffer)
        _playoutTimer = new Timer(PlayoutTimerCallback, null, 0, PLAYOUT_INTERVAL_MS);

        Task.Run(() =>
        {
            while (true)
            {
                var stats = GetStatistics();
                Console.WriteLine($"[Network Stats]");
                Console.WriteLine($"  Packets: received={stats.received}, lost={stats.lost} ({stats.lossPercent:F1}%)");
                Console.WriteLine($"  Jitter: {stats.jitterMs:F1}ms");
                Console.WriteLine($"  Buffer size: {stats.bufferMs:F0}ms (adaptive)");
                Console.WriteLine($"  Buffered packets: {stats.buffered}");
                Task.Delay(5000).Wait();
            }
        });
    }

    /// <summary>
    /// UDP receive loop - receives packets and adds to jitter buffer
    /// </summary>
    private async Task ReceiveLoop()
    {
        Console.WriteLine("[RtpAudioReceiver] Receive loop started");
            
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
            
        Console.WriteLine("[RtpAudioReceiver] Receive loop stopped");
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
                Console.WriteLine("[RtpAudioReceiver] Invalid RTP packet");
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
            RtpPacket? packet;
            
            // Pull all ready packets
            while ((packet = _jitterBuffer.GetNextPacket()) != null)
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
                Console.WriteLine("[RtpAudioReceiver] Payload too small");
                return;
            }

            ushort metadataLength = (ushort)((packet.Payload[0] << 8) | packet.Payload[1]);
                
            if (metadataLength + 2 > packet.Payload.Length)
            {
                Console.WriteLine($"[RtpAudioReceiver] Invalid metadata length: {metadataLength}");
                return;
            }

            // Extract metadata JSON
            string metadataJson = Encoding.UTF8.GetString(packet.Payload, 2, metadataLength);
            var metadata = JsonSerializer.Deserialize<AudioPacketMetadata>(metadataJson);

            if (metadata == null)
            {
                Console.WriteLine("[RtpAudioReceiver] Failed to parse metadata");
                return;
            }
            
            if (metadata.HasAnyBeginMarker)
            {
                //_jitterBuffer.Reset();
            }

            // Extract audio data
            int audioDataStart = 2 + metadataLength;
            int audioDataLength = packet.Payload.Length - audioDataStart;
            byte[] audioData = new byte[audioDataLength];
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
            return Array.Empty<byte>();

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
                Console.WriteLine($"[RtpAudioReceiver] Opus decode failed for {opusData.Length} bytes");
                return Array.Empty<byte>();
            }

            // Convert to byte array (16-bit PCM)
            byte[] decodedAudio = new byte[samplesDecoded * 2];
            Buffer.BlockCopy(pcmSamples, 0, decodedAudio, 0, decodedAudio.Length);
                
            return decodedAudio;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[RtpAudioReceiver] Opus decode exception: {ex.Message}");
            return Array.Empty<byte>();
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
        Console.WriteLine("=== RTP Receiver Statistics ===");
        Console.WriteLine($"  Packets received: {stats.received}");
        Console.WriteLine($"  Packets lost: {stats.lost} ({stats.lossPercent:F2}%)");
        Console.WriteLine($"  Packets late: {stats.late}");
        Console.WriteLine($"  Packets duplicate: {stats.duplicate}");
        Console.WriteLine($"  Packets played: {stats.played}");
        Console.WriteLine($"  Measured jitter: {stats.jitterMs:F1}ms");
        Console.WriteLine($"  Buffer size: {stats.bufferMs:F0}ms (adaptive)");
        Console.WriteLine($"  Currently buffered: {stats.buffered} packets");
        Console.WriteLine("================================");
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
        _cts?.Cancel();
        _playoutTimer?.Dispose();
        _opusDecoder?.Dispose();
            
        PrintStatistics();
        Console.WriteLine("[RtpAudioReceiver] Disposed");
    }
}