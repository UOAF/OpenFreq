using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Json;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using OpenFreq.Common;
using OpenFreq.Common.Signaling;

namespace OpenFreqServer;

public class SignalingServer
{
    private WebApplication? _app;
    private readonly ServerConfig _config;
    private readonly ConcurrentDictionary<string, ClientSession> _clients = new();
    private readonly FrequencyChannelManager _channelManager = new();
    private readonly IAudioStreamServer _audioServer;
    private readonly ILogger<SignalingServer> _logger;
    private CancellationTokenSource _cts = new();

    // Guards against a single wedged socket stalling a broadcast indefinitely. Kept well above
    // the worst-case send latency seen during synchronized channel tune bursts (clients all jumping to 3D) so we
    // don't disconnect slow clients. Dead clients are reaped by the RTP watchdog anyway

    private const double WebsocketTimeoutMillis = 10000;
    private readonly TimeSpan _rtpTimeoutDuration;
    private readonly TimeSpan _watchdogInterval;

    // High-performance logging delegates
    private static readonly Action<ILogger, int, Exception?> LogServerStarted =
        LoggerMessage.Define<int>(
            LogLevel.Information,
            new EventId(1, nameof(StartAsync)),
            "OpenFreqServer listening on port {Port}");

    private static readonly Action<ILogger, string, string, Exception?> LogClientConnected =
        LoggerMessage.Define<string, string>(
            LogLevel.Information,
            new EventId(2, nameof(HandleWebSocketConnection)),
            "{DisplayName} ({ClientId}) connected");

    private static readonly Action<ILogger, string, string, int, Exception?> LogClientAuthenticated =
        LoggerMessage.Define<string, string, int>(
            LogLevel.Information,
            new EventId(3, nameof(HandleAuthenticate)),
            "{DisplayName} ({ClientId}) authenticated, audio port {AudioPort}");

    private static readonly Action<ILogger, string, string, double, Exception?> LogClientJoinedFrequency =
        LoggerMessage.Define<string, string, double>(
            LogLevel.Information,
            new EventId(4, nameof(HandleJoinChannel)),
            "{DisplayName} ({ClientId}) joined frequency {Frequency:F3} MHz");

    private static readonly Action<ILogger, string, string, double, Exception?> LogClientLeftFrequency =
        LoggerMessage.Define<string, string, double>(
            LogLevel.Information,
            new EventId(5, nameof(LeaveCurrentChannel)),
            "{DisplayName} ({ClientId}) left frequency {Frequency:F3} MHz");

    private static readonly Action<ILogger, string, string, bool, double, int, Exception?> LogTransmissionState =
        LoggerMessage.Define<string, string, bool, double, int>(
            LogLevel.Information,
            new EventId(6, nameof(HandleTransmission)),
            "{DisplayName} ({ClientId}) transmission: {IsTransmitting} on {Frequency:F3} MHz, broadcasting to {PeerCount} peer(s)");

    private static readonly Action<ILogger, string, string, Exception?> LogClientCleanedUp =
        LoggerMessage.Define<string, string>(
            LogLevel.Information,
            new EventId(7, nameof(CleanupClient)),
            "{DisplayName} ({ClientId}) cleaned up");

    private static readonly Action<ILogger, string, string, Exception?> LogClientRtpTimeout =
        LoggerMessage.Define<string, string>(
            LogLevel.Warning,
            new EventId(8, nameof(IdleWatchdogAsync)),
            "{DisplayName} ({ClientId}) removed: RTP heartbeat timeout (60 s)");

    public ConcurrentDictionary<string, ClientSession> Clients => _clients;
    public FrequencyChannelManager ChannelManager => _channelManager;
    public IAudioStreamServer AudioServer => _audioServer;

    /// <summary>
    /// The actual TCP port the WebSocket endpoint is bound to. Only meaningful after
    /// <see cref="StartAsync"/> has completed. Useful when the configured port is 0
    /// (let the OS pick a free port), e.g. in integration tests.
    /// </summary>
    public int? BoundWebSocketPort { get; private set; }

    private static string GetDisplayName(ClientSession session) =>
        !string.IsNullOrWhiteSpace(session.DisplayName) ? session.DisplayName : "Unnamed";

    public SignalingServer(ServerConfig config, ILoggerFactory loggerFactory)
        : this(config, loggerFactory, null, null, null)
    {
    }

