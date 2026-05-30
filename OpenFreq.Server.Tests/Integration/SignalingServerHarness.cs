using Microsoft.Extensions.Logging.Abstractions;
using OpenFreqServer;

namespace OpenFreq.Server.Tests.Integration;

/// <summary>
/// Spins up a real <see cref="SignalingServer"/> (Kestrel WebSocket host + routing +
/// channel manager) on an OS-assigned free port, backed by a <see cref="FakeAudioRelay"/>
/// so no real UDP audio port is bound. One harness per test for isolation.
/// </summary>
public sealed class SignalingServerHarness : IAsyncDisposable
{
    public FakeAudioRelay Audio { get; }
    public SignalingServer Server { get; }

    private SignalingServerHarness(FakeAudioRelay audio, SignalingServer server)
    {
        Audio = audio;
        Server = server;
    }

    /// <summary>WebSocket URI clients connect to, e.g. ws://127.0.0.1:49321/.</summary>
    public Uri WsUri => new($"ws://127.0.0.1:{Server.BoundWebSocketPort}/");

    /// <summary>Host:port string accepted by <see cref="OpenFreq.Common.OpenFreqRtcClient"/>.</summary>
    public string ServerAddress => $"127.0.0.1:{Server.BoundWebSocketPort}";

    public static async Task<SignalingServerHarness> StartAsync(
        string? password = null,
        int maxClientsPerChannel = 50,
        bool broadcastPeerUpdates = true,
        TimeSpan? rtpTimeout = null,
        TimeSpan? watchdogInterval = null)
    {
        var config = new ServerConfig
        {
            ServerPassword = password,
            WebSocketPort = 0, // let the OS pick a free port
            AudioPort = FakeAudioRelay.FakePort,
            MaxClientsPerChannel = maxClientsPerChannel,
            BroadcastPeerUpdates = broadcastPeerUpdates,
            EnableOpusCompression = false,
        };

        var audio = new FakeAudioRelay();
        var server = new SignalingServer(
            config, NullLoggerFactory.Instance, audio, rtpTimeout, watchdogInterval);

        await server.StartAsync();
        return new SignalingServerHarness(audio, server);
    }

    public async ValueTask DisposeAsync()
    {
        try
        {
            await Server.StopAsync();
        }
        catch
        {
            // best effort teardown
        }
    }
}
