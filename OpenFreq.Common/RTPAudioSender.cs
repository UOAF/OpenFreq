using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using Concentus;
using Concentus.Enums;
using Concentus.Structs;
using Microsoft.Extensions.Logging;
using OpenFreq.Common.Rtp;

namespace OpenFreq.Common;

/// <summary>
/// RTP audio sender with proper sequencing and timestamps.
/// Wraps audio and metadata in RTP packets for reliable transmission.
/// </summary>
public class RtpAudioSender : IDisposable
{
    private readonly ILogger<RtpAudioSender> _logger;
    private const int OPUS_FRAME_SIZE = 960; // 20ms at 48kHz

    private readonly byte[]
        _audioBuffer = new byte[OPUS_FRAME_SIZE * 2 * OpenFreqRtcClient.CHANNELS]; // *2 for 16-bit, *channels

    private int _bufferPosition = 0;

    private readonly UdpClient _udpClient;
    public UdpClient UdpClient => _udpClient;
    private readonly IPEndPoint _serverEndpoint;
    private readonly OpusEncoder? _opusEncoder;
    private readonly bool _opusEnabled;

    // RTP state
    private ushort _sequenceNumber = 0;
    private uint _timestamp = 0;
    private readonly uint _ssrc;
    private const byte PAYLOAD_TYPE_OPUS = 96;
    private const byte PAYLOAD_TYPE_PCMU = 0;

    // Queued audio data structure (raw PCM, not encoded yet)
    private struct QueuedAudio
    {
        public byte[] PcmData;
        public string ClientId;
        public List<FrequencyTransmission> FrequencyTransmissions;
        public uint Timestamp;
        public ushort SequenceNumber;
    }

    // Sending Queue - now holds raw PCM data, encoding happens in pacing timer
    private readonly Queue<QueuedAudio> _sendQueue = new Queue<QueuedAudio>();

    private System.Timers.Timer? _pacingTimer;
    private readonly object _queueLock = new object();

    // Reusable buffers to avoid allocations in pacing timer
    private readonly byte[] _opusBuffer = new byte[4000];
    private readonly short[] _pcmSamples = new short[OPUS_FRAME_SIZE * OpenFreqRtcClient.CHANNELS];

    // Statistics
    private int _packetsSent = 0;
    private DateTime _startTime = DateTime.UtcNow;

    /// <summary>
    /// Create RTP audio sender
    /// </summary>
    public RtpAudioSender(ILogger<RtpAudioSender> logger, string serverHost, int serverPort, bool opusEnabled = true)
    {
        _logger = logger;
        _opusEnabled = opusEnabled;
        _serverEndpoint = new IPEndPoint(IPAddress.Parse(serverHost), serverPort);
        _udpClient = new UdpClient();

        // Generate unique SSRC (synchronization source identifier)
        _ssrc = (uint)Random.Shared.Next();

        // Send empty RTP keepalive packet to register our port
        var keepalive = new RtpPacket
        {
            Version = 2,
            PayloadType = _opusEnabled ? PAYLOAD_TYPE_OPUS : PAYLOAD_TYPE_PCMU,
            SequenceNumber = 0,
            Timestamp = 0,
            Ssrc = _ssrc,
            Payload = [] // Empty payload
        };
        _udpClient.Send(keepalive.ToBytes(), _serverEndpoint);

        if (_opusEnabled)
        {
            try
            {
                #pragma warning disable CS0618 // Do not use the factory - it does not work with Linux
                _opusEncoder = new OpusEncoder(
                    OpenFreqRtcClient.SAMPLE_RATE,
                    OpenFreqRtcClient.CHANNELS,
                    OpusApplication.OPUS_APPLICATION_RESTRICTED_LOWDELAY
                );
                #pragma warning restore CS0618 // Type or member is obsolete
            }
            catch (OpusException ex)
            {
                // This is the Opus error code
                _logger.LogError("Opus error: {OpusErrorCode}", ex.OpusErrorCode);
                _logger.LogError("Message: {Message}", ex.Message);
            }

            if (_opusEncoder == null)
            {
                _logger.LogError("Could not create OpusEncoder");
                return;
            }
            else
            {
                _opusEncoder.Bitrate = 98000; 
                _opusEncoder.Complexity = 8;
                _opusEncoder.SignalType = OpusSignal.OPUS_SIGNAL_MUSIC;
                _opusEncoder.UseInbandFEC = true;
                _opusEncoder.PacketLossPercent = 15;
                _opusEncoder.ForceMode = OpusMode.MODE_SILK_ONLY;
            }
        }

        // Start pacing timer - sends one packet every 10ms
        _pacingTimer = new System.Timers.Timer(10); // 10ms interval
        _pacingTimer.Elapsed += OnPacingTimerElapsed;
        _pacingTimer.AutoReset = true;
        _pacingTimer.Start();

        _logger.LogInformation("Initialized");
        _logger.LogInformation("  Server: {ServerHost}:{ServerPort}", serverHost, serverPort);
        _logger.LogInformation("  Opus: {OpusEnabled}", _opusEnabled);
        _logger.LogInformation("  SSRC: 0x{Ssrc:X8}", _ssrc);
    }


