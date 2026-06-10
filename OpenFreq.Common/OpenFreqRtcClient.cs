using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using OpenFreq.Common.Signaling;
using OpenFreqAudio;

namespace OpenFreq.Common;

public class OpenFreqRtcClient : IRtcClient
{
    // Audio configuration constants
    public const int SAMPLE_RATE = RadioPlayback.SampleRate;
    public const int FRAME_SIZE_MS = 20;
    public const int OPUS_SAMPLES_PER_FRAME = SAMPLE_RATE / (1000 / FRAME_SIZE_MS);
    public const int DEFAULT_PORT = 9987;

    // Events for UI integration
    public event EventHandler<ConnectionStateChangedEventArgs>? ConnectionStateChanged;
    public event EventHandler<AuthenticationEventArgs>? Authenticated;
    public event EventHandler<FrequencyJoinedEventArgs>? FrequencyJoined;
    public event EventHandler<FrequencyLeftEventArgs>? FrequencyLeft;
    public event EventHandler<PeerEventArgs>? PeerJoined;
    public event EventHandler<PeerEventArgs>? PeerLeft;
    public event EventHandler<TransmissionStateEventArgs>? TransmissionStateChanged;
    public event EventHandler<PeerTransmissionEventArgs>? PeerTransmissionStateChanged;
    public event EventHandler<AudioDataEventArgs>? AudioDataReceived;
    public event EventHandler<AllPeersStatusEventArgs>? AllPeersStatusUpdateReceived;
    public event EventHandler<ErrorEventArgs>? ErrorOccurred;

    private RtpAudioReceiver? _rtpReceiver;
    private RtpAudioSender? _rtpSender;

    // set by the server
    private bool _opusCompressionEnabled = true;

    // Connection state
    public string ServerIp { get; }
    private readonly string _password;
    private ClientWebSocket? _webSocket;
    private CancellationTokenSource _cts = new();
    private volatile bool _authFailed;

    // Set when the caller deliberately tears down the connection (DisconnectAsync/Dispose)
    // so the receive loop does not try to auto-reconnect a connection we closed on purpose
    private volatile bool _intentionalDisconnect;

    // On an unexpected drop the client retries the full connect+auth+rejoin sequence until
    // it succeeds or this total budget elapses, after which it gives up and reports Disconnected.
    private static readonly TimeSpan ReconnectTotalBudget = TimeSpan.FromSeconds(60);

    // Transmission state
    private readonly Dictionary<int, bool> _frequencyTransmissionState = new();
    private readonly Dictionary<int, HashSet<string>> _frequencyPeers = new();

    private readonly ILogger<OpenFreqRtcClient> _logger;
    private readonly ILoggerFactory _loggerFactory;

    // Properties
    public string? MyPeerId { get; private set; }

    public string? MyDisplayName { get; }

    public int AudioPort { get; private set; }

    public bool IsConnected { get; private set; }

    public bool IsAuthenticated { get; private set; }

    public OpenFreqRtcClient(ILoggerFactory loggerFactory, string serverIp, string password, string? myDisplayName)
    {
        _loggerFactory = loggerFactory;
        _logger = loggerFactory.CreateLogger<OpenFreqRtcClient>();
        ServerIp = serverIp;
        _password = password;
        MyDisplayName = myDisplayName;
    }

    /// <summary>
    /// Connect to the OpenFreq server and authenticate
    /// </summary>
    public async Task ConnectAsync(TimeSpan? connectTimeout = null)
    {
        _intentionalDisconnect = false;
        try
        {
            await ConnectInternalAsync(connectTimeout);
        }
        catch (Exception ex)
        {
            IsConnected = false;
            CleanupRtp();
            if (_webSocket?.State == WebSocketState.Open)
                await _webSocket.CloseAsync(WebSocketCloseStatus.NormalClosure, "Connection failed", CancellationToken.None);
            OnConnectionStateChanged(ConnectionState.Disconnected, DisconnectReason.ConnectionFailed);
            OnError($"Connection failed: {ex.Message}");
            throw;
        }
    }

