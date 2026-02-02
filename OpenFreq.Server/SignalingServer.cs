using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Json;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using OpenFreq.Common.Signaling;
using OpenFreqServer.Json;

namespace OpenFreq.Server;

public class SignalingServer
{
    private WebApplication? _app;
    private readonly ServerConfig _config;
    private readonly ConcurrentDictionary<string, ClientSession> _clients = new();
    private readonly FrequencyChannelManager _channelManager = new();
    private readonly AudioStreamServer _audioServer;
    private readonly ILogger<SignalingServer> _logger;
    private readonly ILoggerFactory _loggerFactory;
    private CancellationTokenSource _cts = new();

    // High-performance logging delegates
    private static readonly Action<ILogger, int, Exception?> _logServerStarted =
        LoggerMessage.Define<int>(
            LogLevel.Information,
            new EventId(1, nameof(StartAsync)),
            "OpenFreqServer listening on port {Port}");

    private static readonly Action<ILogger, string, Exception?> _logClientConnected =
        LoggerMessage.Define<string>(
            LogLevel.Information,
            new EventId(2, nameof(HandleWebSocketConnection)),
            "Client {ClientId} connected");

    private static readonly Action<ILogger, string, int, Exception?> _logClientAuthenticated =
        LoggerMessage.Define<string, int>(
            LogLevel.Information,
            new EventId(3, nameof(HandleAuthenticate)),
            "Client {ClientId} authenticated, audio port {AudioPort}");

    private static readonly Action<ILogger, string, double, Exception?> _logClientJoinedFrequency =
        LoggerMessage.Define<string, double>(
            LogLevel.Information,
            new EventId(4, nameof(HandleJoinChannel)),
            "Client {ClientId} joined frequency {Frequency:F3}");

    private static readonly Action<ILogger, string, double, Exception?> _logClientLeftFrequency =
        LoggerMessage.Define<string, double>(
            LogLevel.Information,
            new EventId(5, nameof(LeaveCurrentChannel)),
            "Client {ClientId} left frequency {Frequency:F3}");

    private static readonly Action<ILogger, string, bool, double, int, Exception?> _logTransmissionState =
        LoggerMessage.Define<string, bool, double, int>(
            LogLevel.Information,
            new EventId(6, nameof(HandleTransmission)),
            "Client {ClientId} transmission state: {IsTransmitting} on frequency {Frequency:F3}, broadcasting to {PeerCount} peer(s)");

    private static readonly Action<ILogger, string, Exception?> _logClientCleanedUp =
        LoggerMessage.Define<string>(
            LogLevel.Information,
            new EventId(7, nameof(CleanupClient)),
            "Client {ClientId} cleaned up");

    public ConcurrentDictionary<string, ClientSession> Clients => _clients;
    public FrequencyChannelManager ChannelManager => _channelManager;

    public SignalingServer(ServerConfig config, ILoggerFactory loggerFactory)
    {
        _config = config;
        _logger = loggerFactory.CreateLogger<SignalingServer>();
        _loggerFactory = loggerFactory;
        _audioServer = new AudioStreamServer(_channelManager, _clients, loggerFactory, config.AudioBasePort);

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
            KeepAliveInterval = TimeSpan.FromMinutes(1),
            // Allow messages up to 64KB
            ReceiveBufferSize = 64 * 1024
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
        _logServerStarted(_logger, _config.WebSocketPort, null);
    
        try
        {
            await _app!.StartAsync(_cts.Token);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to start server");
            throw;
        }
    }

