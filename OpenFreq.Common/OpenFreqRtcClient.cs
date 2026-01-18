using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using Concentus.Structs;
using Microsoft.Extensions.Logging;
using OpenFreq.Common.Rtp;
using OpenFreq.Common.Signaling;
using OpenFreqClient;

namespace OpenFreq.Common;

public class OpenFreqRtcClient : IDisposable
{
    // Audio configuration constants
    public const int SAMPLE_RATE = 48000;
    public const int CHANNELS = 1;
    public const int FRAME_SIZE_MS = 20;
    public const int OPUS_SAMPLES_PER_FRAME = SAMPLE_RATE / (1000 / FRAME_SIZE_MS) * CHANNELS;
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
    public event EventHandler<ErrorEventArgs>? ErrorOccurred;

    private RtpAudioReceiver? _rtpReceiver;
    private RtpAudioSender? _rtpSender;

    // set by the server
    private bool _opusCompressionEnabled = true;

    // Connection state
    private readonly string _serverIp;
    private readonly string _password;
    private ClientWebSocket? _webSocket;
    private UdpClient? _audioClient;
    private IPEndPoint? _serverAudioEndpoint;
    private string? _myPeerId;
    private int _audioPort;
    private bool _isConnected;
    private bool _isAuthenticated;
    private readonly CancellationTokenSource _cts = new();
    private string clientId = Guid.NewGuid().ToString();

    // Transmission state
    private readonly Dictionary<double, bool> _frequencyTransmissionState = new();
    private readonly Dictionary<double, bool> _frequencyFirstPacketSent = new();
    private readonly Dictionary<double, HashSet<string>> _frequencyPeers = new();
    
    private readonly ILogger<OpenFreqRtcClient> _logger;

    // Properties
    public string? MyPeerId => _myPeerId;
    public int AudioPort => _audioPort;
    public bool IsConnected => _isConnected;
    public bool IsAuthenticated => _isAuthenticated;
    public IReadOnlyDictionary<double, bool> FrequencyTransmissionState => _frequencyTransmissionState;

    public OpenFreqRtcClient(ILogger<OpenFreqRtcClient> logger, string serverIp, string password)
    {
        _logger = logger;
        _serverIp = serverIp;
        _password = password;
    }

    /// <summary>
    /// Connect to the OpenFreq server and authenticate
    /// </summary>
    public async Task ConnectAsync()
    {
        try
        {
            // Connect WebSocket
            var ipPort = Util.ResolveAddress(_serverIp, DEFAULT_PORT);

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

            _isConnected = true;
            OnConnectionStateChanged(ConnectionState.Connected);

            // Start message receiver
            _ = Task.Run(ReceiveMessagesAsync, _cts.Token);

            // Authenticate
            await SendMessageAsync(SignalingMessageFactory.CreateAuthenticate(_password));

            // Wait for authentication response with timeout
            var authWaitTask = Task.Delay(5000, _cts.Token);
            var startTime = DateTime.UtcNow;
            while (!_isAuthenticated && (DateTime.UtcNow - startTime).TotalSeconds < 5)
            {
                await Task.Delay(100, _cts.Token);
            }

            if (!_isAuthenticated)
            {
                throw new TimeoutException("Authentication timeout");
            }

            // Create RTP sender
            _rtpSender = new RtpAudioSender(
                serverHost: ipPort.ipAddress,
                serverPort: _audioPort,
                opusEnabled: _opusCompressionEnabled
            );

            var port = 10000;
            _rtpReceiver = new RtpAudioReceiver(
                udpClient: _rtpSender.UdpClient,
                opusEnabled: _opusCompressionEnabled,
                initialBufferMs: 150
            );

            // Subscribe to clean audio events
            _rtpReceiver.AudioReceived += OnRtpAudioReceived;
            _rtpReceiver.ErrorOccurred += (sender, error) => { _logger.LogError("RTP Error: {Error}", error); };

          
        }
        catch (Exception ex)
        {
            _isConnected = false;
            OnConnectionStateChanged(ConnectionState.Disconnected);
            OnError($"Connection failed: {ex.Message}");
            throw;
        }
    }