    /// <summary>
    /// Establishes the WebSocket, authenticates and brings up the RTP pipeline.
    /// Throws on any failure and leaves cleanup to the caller; used by both the initial
    /// <see cref="ConnectAsync"/> and the auto-reconnect loop.
    /// </summary>
    private async Task ConnectInternalAsync(TimeSpan? connectTimeout = null)
    {
        _cts.Dispose();
        _cts = new CancellationTokenSource();
        _authFailed = false;

        var timeout = connectTimeout ?? TimeSpan.FromSeconds(10);
        using var connectCts = CancellationTokenSource.CreateLinkedTokenSource(_cts.Token);
        connectCts.CancelAfter(timeout);

        // Connect WebSocket
        var ipPort = Util.ResolveAddress(ServerIp, DEFAULT_PORT);

        // we need to wrap IPv6 into [] for a valid URI
        IPAddress? ip;
        if (IPAddress.TryParse(ipPort.ipAddress, out ip))
        {
            if (ip.AddressFamily == AddressFamily.InterNetworkV6)
            {
                ipPort.ipAddress = $"[{ipPort.ipAddress}]"; // Wrap IPv6 address in square brackets
            }
        }

        _webSocket = new ClientWebSocket();
        // Detect a silently dead link
        _webSocket.Options.KeepAliveInterval = TimeSpan.FromSeconds(3);
        _webSocket.Options.KeepAliveTimeout = TimeSpan.FromSeconds(3);
        await _webSocket.ConnectAsync(new Uri($"ws://{ipPort.ipAddress}:{ipPort.port}"), connectCts.Token);

        // Start message receiver
        _ = Task.Run(ReceiveMessagesAsync, _cts.Token);

        // Authenticate
        await SendMessageAsync(SignalingMessageFactory.CreateAuthenticate(_password, MyDisplayName));

        // Wait for authentication response with timeout
        var startTime = DateTime.UtcNow;
        while (!IsAuthenticated && !_authFailed && (DateTime.UtcNow - startTime) < timeout)
        {
            await Task.Delay(100, connectCts.Token);
        }

        if (_authFailed)
            throw new System.Security.Authentication.AuthenticationException("Bad password");

        if (!IsAuthenticated)
            throw new TimeoutException("Authentication timeout");

        if (string.IsNullOrEmpty(MyPeerId))
        {
            throw new Exception("No PeerId assigned by server, aborting");
        }
        // Create RTP sender
        _rtpSender = new RtpAudioSender(
            logger: _loggerFactory.CreateLogger<RtpAudioSender>(),
            serverHost: ipPort.ipAddress,
            serverPort: AudioPort,
            clid: MyPeerId!,
            opusEnabled: _opusCompressionEnabled
        );

        _rtpReceiver = new RtpAudioReceiver(_loggerFactory,
            udpClient: _rtpSender.UdpClient,
            opusEnabled: _opusCompressionEnabled,
            initialBufferMs: 150
        );

        // Subscribe to clean audio events
        _rtpReceiver.AudioReceived += OnRtpAudioReceived;
        _rtpReceiver.ErrorOccurred += (_, error) => { _logger.LogError("RTP Error: {Error}", error); };

        IsConnected = true;
        OnConnectionStateChanged(ConnectionState.Connected);
    }

