using System.Collections.Concurrent;
using System.Net.WebSockets;
using Microsoft.Extensions.Logging.Abstractions;
using OpenFreqServer;

namespace OpenFreq.Server.Tests;

public class ServerStatsTests
{
    private static ClientSession NewSession(string id, bool authenticated = true)
    {
        var s = new ClientSession(id, id, new FakeWebSocket(), "127.0.0.1") { IsAuthenticated = authenticated };
        return s;
    }

    private static (ServerStats stats, ConcurrentDictionary<string, ClientSession> clients, FrequencyChannelManager mgr)
        CreateStats()
    {
        var clients = new ConcurrentDictionary<string, ClientSession>();
        var mgr = new FrequencyChannelManager();
        // Port 0 binds an ephemeral free UDP port so parallel test runs don't collide.
        var audio = new AudioStreamServer(mgr, clients, NullLoggerFactory.Instance, 0);
        return (new ServerStats(clients, mgr, audio), clients, mgr);
    }

    [Fact]
    public void TotalClients_CountsAll()
    {
        var (stats, clients, _) = CreateStats();
        clients["a"] = NewSession("a");
        clients["b"] = NewSession("b", authenticated: false);

        Assert.Equal(2, stats.TotalClients);
    }

    [Fact]
    public void AuthenticatedClients_CountsOnlyAuthenticated()
    {
        var (stats, clients, _) = CreateStats();
        clients["a"] = NewSession("a");
        clients["b"] = NewSession("b", authenticated: false);

        Assert.Equal(1, stats.AuthenticatedClients);
    }

    [Fact]
    public void ActiveTransmissions_CountsTransmittingFrequencies()
    {
        var (stats, clients, _) = CreateStats();
        var a = NewSession("a");
        a.CurrentFrequencies[251000] = ClientSession.FrequencyClientStatus.Transmitting;
        a.CurrentFrequencies[135100] = ClientSession.FrequencyClientStatus.Receiving;
        clients["a"] = a;

        Assert.Equal(1, stats.ActiveTransmissions);
    }

    [Fact]
    public void Uptime_IsNonNegative()
    {
        var (stats, _, _) = CreateStats();
        Assert.True(stats.Uptime >= TimeSpan.Zero);
    }

    [Fact]
    public void GetActiveClients_ReturnsOnlyAuthenticated()
    {
        var (stats, clients, _) = CreateStats();
        clients["a"] = NewSession("a");
        clients["b"] = NewSession("b", authenticated: false);

        var active = stats.GetActiveClients();

        Assert.Single(active);
        Assert.Equal("a", active[0].Id);
    }

    [Fact]
    public void GetFrequencyStats_ReflectsChannelMembershipAndTransmitState()
    {
        var (stats, clients, mgr) = CreateStats();
        var a = NewSession("a");
        a.CurrentFrequencies[251000] = ClientSession.FrequencyClientStatus.Transmitting;
        clients["a"] = a;
        mgr.JoinChannel(251000, "a", "a");

        var freqStats = stats.GetFrequencyStats();

        var entry = Assert.Single(freqStats);
        Assert.Equal(251000, entry.FrequencyKhz);
        Assert.Equal(1, entry.ClientCount);
        Assert.True(entry.IsTransmitting);
    }

    [Fact]
    public void GetFrequencyStats_SortedByFrequency()
    {
        var (stats, clients, mgr) = CreateStats();
        var a = NewSession("a");
        a.CurrentFrequencies[251000] = ClientSession.FrequencyClientStatus.Receiving;
        a.CurrentFrequencies[135100] = ClientSession.FrequencyClientStatus.Receiving;
        clients["a"] = a;
        mgr.JoinChannel(251000, "a", "a");
        mgr.JoinChannel(135100, "a", "a");

        var freqStats = stats.GetFrequencyStats();

        Assert.Equal([135100, 251000], freqStats.Select(f => f.FrequencyKhz).ToList());
    }

    [Fact]
    public void GetLastRtpReceived_UnknownClient_ReturnsNull()
    {
        var (stats, _, _) = CreateStats();
        Assert.Null(stats.GetLastRtpReceived("ghost"));
    }

    private sealed class FakeWebSocket : WebSocket
    {
        public override WebSocketCloseStatus? CloseStatus => null;
        public override string? CloseStatusDescription => null;
        public override WebSocketState State => WebSocketState.Open;
        public override string? SubProtocol => null;
        public override void Abort() { }
        public override Task CloseAsync(WebSocketCloseStatus s, string? d, CancellationToken c) => Task.CompletedTask;
        public override Task CloseOutputAsync(WebSocketCloseStatus s, string? d, CancellationToken c) => Task.CompletedTask;
        public override void Dispose() { }
        public override Task<WebSocketReceiveResult> ReceiveAsync(ArraySegment<byte> b, CancellationToken c)
            => Task.FromResult(new WebSocketReceiveResult(0, WebSocketMessageType.Close, true));
        public override Task SendAsync(ArraySegment<byte> b, WebSocketMessageType m, bool e, CancellationToken c)
            => Task.CompletedTask;
    }
}