    /// <summary>
    /// Test-friendly constructor. Allows injecting a fake audio relay (avoids binding a
    /// real UDP port) and shortening the idle-watchdog timings for deterministic tests.
    /// </summary>
    /// <param name="audioServer">Audio relay to use; null builds the real <see cref="AudioStreamServer"/>.</param>
    /// <param name="rtpTimeout">Idle RTP timeout before cleanup; null uses the production default (60 s).</param>
    /// <param name="watchdogInterval">How often the idle watchdog runs; null uses the production default (30 s).</param>
    public SignalingServer(
        ServerConfig config,
        ILoggerFactory loggerFactory,
        IAudioStreamServer? audioServer,
        TimeSpan? rtpTimeout,
        TimeSpan? watchdogInterval)
    {
        _config = config;
        _logger = loggerFactory.CreateLogger<SignalingServer>();
        _rtpTimeoutDuration = rtpTimeout ?? TimeSpan.FromMinutes(1);
        _watchdogInterval = watchdogInterval ?? TimeSpan.FromSeconds(30);
        _audioServer = audioServer ?? new AudioStreamServer(_channelManager, _clients, loggerFactory, config.AudioPort);

        // Build Kestrel application
        var builder = WebApplication.CreateBuilder();

        // Configure Kestrel
        builder.WebHost.UseKestrel(options =>
        {
            // Listen on all interfaces
            options.ListenAnyIP(config.WebSocketPort, listenOptions =>
            {
                // Performance tuning
                listenOptions.Protocols = HttpProtocols.Http1;
            });

            // Connection limits
            options.Limits.MaxConcurrentConnections = 1000;
            options.Limits.MaxConcurrentUpgradedConnections = 1000;

            // WebSocket keep-alive
            options.Limits.KeepAliveTimeout = TimeSpan.FromMinutes(2);
            options.Limits.RequestHeadersTimeout = TimeSpan.FromSeconds(30);
        });

        // Replace default logging with LoggerFactory
        builder.Logging.ClearProviders();
        builder.Services.AddSingleton(loggerFactory);

        // Disable unnecessary services to keep it lightweight
        builder.Services.Configure<JsonOptions>(options =>
        {
            options.SerializerOptions.PropertyNameCaseInsensitive = true;
        });

        _app = builder.Build();

        // Configure WebSocket options
        _app.UseWebSockets(new WebSocketOptions
        {
            KeepAliveInterval = TimeSpan.FromSeconds(15),
        });

        // WebSocket signaling endpoint
        _app.Map("/", async context =>
        {
            if (context.WebSockets.IsWebSocketRequest)
            {
                var webSocket = await context.WebSockets.AcceptWebSocketAsync();
                await HandleWebSocketConnection(webSocket, context);
            }
            else
            {
                context.Response.StatusCode = 400;
                await context.Response.WriteAsync("WebSocket connection required");
            }
        });
    }

    public async Task StartAsync()
    {
        LogServerStarted(_logger, _config.WebSocketPort, null);

        try
        {
            await _app!.StartAsync(_cts.Token);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to start server");
            throw;
        }

        BoundWebSocketPort = ResolveBoundPort();

        _ = IdleWatchdogAsync(_cts.Token);
    }

    private int? ResolveBoundPort()
    {
        if (_config.WebSocketPort != 0)
            return _config.WebSocketPort;

        // Configured port 0 → Kestrel picked a free port; read it back from the server's
        // resolved addresses (e.g. "http://[::]:49321").
        var addresses = _app?.Services.GetService<IServer>()?
            .Features.Get<IServerAddressesFeature>()?.Addresses;

        if (addresses == null) return null;

        foreach (var address in addresses)
        {
            if (Uri.TryCreate(address, UriKind.Absolute, out var uri) && uri.Port > 0)
                return uri.Port;
        }

        return null;
    }

    private async Task IdleWatchdogAsync(CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                await Task.Delay(_watchdogInterval, ct);

                var now = DateTime.UtcNow;
                foreach (var (clientId, session) in _clients)
                {
                    if (!session.IsAuthenticated) continue;

                    var lastRtp = _audioServer.GetLastRtpReceived(clientId);
                    if (lastRtp == null) continue; // audio session not yet created

                    if (now - lastRtp.Value <= _rtpTimeoutDuration) continue; // timeout not reached

                    LogClientRtpTimeout(_logger, GetDisplayName(session), clientId, null);
                    _ = CleanupClient(clientId);
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Server shutting down — expected
        }
    }

