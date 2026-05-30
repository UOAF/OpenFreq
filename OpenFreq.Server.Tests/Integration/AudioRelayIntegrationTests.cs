using System.Text;
using System.Text.Json;
using OpenFreq.Common;
using OpenFreq.Common.Rtp;

namespace OpenFreq.Server.Tests.Integration;

/// <summary>
/// End-to-end tests for the real <see cref="OpenFreqServer.AudioStreamServer"/> exercised over
/// real loopback UDP. Packets are sent in production wire format; only the signaling layer is
/// bypassed (sessions wired directly via <see cref="AudioServerHarness"/>).
/// </summary>
public class AudioRelayIntegrationTests
{
    private const int Freq = 251_000;  // 251.000 MHz
    private const int Freq2 = 252_000; // 252.000 MHz

    private static byte[] Audio(params byte[] data) => data;

    /// <summary>A receiver only gets relayed audio after the server has learned its endpoint.</summary>
    private static async Task RegisterEndpoint(TestAudioClient client)
    {
        client.SendRegister();
        // Pong confirms the server processed the packet and recorded the endpoint.
        var pong = await client.ReceiveRtp();
        Assert.NotNull(pong);
    }

    [Fact]
    public async Task Relay_SameFrequency_DeliversPacketToPeer()
    {
        using var harness = AudioServerHarness.Start();
        using var alice = harness.AddClient("alice", Freq);
        using var bob = harness.AddClient("bob", Freq);
        await RegisterEndpoint(bob);

        var payload = Audio(1, 2, 3, 4, 5);
        alice.SendAudio(payload, sequence: 7, timestamp: 4242, Freq);

        var received = await bob.ReceiveRtp();
        Assert.NotNull(received);
        Assert.Equal(payload, received!.Payload);
        Assert.Equal(alice.Ssrc, received.Ssrc);          // SSRC preserved
        Assert.Equal(4242u, received.Timestamp);          // timestamp preserved
        Assert.Equal(TestAudioClient.PayloadTypeOpus, received.PayloadType);
        Assert.Equal(RtpPacket.OpenFreqProfile, received.ExtensionProfile);
    }

    [Fact]
    public async Task Relay_StampsServerSendTimestampInMetadata()
    {
        using var harness = AudioServerHarness.Start();
        using var alice = harness.AddClient("alice", Freq);
        using var bob = harness.AddClient("bob", Freq);
        await RegisterEndpoint(bob);

        alice.SendAudio(Audio(9), sequence: 0, timestamp: 0, Freq);

        var received = await bob.ReceiveRtp();
        Assert.NotNull(received);
        var metadata = ParseMetadata(received!);
        Assert.Equal("alice", metadata.ClientId);
        Assert.True(metadata.ServerSendTimestamp > 0, "server should stamp its send time");
    }

    [Fact]
    public async Task Relay_RenumbersSequencePerReceiverFromZero()
    {
        using var harness = AudioServerHarness.Start();
        using var alice = harness.AddClient("alice", Freq);
        using var bob = harness.AddClient("bob", Freq);
        await RegisterEndpoint(bob);

        // Alice's own sequence numbers start at 100; the server must renumber per-receiver from 0.
        alice.SendAudio(Audio(1), sequence: 100, timestamp: 0, Freq);
        alice.SendAudio(Audio(2), sequence: 101, timestamp: 960, Freq);
        alice.SendAudio(Audio(3), sequence: 102, timestamp: 1920, Freq);

        var first = await bob.ReceiveRtp();
        var second = await bob.ReceiveRtp();
        var third = await bob.ReceiveRtp();

        Assert.Equal(0, first!.SequenceNumber);
        Assert.Equal(1, second!.SequenceNumber);
        Assert.Equal(2, third!.SequenceNumber);
    }

    [Fact]
    public async Task NoRelay_AcrossDifferentFrequencies()
    {
        using var harness = AudioServerHarness.Start();
        using var alice = harness.AddClient("alice", Freq);
        using var bob = harness.AddClient("bob", Freq2);
        await RegisterEndpoint(bob);

        alice.SendAudio(Audio(1, 2, 3), sequence: 0, timestamp: 0, Freq);

        await bob.AssertNoPacket();
    }