    private void CleanupRTP()
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
    public async Task JoinFrequencyAsync(double frequency)
    {
        if (!_isAuthenticated)
        {
            throw new InvalidOperationException("Not authenticated");
        }

        await SendMessageAsync(SignalingMessageFactory.CreateJoin(frequency));

        if (!_frequencyPeers.ContainsKey(frequency))
        {
            _frequencyPeers[frequency] = new HashSet<string>();
        }

        _frequencyTransmissionState[frequency] = false;
    }

    /// <summary>
    /// Leave a frequency channel
    /// </summary>
    public async Task LeaveFrequencyAsync(double frequency)
    {
        if (!_isAuthenticated)
        {
            throw new InvalidOperationException("Not authenticated");
        }

        await SendMessageAsync(SignalingMessageFactory.CreateLeave(frequency));

        _frequencyPeers.Remove(frequency);
        _frequencyTransmissionState.Remove(frequency);
        _frequencyFirstPacketSent.Remove(frequency); // Clean up tracking state
        OnFrequencyLeft(frequency);
    }

    /// <summary>
    /// Start transmitting on a frequency
    /// </summary>
    public async Task StartTransmissionAsync(double frequencyMhz)
    {
        if (!_isAuthenticated)
        {
            throw new InvalidOperationException("Not authenticated");
        }

        if (!_frequencyTransmissionState.ContainsKey(frequencyMhz))
        {
            throw new InvalidOperationException($"Not joined to frequency {frequencyMhz}");
        }
        
        _frequencyTransmissionState[frequencyMhz] = true;
        _frequencyFirstPacketSent[frequencyMhz] = false; // Mark that we need to send startMarker
        
        await SendMessageAsync(SignalingMessageFactory.CreateTransmission(frequencyMhz, true));
        OnTransmissionStateChanged(frequencyMhz, true);

        // Start heartbeat for this frequency
        _ = Task.Run(() => TransmissionHeartbeatAsync(frequencyMhz), _cts.Token);
    }

    /// <summary>
    /// Stop transmitting on a frequency
    /// </summary>
    public async Task StopTransmissionAsync(double frequencyMhz)
    {
        if (!_isAuthenticated)
        {
            throw new InvalidOperationException("Not authenticated");
        }

        if (!_frequencyTransmissionState.ContainsKey(frequencyMhz))
        {
            return;
        }

        // Send final silent packet with endMarker
        var silence = new byte[OPUS_SAMPLES_PER_FRAME * 2]; // 20ms silence, 16-bit PCM
        
        _rtpSender?.SendAudio(
            audioData: silence,
            clientId: clientId,
            position: null,
            frequencyTransmissions: [new FrequencyTransmission(frequencyMhz, 0, false, true)]
        );
        
        _logger.LogInformation("Sent end marker for frequency {Frequency}", frequencyMhz);

        _frequencyTransmissionState[frequencyMhz] = false;
        _frequencyFirstPacketSent.Remove(frequencyMhz); // Clean up tracking state
        
        await SendMessageAsync(SignalingMessageFactory.CreateTransmission(frequencyMhz, false));
        OnTransmissionStateChanged(frequencyMhz, false);
    }


    public void SendAudio(byte[] pcmData, List<(double frequency, double txPowerWatts, Position? position)> frequencies)
    {
        var frequencyTransmissions = new List<FrequencyTransmission>();
    
        foreach (var freq in frequencies)
        {
            bool needsBeginMarker = _frequencyFirstPacketSent.TryGetValue(freq.frequency, out var sent) && !sent;
        
            frequencyTransmissions.Add(new FrequencyTransmission(
                mhz: freq.frequency,
                txPowerWatts: freq.txPowerWatts,
                beginMarker: needsBeginMarker,
                endMarker: false
            ));
        
            if (needsBeginMarker)
                _frequencyFirstPacketSent[freq.frequency] = true;
            
            _rtpSender?.SendAudio(pcmData, clientId, freq.position, frequencyTransmissions);
        }
    
        
    }
    

