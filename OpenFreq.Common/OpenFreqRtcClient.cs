using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using OpenFreq.Common.Signaling;
using OpenFreqAudio;

namespace OpenFreq.Common;

public class OpenFreqRtcClient : IDisposable
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
    public readonly string ServerIp;
    private readonly string _password;
    private ClientWebSocket? _webSocket;
    private CancellationTokenSource _cts = new();

    // Transmission state
    private readonly Dictionary<int, bool> _frequencyTransmissionState = new();
    private readonly Dictionary<int, bool> _frequencyFirstPacketSent = new();
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
    public async Task ConnectAsync()
    {
        try
        {
            _cts.Dispose();
            _cts = new CancellationTokenSource();

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
            await _webSocket.ConnectAsync(new Uri($"ws://{ipPort.ipAddress}:{ipPort.port}"), _cts.Token);

            // Start message receiver
            _ = Task.Run(ReceiveMessagesAsync, _cts.Token);

            // Authenticate
            await SendMessageAsync(SignalingMessageFactory.CreateAuthenticate(_password, MyDisplayName));

            // Wait for authentication response with timeout
            var startTime = DateTime.UtcNow;
            while (!IsAuthenticated && (DateTime.UtcNow - startTime).TotalSeconds < 5)
            {
                await Task.Delay(100, _cts.Token);
            }

            if (!IsAuthenticated)
            {
                throw new TimeoutException("Authentication timeout");
            }

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
        catch (Exception ex)
        {
            IsConnected = false;
            IsConnected = false;
            OnConnectionStateChanged(ConnectionState.Disconnected);
            OnError($"Connection failed: {ex.Message}");
            throw;
        }
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
        _frequencyFirstPacketSent.Remove(frequencyKhz); // Clean up tracking state
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
        _frequencyFirstPacketSent[frequencyKhz] = false; // Mark that we need to send startMarker

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

        // Send final silent packet with endMarker
        var silence = new short[OPUS_SAMPLES_PER_FRAME]; // 20ms silence, 16-bit PCM
        
        _rtpSender?.SendAudio(
            audioData: silence,
            frequencyTransmissions: [new FrequencyTransmission(frequencyKhz, 0, 0, new Vector3(), null, false, true)]
        );

        _logger.LogInformation("Sent end marker for frequency {Frequency:F3}", frequencyKhz / 1000d);

        _frequencyTransmissionState[frequencyKhz] = false;
        _frequencyFirstPacketSent.Remove(frequencyKhz); // Clean up tracking state

        await SendMessageAsync(SignalingMessageFactory.CreateTransmission(frequencyKhz, false, is3d));
        OnTransmissionStateChanged(frequencyKhz, false);
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
    
    public void SendAudio(Memory<short> pcmData, List<(int frequencyKhz, double txPowerWatts, double ppm, Vector3? position, Vector3? velocity, AmbientNoiseType ambientNoiseType)> frequencies, bool in3d)
    {
        var frequencyTransmissions = new List<FrequencyTransmission>();
        foreach (var freq in frequencies)
        {
            bool needsBeginMarker = _frequencyFirstPacketSent.TryGetValue(freq.frequencyKhz, out var sent) && !sent;
            frequencyTransmissions.Add(new FrequencyTransmission(
                khz: freq.frequencyKhz,
                txPowerWatts: freq.txPowerWatts,
                ppm: freq.ppm,
                position: freq.position,
                velocity: freq.velocity,
                ambientNoiseType: freq.ambientNoiseType,
                in3d: in3d,
                beginMarker: needsBeginMarker,
                endMarker: false
            ));

            if (needsBeginMarker)
                _frequencyFirstPacketSent[freq.frequencyKhz] = true;
        }
        
        _rtpSender?.SendAudio(pcmData, frequencyTransmissions);
    }


    /// <summary>
    /// Disconnect from the server
    /// </summary>
    public async Task DisconnectAsync()
    {
        CleanupRtp();
        _cts.Cancel();

        if (_webSocket?.State == WebSocketState.Open)
        {
            await _webSocket.CloseAsync(WebSocketCloseStatus.NormalClosure, "Client disconnecting",
                CancellationToken.None);
        }

        IsConnected = false;
        IsAuthenticated = false;
        OnConnectionStateChanged(ConnectionState.Disconnected);
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
                    IsConnected = false;
                    IsAuthenticated = false;
                    OnConnectionStateChanged(ConnectionState.Disconnected);
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
        catch (OperationCanceledException)
        {
            // Expected during shutdown
        }
        catch (Exception ex)
        {
            OnError($"WebSocket error: {ex.Message}");
            IsConnected = false;
            IsAuthenticated = false;
            OnConnectionStateChanged(ConnectionState.Disconnected);
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
    private void OnConnectionStateChanged(ConnectionState state) =>
        ConnectionStateChanged?.Invoke(this, new ConnectionStateChangedEventArgs(state));

    private void OnAuthenticated(string peerId, Dictionary<int, List<PeerData>> peers, int audioPort) =>
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

    private void OnAudioDataReceived(string peerId, byte[] audioData, AudioPacketMetadata metadata) =>
        AudioDataReceived?.Invoke(this, new AudioDataEventArgs(peerId, audioData, metadata));

    private void OnAllPeersStatusReceived(Dictionary<int, List<PeerData>> allPeersStatus) =>
        AllPeersStatusUpdateReceived?.Invoke(this, new AllPeersStatusEventArgs(allPeersStatus));


    private void OnError(string errorMessage) =>
        ErrorOccurred?.Invoke(this, new ErrorEventArgs(errorMessage));

    public void Dispose()
    {
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

public class ConnectionStateChangedEventArgs : EventArgs
{
    public ConnectionState State { get; }
    public ConnectionStateChangedEventArgs(ConnectionState state) => State = state;
}

public class AuthenticationEventArgs : EventArgs
{
    public string PeerId { get; }
    public int AudioPort { get; }
    
    public Dictionary<int, List<PeerData>> Peers { get; }

    public AuthenticationEventArgs(string peerId, Dictionary<int, List<PeerData>> peers, int audioPort)
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
        Is3d =  is3d;
    }
}

public class AudioDataEventArgs : EventArgs
{
    public string PeerId { get; }
    public byte[] AudioData { get; }
    public AudioPacketMetadata Metadata { get; }

    public AudioDataEventArgs(string peerId, byte[] audioData, AudioPacketMetadata metadata)
    {
        PeerId = peerId;
        AudioData = audioData;
        Metadata = metadata;
    }
}

public class AllPeersStatusEventArgs : EventArgs
{
    public Dictionary<int, List<PeerData>> AllPeers { get; }

    public AllPeersStatusEventArgs(Dictionary<int, List<PeerData>> allPeers)
    {
        AllPeers = allPeers;
    }
}

public class ErrorEventArgs : EventArgs
{
    public string ErrorMessage { get; }
    public ErrorEventArgs(string errorMessage) => ErrorMessage = errorMessage;
}