using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;
using System.Net;
using System.Net.Sockets;
using System.Text;
using Microsoft.Extensions.Logging;
using OpenFreq.Common;
using OpenFreq.Common.Rtp;

namespace OpenFreqServer;

/// <summary>
/// Audio stream server with RTP translation using a single shared UDP port.
/// Demultiplexes incoming packets based on sender's remote endpoint.
/// </summary>
public class AudioStreamServer
{
    // Static stop-flag
    private volatile bool _stopping;

    // Single shared UDP client for all clients
    private readonly UdpClient _udpClient;
    private readonly Lock _udpSendLock = new();

    private readonly int _audioPort;

    // Map remote endpoint -> clientId (learned from first packet)
    private readonly ConcurrentDictionary<IPEndPoint, string> _endpointToClient = new();

    // Track sessions for each client
    private readonly ConcurrentDictionary<string, AudioStreamSession> _sessions = new();

    // Per-receiver state: RTP sequence numbers + send backpressure
    private readonly ConcurrentDictionary<(string clientId, uint Ssrc), ReceiverRtpState> _receiverRtpStates = new();

    private readonly FrequencyChannelManager _channelManager;
    private readonly ConcurrentDictionary<string, ClientSession> _clients;
    private readonly ILogger<AudioStreamServer> _logger;
    private CancellationTokenSource _cts = new();

    // Single receive task for all clients
    private Task? _receiveTask;

    // Backpressure configuration
    private const int MaxPendingSendsPerClient = 3;

    // Pre-built minimal RTP pong packet sent back on every client keepalive to maintain
    // the server→client NAT mapping even during long silent periods.
    private static readonly byte[] _keepalivePong = new RtpPacket
    {
        Version = 2,
        PayloadType = 96,
        SequenceNumber = 0,
        Timestamp = 0,
        Ssrc = 0,
        ExtensionProfile = 0,
        ExtensionData = null,
        Payload = []
    }.ToBytes();

    // High-performance logging delegates
    private static readonly Action<ILogger, string, string, Exception?> LogAudioSessionCreated =
        LoggerMessage.Define<string, string>(
            LogLevel.Information,
            new EventId(1, nameof(CreateAudioSession)),
            "Created audio session for {DisplayName} ({ClientId})");

    private static readonly Action<ILogger, string, string, int, Exception?> LogTransmittingOnFrequencies =
        LoggerMessage.Define<string, string, int>(
            LogLevel.Debug,
            new EventId(2, nameof(ForwardAudioToReceivers)),
            "{DisplayName} ({ClientId}) transmitting on {FrequencyCount} frequency(ies)");

    private static readonly Action<ILogger, string, string, int, int, Exception?> LogSessionRemoved =
        LoggerMessage.Define<string, string, int, int>(
            LogLevel.Information,
            new EventId(4, nameof(RemoveSession)),
            "Removed session for {DisplayName} ({ClientId}) - Sent: {Sent}, Dropped: {Dropped}");

    private static readonly Action<ILogger, string, string, string, Exception?> LogEndpointMapped =
        LoggerMessage.Define<string, string, string>(
            LogLevel.Debug,
            new EventId(5, nameof(ReceiveAudioLoop)),
            "Mapped endpoint {Endpoint} to {DisplayName} ({ClientId})");


    public AudioStreamServer(
        FrequencyChannelManager channelManager,
        ConcurrentDictionary<string, ClientSession> clients,
        ILoggerFactory loggerFactory,
        int audioPort)
    {
        _channelManager = channelManager;
        _clients = clients;
        _logger = loggerFactory.CreateLogger<AudioStreamServer>();
        _audioPort = audioPort;

        // Create single shared UDP client
        _udpClient = new UdpClient(_audioPort);
        _udpClient.DontFragment = true;

        // According to MS KB263823, sending a UDP packet to a client that is no longer listening will cause a
        // WSAECONNRESET (10054) for any further socket operations (even recv()). Disable SIO_UDP_CONNRESET  
        if (OperatingSystem.IsWindows())
        {
            const int sioUdpConnReset = -1744830452;
            _udpClient.Client.IOControl((IOControlCode)sioUdpConnReset, new byte[] { 0 }, null);
        }

        // Start single receive loop for all clients
        _receiveTask = Task.Run(ReceiveAudioLoop);

        _logger.LogInformation("AudioStreamServer initialized on UDP port {Port}", _audioPort);
    }