    /// <summary>
    /// Disconnect from the server
    /// </summary>
    public async Task DisconnectAsync()
    {
        CleanupRTP();
        _cts.Cancel();

        if (_webSocket?.State == WebSocketState.Open)
        {
            await _webSocket.CloseAsync(WebSocketCloseStatus.NormalClosure, "Client disconnecting",
                CancellationToken.None);
        }

        _isConnected = false;
        _isAuthenticated = false;
        OnConnectionStateChanged(ConnectionState.Disconnected);
    }

    private async Task TransmissionHeartbeatAsync(double frequency)
    {
        while (!_cts.Token.IsCancellationRequested)
        {
            if (!_frequencyTransmissionState.TryGetValue(frequency, out var isTransmitting) || !isTransmitting)
            {
                break;
            }

            await SendMessageAsync(SignalingMessageFactory.CreateTransmission(frequency, true));
            await Task.Delay(333, _cts.Token); // ~3 times per second
        }
    }

    private void OnRtpAudioReceived(object? sender, RtpAudioReceiver.AudioReceivedEventArgs e)
    {
        OnAudioDataReceived(e.Metadata.clientId, e.AudioData, e.Metadata);
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
                    _isConnected = false;
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
            _isConnected = false;
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
                        _myPeerId = success.PeerId;
                        _audioPort = success.AudioPort ?? 0;
                        _isAuthenticated = true;
                        _opusCompressionEnabled = success.OpusCompressionEnabled;
                        _logger.LogDebug("Opus compression enabled: " + _opusCompressionEnabled);
                        OnConnectionStateChanged(ConnectionState.Authenticated);
                        OnAuthenticated(_myPeerId, _audioPort);
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
                        if (_frequencyPeers.TryGetValue(joined.FrequencyMhz, out var peers))
                        {
                            peers.Add(joined.PeerId);
                        }

                        OnPeerJoined(joined.PeerId, joined.FrequencyMhz);
                    }

                    break;

                case SignalingMessageTypes.PeerLeft:
                    var left = SignalingMessageFactory.DeserializePayload<PeerLeftMessage>(message.Payload);
                    if (left != null)
                    {
                        if (_frequencyPeers.TryGetValue(left.FrequencyMhz, out var peers))
                        {
                            peers.Remove(left.PeerId);
                        }

                        OnPeerLeft(left.PeerId, left.FrequencyMhz);
                    }

                    break;

                case SignalingMessageTypes.Transmission:
                    var transmission =
                        SignalingMessageFactory.DeserializePayload<TransmissionEventMessage>(message.Payload);
                    if (transmission != null && transmission.PeerId != _myPeerId)
                    {
                        OnPeerTransmissionStateChanged(transmission.PeerId, transmission.FrequencyMhz,
                            transmission.Transmitting);
                    }

                    break;

                case SignalingMessageTypes.ChannelState:
                    var channelState = SignalingMessageFactory.DeserializePayload<ChannelStateMessage>(message.Payload);
                    if (channelState != null)
                    {
                        _frequencyPeers[channelState.FrequencyMhz] = new HashSet<string>(channelState.Peers);
                        OnFrequencyJoined(channelState.FrequencyMhz, channelState.Peers);
                    }