    [Fact]
    public async Task Sender_DoesNotReceiveOwnTransmission()
    {
        using var harness = AudioServerHarness.Start();
        using var alice = harness.AddClient("alice", Freq);
        using var bob = harness.AddClient("bob", Freq);
        await RegisterEndpoint(alice);
        await RegisterEndpoint(bob);

        alice.SendAudio(Audio(1, 2, 3), sequence: 0, timestamp: 0, Freq);

        // Bob hears it; Alice does not get her own audio echoed back.
        Assert.NotNull(await bob.ReceiveRtp());
        await alice.AssertNoPacket();
    }

    [Fact]
    public async Task UnjoinedFrequency_IsNotRelayed()
    {
        using var harness = AudioServerHarness.Start();
        // Alice is authenticated but has joined no frequencies.
        using var alice = harness.AddClient("alice");
        using var bob = harness.AddClient("bob", Freq);
        await RegisterEndpoint(bob);

        alice.SendAudio(Audio(1, 2, 3), sequence: 0, timestamp: 0, Freq);

        await bob.AssertNoPacket();
    }

    [Fact]
    public async Task UnauthenticatedSender_IsIgnored()
    {
        using var harness = AudioServerHarness.Start();
        using var alice = harness.AddClient("alice", authenticated: false, Freq);
        using var bob = harness.AddClient("bob", Freq);
        await RegisterEndpoint(bob);

        alice.SendAudio(Audio(1, 2, 3), sequence: 0, timestamp: 0, Freq);

        await bob.AssertNoPacket();
    }

    [Fact]
    public async Task MultipleFrequencies_DeliverExactlyOnePacketToSharedPeer()
    {
        using var harness = AudioServerHarness.Start();
        using var alice = harness.AddClient("alice", Freq, Freq2);
        using var bob = harness.AddClient("bob", Freq, Freq2);
        await RegisterEndpoint(bob);

        // Alice transmits on both frequencies Bob is listening to.
        alice.SendAudio(Audio(1, 2, 3), sequence: 0, timestamp: 0, Freq, Freq2);

        // Bob is deduplicated: exactly one packet, not one per matching frequency.
        Assert.NotNull(await bob.ReceiveRtp());
        await bob.AssertNoPacket();
    }

    [Fact]
    public async Task Keepalive_WithEmptyFrequencies_EchoesPong()
    {
        using var harness = AudioServerHarness.Start();
        using var alice = harness.AddClient("alice", Freq);

        alice.SendRegister();

        var pong = await alice.ReceiveRtp();
        Assert.NotNull(pong);
        Assert.Empty(pong!.Payload);
        Assert.Null(pong.ExtensionData); // pong carries no metadata extension
    }

    [Fact]
    public async Task MalformedPacket_WithoutExtension_IsIgnored()
    {
        using var harness = AudioServerHarness.Start();
        using var alice = harness.AddClient("alice", Freq);
        using var bob = harness.AddClient("bob", Freq);
        await RegisterEndpoint(bob);

        // RTP packet with no OpenFreq metadata extension — server treats it as malformed.
        alice.Send(new RtpPacket
        {
            Version = 2,
            PayloadType = TestAudioClient.PayloadTypeOpus,
            SequenceNumber = 0,
            Timestamp = 0,
            Ssrc = alice.Ssrc,
            Payload = [1, 2, 3],
        });

        await bob.AssertNoPacket();

        // Server still alive: a well-formed transmission afterwards is relayed normally.
        alice.SendAudio(Audio(4, 5, 6), sequence: 1, timestamp: 0, Freq);
        Assert.NotNull(await bob.ReceiveRtp());
    }

    [Fact]
    public async Task SpoofedClientId_FromMappedEndpoint_IsRejected()
    {
        using var harness = AudioServerHarness.Start();
        using var alice = harness.AddClient("alice", Freq);
        using var bob = harness.AddClient("bob", Freq);
        await RegisterEndpoint(bob);

        // First a legitimate packet so the server maps Alice's endpoint to "alice".
        alice.SendAudio(Audio(1), sequence: 0, timestamp: 0, Freq);
        Assert.NotNull(await bob.ReceiveRtp());

        // Same endpoint now claims to be "bob" — server must reject the identity mismatch.
        alice.SendAudioAs("bob", Audio(2), sequence: 1, timestamp: 0, Freq);

        await bob.AssertNoPacket();
    }