    /// <summary>
    /// Attempts to transparently re-establish a connection that dropped unexpectedly.
    /// Retries connect+auth+rejoin until it succeeds or <see cref="ReconnectTotalBudget"/>
    /// elapses; reports <see cref="ConnectionState.Connecting"/> while trying and falls back
    /// to <see cref="ConnectionState.Disconnected"/> if the budget runs out.
    /// </summary>
    private async Task ReconnectAsync()
    {
        var deadline = DateTime.UtcNow + ReconnectTotalBudget;
        var joinedFrequencies = _frequencyTransmissionState.Keys.ToList();
        var attempt = 0;

        OnConnectionStateChanged(ConnectionState.Connecting);

        // Initial jitter: a server-side drop knocks the whole flight offline at once. Without
        // this they all retry on the same tick and stampede the server back down. Spread the
        // first attempt over a few seconds so reconnects fan out.
        var initialJitter = TimeSpan.FromMilliseconds(Random.Shared.Next(0, 3000));
        if (initialJitter < deadline - DateTime.UtcNow)
        {
            try { await Task.Delay(initialJitter); }
            catch (OperationCanceledException) { }
        }

        while (!_intentionalDisconnect && DateTime.UtcNow < deadline)
        {
            attempt++;
            var remaining = deadline - DateTime.UtcNow;
            var connectTimeout = remaining < TimeSpan.FromSeconds(10) ? remaining : TimeSpan.FromSeconds(10);

            try
            {
                await ConnectInternalAsync(connectTimeout);

                // Channel membership lives on the server and is dropped when the socket dies,
                // so re-send a join for every frequency we were on before the drop.
                foreach (var frequencyKhz in joinedFrequencies)
                    await SendMessageAsync(SignalingMessageFactory.CreateJoin(frequencyKhz));

                _logger.LogInformation(
                    "Reconnected after {Attempts} attempt(s); rejoined {Count} frequency(ies)",
                    attempt, joinedFrequencies.Count);
                return;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Reconnect attempt {Attempt} failed", attempt);
                CleanupRtp();
            }

            // Linear backoff capped at 5s, jittered +/-50% so retries stay de-synchronized
            // across clients, never sleeping past the overall deadline.
            var timeLeft = deadline - DateTime.UtcNow;
            if (timeLeft <= TimeSpan.Zero || _intentionalDisconnect) break;
            var baseBackoff = Math.Min(5.0, attempt);
            var backoff = TimeSpan.FromSeconds(baseBackoff * (0.5 + Random.Shared.NextDouble()));
            try { await Task.Delay(backoff < timeLeft ? backoff : timeLeft); }
            catch (OperationCanceledException) { break; }
        }

        IsConnected = false;
        IsAuthenticated = false;
        CleanupRtp();
        SafeNotifyDisconnected(_intentionalDisconnect ? DisconnectReason.UserRequested : DisconnectReason.ConnectionLost);
        if (!_intentionalDisconnect)
            OnError($"Lost connection to server — reconnect failed after {ReconnectTotalBudget.TotalSeconds:F0}s");
    }

    private void CleanupRtp()
    {
        _rtpSender?.Dispose();
        _rtpSender = null;

        _rtpReceiver?.AudioReceived -= OnRtpAudioReceived;
        _rtpReceiver?.Dispose();
        _rtpReceiver = null;

        _logger.LogDebug("RTP Closed");
    }

    /// <summary>
    /// Join a frequency channel
    /// </summary>
    public async Task JoinFrequencyAsync(int frequencyKhz)
    {
        if (!IsAuthenticated)
        {
            throw new InvalidOperationException("Not authenticated");
        }

        await SendMessageAsync(SignalingMessageFactory.CreateJoin(frequencyKhz));

        if (!_frequencyPeers.ContainsKey(frequencyKhz))
        {
            _frequencyPeers[frequencyKhz] = new HashSet<string>();
        }

        _frequencyTransmissionState[frequencyKhz] = false;
    }

    /// <summary>
    /// Leave a frequency channel
    /// </summary>
    public async Task LeaveFrequencyAsync(int frequencyKhz)
    {
        if (!IsAuthenticated)
        {
            throw new InvalidOperationException("Not authenticated");
        }

        await SendMessageAsync(SignalingMessageFactory.CreateLeave(frequencyKhz));

        _frequencyPeers.Remove(frequencyKhz);
        _frequencyTransmissionState.Remove(frequencyKhz);
        OnFrequencyLeft(frequencyKhz);
    }

    /// <summary>
    /// Start transmitting on a frequency
    /// </summary>
    public async Task StartTransmissionAsync(int frequencyKhz, bool is3d)
    {
        if (!IsAuthenticated)
        {
            throw new InvalidOperationException("Not authenticated");
        }

        if (!_frequencyTransmissionState.ContainsKey(frequencyKhz))
        {
            throw new InvalidOperationException($"Not joined on frequency {frequencyKhz}");
        }

        _frequencyTransmissionState[frequencyKhz] = true;

        await SendMessageAsync(SignalingMessageFactory.CreateTransmission(frequencyKhz, true, is3d));
        OnTransmissionStateChanged(frequencyKhz, true);

        // Start heartbeat for this frequency
        _ = Task.Run(() => TransmissionHeartbeatAsync(frequencyKhz, is3d), _cts.Token);
    }