    private async Task HandleWebSocketConnection(WebSocket webSocket, HttpContext httpContext)
    {
        string clientId = Guid.NewGuid().ToString();

        try
        {
            var remoteIp = httpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown";
            _logger.LogDebug("Client {ClientId} connecting from {RemoteIp}", clientId, remoteIp);

            var session = new ClientSession(clientId, string.Empty, webSocket, remoteIp);
            _clients[clientId] = session;

            LogClientConnected(_logger, GetDisplayName(session), clientId, null);

            await HandleClientMessages(session);
        }
        catch (Exception ex)
        {
            var displayName = _clients.TryGetValue(clientId, out var session)
                ? GetDisplayName(session)
                : "Unnamed";
            _logger.LogError(ex, "Error in WebSocket connection for {DisplayName} ({ClientId})", displayName, clientId);
        }
        finally
        {
            await CleanupClient(clientId);
        }
    }

    private async Task HandleClientMessages(ClientSession session)
    {
        var buffer = new byte[8192];

        try
        {
            while (session.WebSocket.State == WebSocketState.Open && !_cts.Token.IsCancellationRequested)
            {
                var result = await session.WebSocket.ReceiveAsync(
                    new ArraySegment<byte>(buffer),
                    _cts.Token);

                if (result.MessageType == WebSocketMessageType.Close)
                {
                    _logger.LogInformation(
                        "{DisplayName} ({ClientId}) sent close frame: {Status} \"{Description}\"",
                        GetDisplayName(session), session.Id,
                        result.CloseStatus, result.CloseStatusDescription ?? "");

                    if (session.WebSocket.State == WebSocketState.CloseReceived)
                    {
                        await session.WebSocket.CloseAsync(
                            WebSocketCloseStatus.NormalClosure,
                            "Closing",
                            CancellationToken.None);
                    }
                    break;
                }

                if (result.MessageType == WebSocketMessageType.Text)
                {
                    var message = Encoding.UTF8.GetString(buffer, 0, result.Count);
                    await ProcessMessage(session, message);
                }

                session.UpdateActivity();
            }
        }
        catch (WebSocketException ex) when (ex.WebSocketErrorCode == WebSocketError.ConnectionClosedPrematurely)
        {
            // Transport dropped without a close handshake (TCP reset / network loss)
            _logger.LogInformation(
                "{DisplayName} ({ClientId}) disconnected abruptly: {Error} (TCP reset / network loss)",
                GetDisplayName(session), session.Id, ex.WebSocketErrorCode);
        }
        catch (OperationCanceledException)
        {
            // Server shutting down
            _logger.LogDebug("{DisplayName} ({ClientId}) connection cancelled during shutdown", GetDisplayName(session),
                session.Id);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unexpected error handling messages for {DisplayName} ({ClientId})",
                GetDisplayName(session), session.Id);
        }
    }

    private async Task ProcessMessage(ClientSession session, string messageText)
    {
        try
        {
            var message = Json.Json.Instance.Deserialize<SignalingMessage>(messageText);
            if (message == null) return;

            switch (message.Type)
            {
                case "authenticate":
                    await HandleAuthenticate(session, message);
                    break;

                case "join":
                    await HandleJoinChannel(session, message);
                    break;

                case "leave":
                    await HandleLeaveChannel(session, message);
                    break;

                case "transmission":
                    await HandleTransmission(session, message);
                    break;
                case "mode-update":
                    await HandleModeUpdate(session, message);
                    break;
                case "set-display-name":
                    await SetDisplayName(session, message);
                    break;

                default:
                    if (_logger.IsEnabled(LogLevel.Warning))
                        _logger.LogWarning("Unknown message type: {MessageType}", message.Type);
                    break;
            }
        }
        catch (JsonException ex)
        {
            _logger.LogWarning(ex, "Invalid JSON from {DisplayName} ({ClientId})", GetDisplayName(session), session.Id);
            await SendError(session, "Invalid message format");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error processing message from {DisplayName} ({ClientId})", GetDisplayName(session),
                session.Id);
            await SendError(session, $"Server error processing message");
        }
    }

    private async Task HandleAuthenticate(ClientSession session, SignalingMessage message)
    {
        var authMsg = SignalingMessageFactory.DeserializePayload<AuthenticateMessage>(message.Payload);

        if (authMsg == null)
        {
            await SendError(session, "Invalid authentication message");
            return;
        }

        session.DisplayName = authMsg.DisplayName;

        if (string.IsNullOrEmpty(_config.ServerPassword) ||
            authMsg.Password == _config.ServerPassword)
        {
            session.IsAuthenticated = true;

            var audioPort = _audioServer.CreateAudioSession(session.Id);
            LogClientAuthenticated(_logger, GetDisplayName(session), session.Id, audioPort, null);

            await SendSuccess(session, "Authenticated", session.Id, audioPort, _config.EnableOpusCompression);
        }
        else
        {
            await SendError(session, "Authentication failed");

            // Disconnect on auth failure
            await Task.Delay(1000); // Brief delay to prevent brute force
            if (session.WebSocket.State == WebSocketState.Open)
            {
                await session.WebSocket.CloseAsync(
                    WebSocketCloseStatus.PolicyViolation,
                    "Authentication failed",
                    CancellationToken.None);
            }
        }
    }

    private async Task HandleJoinChannel(ClientSession session, SignalingMessage message)
    {
        if (!session.IsAuthenticated)
        {
            await SendError(session, "Not authenticated");
            return;
        }

        var joinMsg = SignalingMessageFactory.DeserializePayload<JoinChannelMessage>(message.Payload);

        if (joinMsg == null)
        {
            await SendError(session, "Invalid join message");
            return;
        }

        var channelCount = _channelManager.GetChannelCount(joinMsg.FrequencyKhz);

        if (channelCount >= _config.MaxClientsPerChannel)
        {
            await SendError(session, "Channel is full");
            return;
        }

        if (session.CurrentFrequencies.ContainsKey(joinMsg.FrequencyKhz))
        {
            await SendError(session, "Frequency already joined");
            return;
        }

        _channelManager.JoinChannel(joinMsg.FrequencyKhz, session.Id, session.DisplayName ?? "Unnamed", session.Is3d);

        session.CurrentFrequencies.TryAdd(joinMsg.FrequencyKhz, ClientSession.FrequencyClientStatus.Receiving);

        List<ChannelStateMessage.Peer> peers = [];
        foreach (var clientId in _channelManager.GetClientsInChannel(joinMsg.FrequencyKhz))
        {
            if (clientId == session.Id) continue;
            _clients.TryGetValue(clientId, out var clientSession);
            if (clientSession == null) continue;
            peers.Add(new ChannelStateMessage.Peer(clientSession.Id, clientSession.DisplayName ?? "Unnamed"));
        }

        await SendChannelState(session, joinMsg.FrequencyKhz, peers);

        await BroadcastToChannel(
            joinMsg.FrequencyKhz,
            session.Id,
            SignalingMessageFactory.CreatePeerJoined(session.Id, session.DisplayName, joinMsg.FrequencyKhz));

        LogClientJoinedFrequency(_logger, GetDisplayName(session), session.Id, joinMsg.FrequencyKhz / 1000d, null);

        if (_config.BroadcastPeerUpdates)
        {
            await BroadcastToAllChannels(
                SignalingMessageFactory.CreateAllPeersStatusMessage(_channelManager.GetAllChannelStates()));
        }
    }

    private async Task HandleLeaveChannel(ClientSession session, SignalingMessage message)
    {
        await LeaveCurrentChannel(session, message);
    }

    private async Task LeaveCurrentChannel(ClientSession session, SignalingMessage message)
    {
        if (session.CurrentFrequencies.IsEmpty) return;

        var transmissionMsg = SignalingMessageFactory.DeserializePayload<AudioTransmissionMessage>(message.Payload);
        if (transmissionMsg == null) return;

        var frequencyKhz = transmissionMsg.FrequencyKhz;
        _channelManager.LeaveChannel(frequencyKhz, session.Id);

        await BroadcastToChannel(
            frequencyKhz,
            session.Id,
            SignalingMessageFactory.CreatePeerLeft(session.Id, frequencyKhz));

        session.CurrentFrequencies.TryRemove(frequencyKhz, out _);
        LogClientLeftFrequency(_logger, GetDisplayName(session), session.Id, frequencyKhz / 1000d, null);

        if (_config.BroadcastPeerUpdates)
        {
            await BroadcastToAllChannels(
                SignalingMessageFactory.CreateAllPeersStatusMessage(_channelManager.GetAllChannelStates()));
        }
    }

    private async Task LeaveAllChannels(ClientSession session)
    {
        var frequencies = session.CurrentFrequencies.Keys.ToArray();

        foreach (var frequency in frequencies)
        {
            _channelManager.LeaveChannel(frequency, session.Id);

            await BroadcastToChannel(
                frequency,
                session.Id,
                SignalingMessageFactory.CreatePeerLeft(session.Id, frequency));

            session.CurrentFrequencies.TryRemove(frequency, out _);
            LogClientLeftFrequency(_logger, GetDisplayName(session), session.Id, frequency / 1000d, null);
        }

        if (_config.BroadcastPeerUpdates)
        {
            await BroadcastToAllChannels(
                SignalingMessageFactory.CreateAllPeersStatusMessage(_channelManager.GetAllChannelStates()));
        }
    }

    private async Task HandleTransmission(ClientSession session, SignalingMessage message)
    {
        if (!session.IsAuthenticated) return;
        if (session.CurrentFrequencies.IsEmpty) return;

        var transmissionMsg = SignalingMessageFactory.DeserializePayload<AudioTransmissionMessage>(message.Payload);
        if (transmissionMsg == null) return;

        if (session.CurrentFrequencies.TryGetValue(transmissionMsg.FrequencyKhz,
                out var frequencyStatus))
        {
            session.CurrentFrequencies.TryUpdate(transmissionMsg.FrequencyKhz,
                transmissionMsg.Transmitting
                    ? ClientSession.FrequencyClientStatus.Transmitting
                    : ClientSession.FrequencyClientStatus.Receiving, frequencyStatus);
        }
        else return;

        _channelManager.UpdateIs3d(transmissionMsg.FrequencyKhz, session.Id, transmissionMsg.Is3d);

        if (!session.CurrentFrequencies.ContainsKey(transmissionMsg.FrequencyKhz)) return;

        var peersInChannel = _channelManager.GetClientsInChannel(transmissionMsg.FrequencyKhz)
            .Where(id => id != session.Id)
            .ToArray();

        LogTransmissionState(_logger, GetDisplayName(session), session.Id, transmissionMsg.Transmitting,
            transmissionMsg.FrequencyKhz / 1000d, peersInChannel.Length, null);

        await BroadcastToChannel(
            transmissionMsg.FrequencyKhz,
            session.Id,
            SignalingMessageFactory.CreateTransmissionEvent(
                session.Id,
                transmissionMsg.FrequencyKhz,
                transmissionMsg.Transmitting,
                transmissionMsg.Is3d));
    }

    private async Task HandleModeUpdate(ClientSession session, SignalingMessage message)
    {
        if (!session.IsAuthenticated) return;

        var modeMsg = SignalingMessageFactory.DeserializePayload<ModeUpdateMessage>(message.Payload);
        if (modeMsg == null) return;

        session.Is3d = modeMsg.Is3d;
        foreach (var frequencyKhz in session.CurrentFrequencies.Keys)
            _channelManager.UpdateIs3d(frequencyKhz, session.Id, modeMsg.Is3d);

        await BroadcastToAllChannels(
            SignalingMessageFactory.CreateAllPeersStatusMessage(_channelManager.GetAllChannelStates()));
    }

    private async Task SetDisplayName(ClientSession session, SignalingMessage message)
    {
        var setDisplayNameMsg = SignalingMessageFactory.DeserializePayload<DisplayNameMessage>(message.Payload);

        if (setDisplayNameMsg == null)
        {
            await SendError(session, "Invalid set display name message");
            return;
        }

        session.DisplayName = setDisplayNameMsg.DisplayName;
        _channelManager.UpdateDisplayName(session.Id, setDisplayNameMsg.DisplayName);

        if (_config.BroadcastPeerUpdates)
        {
            await BroadcastToAllChannels(
                SignalingMessageFactory.CreateAllPeersStatusMessage(_channelManager.GetAllChannelStates()));
        }
    }

    private async Task BroadcastToAllChannels(SignalingMessage message)
    {
        var broadcastTasks = _clients.Values
            .Where(session => session.IsAuthenticated)
            .Select(async session =>
            {
                await SendToClient(session, message);

                if (_logger.IsEnabled(LogLevel.Debug))
                    _logger.LogDebug(
                        "Broadcasting {MessageType} to {DisplayName} ({ClientId})",
                        message.Type, GetDisplayName(session), session.Id);
            });

        await Task.WhenAll(broadcastTasks);
    }

    private async Task BroadcastToChannel(int frequencyKhz, string excludeClientId, SignalingMessage message)
    {
        var clients = _channelManager.GetClientsInChannel(frequencyKhz);

        var broadcastTasks = clients
            .Where(clientId => clientId != excludeClientId)
            .Select(async clientId =>
            {
                if (_clients.TryGetValue(clientId, out var session))
                {
                    await SendToClient(session, message);

                    if (_logger.IsEnabled(LogLevel.Debug))
                        _logger.LogDebug(
                            "Broadcasting {MessageType} to {DisplayName} ({ClientId}) in channel {Frequency:F3} MHz",
                            message.Type, GetDisplayName(session), clientId, frequencyKhz / 1000d);
                }
            });

        await Task.WhenAll(broadcastTasks);
    }

    private async Task SendToClient(ClientSession session, SignalingMessage message)
    {
        if (session.IsDisposed)
            return;

        if (session.WebSocket.State != WebSocketState.Open)
        {
            await CleanupClient(session.Id);
            return;
        }

        await session.SendLock.WaitAsync();
        try
        {
            if (session.WebSocket.State != WebSocketState.Open)
                return;

            var json = Json.Json.Instance.Serialize(message);
            var buffer = Encoding.UTF8.GetBytes(json);

            using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(WebsocketTimeoutMillis));

            await session.WebSocket.SendAsync(
                new ArraySegment<byte>(buffer),
                WebSocketMessageType.Text,
                true,
                cts.Token);
        }
        catch (Exception ex) when (ex is OperationCanceledException or WebSocketException)
        {
            _logger.LogWarning(ex, "WebSocket error for {ClientId}", session.Id);
            await CleanupClient(session.Id);
        }
        finally
        {
            session.SendLock.Release();
        }
    }

    private async Task SendError(ClientSession session, string error)
    {
        await SendToClient(session, SignalingMessageFactory.CreateError(error));
    }

    private async Task SendSuccess(ClientSession session, string message, string? peerId = null, int? audioPort = null,
        bool opusEnabled = true)
    {
        await SendToClient(session,
            SignalingMessageFactory.CreateSuccess(message, _channelManager.GetAllChannelStates(), peerId, audioPort,
                opusEnabled));
    }

    private async Task SendChannelState(ClientSession session, int frequencyKhz, List<ChannelStateMessage.Peer> peers)
    {
        await SendToClient(session, SignalingMessageFactory.CreateChannelState(frequencyKhz, peers));
    }

    private async Task CleanupClient(string clientId)
    {
        if (_clients.TryRemove(clientId, out var session))
        {
            await LeaveAllChannels(session);

            _channelManager.LeaveAllChannels(clientId);
            _audioServer.RemoveSession(clientId);

            if (session.WebSocket.State == WebSocketState.Open)
            {
                try
                {
                    using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(3));
                    await session.WebSocket.CloseAsync(
                        WebSocketCloseStatus.NormalClosure,
                        "Cleanup",
                        cts.Token);
                }
                catch (Exception ex) when (ex is WebSocketException or OperationCanceledException)
                {
                    // Graceful close failed or timed out — force abort so client detects disconnect
                    session.WebSocket.Abort();
                }
            }

            session.Dispose();

            LogClientCleanedUp(_logger, GetDisplayName(session), clientId, null);
        }
    }

    public async Task StopAsync()
    {
        _logger.LogInformation("Stopping signaling server...");

        // Stop audio server first
        _audioServer.Stop();

        // Notify connected clients before cancelling so they receive a proper close frame.
        // CloseAsync (full handshake) ensures the frame is transmitted before we return —
        // the client's echo close frame proves delivery. Per-client 500ms timeout prevents
        // any single slow client from blocking shutdown.
        var closeTasks = _clients.Values
            .Where(s => s.WebSocket.State == WebSocketState.Open)
            .Select(async s =>
            {
                try
                {
                    using var closeCts = new CancellationTokenSource(TimeSpan.FromMilliseconds(500));
                    await s.WebSocket.CloseAsync(
                        WebSocketCloseStatus.NormalClosure,
                        "Server shutting down",
                        closeCts.Token);
                }
                catch
                {
                    // don't care
                }
            });
        try
        {
            await Task.WhenAll(closeTasks).WaitAsync(TimeSpan.FromSeconds(2));
        }
        catch
        {
            // don't care
        }

        // Cancel the CTS
        _cts.Cancel();
        _cts.Dispose();

        // Stop the web app
        if (_app != null)
        {
            using var shutdownCts = new CancellationTokenSource(TimeSpan.FromSeconds(5));

            try
            {
                await _app.StopAsync(shutdownCts.Token);
            }
            catch (OperationCanceledException)
            {
                _logger.LogWarning("App shutdown timed out");
            }

            await _app.DisposeAsync();
        }

        _logger.LogInformation("Signaling server stopped");
    }
}