                    break;
            }
        }
        catch (Exception ex)
        {
            OnError($"Error handling message: {ex.Message}");
        }
    }

    private async Task SendMessageAsync(SignalingMessage message)
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

    private void OnAuthenticated(string peerId, int audioPort) =>
        Authenticated?.Invoke(this, new AuthenticationEventArgs(peerId, audioPort));

    private void OnFrequencyJoined(double frequencyMhz, List<string> peers) =>
        FrequencyJoined?.Invoke(this, new FrequencyJoinedEventArgs(frequencyMhz, peers));

    private void OnFrequencyLeft(double frequencyMhz) =>
        FrequencyLeft?.Invoke(this, new FrequencyLeftEventArgs(frequencyMhz));

    private void OnPeerJoined(string peerId, double frequencyMhz) =>
        PeerJoined?.Invoke(this, new PeerEventArgs(peerId, frequencyMhz));

    private void OnPeerLeft(string peerId, double frequencyMhz) =>
        PeerLeft?.Invoke(this, new PeerEventArgs(peerId, frequencyMhz));

    private void OnTransmissionStateChanged(double frequencyMhz, bool isTransmitting) =>
        TransmissionStateChanged?.Invoke(this, new TransmissionStateEventArgs(frequencyMhz, isTransmitting));

    private void OnPeerTransmissionStateChanged(string peerId, double frequencyMhz, bool isTransmitting) =>
        PeerTransmissionStateChanged?.Invoke(this, new PeerTransmissionEventArgs(peerId, frequencyMhz, isTransmitting));

    private void OnAudioDataReceived(string peerId, byte[] audioData, AudioPacketMetadata metadata) =>
        AudioDataReceived?.Invoke(this, new AudioDataEventArgs(peerId, audioData, metadata));

    private void OnError(string errorMessage) =>
        ErrorOccurred?.Invoke(this, new ErrorEventArgs(errorMessage));

    public void Dispose()
    {
        Console.WriteLine($"[CLIENT] Dispose called - Instance: {GetHashCode()}");
        _cts.Cancel();

        _audioClient?.Close();
        _audioClient?.Dispose();

        if (_webSocket?.State == WebSocketState.Open)
        {
            _webSocket.CloseAsync(WebSocketCloseStatus.NormalClosure, "Disposing", CancellationToken.None).Wait(1000);
        }

        _webSocket?.Dispose();
        _cts.Dispose();
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

    public AuthenticationEventArgs(string peerId, int audioPort)
    {
        PeerId = peerId;
        AudioPort = audioPort;
    }
}

public class FrequencyJoinedEventArgs(double frequencyMhz, List<string> peers) : EventArgs
{
    public List<string> Peers { get; } = peers;
    public double FrequencyMhz { get; } = frequencyMhz;
}

public class FrequencyLeftEventArgs : EventArgs
{
    public double FrequencyMhz { get; }
    public FrequencyLeftEventArgs(double frequencyMhz) => FrequencyMhz = frequencyMhz;
}

public class PeerEventArgs : EventArgs
{
    public string PeerId { get; }
    public double FrequencyMhz { get; }

    public PeerEventArgs(string peerId, double frequencyMhz)
    {
        PeerId = peerId;
        FrequencyMhz = frequencyMhz;
    }
}

public class TransmissionStateEventArgs : EventArgs
{
    public double FrequencyMhz { get; }
    public bool IsTransmitting { get; }

    public TransmissionStateEventArgs(double frequencyMhz, bool isTransmitting)
    {
        FrequencyMhz = frequencyMhz;
        IsTransmitting = isTransmitting;
    }
}

public class PeerTransmissionEventArgs : EventArgs
{
    public string PeerId { get; }
    public double FrequencyMhz { get; }
    public bool IsTransmitting { get; }

    public PeerTransmissionEventArgs(string peerId, double frequencyMhz, bool isTransmitting)
    {
        PeerId = peerId;
        FrequencyMhz = frequencyMhz;
        IsTransmitting = isTransmitting;
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

public class ErrorEventArgs : EventArgs
{
    public string ErrorMessage { get; }
    public ErrorEventArgs(string errorMessage) => ErrorMessage = errorMessage;
}