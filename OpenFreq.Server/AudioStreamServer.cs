using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text;
using Microsoft.Extensions.Logging;
using OpenFreq.Common;
using OpenFreq.Common.Rtp;
using OpenFreqServer.Json; // Import RTP classes

namespace OpenFreq.Server;

/// <summary>
/// Audio stream server with RTP translation.
/// Acts as RTP translator: parses incoming RTP from clients, rewrites RTP headers
/// with per-receiver sequence numbers, and forwards to recipients.
/// </summary>
public class AudioStreamServer
{
    private readonly ConcurrentDictionary<string, AudioStreamSession> _sessions = new();
    private readonly ConcurrentDictionary<string, ReceiverRtpState> _receiverRtpStates = new();
    private readonly FrequencyChannelManager _channelManager;
    private readonly ConcurrentDictionary<string, ClientSession> _clients;
    private readonly ILogger<AudioStreamServer> _logger;
    private readonly UdpStreamManager _udpManager;
    private readonly int _basePort;
    private int _nextPortOffset = 0;
    private CancellationTokenSource _cts = new();
    
    // Server's SSRC (synchronization source identifier)
    private readonly uint _serverSsrc = (uint)Random.Shared.Next();

    // High-performance logging delegates
    private static readonly Action<ILogger, string, int, Exception?> _logAudioSessionCreated =
        LoggerMessage.Define<string, int>(
            LogLevel.Information,
            new EventId(1, nameof(CreateAudioSession)),
            "Created audio session for client {ClientId} on port {Port}");

    private static readonly Action<ILogger, string, int, Exception?> _logTransmittingOnFrequencies =
        LoggerMessage.Define<string, int>(
            LogLevel.Debug,
            new EventId(2, nameof(ForwardAudioToChannel)),
            "Client {ClientId} transmitting on {FrequencyCount} frequency(ies)");

    private static readonly Action<ILogger, string, Exception?> _logSessionRemoved =
        LoggerMessage.Define<string>(
            LogLevel.Information,
            new EventId(4, nameof(RemoveSession)),
            "Removed audio session for client {ClientId}");

    public AudioStreamServer(
        FrequencyChannelManager channelManager,
        ConcurrentDictionary<string, ClientSession> clients,
        ILoggerFactory loggerFactory,
        int basePort = 10000)
    {
        _channelManager = channelManager;
        _clients = clients;
        _logger = loggerFactory.CreateLogger<AudioStreamServer>();
        _basePort = basePort;
        _udpManager = new UdpStreamManager(loggerFactory.CreateLogger<UdpStreamManager>());
        
        _logger.LogInformation("AudioStreamServer initialized as RTP translator (SSRC: 0x{Ssrc:X8})", _serverSsrc);
    }

    public async Task<int> CreateAudioSession(string clientId)
    {
        const int maxRetries = 10;
        UdpClient? udpClient = null;
        int port = 0;
        
        for (int attempt = 0; attempt < maxRetries; attempt++)
        {
            try
            {
                var portOffset = Interlocked.Increment(ref _nextPortOffset) - 1;
                port = _basePort + portOffset;
                udpClient = _udpManager.CreateUdpClient(clientId, port);
                break;
            }
            catch (SocketException ex) when (ex.SocketErrorCode == SocketError.AddressAlreadyInUse)
            {
                _logger.LogWarning("Port {Port} already in use (attempt {Attempt}/{MaxRetries}), trying next port", 
                    port, attempt + 1, maxRetries);
                
                udpClient?.Dispose();
                udpClient = null;
                
                if (attempt == maxRetries - 1)
                {
                    throw new InvalidOperationException(
                        $"Failed to allocate UDP port after {maxRetries} attempts. Base port: {_basePort}, last attempted: {port}", 
                        ex);
                }
            }
        }
        
        if (udpClient == null)
        {
            throw new InvalidOperationException("Failed to create UDP client");
        }
        
        var session = new AudioStreamSession
        {
            ClientId = clientId,
            Port = port,
            UdpClient = udpClient
        };

        _sessions[clientId] = session;

        _ = Task.Run(() => ReceiveAudioLoop(clientId, udpClient));

        _logAudioSessionCreated(_logger, clientId, port, null);
        return port;
    }

