using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using Concentus.Enums;
using Concentus.Structs;
using Microsoft.Extensions.Logging;
using OpenFreqAudio;
using OpenFreq.Common.Rtp;
using System.Runtime.InteropServices;

namespace OpenFreq.Common;

/// <summary>
/// RTP audio sender with proper sequencing and timestamps.
/// Wraps audio and metadata in RTP packets for reliable transmission.
/// </summary>
public class RtpAudioSender : IDisposable
{
    private readonly ILogger<RtpAudioSender> _logger;
    private const int OPUS_FRAME_SIZE = OpenFreqRtcClient.OPUS_SAMPLES_PER_FRAME;


    private readonly UdpClient _udpClient;
    public UdpClient UdpClient => _udpClient;
    private readonly IPEndPoint _serverEndpoint;
    private readonly bool _opusEnabled;

#pragma warning disable CS0618 // Do not use the factory - it does not work with Linux
    private readonly OpusEncoder _opusEncoder = new OpusEncoder(
            OpenFreqRtcClient.SAMPLE_RATE,
            1,
            OpusApplication.OPUS_APPLICATION_RESTRICTED_LOWDELAY
        );
#pragma warning restore CS0618 // Type or member is obsolete

    // RTP state
    private volatile List<FrequencyTransmission> _frequencies = [];
    private readonly uint _ssrc;
    private readonly string _clientId;
    private const byte PAYLOAD_TYPE_OPUS = 96;
    private const byte PAYLOAD_TYPE_PCMU = 0;

    // Sending Queue - now holds raw PCM data, encoding happens in the send thread
    private readonly SyncRope<short> _sendQueue = new();

    private readonly Thread? _sendThread;

    // Statistics
    private int _packetsSent = 0;
    private readonly long _startTimeTicks = Stopwatch.GetTimestamp();

    /// <summary>
    /// Create RTP audio sender
    /// </summary>
    public RtpAudioSender(ILogger<RtpAudioSender> logger, string serverHost, int serverPort, string clid, bool opusEnabled = true)
    {
        _logger = logger;
        _opusEnabled = opusEnabled;
        _serverEndpoint = new IPEndPoint(IPAddress.Parse(serverHost), serverPort);
        _udpClient = new UdpClient();

        // Generate unique SSRC (synchronization source identifier)
        _ssrc = (uint)Random.Shared.Next();
        _clientId = clid;

        var metadata = new AudioPacketMetadata
        {
            ClientId = _clientId,
            Frequencies = []  // Empty frequency list
        };

        var metadataJson = JsonSerializer.Serialize(metadata, OpenFreqJsonContext.Default.AudioPacketMetadata);
        var metadataBytes = Encoding.UTF8.GetBytes(metadataJson);

        // Send single packet with silence to register our endpoint
        var keepalive = new RtpPacket
        {
            Version = 2,
            PayloadType = _opusEnabled ? PAYLOAD_TYPE_OPUS : PAYLOAD_TYPE_PCMU,
            SequenceNumber = 0,
            Timestamp = 0,
            Ssrc = _ssrc,
            ExtensionProfile = RtpPacket.OpenFreqProfile,
            ExtensionData = metadataBytes,
            Payload = []  // Empty audio payload
        };
        _udpClient.Send(keepalive.ToBytes(), _serverEndpoint);

        _opusEncoder.Bitrate = 24000;
        _opusEncoder.Complexity = 8;
        _opusEncoder.SignalType = OpusSignal.OPUS_SIGNAL_VOICE;
        _opusEncoder.UseInbandFEC = true;
        _opusEncoder.PacketLossPercent = 15;

        _sendThread = new Thread(SendThreadProc);
        _sendThread.Start();

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
    public void SendAudio(Memory<short> audioData, List<FrequencyTransmission> frequencyTransmissions)
    {
        _frequencies = frequencyTransmissions;
        _sendQueue.Fill(audioData);
    }

    private void SendThreadProc()
    {
        uint timestamp = 0;
        ushort sequence = 0;
        var drainbuf = new short[OPUS_FRAME_SIZE];
        var encoded = new byte[OPUS_FRAME_SIZE * 2];
        var encspan = new Memory<byte>();

        while (true)
        {
            var dspan = new Memory<short>(drainbuf);
            if (!_sendQueue.DrainExactly(dspan.Span)) return;
            ushort nextSequence = (ushort)(sequence + dspan.Length);

            // Encode audio (happens here, not in audio callback)
            encspan = new Memory<byte>(encoded);
            if (_opusEnabled)
            {
                var opusBytes = _opusEncoder.Encode(dspan.Span, dspan.Length, encspan.Span, encspan.Length);
                if (opusBytes <= 0)
                {
                    throw new Exception("Opus encode failed");
                }
                encspan = encspan[..opusBytes];
            }
            else
            {
                MemoryMarshal.AsBytes(dspan.Span).CopyTo(encspan.Span);
            }

            // Build metadata
            var metadata = new AudioPacketMetadata
            {
                ClientId = _clientId,
                Frequencies = _frequencies,
            };

            var metadataJson = JsonSerializer.Serialize(metadata, OpenFreqJsonContext.Default.AudioPacketMetadata);
            var metadataBytes = Encoding.UTF8.GetBytes(metadataJson);

            // Build RTP packet — metadata in header extension, payload is pure audio
            var rtpPacket = new RtpPacket
            {
                Version = 2,
                PayloadType = _opusEnabled ? PAYLOAD_TYPE_OPUS : PAYLOAD_TYPE_PCMU,
                SequenceNumber = sequence,
                Timestamp = timestamp,
                Ssrc = _ssrc,
                ExtensionProfile = RtpPacket.OpenFreqProfile,
                ExtensionData = metadataBytes,
                Payload = encspan.ToArray()
            };

            // Send the packet
            var rtpBytes = rtpPacket.ToBytes();
            _udpClient.Send(rtpBytes, rtpBytes.Length, _serverEndpoint);
            sequence = nextSequence;
        }
    }

    /// <summary>
    /// Get sender statistics
    /// </summary>
    public (int packetsSent, TimeSpan uptime, double packetsPerSecond) GetStatistics()
    {
        var uptime = Stopwatch.GetElapsedTime(_startTimeTicks);
        var pps = uptime.TotalSeconds > 0 ? _packetsSent / uptime.TotalSeconds : 0;
        return (_packetsSent, uptime, pps);
    }

    public void Dispose()
    {
        _sendQueue.Close(); // Sentinel kills the thread
        _sendThread?.Join();
        _udpClient.Close();
        _udpClient.Dispose();
        _opusEncoder?.Dispose();

        var stats = GetStatistics();
        _logger.LogInformation(
            "Disposed. Sent {PacketsSent} packets over {UptimeSeconds:F1}s ({PacketsPerSecond:F1} pps)",
            stats.packetsSent, stats.uptime.TotalSeconds, stats.packetsPerSecond);
    }
}