    [Fact]
    public async Task UnparseableRtpHeader_IsIgnored()
    {
        using var harness = AudioServerHarness.Start();
        using var alice = harness.AddClient("alice", Freq);
        using var bob = harness.AddClient("bob", Freq);
        await RegisterEndpoint(bob);

        // 12 bytes (>= header size) but RTP version 0 — Parse rejects it.
        alice.SendRaw(new byte[12]);
        await bob.AssertNoPacket();

        // Server survives; a normal transmission still relays.
        alice.SendAudio(Audio(1), sequence: 0, timestamp: 0, Freq);
        Assert.NotNull(await bob.ReceiveRtp());
    }

    [Fact]
    public async Task UndersizedPacket_IsIgnored()
    {
        using var harness = AudioServerHarness.Start();
        using var alice = harness.AddClient("alice", Freq);
        using var bob = harness.AddClient("bob", Freq);
        await RegisterEndpoint(bob);

        // Fewer than the 12-byte RTP header — rejected before parsing.
        alice.SendRaw(new byte[4]);
        await bob.AssertNoPacket();

        alice.SendAudio(Audio(1), sequence: 0, timestamp: 0, Freq);
        Assert.NotNull(await bob.ReceiveRtp());
    }

    [Fact]
    public async Task PartiallyUnjoinedFrequencies_RelaysOnlyJoinedOnes()
    {
        using var harness = AudioServerHarness.Start();
        using var alice = harness.AddClient("alice", Freq); // joined Freq only
        using var bob = harness.AddClient("bob", Freq);
        await RegisterEndpoint(bob);

        // Alice transmits on Freq (joined) + Freq2 (not joined) — server logs the invalid one
        // and relays on the valid one.
        alice.SendAudio(Audio(1, 2, 3), sequence: 0, timestamp: 0, Freq, Freq2);

        Assert.NotNull(await bob.ReceiveRtp());
    }

    [Fact]
    public async Task InvalidMetadataJson_IsIgnored()
    {
        using var harness = AudioServerHarness.Start();
        using var alice = harness.AddClient("alice", Freq);
        using var bob = harness.AddClient("bob", Freq);
        await RegisterEndpoint(bob);

        alice.SendRawExtension("{ not valid json", Audio(1, 2, 3));
        await bob.AssertNoPacket();

        alice.SendAudio(Audio(4), sequence: 0, timestamp: 0, Freq);
        Assert.NotNull(await bob.ReceiveRtp());
    }

    [Fact]
    public async Task NullMetadata_IsIgnored()
    {
        using var harness = AudioServerHarness.Start();
        using var alice = harness.AddClient("alice", Freq);
        using var bob = harness.AddClient("bob", Freq);
        await RegisterEndpoint(bob);

        // Valid JSON that deserializes to null.
        alice.SendRawExtension("null", Audio(1, 2, 3));
        await bob.AssertNoPacket();
    }

    [Fact]
    public async Task ReceiverWithoutKnownEndpoint_IsSkipped()
    {
        using var harness = AudioServerHarness.Start();
        using var alice = harness.AddClient("alice", Freq);
        // Bob has a session and channel membership but has never sent a packet, so the server
        // has no endpoint to relay to.
        using var bob = harness.AddClient("bob", Freq);

        alice.SendAudio(Audio(1, 2, 3), sequence: 0, timestamp: 0, Freq);

        await bob.AssertNoPacket();
    }

    [Fact]
    public async Task RemoveSession_StopsRelayToClient()
    {
        using var harness = AudioServerHarness.Start();
        using var alice = harness.AddClient("alice", Freq);
        using var bob = harness.AddClient("bob", Freq);
        await RegisterEndpoint(bob);

        // Bob receives once, so the server holds per-receiver RTP state for him.
        alice.SendAudio(Audio(1), sequence: 0, timestamp: 0, Freq);
        Assert.NotNull(await bob.ReceiveRtp());

        // Tear down Bob's audio session (clears that state) — further audio must not reach him.
        harness.Server.RemoveSession("bob");

        alice.SendAudio(Audio(2, 3), sequence: 1, timestamp: 0, Freq);

        await bob.AssertNoPacket();
    }

    private static AudioPacketMetadata ParseMetadata(RtpPacket packet)
    {
        var json = Encoding.UTF8.GetString(packet.ExtensionData!).TrimEnd('\0');
        var metadata = JsonSerializer.Deserialize(json, OpenFreqJsonContext.Default.AudioPacketMetadata);
        Assert.NotNull(metadata);
        return metadata!;
    }
}