    /// <summary>
    /// Stop transmitting on a frequency
    /// </summary>
    public async Task StopTransmissionAsync(int frequencyKhz, bool is3d)
    {
        if (!IsAuthenticated)
        {
            throw new InvalidOperationException("Not authenticated");
        }

        if (!_frequencyTransmissionState.ContainsKey(frequencyKhz))
        {
            return;
        }

        _frequencyTransmissionState[frequencyKhz] = false;

        await SendMessageAsync(SignalingMessageFactory.CreateTransmission(frequencyKhz, false, is3d));
        OnTransmissionStateChanged(frequencyKhz, false);
    }

    /// <summary>
    /// Notifies the server of the local client's current 3D mode.
    /// The server updates its state and broadcasts AllPeersStatusMessage to all clients.
    /// </summary>
    public async Task SendModeUpdateAsync(bool is3d)
    {
        if (!IsAuthenticated) return;
        await SendMessageAsync(SignalingMessageFactory.CreateModeUpdate(is3d));
    }

    /// <summary>
    /// Sets the Display Name (=Nickname)
    /// </summary>
    public async Task SetDisplayNameAsync(string displayName)
    {
        if (!IsAuthenticated)
        {
            throw new InvalidOperationException("Not authenticated");
        }

        await SendMessageAsync(SignalingMessageFactory.CreateSetDisplayName(displayName));
    }

    /// <summary>
    /// Marks the start time of a TX
    /// </summary>
    /// <see cref="RtpAudioSender.MarkTransmitStartTime"/>
    public void MarkTransmitStartTime()
    {
        _rtpSender!.MarkTransmitStartTime();
    }

    public void SendAudio(Memory<short> pcmData, List<(int frequencyKhz, double txPowerWatts, double ppm, Vector3? position, Vector3? velocity, AmbientNoiseType ambientNoiseType)> frequencies, bool in3d)
    {
        var frequencyTransmissions = new List<FrequencyTransmission>();
        foreach (var freq in frequencies)
        {
            frequencyTransmissions.Add(new FrequencyTransmission(
                khz: freq.frequencyKhz,
                txPowerWatts: freq.txPowerWatts,
                ppm: freq.ppm,
                position: freq.position,
                velocity: freq.velocity,
                ambientNoiseType: freq.ambientNoiseType,
                in3d: in3d
            ));
        }

        _rtpSender?.SendAudio(pcmData, frequencyTransmissions);
    }


    /// <summary>
    /// Disconnect from the server
    /// </summary>
    public async Task DisconnectAsync()
    {
        _intentionalDisconnect = true;
        CleanupRtp();
        _cts.Cancel();

        if (_webSocket?.State == WebSocketState.Open)
        {
            await _webSocket.CloseAsync(WebSocketCloseStatus.NormalClosure, "Client disconnecting",
                CancellationToken.None);
        }

        IsConnected = false;
        IsAuthenticated = false;
        OnConnectionStateChanged(ConnectionState.Disconnected, DisconnectReason.UserRequested);
    }

    private async Task TransmissionHeartbeatAsync(int frequencyKhz, bool is3d)
    {
        while (!_cts.Token.IsCancellationRequested)
        {
            if (!_frequencyTransmissionState.TryGetValue(frequencyKhz, out var isTransmitting) || !isTransmitting)
            {
                break;
            }

            await SendMessageAsync(SignalingMessageFactory.CreateTransmission(frequencyKhz, true, is3d));
            await Task.Delay(333, _cts.Token); // ~3 times per second
        }
    }

    private void OnRtpAudioReceived(object? sender, RtpAudioReceiver.AudioReceivedEventArgs e)
    {
        OnAudioDataReceived(e.Metadata.ClientId, e.AudioData, e.Metadata);
    }

