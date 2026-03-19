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
    private readonly ConcurrentDictionary<(string clientId, uint Ssrc), ReceiverRtpState> _receiverRtpStates = new();
    private readonly FrequencyChannelManager _channelManager;
    private readonly ConcurrentDictionary<string, ClientSession> _clients;
    private readonly ILogger<AudioStreamServer> _logger;
    private readonly UdpStreamManager _udpManager;
    private readonly int _basePort;
    private int _nextPortOffset = 0;
    private CancellationTokenSource _cts = new();

    // High-performance logging delegates
    private static readonly Action<ILogger, string, string, int, Exception?> LogAudioSessionCreated =
        LoggerMessage.Define<string, string, int>(
            LogLevel.Information,
            new EventId(1, nameof(CreateAudioSession)),
            "Created audio session for {DisplayName} ({ClientId}) on port {Port}");

    private static readonly Action<ILogger, string, string, int, Exception?> LogTransmittingOnFrequencies =
        LoggerMessage.Define<string, string, int>(
            LogLevel.Debug,
            new EventId(2, nameof(ForwardAudioToChannel)),
            "{DisplayName} ({ClientId}) transmitting on {FrequencyCount} frequency(ies)");

    private static readonly Action<ILogger, string, string, Exception?> LogSessionRemoved =
        LoggerMessage.Define<string, string>(
            LogLevel.Information,
            new EventId(4, nameof(RemoveSession)),
            "Removed audio session for {DisplayName} ({ClientId})");

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
        
        _logger.LogInformation("AudioStreamServer initialized");
    }

    private string GetDisplayName(string clientId)
    {
        if (_clients.TryGetValue(clientId, out var session))
        {
            return !string.IsNullOrWhiteSpace(session.DisplayName) ? session.DisplayName : "Unnamed";
        }
        return "Unnamed";
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

        LogAudioSessionCreated(_logger, GetDisplayName(clientId), clientId, port, null);
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
                        _logger.LogWarning("{DisplayName} ({ClientId}) sent malformed RTP audio packet, skipping", 
                            GetDisplayName(clientId), clientId);
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
                        _logger.LogWarning("{DisplayName} ({ClientId}) attempted to transmit on unjoined frequencies: {Frequencies}", 
                            GetDisplayName(clientId), clientId, string.Join(", ", invalidMhz));
                }

                if (validFrequencies.Count == 0)
                    continue;

                if (_logger.IsEnabled(LogLevel.Debug))
                    LogTransmittingOnFrequencies(_logger, GetDisplayName(clientId), clientId, validFrequencies.Count, null);
                
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
            _logger.LogError(ex, "Error in audio receive loop for {DisplayName} ({ClientId})", 
                GetDisplayName(clientId), clientId);
        }
    }

    /// <summary>
    /// Parses a UDP RTP audio packet with metadata in the header extension
    /// Packet format: [12 bytes RTP header][4 bytes ext header][N bytes JSON metadata + padding][audio data]
    /// </summary>
    private (RtpPacket? rtpPacket, AudioPacketMetadata? metadata, byte[]? audioData) ParseRtpAudioPacket(
        byte[] packet,
        string clientId)
    {
        try
        {
            if (packet.Length < RtpPacket.HEADER_SIZE)
            {
                if (_logger.IsEnabled(LogLevel.Warning))
                    _logger.LogWarning("Packet too small: {Length} bytes", packet.Length);
                return (null, null, null);
            }

            var rtpPacket = RtpPacket.Parse(packet);
            if (rtpPacket == null)
            {
                if (_logger.IsEnabled(LogLevel.Warning))
                    _logger.LogWarning("Failed to parse RTP header from {DisplayName} ({ClientId})", 
                        GetDisplayName(clientId), clientId);
                return (null, null, null);
            }

            // Keepalive packets have no extension — silently ignore
            if (rtpPacket.ExtensionData is not { Length: > 0 })
                return (null, null, null);

            var metadataJson = Encoding.UTF8.GetString(rtpPacket.ExtensionData).TrimEnd('\0');
            var metadata = Json.Instance.Deserialize<AudioPacketMetadata>(metadataJson);
            if (metadata == null)
            {
                if (_logger.IsEnabled(LogLevel.Warning))
                    _logger.LogWarning("Failed to deserialize metadata from {DisplayName} ({ClientId})", 
                        GetDisplayName(clientId), clientId);
                return (null, null, null);
            }

            return (rtpPacket, metadata, rtpPacket.Payload);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error parsing RTP audio packet from {DisplayName} ({ClientId})", 
                GetDisplayName(clientId), clientId);
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
        var senderSsrc = originalRtpPacket.Ssrc;
        // Get or create RTP state for this receiver
        var rtpState = _receiverRtpStates.GetOrAdd((receiverClientId, senderSsrc), _ => new ReceiverRtpState
        {
            NextSequence = 0,
            PacketsSent = 0
        });

        // Stamp server send time into metadata, then place in header extension
        metadata.ServerSendTimestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var metadataJson = Json.Instance.Serialize(metadata);
        var metadataBytes = Encoding.UTF8.GetBytes(metadataJson);

        // Create new RTP packet with server's sequence number
        var rtpPacket = new RtpPacket
        {
            Version = 2,
            PayloadType = originalRtpPacket.PayloadType,  // Preserve payload type (Opus/PCM)
            SequenceNumber = rtpState.NextSequence,       // SERVER's sequence for this receiver
            Timestamp = originalRtpPacket.Timestamp,      // Preserve original timestamp for jitter calc
            Ssrc = originalRtpPacket.Ssrc,                // Preserve original SSRC
            Marker = originalRtpPacket.Marker,            // Currently unused but still preserve it
            ExtensionProfile = RtpPacket.OpenFreqProfile,
            ExtensionData = metadataBytes,
            Payload = audioData
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
                    _logger.LogDebug("RTP packet dropped for {DisplayName} ({ClientId}) due to congestion", 
                        GetDisplayName(clientId), clientId);
                }
            }
        }
    }

    public void RemoveSession(string clientId)
    {
        if (!_sessions.TryRemove(clientId, out var session)) return;
        _clients.TryGetValue(clientId, out var client);
        _udpManager.RemoveClient(clientId);
        session.UdpClient.Close();
        session.UdpClient.Dispose();

        // Remove all per-SSRC RTP states for this receiver
        foreach (var key in _receiverRtpStates.Keys.Where(k => k.clientId == clientId).ToList())
            _receiverRtpStates.TryRemove(key, out _);

        _logger.LogInformation("Session removed for {DisplayName} ({ClientId})", client?.DisplayName ?? "Unnamed", clientId);
    }

    public ClientStreamStats? GetClientStats(string clientId)
    {
        return _udpManager.GetStats(clientId);
    }

    /// <summary>
    /// Get RTP statistics for a receiver
    /// </summary>
    public IEnumerable<ReceiverRtpStats> GetReceiverRtpStats(string clientId)
    {
        return _receiverRtpStates
            .Where(kvp => kvp.Key.clientId == clientId)
            .Select(kvp => new ReceiverRtpStats
            {
                ClientId = clientId,
                Ssrc = kvp.Key.Ssrc,
                PacketsSent = kvp.Value.PacketsSent,
                CurrentSequence = kvp.Value.NextSequence
            });
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
    
    public uint Ssrc {get; set; }
    
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