    /// <summary>
    /// Send audio packet with metadata
    /// </summary>
    /// <param name="audioData">Raw PCM audio data (16-bit, mono, 48kHz)</param>
    /// <param name="clientId">Sender client ID</param>
    /// <param name="position">Aircraft position</param>
    /// <param name="frequencyTransmissions">List of FrequencyTransmissions</param>
    /// 
    public void SendAudio(byte[] audioData, string clientId, List<FrequencyTransmission> frequencyTransmissions)
    {
        try
        {
            int offset = 0;
            bool isFirstChunk = true;

            // Process all incoming data in chunks
            while (offset < audioData.Length)
            {
                int bytesToCopy = Math.Min(
                    audioData.Length - offset,
                    _audioBuffer.Length - _bufferPosition
                );

                Buffer.BlockCopy(audioData, offset, _audioBuffer, _bufferPosition, bytesToCopy);
                _bufferPosition += bytesToCopy;
                offset += bytesToCopy;

                // If we have a complete frame, queue it
                if (_bufferPosition >= _audioBuffer.Length)
                {
                    // First chunk uses original markers, subsequent chunks clear beginMarkers
                    var markers = isFirstChunk 
                        ? frequencyTransmissions 
                        : frequencyTransmissions.Select(f => new FrequencyTransmission(f.Khz, f.TxPowerWatts, f.Position,false, f.EndMarker)).ToList();
                
                    QueueRawFrame(clientId, markers);
                    isFirstChunk = false;
                    _bufferPosition = 0;
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error in SendAudio");
        }
    }

    private void QueueRawFrame(string clientId, List<FrequencyTransmission> frequencyTransmissions)
    {
       // Copy the buffer data (must copy since _audioBuffer will be reused)
        var pcmCopy = new byte[_audioBuffer.Length];
        Buffer.BlockCopy(_audioBuffer, 0, pcmCopy, 0, _audioBuffer.Length);

        var queued = new QueuedAudio
        {
            PcmData = pcmCopy,
            ClientId = clientId,
            FrequencyTransmissions = frequencyTransmissions,
            Timestamp = _timestamp,
            SequenceNumber = _sequenceNumber,
        };

        lock (_queueLock)
        {
            _sendQueue.Enqueue(queued);
        }

        // Update RTP state
        _sequenceNumber++;
        _timestamp += (uint)OPUS_FRAME_SIZE;
        _packetsSent++;
    }

    private void OnPacingTimerElapsed(object? sender, System.Timers.ElapsedEventArgs e)
    {
        QueuedAudio? queued;

        lock (_queueLock)
        {
            if (_sendQueue.Count == 0)
                return;

            queued = _sendQueue.Dequeue();
        }

        try
        {
            var queuedValue = queued.Value;

            // Encode audio (happens here, not in audio callback)
            byte[] encodedAudio;
            if (_opusEnabled && _opusEncoder != null)
            {
                // Convert byte[] to short[] using reusable buffer
                Buffer.BlockCopy(queuedValue.PcmData, 0, _pcmSamples, 0, queuedValue.PcmData.Length);

                // Encode to Opus using reusable buffer
                var opusBytes = _opusEncoder.Encode(
                    _pcmSamples,
                    0,
                    OPUS_FRAME_SIZE, // Samples per channel
                    _opusBuffer,
                    0,
                    _opusBuffer.Length
                );

                if (opusBytes <= 0)
                {
                    _logger.LogWarning("Opus encode failed");
                    return;
                }

                encodedAudio = new byte[opusBytes];
                Array.Copy(_opusBuffer, encodedAudio, opusBytes);
            }
            else
            {
                // Raw PCM
                encodedAudio = queuedValue.PcmData;
            }

            // Build metadata
            var metadata = new AudioPacketMetadata
            {
                ClientId = queuedValue.ClientId,
                Frequencies = queuedValue.FrequencyTransmissions,
            };

            var metadataJson = JsonSerializer.Serialize(metadata, OpenFreqJsonContext.Default.AudioPacketMetadata);
            var metadataBytes = Encoding.UTF8.GetBytes(metadataJson);
            var metadataLength = (ushort)metadataBytes.Length;

            // Build payload: [2 bytes length][metadata JSON][audio data]
            var payload = new byte[2 + metadataLength + encodedAudio.Length];
            payload[0] = (byte)(metadataLength >> 8);
            payload[1] = (byte)(metadataLength & 0xFF);
            Array.Copy(metadataBytes, 0, payload, 2, metadataLength);
            Array.Copy(encodedAudio, 0, payload, 2 + metadataLength, encodedAudio.Length);

            // Build RTP packet
            var rtpPacket = new RtpPacket
            {
                Version = 2,
                PayloadType = _opusEnabled ? PAYLOAD_TYPE_OPUS : PAYLOAD_TYPE_PCMU,
                SequenceNumber = queuedValue.SequenceNumber,
                Timestamp = queuedValue.Timestamp,
                Ssrc = _ssrc,
                Payload = payload
            };
            
            // Send the packet
            var rtpBytes = rtpPacket.ToBytes();
            _udpClient.Send(rtpBytes, rtpBytes.Length, _serverEndpoint);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Pacing timer error");
        }
    }

    /// <summary>
    /// Get sender statistics
    /// </summary>
    public (int packetsSent, TimeSpan uptime, double packetsPerSecond) GetStatistics()
    {
        var uptime = DateTime.UtcNow - _startTime;
        var pps = uptime.TotalSeconds > 0 ? _packetsSent / uptime.TotalSeconds : 0;
        return (_packetsSent, uptime, pps);
    }

    public void Dispose()
    {
        _pacingTimer?.Stop();
        _pacingTimer?.Dispose();
        _udpClient.Close();
        _udpClient.Dispose();
        _opusEncoder?.Dispose();

        var stats = GetStatistics();
        _logger.LogInformation("Disposed. Sent {PacketsSent} packets over {UptimeSeconds:F1}s ({PacketsPerSecond:F1} pps)", 
            stats.packetsSent, stats.uptime.TotalSeconds, stats.packetsPerSecond);
    }
}