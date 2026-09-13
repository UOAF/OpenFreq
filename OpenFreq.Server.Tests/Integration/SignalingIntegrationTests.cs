using System.Security.Authentication;
using OpenFreq.Common;

namespace OpenFreq.Server.Tests.Integration;

/// <summary>
/// End-to-end signaling tests driven by the real production client (<see cref="OpenFreqRtcClient"/>)
/// over real WebSockets against a real Kestrel-hosted <c>SignalingServer</c>. Only the UDP audio
/// relay is faked.
/// </summary>
public class SignalingIntegrationTests
{
    private const int Freq = 251_000; // 251.000 MHz

    [Fact]
    public async Task Authenticate_WithCorrectPassword_Succeeds()
    {
        await using var server = await SignalingServerHarness.StartAsync(password: "secret");
        await using var client = RtcClientHarness.Create(server, "Alice", password: "secret");

        await client.ConnectAsync();

        Assert.True(client.Client.IsAuthenticated);
        Assert.False(string.IsNullOrEmpty(client.PeerId));
        Assert.Equal(FakeAudioRelay.FakePort, client.Client.AudioPort);
        Assert.Contains(client.PeerId, server.Audio.CreatedSessions);
    }

    [Fact]
    public async Task Authenticate_WithEmptyServerPassword_AcceptsAnyPassword()
    {
        await using var server = await SignalingServerHarness.StartAsync(password: null);
        await using var client = RtcClientHarness.Create(server, "Alice", password: "whatever");

        await client.ConnectAsync();

        Assert.True(client.Client.IsAuthenticated);
    }

    [Fact]
    public async Task Authenticate_WithWrongPassword_Throws()
    {
        await using var server = await SignalingServerHarness.StartAsync(password: "secret");
        await using var client = RtcClientHarness.Create(server, "Mallory", password: "wrong");

        await Assert.ThrowsAsync<AuthenticationException>(() => client.ConnectAsync());
        Assert.False(client.Client.IsAuthenticated);
    }

    [Fact]
    public async Task Join_ReturnsChannelState()
    {
        await using var server = await SignalingServerHarness.StartAsync();
        await using var client = RtcClientHarness.Create(server, "Alice");
        await client.ConnectAsync();

        await client.Client.JoinFrequencyAsync(Freq);

        var joined = await client.FrequencyJoined.WaitForAsync(e => e.FrequencyKhz == Freq);
        Assert.Empty(joined.Peers);
    }

    [Fact]
    public async Task Join_FullChannel_RaisesError()
    {
        await using var server = await SignalingServerHarness.StartAsync(maxClientsPerChannel: 1);
        await using var alice = RtcClientHarness.Create(server, "Alice");
        await using var bob = RtcClientHarness.Create(server, "Bob");
        await alice.ConnectAsync();
        await bob.ConnectAsync();

        await alice.Client.JoinFrequencyAsync(Freq);
        await alice.FrequencyJoined.WaitForAsync(e => e.FrequencyKhz == Freq);

        await bob.Client.JoinFrequencyAsync(Freq);

        var error = await bob.Errors.WaitForAsync();
        Assert.Contains("full", error.ErrorMessage, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task TwoClients_SameFrequency_SeeEachOther()
    {
        await using var server = await SignalingServerHarness.StartAsync();
        await using var alice = RtcClientHarness.Create(server, "Alice");
        await using var bob = RtcClientHarness.Create(server, "Bob");
        await alice.ConnectAsync();
        await bob.ConnectAsync();

        await alice.Client.JoinFrequencyAsync(Freq);
        await alice.FrequencyJoined.WaitForAsync(e => e.FrequencyKhz == Freq);

        await bob.Client.JoinFrequencyAsync(Freq);

        // Bob's channel-state lists Alice (already present).
        var bobJoined = await bob.FrequencyJoined.WaitForAsync(e => e.FrequencyKhz == Freq);
        Assert.Contains(bobJoined.Peers, p => p.Id == alice.PeerId);

        // Alice is notified that Bob joined.
        var peerJoined = await alice.PeerJoined.WaitForAsync(e => e.FrequencyKhz == Freq);
        Assert.Equal(bob.PeerId, peerJoined.PeerId);
        Assert.Equal("Bob", peerJoined.PeerDisplayName);
    }

    [Fact]
    public async Task Transmission_IsRelayedToPeerOnSameFrequency()
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

        await alice.Client.StartTransmissionAsync(Freq, is3d: true);

        var tx = await bob.PeerTransmission.WaitForAsync(e => e.IsTransmitting);
        Assert.Equal(alice.PeerId, tx.PeerId);
        Assert.Equal("Alice", tx.PeerDisplayName);
        Assert.Equal(Freq, tx.FrequencyKhz);
        Assert.True(tx.Is3d);

        await alice.Client.StopTransmissionAsync(Freq, is3d: true);

        var stopped = await bob.PeerTransmission.WaitForAsync(e => !e.IsTransmitting);
        Assert.Equal(alice.PeerId, stopped.PeerId);
    }

    [Fact]
    public async Task Transmission_LogsOnePttStartAndEnd_WithNameAndGameTime()
    {
        var log = new CapturingLoggerFactory();
        await using var server = await SignalingServerHarness.StartAsync(loggerFactory: log);
        await using var alice = RtcClientHarness.Create(server, "Alice");
        await using var bob = RtcClientHarness.Create(server, "Bob");
        await alice.ConnectAsync();
        await bob.ConnectAsync();
        alice.Client.GameTimeSeconds = () => 45296;

        await alice.Client.JoinFrequencyAsync(Freq);
        await alice.FrequencyJoined.WaitForAsync(e => e.FrequencyKhz == Freq);
        await bob.Client.JoinFrequencyAsync(Freq);
        await bob.FrequencyJoined.WaitForAsync(e => e.FrequencyKhz == Freq);

        await alice.Client.StartTransmissionAsync(Freq, is3d: true);

        // The start, then two heartbeats that repeat it.
        for (var i = 0; i < 3; i++)
            await bob.PeerTransmission.WaitForAsync(e => e.IsTransmitting);

        await alice.Client.StopTransmissionAsync(Freq, is3d: true);
        await bob.PeerTransmission.WaitForAsync(e => !e.IsTransmitting);

        var start = Assert.Single(log.Messages, m => m.StartsWith("PTT start"));
        Assert.Equal($"PTT start: Alice ({alice.PeerId}) on 251.000 MHz, 3D, game time 12:34:56", start);
        var end = Assert.Single(log.Messages, m => m.StartsWith("PTT end"));
        Assert.Equal($"PTT end: Alice ({alice.PeerId}) on 251.000 MHz, released, game time 12:34:56", end);
    }

    [Fact]
    public async Task Transmission_EndedByDisconnect_LogsPttEnd()
    {
        var log = new CapturingLoggerFactory();
        await using var server = await SignalingServerHarness.StartAsync(loggerFactory: log);
        await using var alice = RtcClientHarness.Create(server, "Alice");
        await alice.ConnectAsync();
        var alicePeerId = alice.PeerId;
        alice.Client.GameTimeSeconds = () => 45296;

        await alice.Client.JoinFrequencyAsync(Freq);
        await alice.FrequencyJoined.WaitForAsync(e => e.FrequencyKhz == Freq);
        await alice.Client.StartTransmissionAsync(Freq, is3d: false);
        await log.WaitForAsync(m => m.StartsWith("PTT start"));

        // No stop message: the server has to end the transmission itself when it cleans up the client.
        await alice.Client.DisconnectAsync();

        var end = await log.WaitForAsync(m => m.StartsWith("PTT end"));
        Assert.Equal($"PTT end: Alice ({alicePeerId}) on 251.000 MHz, disconnected, game time 12:34:56", end);
    }

    [Fact]
    public async Task Leave_NotifiesRemainingPeers()
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
        await alice.PeerJoined.WaitForAsync(e => e.PeerId == bob.PeerId);

        await bob.Client.LeaveFrequencyAsync(Freq);

        var left = await alice.PeerLeft.WaitForAsync(e => e.FrequencyKhz == Freq);
        Assert.Equal(bob.PeerId, left.PeerId);
    }

