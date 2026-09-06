using OpenFreq.Common;

namespace OpenFreq.Server.Tests.Integration;

/// <summary>
/// Rejoin behaviour. These started life as regression tests for a membership desync: the
/// server used to track channel membership twice — once on the session, once in the routing
/// table — and a join/leave race could drive the two apart, leaving a client deaf and mute on
/// a frequency its UI still showed as tuned. That state is now unrepresentable, since
/// <c>FrequencyChannelManager</c> is the only place membership lives, so what remains is
/// coverage of the rejoin path itself.
/// </summary>
public class RejoinTests
{
    private const int Freq = 251_000; // 251.000 MHz

    [Fact]
    public async Task Rejoin_WhenAlreadyJoined_IsIdempotent()
    {
        await using var server = await SignalingServerHarness.StartAsync();
        await using var alice = RtcClientHarness.Create(server, "Alice");
        await alice.ConnectAsync();

        await alice.Client.JoinFrequencyAsync(Freq);
        await alice.FrequencyJoined.WaitForAsync(e => e.FrequencyKhz == Freq);

        // A reconnect race can send a duplicate join on the same session.
        await alice.Client.JoinFrequencyAsync(Freq);
        await alice.FrequencyJoined.WaitForAsync(e => e.FrequencyKhz == Freq);

        Assert.Single(server.Server.ChannelManager.GetClientsInChannel(Freq));
        Assert.Equal([Freq], server.Server.ChannelManager.GetClientChannels(alice.PeerId!));
    }

    [Fact]
    public async Task Rejoin_OnFullChannel_IsNotRefused()
    {
        // The capacity check must gate new joins only: a member rejoining its own channel
        // is already accounted for in the count, so refusing it would strand them.
        await using var server = await SignalingServerHarness.StartAsync(maxClientsPerChannel: 1);
        await using var alice = RtcClientHarness.Create(server, "Alice");
        await alice.ConnectAsync();

        await alice.Client.JoinFrequencyAsync(Freq);
        await alice.FrequencyJoined.WaitForAsync(e => e.FrequencyKhz == Freq);
        Assert.Single(server.Server.ChannelManager.GetClientsInChannel(Freq));

        await alice.Client.JoinFrequencyAsync(Freq);
        await alice.FrequencyJoined.WaitForAsync(e => e.FrequencyKhz == Freq);

        await alice.Errors.AssertNoneAsync();
        Assert.Contains(alice.PeerId, server.Server.ChannelManager.GetClientsInChannel(Freq));
    }

    [Fact]
    public async Task LeaveThenRejoin_RestoresMembershipAndAnnouncesPeer()
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

        await alice.Client.LeaveFrequencyAsync(Freq);
        await bob.PeerLeft.WaitForAsync(e => e.PeerId == alice.PeerId);
        Assert.DoesNotContain(alice.PeerId, server.Server.ChannelManager.GetClientsInChannel(Freq));

        await alice.Client.JoinFrequencyAsync(Freq);

        var announced = await bob.PeerJoined.WaitForAsync(e => e.PeerId == alice.PeerId);
        Assert.Equal(Freq, announced.FrequencyKhz);
        Assert.Contains(alice.PeerId, server.Server.ChannelManager.GetClientsInChannel(Freq));
    }

    /// <summary>
    /// The channel snapshot broadcast as allPeersStatus reports transmit state. It used to
    /// say "receiving" for every peer forever, because the server tracked transmit state on
    /// the session and only ever wrote Receiving to the peer entry. Clients rebuild their
    /// peer lists from that snapshot, so any peer-list broadcast landing mid-transmission
    /// cleared the sender's TX indicator until the next transmission event arrived.
    /// </summary>
    [Fact]
    public async Task AllPeersStatus_ReportsWhoIsTransmitting()
    {
        await using var server = await SignalingServerHarness.StartAsync();
        await using var alice = RtcClientHarness.Create(server, "Alice");
        await using var bob = RtcClientHarness.Create(server, "Bob");
        await alice.ConnectAsync();
        await bob.ConnectAsync();

        await alice.Client.JoinFrequencyAsync(Freq);
        await alice.FrequencyJoined.WaitForAsync(e => e.FrequencyKhz == Freq);
        await bob.Client.JoinFrequencyAsync(Freq);
        await bob.FrequencyJoined.WaitForAsync(e => e.FrequencyKhz == Freq);

        await alice.Client.StartTransmissionAsync(Freq, is3d: false);
        await bob.PeerTransmission.WaitForAsync(e => e.PeerId == alice.PeerId && e.IsTransmitting);

        Assert.Equal(PeerData.PeerStatus.Transmitting, await NextStatusOfAliceAsync());

        await alice.Client.StopTransmissionAsync(Freq, is3d: false);
        await bob.PeerTransmission.WaitForAsync(e => e.PeerId == alice.PeerId && !e.IsTransmitting);

        Assert.Equal(PeerData.PeerStatus.Receiving, await NextStatusOfAliceAsync());
        return;

        // Snapshots are only broadcast on membership-ish changes, so provoke one rather than
        // waiting for an incidental broadcast to land — and drop anything already buffered so
        // the assertion is about the snapshot taken *after* the transmit state changed.
        async Task<PeerData.PeerStatus> NextStatusOfAliceAsync()
        {
            bob.AllPeersStatus.Drain();
            await bob.Client.SendModeUpdateAsync(is3d: false);

            var snapshot = await bob.AllPeersStatus.WaitForAsync(e => e.AllPeers.ContainsKey(Freq));
            return snapshot.AllPeers[Freq].Single(p => p.Id == alice.PeerId).Status;
        }
    }
}
