using OpenFreq.Common;

namespace OpenFreq.Server.Tests.Integration;

/// <summary>
/// Membership is tracked twice on the server — <c>ClientSession.CurrentFrequencies</c> gates
/// transmission, <c>FrequencyChannelManager</c> decides who receives — and a join/leave race
/// used to be able to drive them apart. The divergence was silent and permanent: the client's
/// UI still showed it tuned, but the routing table no longer listed it, so it was deaf and
/// mute on that frequency, and every rejoin short-circuited on the session list without ever
/// repairing the routing table.
///
/// The race itself is fixed in <c>FrequencyChannelManager</c>. These tests cover the backstop:
/// whatever the cause, a join that observes the two halves disagreeing must log it and repair
/// rather than trust the stale state.
/// </summary>
public class ChannelDesyncRepairTests
{
    private const int Freq = 251_000; // 251.000 MHz

    /// <summary>
    /// Reproduces the post-race state: routing has dropped the client while its session list
    /// still believes it is tuned.
    /// </summary>
    private static void DropFromRouting(SignalingServerHarness server, string peerId)
    {
        server.Server.ChannelManager.LeaveChannel(Freq, peerId);

        Assert.False(server.Server.ChannelManager.IsInChannel(Freq, peerId));
        Assert.True(server.Server.Clients[peerId].CurrentFrequencies.ContainsKey(Freq));
    }

    [Fact]
    public async Task Rejoin_AfterRoutingLostClient_RestoresRouting()
    {
        await using var server = await SignalingServerHarness.StartAsync();
        await using var alice = RtcClientHarness.Create(server, "Alice");
        await alice.ConnectAsync();

        await alice.Client.JoinFrequencyAsync(Freq);
        await alice.FrequencyJoined.WaitForAsync(e => e.FrequencyKhz == Freq);

        DropFromRouting(server, alice.PeerId!);

        // The rejoin used to short-circuit on the session list and leave routing broken.
        await alice.Client.JoinFrequencyAsync(Freq);
        await alice.FrequencyJoined.WaitForAsync(e => e.FrequencyKhz == Freq);

        Assert.True(server.Server.ChannelManager.IsInChannel(Freq, alice.PeerId!));
        Assert.True(server.Server.Clients[alice.PeerId!].CurrentFrequencies.ContainsKey(Freq));
    }

    [Fact]
    public async Task Rejoin_AfterSessionListLostFrequency_RestoresBothHalves()
    {
        await using var server = await SignalingServerHarness.StartAsync();
        await using var alice = RtcClientHarness.Create(server, "Alice");
        await alice.ConnectAsync();

        await alice.Client.JoinFrequencyAsync(Freq);
        await alice.FrequencyJoined.WaitForAsync(e => e.FrequencyKhz == Freq);

        // The other divergence direction: routing keeps the client, the session list loses it.
        server.Server.Clients[alice.PeerId!].CurrentFrequencies.TryRemove(Freq, out _);
        Assert.True(server.Server.ChannelManager.IsInChannel(Freq, alice.PeerId!));

        await alice.Client.JoinFrequencyAsync(Freq);
        await alice.FrequencyJoined.WaitForAsync(e => e.FrequencyKhz == Freq);

        Assert.True(server.Server.ChannelManager.IsInChannel(Freq, alice.PeerId!));
        Assert.True(server.Server.Clients[alice.PeerId!].CurrentFrequencies.ContainsKey(Freq));
    }

    [Fact]
    public async Task Rejoin_RepairIsNotRefusedByFullChannel()
    {
        // Capacity 1: once Bob takes the freed slot, a naive capacity check would refuse
        // Alice's repair and strand her off comms permanently.
        await using var server = await SignalingServerHarness.StartAsync(maxClientsPerChannel: 1);
        await using var alice = RtcClientHarness.Create(server, "Alice");
        await using var bob = RtcClientHarness.Create(server, "Bob");
        await alice.ConnectAsync();
        await bob.ConnectAsync();

        await alice.Client.JoinFrequencyAsync(Freq);
        await alice.FrequencyJoined.WaitForAsync(e => e.FrequencyKhz == Freq);

        DropFromRouting(server, alice.PeerId!);

        await bob.Client.JoinFrequencyAsync(Freq);
        await bob.FrequencyJoined.WaitForAsync(e => e.FrequencyKhz == Freq);
        Assert.Equal(1, server.Server.ChannelManager.GetChannelCount(Freq));

        await alice.Client.JoinFrequencyAsync(Freq);
        await alice.FrequencyJoined.WaitForAsync(e => e.FrequencyKhz == Freq);

        Assert.True(server.Server.ChannelManager.IsInChannel(Freq, alice.PeerId!));
    }

    [Fact]
    public async Task Rejoin_AfterRepair_PeerIsAnnouncedToChannelAgain()
    {
        await using var server = await SignalingServerHarness.StartAsync();
        await using var alice = RtcClientHarness.Create(server, "Alice");
        await using var bob = RtcClientHarness.Create(server, "Bob");
        await alice.ConnectAsync();
        await bob.ConnectAsync();

        await bob.Client.JoinFrequencyAsync(Freq);
        await bob.FrequencyJoined.WaitForAsync(e => e.FrequencyKhz == Freq);

        await alice.Client.JoinFrequencyAsync(Freq);
        await alice.FrequencyJoined.WaitForAsync(e => e.FrequencyKhz == Freq);
        await bob.PeerJoined.WaitForAsync(e => e.PeerId == alice.PeerId);

        DropFromRouting(server, alice.PeerId!);

        // Repairing re-announces Alice, so Bob's peer list recovers too — not just routing.
        await alice.Client.JoinFrequencyAsync(Freq);

        var announced = await bob.PeerJoined.WaitForAsync(e => e.PeerId == alice.PeerId);
        Assert.Equal(Freq, announced.FrequencyKhz);
        Assert.Contains(alice.PeerId, server.Server.ChannelManager.GetClientsInChannel(Freq));
    }

    [Fact]
    public async Task Rejoin_WhenBothHalvesAgree_IsStillIdempotent()
    {
        await using var server = await SignalingServerHarness.StartAsync();
        await using var alice = RtcClientHarness.Create(server, "Alice");
        await alice.ConnectAsync();

        await alice.Client.JoinFrequencyAsync(Freq);
        await alice.FrequencyJoined.WaitForAsync(e => e.FrequencyKhz == Freq);

        await alice.Client.JoinFrequencyAsync(Freq);
        await alice.FrequencyJoined.WaitForAsync(e => e.FrequencyKhz == Freq);

        Assert.Equal(1, server.Server.ChannelManager.GetChannelCount(Freq));
        Assert.Single(server.Server.Clients[alice.PeerId!].CurrentFrequencies);
    }
}
