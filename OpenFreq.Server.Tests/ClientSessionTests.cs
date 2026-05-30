using System.Net.WebSockets;
using OpenFreqServer;

namespace OpenFreq.Server.Tests;

public class ClientSessionTests
{
    private static ClientSession Create(string id = "test-id", string displayName = "Viper", string ip = "127.0.0.1")
        => new(id, displayName, new FakeWebSocket(), ip);

    [Fact]
    public void Constructor_SetsId()
    {
        var session = Create(id: "client-42");
        Assert.Equal("client-42", session.Id);
    }

    [Fact]
    public void Constructor_SetsDisplayName()
    {
        var session = Create(displayName: "Maverick");
        Assert.Equal("Maverick", session.DisplayName);
    }

    [Fact]
    public void Constructor_SetsIp()
    {
        var session = Create(ip: "10.0.0.1");
        Assert.Equal("10.0.0.1", session.Ip);
    }

    [Fact]
    public void Constructor_IsAuthenticatedFalse()
    {
        var session = Create();
        Assert.False(session.IsAuthenticated);
    }

    [Fact]
    public void Constructor_IsDisposedFalse()
    {
        var session = Create();
        Assert.False(session.IsDisposed);
    }

    [Fact]
    public void Constructor_CurrentFrequenciesEmpty()
    {
        var session = Create();
        Assert.Empty(session.CurrentFrequencies);
    }

    [Fact]
    public void Dispose_SetsIsDisposedTrue()
    {
        var session = Create();
        session.Dispose();
        Assert.True(session.IsDisposed);
    }

    [Fact]
    public void Dispose_CalledTwice_NoException()
    {
        var session = Create();
        session.Dispose();
        session.Dispose();
    }

    [Fact]
    public void UpdateActivity_AdvancesLastActivity()
    {
        var session = Create();
        session.LastActivity = DateTime.UtcNow.AddHours(-1);
        var before = session.LastActivity;

        session.UpdateActivity();

        Assert.True(session.LastActivity > before);
    }

    private sealed class FakeWebSocket : WebSocket
    {
        public override WebSocketCloseStatus? CloseStatus => null;
        public override string? CloseStatusDescription => null;
        public override WebSocketState State => WebSocketState.Open;
        public override string? SubProtocol => null;
        public override void Abort() { }
        public override Task CloseAsync(WebSocketCloseStatus closeStatus, string? statusDescription, CancellationToken cancellationToken) => Task.CompletedTask;
        public override Task CloseOutputAsync(WebSocketCloseStatus closeStatus, string? statusDescription, CancellationToken cancellationToken) => Task.CompletedTask;
        public override void Dispose() { }
        public override Task<WebSocketReceiveResult> ReceiveAsync(ArraySegment<byte> buffer, CancellationToken cancellationToken)
            => Task.FromResult(new WebSocketReceiveResult(0, WebSocketMessageType.Close, true));
        public override Task SendAsync(ArraySegment<byte> buffer, WebSocketMessageType messageType, bool endOfMessage, CancellationToken cancellationToken)
            => Task.CompletedTask;
    }
}