    private async Task ReceiveMessagesAsync()
    {
        if (_webSocket == null) return;

        var buffer = new byte[8192];
        var messageBuffer = new StringBuilder();

        try
        {
            while (_webSocket.State == WebSocketState.Open && !_cts.Token.IsCancellationRequested)
            {
                var result = await _webSocket.ReceiveAsync(new ArraySegment<byte>(buffer), _cts.Token);

                if (result.MessageType == WebSocketMessageType.Close)
                {
                    await _webSocket.CloseAsync(WebSocketCloseStatus.NormalClosure, "Server closing",
                        CancellationToken.None);
                    CleanupRtp();
                    IsConnected = false;
                    IsAuthenticated = false;
                    // Server closed the socket on us — treat as a lost connection unless we
                    // were already tearing down deliberately.
                    OnConnectionStateChanged(ConnectionState.Disconnected,
                        _intentionalDisconnect ? DisconnectReason.UserRequested : DisconnectReason.ConnectionLost);
                    break;
                }

                if (result.MessageType == WebSocketMessageType.Text)
                {
                    messageBuffer.Append(Encoding.UTF8.GetString(buffer, 0, result.Count));

                    if (result.EndOfMessage)
                    {
                        var message = messageBuffer.ToString();
                        messageBuffer.Clear();
                        HandleMessage(message);
                    }
                }
            }
        }
        catch (OperationCanceledException) when (_cts.IsCancellationRequested || _intentionalDisconnect)
        {
            // Expected: we cancelled the receive loop during a deliberate shutdown/disconnect.
            // A cancellation NOT originating from us (e.g. a keep-alive timeout abort surfacing
            // as OperationCanceledException) deliberately falls through to the handler below.
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "WebSocket receive loop terminated unexpectedly");
            CleanupRtp();
            IsConnected = false;
            IsAuthenticated = false;

            if (_intentionalDisconnect || _cts.IsCancellationRequested)
            {
                SafeNotifyDisconnected(DisconnectReason.UserRequested);
                return;
            }

            // Unexpected drop — try to recover transparently rather than dumping the user
            // back to a disconnected state. Run on a detached task: this receive loop is
            // ending and ReconnectAsync replaces _cts / starts a fresh receive loop.
            _logger.LogInformation("Connection lost unexpectedly — reconnecting for up to {Seconds:F0}s",
                ReconnectTotalBudget.TotalSeconds);
            _ = Task.Run(ReconnectAsync);
        }
    }

    private void SafeNotifyDisconnected(DisconnectReason reason)
    {
        try
        {
            OnConnectionStateChanged(ConnectionState.Disconnected, reason);
        }
        catch (Exception notifyEx)
        {
            _logger.LogError(notifyEx, "Failed to notify disconnected state — client may appear stuck");
        }
    }

    private void HandleMessage(string json)
    {
        try
        {
            var message = JsonSerializer.Deserialize(json, OpenFreqJsonContext.Default.SignalingMessage);
            if (message == null) return;

            switch (message.Type)
            {
                case SignalingMessageTypes.Success:
                    var success = SignalingMessageFactory.DeserializePayload<SuccessMessage>(message.Payload);
                    if (success?.PeerId != null)
                    {
                        MyPeerId = success.PeerId;
                        AudioPort = success.AudioPort ?? 0;
                        IsAuthenticated = true;
                        _opusCompressionEnabled = success.OpusCompressionEnabled;
                        _logger.LogDebug("Opus compression enabled: " + _opusCompressionEnabled);
                        OnConnectionStateChanged(ConnectionState.Authenticated);
                        OnAuthenticated(MyPeerId, success.FrequenciesPeers, AudioPort);
                    }

                    break;

                case SignalingMessageTypes.Error:
                    var error = SignalingMessageFactory.DeserializePayload<ErrorMessage>(message.Payload);
                    if (error != null)
                    {
                        if (!IsAuthenticated)
                            _authFailed = true;
                        OnError(error.Error);
                    }

                    break;

                case SignalingMessageTypes.PeerJoined:
                    var joined = SignalingMessageFactory.DeserializePayload<PeerJoinedMessage>(message.Payload);
                    if (joined != null)
                    {
                        if (_frequencyPeers.TryGetValue(joined.FrequencyKhz, out var peers))
                        {
                            peers.Add(joined.PeerId);
                        }

                        OnPeerJoined(joined.PeerId, joined.PeerDisplayName, joined.FrequencyKhz);
                    }

                    break;

                case SignalingMessageTypes.PeerLeft:
                    var left = SignalingMessageFactory.DeserializePayload<PeerLeftMessage>(message.Payload);
                    if (left != null)
                    {
                        if (_frequencyPeers.TryGetValue(left.FrequencyKhz, out var peers))
                        {
                            peers.Remove(left.PeerId);
                        }

                        OnPeerLeft(left.PeerId, left.FrequencyKhz);
                    }

                    break;

                case SignalingMessageTypes.Transmission:
                    var transmission =
                        SignalingMessageFactory.DeserializePayload<TransmissionEventMessage>(message.Payload);
                    if (transmission != null && transmission.PeerId != MyPeerId)
                    {
                        OnPeerTransmissionStateChanged(transmission.PeerId, transmission.PeerDisplayName,
                            transmission.FrequencyKhz,
                            transmission.Transmitting, transmission.Is3d);
                    }

                    break;

                case SignalingMessageTypes.ChannelState:
                    var channelState = SignalingMessageFactory.DeserializePayload<ChannelStateMessage>(message.Payload);
                    if (channelState != null)
                    {
                        _frequencyPeers[channelState.FrequencyKhz] =
                            new HashSet<string>(channelState.Peers.Select(p => p.Id).ToList());
                        OnFrequencyJoined(channelState.FrequencyKhz, channelState.Peers);
                    }

                    break;

                case SignalingMessageTypes.AllPeersStatus:
                    var allPeersStatusMsg =
                        SignalingMessageFactory.DeserializePayload<AllPeersStatusMessage>(message.Payload);
                    if (allPeersStatusMsg != null)
                    {
                        OnAllPeersStatusReceived(allPeersStatusMsg.FrequenciesPeers);
                    }

                    break;
            }
        }
        catch (Exception ex)
        {
            OnError($"Error handling message: {ex.Message}");
        }
    }


    public async Task SendMessageAsync(SignalingMessage message)
    {
        if (_webSocket?.State != WebSocketState.Open) return;

        try
        {
            var json = JsonSerializer.Serialize(message, OpenFreqJsonContext.Default.SignalingMessage);
            var buffer = Encoding.UTF8.GetBytes(json);
            await _webSocket.SendAsync(new ArraySegment<byte>(buffer), WebSocketMessageType.Text, true, _cts.Token);
        }
        catch (Exception ex)
        {
            OnError($"Error sending message: {ex.Message}");
        }
    }

    // Event raising methods
    private void OnConnectionStateChanged(ConnectionState state, DisconnectReason reason = DisconnectReason.None) =>
        ConnectionStateChanged?.Invoke(this, new ConnectionStateChangedEventArgs(state, reason));

    private void OnAuthenticated(string peerId, SortedDictionary<int, List<PeerData>> peers, int audioPort) =>
        Authenticated?.Invoke(this, new AuthenticationEventArgs(peerId, peers, audioPort));

    private void OnFrequencyJoined(int frequencyKhz, List<ChannelStateMessage.Peer> peers) =>
        FrequencyJoined?.Invoke(this, new FrequencyJoinedEventArgs(frequencyKhz, peers));

    private void OnFrequencyLeft(int frequencyKhz) =>
        FrequencyLeft?.Invoke(this, new FrequencyLeftEventArgs(frequencyKhz));

    private void OnPeerJoined(string peerId, string peerDisplayName, int frequencyKhz) =>
        PeerJoined?.Invoke(this, new PeerEventArgs(peerId, peerDisplayName, frequencyKhz));

    private void OnPeerLeft(string peerId, int frequencyKhz) =>
        PeerLeft?.Invoke(this, new PeerEventArgs(peerId, null, frequencyKhz));

    private void OnTransmissionStateChanged(int frequencyKhz, bool isTransmitting) =>
        TransmissionStateChanged?.Invoke(this, new TransmissionStateEventArgs(frequencyKhz, isTransmitting));

    private void OnPeerTransmissionStateChanged(string peerId, string peerDisplayName, int frequencyKhz,
        bool isTransmitting, bool is3d) =>
        PeerTransmissionStateChanged?.Invoke(this,
            new PeerTransmissionEventArgs(peerId, peerDisplayName, frequencyKhz, isTransmitting, is3d));

    private void OnAudioDataReceived(string peerId, Memory<short> audioData, AudioPacketMetadata metadata) =>
        AudioDataReceived?.Invoke(this, new AudioDataEventArgs(peerId, audioData, metadata));

    private void OnAllPeersStatusReceived(SortedDictionary<int, List<PeerData>> allPeersStatus) =>
        AllPeersStatusUpdateReceived?.Invoke(this, new AllPeersStatusEventArgs(allPeersStatus));


    private void OnError(string errorMessage) =>
        ErrorOccurred?.Invoke(this, new ErrorEventArgs(errorMessage));

    public void Dispose()
    {
        _intentionalDisconnect = true;
        CleanupRtp();
        _cts.Cancel();
        _cts.Dispose();

        if (_webSocket?.State == WebSocketState.Open)
        {
            _webSocket.CloseAsync(WebSocketCloseStatus.NormalClosure, "Disposing", CancellationToken.None).Wait(1000);
        }

        _webSocket?.Dispose();
    }
}