    private async Task ReceiveAudioLoop(string clientId, UdpClient udpClient)
    {
        try
        {
            while (!_cts.Token.IsCancellationRequested)
            {
                var result = await udpClient.ReceiveAsync(_cts.Token);
                
                if (!_sessions.TryGetValue(clientId, out var session))
                    break;

                session.LastReceived = DateTime.UtcNow;
                session.RemoteEndPoint = result.RemoteEndPoint;

                // Parse RTP packet and extract payload
                var (rtpPacket, metadata, audioData) = ParseRtpAudioPacket(result.Buffer, clientId);
                
                if (rtpPacket == null || metadata == null || audioData == null)
                {
                    if (_logger.IsEnabled(LogLevel.Warning))
                        _logger.LogWarning("Client {ClientId} sent malformed RTP audio packet, skipping", clientId);
                    continue;
                }

                if (metadata.Frequencies == null || metadata.Frequencies.Count == 0)
                    continue;

                // Verify client has joined all specified frequencies
                if (!_clients.TryGetValue(clientId, out var clientSession))
                    continue;

                var validFrequencies = metadata.Frequencies
                    .Where(freq => clientSession.CurrentFrequencies.ContainsKey(freq.Khz))
                    .ToList();

                if (validFrequencies.Count < metadata.Frequencies.Count)
                {
                    var validKhz = validFrequencies.Select(f => f.Khz).ToHashSet();
                    var invalidMhz = metadata.Frequencies
                        .Where(f => !validKhz.Contains(f.Khz))
                        .Select(f => $"{f.Khz/1000d:F3} MHz")
                        .ToList();
    
                    if (_logger.IsEnabled(LogLevel.Warning))
                        _logger.LogWarning("Client {ClientId} attempted to transmit on unjoined frequencies: {Frequencies}", 
                            clientId, string.Join(", ", invalidMhz));
                }

                if (validFrequencies.Count == 0)
                    continue;

                if (_logger.IsEnabled(LogLevel.Debug))
                    _logTransmittingOnFrequencies(_logger, clientId, validFrequencies.Count, null);
                
                // Forward audio to all specified frequencies
                // Each recipient gets their own RTP packet with unique sequence number
                foreach (var frequency in validFrequencies)
                {
                    ForwardAudioToChannel(frequency.Khz, clientId, rtpPacket, metadata, audioData);
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Normal shutdown
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error in audio receive loop for client {ClientId}", clientId);
        }
    }

    /// <summary>
    /// Parses a UDP RTP audio packet with metadata
    /// Packet format: [12 bytes RTP header][2 bytes: header length][N bytes: JSON metadata][remaining: audio data]
    /// </summary>
    private (RtpPacket? rtpPacket, AudioPacketMetadata? metadata, byte[]? audioData) ParseRtpAudioPacket(
        byte[] packet, 
        string clientId)
    {
        try
        {
            // Minimum packet size: 12 bytes RTP + 2 bytes header length + metadata
            if (packet.Length < RtpPacket.HEADER_SIZE + 4)
            {
                if (_logger.IsEnabled(LogLevel.Warning))
                    _logger.LogWarning("Packet too small: {Length} bytes", packet.Length);
                return (null, null, null);
            }

            // Parse RTP header
            var rtpPacket = RtpPacket.Parse(packet);
            if (rtpPacket == null)
            {
                if (_logger.IsEnabled(LogLevel.Warning))
                    _logger.LogWarning("Failed to parse RTP header from client {ClientId}", clientId);
                return (null, null, null);
            }

            // RTP payload contains: [2 bytes header len][JSON metadata][audio data]
            var payload = rtpPacket.Payload;
            
            if (payload.Length < 4)
            {
                if (_logger.IsEnabled(LogLevel.Warning))
                    _logger.LogWarning("RTP payload too small: {Length} bytes", payload.Length);
                return (null, null, null);
            }

            // Read metadata header length (first 2 bytes of payload, big-endian)
            var headerLength = BinaryPrimitives.ReadUInt16BigEndian(payload.AsSpan(0, 2));
            
            // Validate header length
            if (headerLength > payload.Length - 2)
            {
                if (_logger.IsEnabled(LogLevel.Warning))
                    _logger.LogWarning("Invalid metadata header length: {HeaderLength}, payload size: {PayloadLength}", 
                        headerLength, payload.Length);
                return (null, null, null);
            }

            // Extract metadata JSON
            var metadataBytes = new byte[headerLength];
            Array.Copy(payload, 2, metadataBytes, 0, headerLength);
            var metadataJson = Encoding.UTF8.GetString(metadataBytes);
            
            // Parse metadata
            var metadata = Json.Instance.Deserialize<AudioPacketMetadata>(metadataJson);
            if (metadata == null)
            {
                if (_logger.IsEnabled(LogLevel.Warning))
                    _logger.LogWarning("Failed to deserialize metadata");
                return (null, null, null);
            }

            // Extract audio data (everything after metadata in payload)
            var audioDataLength = payload.Length - 2 - headerLength;
            var audioData = new byte[audioDataLength];
            Array.Copy(payload, 2 + headerLength, audioData, 0, audioDataLength);

            return (rtpPacket, metadata, audioData);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error parsing RTP audio packet from client {ClientId}", clientId);
            return (null, null, null);
        }
    }

    /// <summary>
    /// Creates an RTP audio packet for a specific receiver
    /// Each receiver gets their own sequence number from the server
    /// </summary>
    private byte[] CreateRtpAudioPacket(
        string receiverClientId,
        RtpPacket originalRtpPacket,
        AudioPacketMetadata metadata,
        byte[] audioData)
    {
        // Get or create RTP state for this receiver
        var rtpState = _receiverRtpStates.GetOrAdd(receiverClientId, _ => new ReceiverRtpState
        {
            NextSequence = 0,
            PacketsSent = 0
        });

        // Build metadata payload: [2 bytes header len][JSON metadata][audio data]
        metadata.ServerSendTimestamp =  DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var metadataJson = Json.Instance.Serialize(metadata);
        var metadataBytes = Encoding.UTF8.GetBytes(metadataJson);
        var headerLength = (ushort)metadataBytes.Length;

        var payload = new byte[2 + metadataBytes.Length + audioData.Length];
        
        // Write header length (big-endian)
        payload[0] = (byte)(headerLength >> 8);
        payload[1] = (byte)(headerLength & 0xFF);
        
        // Write metadata
        Array.Copy(metadataBytes, 0, payload, 2, metadataBytes.Length);
        
        // Write audio data
        Array.Copy(audioData, 0, payload, 2 + metadataBytes.Length, audioData.Length);

        // Create new RTP packet with server's sequence number and SSRC
        var rtpPacket = new RtpPacket
        {
            Version = 2,
            PayloadType = originalRtpPacket.PayloadType,  // Preserve payload type (Opus/PCM)
            SequenceNumber = rtpState.NextSequence,       // SERVER's sequence for this receiver
            Timestamp = originalRtpPacket.Timestamp,      // Preserve original timestamp for jitter calc
            Ssrc = _serverSsrc,                           // Server is the source
            Payload = payload,
            Marker =  originalRtpPacket.Marker            // Currently unused but still preserve it
        };

        // Update receiver state
        rtpState.NextSequence++;
        rtpState.PacketsSent++;

        return rtpPacket.ToBytes();
    }

    /// <summary>
    /// Forward audio to all clients in a channel
    /// Each recipient gets a unique RTP packet with their own sequence number
    /// </summary>
    private void ForwardAudioToChannel(
        int frequencyKhz, 
        string sourceClientId,
        RtpPacket originalRtpPacket,
        AudioPacketMetadata metadata,
        byte[] audioData)
    {
        var clients = _channelManager.GetClientsInChannel(frequencyKhz);

        foreach (var clientId in clients)
        {
            if (clientId == sourceClientId) 
                continue;

            if (_sessions.TryGetValue(clientId, out var targetSession) && 
                targetSession.RemoteEndPoint != null)
            {
                // Create RTP packet with receiver-specific sequence number
                var rtpPacket = CreateRtpAudioPacket(clientId, originalRtpPacket, metadata, audioData);

                // Returns false if packet was dropped due to congestion
                var sent = _udpManager.SendPacketAsync(
                    clientId, 
                    targetSession.UdpClient, 
                    rtpPacket, 
                    targetSession.RemoteEndPoint);

                if (!sent && _logger.IsEnabled(LogLevel.Debug))
                {
                    _logger.LogDebug("RTP packet dropped for client {ClientId} due to congestion", clientId);
                }
            }
        }
    }

    public void RemoveSession(string clientId)
    {
        if (_sessions.TryRemove(clientId, out var session))
        {
            _udpManager.RemoveClient(clientId);
            session.UdpClient.Close();
            session.UdpClient.Dispose();
            
            // Clean up RTP state
            _receiverRtpStates.TryRemove(clientId, out _);
            
            _logSessionRemoved(_logger, clientId, null);
        }
    }

    public ClientStreamStats? GetClientStats(string clientId)
    {
        return _udpManager.GetStats(clientId);
    }

    /// <summary>
    /// Get RTP statistics for a receiver
    /// </summary>
    public ReceiverRtpStats? GetReceiverRtpStats(string clientId)
    {
        if (_receiverRtpStates.TryGetValue(clientId, out var state))
        {
            return new ReceiverRtpStats
            {
                ClientId = clientId,
                PacketsSent = state.PacketsSent,
                CurrentSequence = state.NextSequence
            };
        }
        return null;
    }

    public void Stop()
    {
        _cts.Cancel();
        
        foreach (var session in _sessions.Values)
        {
            _udpManager.RemoveClient(session.ClientId);
            session.UdpClient.Close();
            session.UdpClient.Dispose();
        }
        
        _sessions.Clear();
        _receiverRtpStates.Clear();
    }
}

/// <summary>
/// Per-receiver RTP state tracked by the server
/// Each receiver gets their own continuous sequence of packets from the server
/// </summary>
public class ReceiverRtpState
{
    public ushort NextSequence { get; set; }
    public long PacketsSent { get; set; }
}

/// <summary>
/// RTP statistics for a receiver
/// </summary>
public class ReceiverRtpStats
{
    public string ClientId { get; set; } = "";
    public long PacketsSent { get; set; }
    public ushort CurrentSequence { get; set; }
}

public class AudioStreamSession
{
    public string ClientId { get; set; } = string.Empty;
    public int Port { get; set; }
    public UdpClient UdpClient { get; set; } = null!;
    public IPEndPoint? RemoteEndPoint { get; set; }
    public DateTime LastReceived { get; set; } = DateTime.UtcNow;
}