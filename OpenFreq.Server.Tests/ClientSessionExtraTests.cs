using System.Net.WebSockets;
using OpenFreqServer;

namespace OpenFreq.Server.Tests;

public class ClientSessionExtraTests
{
    private static ClientSession Create() => new("id", "Viper", new FakeWebSocket(), "127.0.0.1");

    [Fact]
    public void CurrentFrequencies_TracksPerFrequencyStatus()
    {
        var s = Create();
        s.CurrentFrequencies[251000] = ClientSession.FrequencyClientStatus.Transmitting;
        s.CurrentFrequencies[135100] = ClientSession.FrequencyClientStatus.Receiving;

        Assert.Equal(ClientSession.FrequencyClientStatus.Transmitting, s.CurrentFrequencies[251000]);
        Assert.Equal(ClientSession.FrequencyClientStatus.Receiving, s.CurrentFrequencies[135100]);
    }

    [Fact]
    public void IsAuthenticated_Settable()
    {
        var s = Create();
        Assert.False(s.IsAuthenticated);
        s.IsAuthenticated = true;
        Assert.True(s.IsAuthenticated);
    }

    [Fact]
    public void Is3d_DefaultsFalse_AndSettable()
    {
        var s = Create();
        Assert.False(s.Is3d);
        s.Is3d = true;
        Assert.True(s.Is3d);
    }

    [Fact]
    public void DisplayName_Settable()
    {
        var s = Create();
        s.DisplayName = "Maverick";
        Assert.Equal("Maverick", s.DisplayName);
    }

    [Fact]
    public void Ip_Settable()
    {
        var s = Create();
        s.Ip = "10.0.0.9";
        Assert.Equal("10.0.0.9", s.Ip);
    }

    [Fact]
    public void WebSocket_ExposesProvidedInstance()
    {
        var ws = new FakeWebSocket();
        var s = new ClientSession("id", "name", ws, "127.0.0.1");
        Assert.Same(ws, s.WebSocket);
    }

    [Fact]
    public void SendLock_InitiallyAvailable()
    {
        var s = Create();
        Assert.Equal(1, s.SendLock.CurrentCount);
    }

    [Fact]
    public void Dispose_DisposesWebSocket()
    {
        var ws = new FakeWebSocket();
        var s = new ClientSession("id", "name", ws, "127.0.0.1");
        s.Dispose();
        Assert.True(ws.Disposed);
    }

    private sealed class FakeWebSocket : WebSocket
    {
        public bool Disposed { get; private set; }
        public override WebSocketCloseStatus? CloseStatus => null;
        public override string? CloseStatusDescription => null;
        public override WebSocketState State => WebSocketState.Open;
        public override string? SubProtocol => null;
        public override void Abort() { }
        public override Task CloseAsync(WebSocketCloseStatus s, string? d, CancellationToken c) => Task.CompletedTask;
        public override Task CloseOutputAsync(WebSocketCloseStatus s, string? d, CancellationToken c) => Task.CompletedTask;
        public override void Dispose() => Disposed = true;
        public override Task<WebSocketReceiveResult> ReceiveAsync(ArraySegment<byte> b, CancellationToken c)
            => Task.FromResult(new WebSocketReceiveResult(0, WebSocketMessageType.Close, true));
        public override Task SendAsync(ArraySegment<byte> b, WebSocketMessageType m, bool e, CancellationToken c)
            => Task.CompletedTask;
    }
}
