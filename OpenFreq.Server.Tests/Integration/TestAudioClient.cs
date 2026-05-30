using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using OpenFreq.Common;
using OpenFreq.Common.Rtp;

namespace OpenFreq.Server.Tests.Integration;

/// <summary>
/// Raw UDP audio client for relay integration tests. Builds production-format RTP packets
/// (OpenFreq metadata in the header extension) identically to the real <c>RtpAudioSender</c>,
/// and parses anything the server sends back. Connected to the server endpoint so receives are
/// filtered to it.
/// </summary>
public sealed class TestAudioClient : IDisposable
{
    public const byte PayloadTypeOpus = 96;

    public string ClientId { get; }
    public uint Ssrc { get; }

    private readonly UdpClient _udp;

    public TestAudioClient(int serverPort, string clientId, uint? ssrc = null)
    {
        ClientId = clientId;
        Ssrc = ssrc ?? (uint)Random.Shared.Next();
        _udp = new UdpClient();
        _udp.Connect(new IPEndPoint(IPAddress.Loopback, serverPort));
    }

    /// <summary>Send an audio packet transmitting on the given frequencies.</summary>
    public void SendAudio(byte[] payload, ushort sequence, uint timestamp, params int[] frequencies) =>
        SendAudioAs(ClientId, payload, sequence, timestamp, frequencies);

    /// <summary>
    /// Send an audio packet whose metadata claims an arbitrary client id — used to exercise the
    /// server's endpoint/identity mismatch handling.
    /// </summary>
    public void SendAudioAs(
        string metadataClientId, byte[] payload, ushort sequence, uint timestamp, params int[] frequencies)
    {
        var metadata = new AudioPacketMetadata
        {
            ClientId = metadataClientId,
            Frequencies = frequencies.Select(khz => new FrequencyTransmission { Khz = khz }).ToList(),
        };

        Send(new RtpPacket
        {
            Version = 2,
            PayloadType = PayloadTypeOpus,
            SequenceNumber = sequence,
            Timestamp = timestamp,
            Ssrc = Ssrc,
            ExtensionProfile = RtpPacket.OpenFreqProfile,
            ExtensionData = Serialize(metadata),
            Payload = payload,
        });
    }

    /// <summary>Send a valid RTP packet whose extension carries arbitrary (possibly invalid) JSON.</summary>
    public void SendRawExtension(string extensionJson, byte[] payload, ushort sequence = 0, uint timestamp = 0)
    {
        Send(new RtpPacket
        {
            Version = 2,
            PayloadType = PayloadTypeOpus,
            SequenceNumber = sequence,
            Timestamp = timestamp,
            Ssrc = Ssrc,
            ExtensionProfile = RtpPacket.OpenFreqProfile,
            ExtensionData = Encoding.UTF8.GetBytes(extensionJson),
            Payload = payload,
        });
    }

    /// <summary>
    /// Send a keepalive-style packet (valid metadata, empty frequency list). Registers this
    /// client's endpoint with the server and elicits a pong.
    /// </summary>
    public void SendRegister()
    {
        var metadata = new AudioPacketMetadata { ClientId = ClientId, Frequencies = [] };
        Send(new RtpPacket
        {
            Version = 2,
            PayloadType = PayloadTypeOpus,
            SequenceNumber = 0,
            Timestamp = 0,
            Ssrc = Ssrc,
            ExtensionProfile = RtpPacket.OpenFreqProfile,
            ExtensionData = Serialize(metadata),
            Payload = [],
        });
    }

    public void Send(RtpPacket packet)
    {
        var bytes = packet.ToBytes();
        _udp.Send(bytes, bytes.Length);
    }

    public void SendRaw(byte[] bytes) => _udp.Send(bytes, bytes.Length);

    /// <summary>Receive and parse the next packet, or return null on timeout.</summary>
    public async Task<RtpPacket?> ReceiveRtp(TimeSpan? timeout = null)
    {
        using var cts = new CancellationTokenSource(timeout ?? TimeSpan.FromSeconds(2));
        try
        {
            var result = await _udp.ReceiveAsync(cts.Token);
            return RtpPacket.Parse(result.Buffer);
        }
        catch (OperationCanceledException)
        {
            return null;
        }
    }

    /// <summary>Assert no packet arrives within the window.</summary>
    public async Task AssertNoPacket(TimeSpan? window = null)
    {
        var packet = await ReceiveRtp(window ?? TimeSpan.FromMilliseconds(500));
        if (packet != null)
            throw new Xunit.Sdk.XunitException($"Expected no packet, but received {packet}");
    }

    private static byte[] Serialize(AudioPacketMetadata metadata) =>
        Encoding.UTF8.GetBytes(
            JsonSerializer.Serialize(metadata, OpenFreqJsonContext.Default.AudioPacketMetadata));

    public void Dispose() => _udp.Dispose();
}