    private async Task HandleWebSocketConnection(WebSocket webSocket, HttpContext httpContext)
    {
        string clientId = Guid.NewGuid().ToString();

        try
        {
            // Optional: Log client IP for diagnostics
            var remoteIp = httpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown";
            _logger.LogDebug("Client {ClientId} connecting from {RemoteIp}", clientId, remoteIp);

            var session = new ClientSession(clientId, webSocket);
            _clients[clientId] = session;

            _logClientConnected(_logger, clientId, null);

            await HandleClientMessages(session);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error in WebSocket connection for client {ClientId}", clientId);
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
                    await session.WebSocket.CloseAsync(
                        WebSocketCloseStatus.NormalClosure,
                        "Closing",
                        CancellationToken.None);
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
            // Client disconnected abruptly - normal behavior
            _logger.LogDebug("Client {ClientId} disconnected abruptly", session.Id);
        }
        catch (OperationCanceledException)
        {
            // Server shutting down
            _logger.LogDebug("Client {ClientId} connection cancelled during shutdown", session.Id);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unexpected error handling messages for client {ClientId}", session.Id);
        }
    }

    private async Task ProcessMessage(ClientSession session, string messageText)
    {
        try
        {
            var message = Json.Instance.Deserialize<SignalingMessage>(messageText);
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

                default:
                    if (_logger.IsEnabled(LogLevel.Warning))
                        _logger.LogWarning("Unknown message type: {MessageType}", message.Type);
                    break;
            }
        }
        catch (JsonException ex)
        {
            _logger.LogWarning(ex, "Invalid JSON from client {ClientId}", session.Id);
            await SendError(session, "Invalid message format");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error processing message from client {ClientId}", session.Id);
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

        if (string.IsNullOrEmpty(_config.ServerPassword) ||
            authMsg.Password == _config.ServerPassword)
        {
            session.IsAuthenticated = true;

            var audioPort = _audioServer.CreateAudioSession(session.Id).Result;

            _logClientAuthenticated(_logger, session.Id, audioPort, null);

            await SendSuccess(session, "Authenticated", session.Id, audioPort, _config.EnableOpusCompression);
        }
        else
        {
            await SendError(session, "Authentication failed");
            
            // Optional: Disconnect on auth failure
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

        _channelManager.JoinChannel(joinMsg.FrequencyKhz, session.Id);
        
        session.CurrentFrequencies.TryAdd(joinMsg.FrequencyKhz, ClientSession.FrequencyClientStatus.Receiving);

        var peers = _channelManager.GetClientsInChannel(joinMsg.FrequencyKhz)
            .Where(id => id != session.Id)
            .ToList();

        await SendChannelState(session, joinMsg.FrequencyKhz, peers);

        BroadcastToChannel(
            joinMsg.FrequencyKhz,
            session.Id,
            SignalingMessageFactory.CreatePeerJoined(session.Id, joinMsg.FrequencyKhz));

        _logClientJoinedFrequency(_logger, session.Id, joinMsg.FrequencyKhz/1000d, null);
    }

    private async Task HandleLeaveChannel(ClientSession session, SignalingMessage message)
    {
        await LeaveCurrentChannel(session, message);
    }

    private async Task LeaveCurrentChannel(ClientSession session, SignalingMessage message)
    {
        if (session.CurrentFrequencies.Count == 0) return;

        var transmissionMsg = SignalingMessageFactory.DeserializePayload<AudioTransmissionMessage>(message.Payload);
        if (transmissionMsg == null) return;

        var frequencyKhz = transmissionMsg.FrequencyKhz;
        _channelManager.LeaveChannel(frequencyKhz, session.Id);
        
        BroadcastToChannel(
            frequencyKhz,
            session.Id,
            SignalingMessageFactory.CreatePeerLeft(session.Id, frequencyKhz));

        session.CurrentFrequencies.TryRemove(frequencyKhz, out var frequencyClientStatus);
        _logClientLeftFrequency(_logger, session.Id, frequencyKhz/1000d, null);
    }

    private async Task LeaveAllChannels(ClientSession session)
    {
        var frequencies = session.CurrentFrequencies.Keys.ToArray();
        
        foreach (var frequency in frequencies)
        {
            _channelManager.LeaveChannel(frequency, session.Id);

            BroadcastToChannel(
                frequency,
                session.Id,
                SignalingMessageFactory.CreatePeerLeft(session.Id, frequency));

            session.CurrentFrequencies.TryRemove(frequency, out var frequencyClientStatus);
            _logClientLeftFrequency(_logger, session.Id, frequency/1000d, null);
        }
    }

    private async Task HandleTransmission(ClientSession session, SignalingMessage message)
    {
        if (!session.IsAuthenticated) return;
        if (session.CurrentFrequencies.Count == 0) return;

        var transmissionMsg = SignalingMessageFactory.DeserializePayload<AudioTransmissionMessage>(message.Payload);
        if (transmissionMsg == null) return;

        if (session.CurrentFrequencies.TryGetValue(transmissionMsg.FrequencyKhz,
                out ClientSession.FrequencyClientStatus frequencyStatus))
        {
            session.CurrentFrequencies.TryUpdate(transmissionMsg.FrequencyKhz,
                transmissionMsg.Transmitting
                    ? ClientSession.FrequencyClientStatus.Transmitting
                    : ClientSession.FrequencyClientStatus.Receiving, frequencyStatus);
        }
        else return;

        if (!session.CurrentFrequencies.ContainsKey(transmissionMsg.FrequencyKhz)) return;

        var peersInChannel = _channelManager.GetClientsInChannel(transmissionMsg.FrequencyKhz)
            .Where(id => id != session.Id)
            .ToArray();

        _logTransmissionState(_logger, session.Id, transmissionMsg.Transmitting, 
            transmissionMsg.FrequencyKhz/1000d, peersInChannel.Length, null);

        BroadcastToChannel(
            transmissionMsg.FrequencyKhz,
            session.Id,
            SignalingMessageFactory.CreateTransmissionEvent(
                session.Id,
                transmissionMsg.FrequencyKhz,
                transmissionMsg.Transmitting));
    }

    private void BroadcastToChannel(int frequencyKhz, string excludeClientId, SignalingMessage message)
    {
        var clients = _channelManager.GetClientsInChannel(frequencyKhz);

        foreach (var clientId in clients)
        {
            if (clientId == excludeClientId) continue;

            if (_clients.TryGetValue(clientId, out var session))
            {
                _ = SendToClient(session, message)
                    .ContinueWith(t =>
                    {
                        if (!t.IsFaulted || t.Exception == null) return;
                        var ex = t.Exception.GetBaseException();
                        _logger.LogError(ex, "Error broadcasting to client {ClientId} in channel {Frequency/1000d:F3}", 
                            clientId, frequencyKhz);
                    }, TaskScheduler.Default);

                if (_logger.IsEnabled(LogLevel.Debug))
                    _logger.LogDebug("Broadcasting {MessageType} to client {ClientId} in channel {Frequency/1000d:F3}", 
                        message.Type, clientId, frequencyKhz);
            }
        }
    }

    private async Task SendToClient(ClientSession session, SignalingMessage message)
    {
        if (session.WebSocket.State == WebSocketState.Open)
        {
            var json = Json.Instance.Serialize(message);
            var buffer = Encoding.UTF8.GetBytes(json);
            await session.WebSocket.SendAsync(
                new ArraySegment<byte>(buffer),
                WebSocketMessageType.Text,
                true,
                CancellationToken.None);
        }
    }

    private async Task SendError(ClientSession session, string error)
    {
        await SendToClient(session, SignalingMessageFactory.CreateError(error));
    }

    private async Task SendSuccess(ClientSession session, string message, string? peerId = null, int? audioPort = null, bool opusEnabled = true)
    {
        await SendToClient(session, SignalingMessageFactory.CreateSuccess(message, peerId, audioPort, opusEnabled));
    }

    private async Task SendChannelState(ClientSession session, int frequencyKhz, List<string> peers)
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
                    await session.WebSocket.CloseAsync(
                        WebSocketCloseStatus.NormalClosure,
                        "Cleanup",
                        CancellationToken.None);
                }
                catch (WebSocketException)
                {
                    // Already closed, ignore
                }
            }

            session.WebSocket.Dispose();

            _logClientCleanedUp(_logger, clientId, null);
        }
    }

    public async Task StopAsync()
    {
        _logger.LogInformation("Stopping signaling server...");
    
        // Stop audio server first
        _audioServer.Stop();
    
        // Cancel the CTS
        _cts.Cancel();
    
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