// Event argument classes
public enum ConnectionState
{
    Disconnected,
    Connecting,
    Connected,
    Authenticated
}

/// <summary>
/// Why a transition to <see cref="ConnectionState.Disconnected"/> happened. Only meaningful
/// for the Disconnected state; other states report <see cref="DisconnectReason.None"/>.
/// </summary>
public enum DisconnectReason
{
    None,

    /// <summary>The caller deliberately disconnected (DisconnectAsync/Dispose). Not an error.</summary>
    UserRequested,

    /// <summary>An established connection dropped and could not be recovered.</summary>
    ConnectionLost,

    /// <summary>The initial connect/authentication attempt failed.</summary>
    ConnectionFailed
}

public class ConnectionStateChangedEventArgs : EventArgs
{
    public ConnectionState State { get; }
    public DisconnectReason Reason { get; }
    public ConnectionStateChangedEventArgs(ConnectionState state, DisconnectReason reason = DisconnectReason.None)
    {
        State = state;
        Reason = reason;
    }
}

public class AuthenticationEventArgs : EventArgs
{
    public string PeerId { get; }
    public int AudioPort { get; }

    public SortedDictionary<int, List<PeerData>> Peers { get; }

    public AuthenticationEventArgs(string peerId, SortedDictionary<int, List<PeerData>> peers, int audioPort)
    {
        PeerId = peerId;
        Peers = peers;
        AudioPort = audioPort;
    }
}

