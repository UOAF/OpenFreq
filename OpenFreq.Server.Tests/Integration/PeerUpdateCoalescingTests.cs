using OpenFreq.Common;
using OpenFreq.Common.Signaling;

namespace OpenFreq.Server.Tests.Integration;

/// <summary>
/// Verifies the server coalesces full-peer-state (<c>all-peers-status</c>) broadcasts instead of
/// emitting one per join/leave.
/// </summary>
public class PeerUpdateCoalescingTests
{
    private const int Freq = 251_000;

    private static async Task<RawSignalingClient> ConnectAuthedAsync(SignalingServerHarness server, string name)
    {
        var client = new RawSignalingClient();
        await client.ConnectAsync(server.WsUri);
        await client.SendAsync(SignalingMessageFactory.CreateAuthenticate("", name));
        await client.ReceiveUntilAsync<SuccessMessage>(SignalingMessageTypes.Success);
        return client;
    }

    private static async Task<RawSignalingClient> ConnectJoinedAsync(SignalingServerHarness server, string name)
    {
        var client = await ConnectAuthedAsync(server, name);
        await client.SendAsync(SignalingMessageFactory.CreateJoin(Freq));
        await client.ReceiveUntilAsync<ChannelStateMessage>(SignalingMessageTypes.ChannelState);
        return client;
    }

    [Fact]
    public async Task SimultaneousJoinBurst_CoalescesIntoFewBroadcasts()
    {
        const int senderCount = 10;

        await using var server = await SignalingServerHarness.StartAsync();

        // A listener already on the channel observes the broadcasts caused by everyone else joining.
        await using var listener = await ConnectJoinedAsync(server, "Listener");

        // Pre-connect+auth all senders WITHOUT joining yet, so the join burst lands together.
        var senders = new List<RawSignalingClient>();
        for (var i = 0; i < senderCount; i++)
            senders.Add(await ConnectAuthedAsync(server, $"Sender{i}"));

        try
        {
            // Fire all joins at once — the realistic "flight jumps to 3D together" case.
            await Task.WhenAll(senders.Select(s =>
                s.SendAsync(SignalingMessageFactory.CreateJoin(Freq))));

            // Count every all-peers-status until the terminal one (all listener + senders present).
            // Traffic flows the whole time (peer-joined + broadcasts), so reads never time out —
            // which matters because a timed-out WebSocket receive aborts the socket.
            var broadcastCount = 0;
            var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(5);
            while (true)
            {
                var remaining = deadline - DateTime.UtcNow;
                Assert.True(remaining > TimeSpan.Zero, "terminal broadcast never arrived");

                var message = await listener.ReceiveAsync(remaining);
                if (message.Type != SignalingMessageTypes.AllPeersStatus) continue;

                broadcastCount++;
                var payload = SignalingMessageFactory.DeserializePayload<AllPeersStatusMessage>(message.Payload);
                if (payload != null
                    && payload.FrequenciesPeers.TryGetValue(Freq, out var peers)
                    && peers.Count == senderCount + 1)
                {
                    break;
                }
            }

            // Coalesced: far fewer broadcasts than joins (pre-fix this was one per join).
            Assert.True(broadcastCount < senderCount,
                $"expected coalescing (< {senderCount} broadcasts), got {broadcastCount}");
        }
        finally
        {
            foreach (var s in senders)
                await s.DisposeAsync();
        }
    }

    [Fact]
    public async Task SingleJoin_StillDeliversCoalescedBroadcast()
    {
        await using var server = await SignalingServerHarness.StartAsync();
        await using var listener = await ConnectJoinedAsync(server, "Listener");

        await using var joiner = await ConnectJoinedAsync(server, "Joiner");

        // Debounced, but must still arrive and reflect both peers. The predicate skips the
        // listener's own-join broadcast (1 peer) and waits for the 2-peer one.
        var status = await listener.ReceiveUntilAsync<AllPeersStatusMessage>(
            SignalingMessageTypes.AllPeersStatus,
            s => s.FrequenciesPeers.TryGetValue(Freq, out var peers) && peers.Count == 2,
            TimeSpan.FromSeconds(2));

        Assert.Equal(2, status.FrequenciesPeers[Freq].Count);
    }

    [Fact]
    public async Task BroadcastPeerUpdatesDisabled_SuppressesAllPeersStatus()
    {
        await using var server = await SignalingServerHarness.StartAsync(broadcastPeerUpdates: false);
        await using var listener = await ConnectJoinedAsync(server, "Listener");

        // A peer joining still sends the incremental peer-joined, but no full-state broadcast.
        await using var joiner = await ConnectJoinedAsync(server, "Joiner");

        await listener.ReceiveUntilAsync<PeerJoinedMessage>(SignalingMessageTypes.PeerJoined);

        var broadcasts = await listener.CollectAsync<AllPeersStatusMessage>(
            SignalingMessageTypes.AllPeersStatus, TimeSpan.FromMilliseconds(700));
        Assert.Empty(broadcasts);
    }
}