    [Fact]
    public async Task SetDisplayName_BroadcastsUpdatedPeerStatus()
    {
        await using var server = await SignalingServerHarness.StartAsync();
        await using var client = RtcClientHarness.Create(server, "Alice");
        await client.ConnectAsync();
        await client.Client.JoinFrequencyAsync(Freq);
        await client.FrequencyJoined.WaitForAsync(e => e.FrequencyKhz == Freq);

        await client.Client.SetDisplayNameAsync("Renamed");

        var status = await client.AllPeersStatus.WaitForAsync(e =>
            e.AllPeers.TryGetValue(Freq, out var peers) &&
            peers.Any(p => p.Id == client.PeerId && p.Name == "Renamed"));
        Assert.True(status.AllPeers.ContainsKey(Freq));
    }

    [Fact]
    public async Task ModeUpdate_BroadcastsIs3dToPeers()
    {
        await using var server = await SignalingServerHarness.StartAsync();
        await using var client = RtcClientHarness.Create(server, "Alice");
        await client.ConnectAsync();
        await client.Client.JoinFrequencyAsync(Freq);
        await client.FrequencyJoined.WaitForAsync(e => e.FrequencyKhz == Freq);

        await client.Client.SendModeUpdateAsync(is3d: true);

        var status = await client.AllPeersStatus.WaitForAsync(e =>
            e.AllPeers.TryGetValue(Freq, out var peers) &&
            peers.Any(p => p.Id == client.PeerId && p.Is3d));
        Assert.True(status.AllPeers.ContainsKey(Freq));
    }

    [Fact]
    public async Task Disconnect_RemovesClientAndNotifiesPeers()
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
        await alice.PeerJoined.WaitForAsync(e => e.PeerId == bob.PeerId);

        var bobId = bob.PeerId!;
        await bob.Client.DisconnectAsync();

        var left = await alice.PeerLeft.WaitForAsync(e => e.FrequencyKhz == Freq);
        Assert.Equal(bobId, left.PeerId);

        await TestAsync.PollUntil(() => !server.Server.Clients.ContainsKey(bobId));
        Assert.Contains(bobId, server.Audio.RemovedSessions);
    }

    [Fact]
    public async Task IdleWatchdog_RemovesClientWithStaleRtp()
    {
        await using var server = await SignalingServerHarness.StartAsync(
            rtpTimeout: TimeSpan.FromMilliseconds(200),
            watchdogInterval: TimeSpan.FromMilliseconds(100));
        await using var client = RtcClientHarness.Create(server, "Alice");
        await client.ConnectAsync();

        var peerId = client.PeerId!;
        // Simulate a client whose audio heartbeat went silent long ago.
        server.Audio.SetLastRtpReceived(peerId, DateTime.UtcNow - TimeSpan.FromMinutes(5));

        await TestAsync.PollUntil(() => !server.Server.Clients.ContainsKey(peerId),
            timeout: TimeSpan.FromSeconds(3));
        Assert.Contains(peerId, server.Audio.RemovedSessions);
    }
}