    private string GetDisplayName(string? clientId)
    {
        if (clientId == null) return "Unnamed";
        if (_clients.TryGetValue(clientId, out var session))
        {
            return !string.IsNullOrWhiteSpace(session.DisplayName) ? session.DisplayName : "Unnamed";
        }

        return "Unnamed";
    }

    /// <summary>
    /// Create audio session: registers the client, returns the shared port
    /// </summary>
    public int CreateAudioSession(string clientId)
    {
        var session = new AudioStreamSession
        {
            ClientId = clientId,
            Port = _audioPort
        };

        _sessions[clientId] = session;

        LogAudioSessionCreated(_logger, GetDisplayName(clientId), clientId, null);

        return _audioPort;
    }

    /// <summary>
    /// Single receive loop that handles all clients
    /// Demultiplexes based on remote endpoint
    /// </summary>
    private async Task ReceiveAudioLoop()
    {
        while (!_cts.Token.IsCancellationRequested)
        {
            try
            {
                var result = await _udpClient.ReceiveAsync(_cts.Token);
                // We are actually stopping, bail out
                if (_stopping) break;

                var remoteEndpoint = result.RemoteEndPoint;

                // Check if we already know this endpoint
                if (_endpointToClient.TryGetValue(remoteEndpoint, out var clientId))
                {
                    // Verify client still exists
                    if (!_sessions.ContainsKey(clientId))
                    {
                        _endpointToClient.TryRemove(remoteEndpoint, out _);
                        clientId = null;
                    }
                    else if (_sessions.TryGetValue(clientId, out var knownSession))
                    {
                        // Update heartbeat for all packets from known clients, including
                        // keepalives (no metadata extension) that the parser discards below.
                        knownSession.LastReceived = DateTime.UtcNow;
                    }
                }

                // Parse packet
                var (rtpPacket, metadata, audioData) = ParseRtpAudioPacket(result.Buffer, clientId);

                // Skip malformed packets
                if (rtpPacket == null || metadata == null || audioData == null)
                    continue;

                // Use metadata to identify/remap
                if (clientId == null)
                {
                    if (!string.IsNullOrEmpty(metadata.ClientId) &&
                        _sessions.ContainsKey(metadata.ClientId) &&
                        _clients.TryGetValue(metadata.ClientId, out var claimedClient) &&
                        claimedClient.IsAuthenticated)
                    {
                        clientId = metadata.ClientId;
                        _endpointToClient[remoteEndpoint] = clientId;

                        LogEndpointMapped(_logger, remoteEndpoint.ToString(),
                            GetDisplayName(clientId), clientId, null);
                    }
                    else
                    {
                        if (_logger.IsEnabled(LogLevel.Debug))
                            _logger.LogDebug("Received packet from unauthenticated client {ClientId} at {Endpoint}",
                                metadata.ClientId, remoteEndpoint);
                        continue;
                    }
                }

                // Verify metadata matches mapped clientId
                if (metadata.ClientId != clientId)
                {
                    _logger.LogWarning(
                        "Endpoint {Endpoint} mapped to {MappedClient} but packet claims {ClaimedClient}",
                        remoteEndpoint, clientId, metadata.ClientId);
                    continue;
                }

                // Update session state
                if (!_sessions.TryGetValue(clientId, out var session))
                    continue;

                session.LastReceived = DateTime.UtcNow;
                session.RemoteEndPoint = remoteEndpoint;

                // Validate frequencies
                if (metadata.Frequencies.Count == 0)
                {
                    // Keepalive from client — echo back a pong to keep the server→client
                    // NAT path alive. Without this, NAT entries expire after ~5 min of
                    // silence (no audio to relay) and the client stops hearing audio.
                    SendPacket(_keepalivePong, remoteEndpoint);
                    continue;
                }

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
                        .Select(f => $"{f.Khz / 1000d:F3} MHz")
                        .ToList();

                    if (_logger.IsEnabled(LogLevel.Warning))
                        _logger.LogWarning(
                            "{DisplayName} ({ClientId}) attempted to transmit on unjoined frequencies: {Frequencies}",
                            GetDisplayName(clientId), clientId, string.Join(", ", invalidMhz));
                }

                if (validFrequencies.Count == 0)
                    continue;

                if (_logger.IsEnabled(LogLevel.Debug))
                    LogTransmittingOnFrequencies(_logger, GetDisplayName(clientId), clientId,
                        validFrequencies.Count, null);

                // Forward audio to all receivers — deduplicated so a client on multiple matching
                // frequencies gets exactly one packet (metadata contains all frequencies).
                ForwardAudioToReceivers(validFrequencies.Select(f => f.Khz), clientId, rtpPacket, metadata, audioData);
            }

            catch (SocketException ex)
            {
                // Just warn and continue - this will also be raised on error 10054 (host forcibly closed connection)
                _logger.LogWarning(ex, "Socket error in audio receive loop");
            }
            catch (OperationCanceledException)
            {
                // Normal shutdown
                break;
            }
            catch (Exception ex)
            {
                // something has broken more fundamentally
                _logger.LogError(ex, "Error in shared audio receive loop");
            }
        }
    }

    /// <summary>
    /// Parses a UDP RTP audio packet with metadata in the header extension
    /// Packet format: [12 bytes RTP header][4 bytes ext header][N bytes JSON metadata + padding][audio data]
    /// </summary>
    private (RtpPacket? rtpPacket, AudioPacketMetadata? metadata, byte[]? audioData) ParseRtpAudioPacket(
        byte[] packet,
        string? clientId)
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
                        GetDisplayName(clientId), clientId ?? "unknown");
                return (null, null, null);
            }

            // Keepalive packets have no extension — silently ignore
            if (rtpPacket.ExtensionData is not { Length: > 0 })
                return (null, null, null);

            var metadataJson = Encoding.UTF8.GetString(rtpPacket.ExtensionData).TrimEnd('\0');
            var metadata = Json.Json.Instance.Deserialize<AudioPacketMetadata>(metadataJson);
            if (metadata != null) return (rtpPacket, metadata, rtpPacket.Payload);
            if (_logger.IsEnabled(LogLevel.Warning))
                _logger.LogWarning("Failed to deserialize metadata from {DisplayName} ({ClientId})",
                    GetDisplayName(clientId), clientId ?? "unknown");
            return (null, null, null);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error parsing RTP audio packet from {DisplayName} ({ClientId})",
                GetDisplayName(clientId), clientId ?? "unknown");
            return (null, null, null);
        }
    }

    /// <summary>
    /// Creates an RTP audio packet for a specific receiver
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

        // Stamp server send time into metadata
        metadata.ServerSendTimestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var metadataJson = Json.Json.Instance.Serialize(metadata);
        var metadataBytes = Encoding.UTF8.GetBytes(metadataJson);

        // Create new RTP packet with server's sequence number
        var rtpPacket = new RtpPacket
        {
            Version = 2,
            PayloadType = originalRtpPacket.PayloadType,
            SequenceNumber = rtpState.NextSequence,
            Timestamp = originalRtpPacket.Timestamp,
            Ssrc = originalRtpPacket.Ssrc,
            Marker = originalRtpPacket.Marker,
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
    /// Forward audio to all unique receivers across the given frequency channels.
    /// A receiver joined to multiple matching frequencies receives exactly one packet.
    /// </summary>
    private void ForwardAudioToReceivers(
        IEnumerable<int> frequencyKhzList,
        string sourceClientId,
        RtpPacket originalRtpPacket,
        AudioPacketMetadata metadata,
        byte[] audioData)
    {
        // Collect unique receivers across all matched channels.
        var seen = new HashSet<string>();
        foreach (var frequencyKhz in frequencyKhzList)
        {
            foreach (var clientId in _channelManager.GetClientsInChannel(frequencyKhz))
            {
                if (clientId != sourceClientId)
                    seen.Add(clientId);
            }
        }

        foreach (var clientId in seen)
        {
            if (!_sessions.TryGetValue(clientId, out var targetSession))
                continue;

            if (targetSession.RemoteEndPoint == null)
            {
                _logger.LogWarning(
                    "Cannot relay audio to {DisplayName} ({ClientId}): no RTP endpoint registered yet",
                    GetDisplayName(clientId), clientId);
                continue;
            }

            var rtpPacket = CreateRtpAudioPacket(clientId, originalRtpPacket, metadata, audioData);
            SendPacket(rtpPacket, targetSession.RemoteEndPoint);
        }
    }

    private void SendPacket(byte[] packet, IPEndPoint remoteEndPoint)
    {
        if (_stopping) return;
        try
        {
            // UdpClient is not thread-safe, so...
            lock (_udpSendLock)
            {
                _udpClient.Send(packet, packet.Length, remoteEndPoint);
            }
        }
        catch (SocketException ex)
        {
            // We don't care for network exceptions - let's just assume that FEC and PLC help us
            if (_logger.IsEnabled(LogLevel.Debug))
                _logger.LogDebug(ex, "Network send failed to {Endpoint}: {ErrorCode}",
                    remoteEndPoint, ex.SocketErrorCode);
        }
        catch (ObjectDisposedException)
        {
            // Can happen during Dispose(), don't care
        }
    }

    public DateTime? GetLastRtpReceived(string clientId) =>
        _sessions.TryGetValue(clientId, out var s) ? s.LastReceived : null;

    public void RemoveSession(string clientId)
    {
        if (!_sessions.TryRemove(clientId, out _)) return;

        _clients.TryGetValue(clientId, out var client);

        // Remove endpoint mapping
        var staleEndpoints = _endpointToClient
            .Where(kvp => kvp.Value == clientId)
            .Select(kvp => kvp.Key)
            .ToList();

        foreach (var endpoint in staleEndpoints)
            _endpointToClient.TryRemove(endpoint, out _);

        // Remove RTP states
        foreach (var key in _receiverRtpStates.Keys.Where(k => k.clientId == clientId).ToList())
            _receiverRtpStates.TryRemove(key, out _);
    }

    public void Stop()
    {
        if (_stopping) return;
        _stopping = true;

        // Cancel receive loop
        _cts.Cancel();

        try
        {
            // Give the _receiveTask some more time to shut down properly
            _receiveTask?.Wait();
        }
        catch (AggregateException ex) when (ex.InnerException is OperationCanceledException)
        {
            // Expected due to ReceiveAsync being cancelled
        }

        lock (_udpSendLock)
        {
            _udpClient.Dispose();
        }

        _cts.Dispose();

        _sessions.Clear();
        _endpointToClient.Clear();
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
[SuppressMessage("ReSharper", "UnusedAutoPropertyAccessor.Global")]
public class ReceiverRtpStats
{
    public string ClientId { get; set; } = "";
    public uint Ssrc { get; set; }
    public long PacketsSent { get; set; }
    public ushort CurrentSequence { get; set; }
}

[SuppressMessage("ReSharper", "UnusedAutoPropertyAccessor.Global")]
public class AudioStreamSession
{
    public string ClientId { get; set; } = string.Empty;
    public int Port { get; set; }
    public IPEndPoint? RemoteEndPoint { get; set; }
    public DateTime LastReceived { get; set; } = DateTime.UtcNow;
}

[SuppressMessage("ReSharper", "UnusedAutoPropertyAccessor.Global")]
public class ClientStreamStats
{
    public string ClientId { get; set; } = string.Empty;
    public int SentPackets { get; set; }
    public int DroppedPackets { get; set; }
    public int PendingSends { get; set; }
    public DateTime LastSendTime { get; set; }
}