public class FrequencyJoinedEventArgs(int frequencyKhz, List<ChannelStateMessage.Peer> peers) : EventArgs
{
    public List<ChannelStateMessage.Peer> Peers { get; } = peers;
    public int FrequencyKhz { get; } = frequencyKhz;
}

public class FrequencyLeftEventArgs : EventArgs
{
    public int FrequencyKhz { get; }
    public FrequencyLeftEventArgs(int frequencyKhz) => FrequencyKhz = frequencyKhz;
}

public class PeerEventArgs : EventArgs
{
    public string PeerId { get; }

    public string? PeerDisplayName { get; }
    public int FrequencyKhz { get; }

    public PeerEventArgs(string peerId, string? peerDisplayName, int frequencyKhz)
    {
        PeerId = peerId;
        PeerDisplayName = peerDisplayName;
        FrequencyKhz = frequencyKhz;
    }
}

public class TransmissionStateEventArgs : EventArgs
{
    public int FrequencyKhz { get; }
    public bool IsTransmitting { get; }

    public TransmissionStateEventArgs(int frequencyKhz, bool isTransmitting)
    {
        FrequencyKhz = frequencyKhz;
        IsTransmitting = isTransmitting;
    }
}

public class PeerTransmissionEventArgs : EventArgs
{
    public string PeerId { get; }
    public string PeerDisplayName { get; }

    public int FrequencyKhz { get; }
    public bool IsTransmitting { get; }
    public bool Is3d { get; }

    public PeerTransmissionEventArgs(string peerId, string peerDisplayName, int frequencyKhz, bool isTransmitting, bool is3d)
    {
        PeerId = peerId;
        PeerDisplayName = peerDisplayName;
        FrequencyKhz = frequencyKhz;
        IsTransmitting = isTransmitting;
        Is3d = is3d;
    }
}

public class AudioDataEventArgs : EventArgs
{
    public string PeerId { get; }
    public Memory<short> AudioData { get; }
    public AudioPacketMetadata Metadata { get; }

    public AudioDataEventArgs(string peerId, Memory<short> audioData, AudioPacketMetadata metadata)
    {
        PeerId = peerId;
        AudioData = audioData;
        Metadata = metadata;
    }
}

public class AllPeersStatusEventArgs : EventArgs
{
    public SortedDictionary<int, List<PeerData>> AllPeers { get; }

    public AllPeersStatusEventArgs(SortedDictionary<int, List<PeerData>> allPeers)
    {
        AllPeers = allPeers;
    }
}

public class ErrorEventArgs : EventArgs
{
    public string ErrorMessage { get; }
    public ErrorEventArgs(string errorMessage) => ErrorMessage = errorMessage;
}
