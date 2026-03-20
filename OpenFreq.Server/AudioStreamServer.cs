using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text;
using Microsoft.Extensions.Logging;
using OpenFreq.Common;
using OpenFreq.Common.Rtp;
using OpenFreqServer.Json;

namespace OpenFreq.Server;

/// <summary>
/// Audio stream server with RTP translation.
/// Acts as RTP translator: parses incoming RTP from clients, rewrites RTP headers
/// with per-receiver sequence numbers, and forwards to recipients.
/// </summary>
public class AudioStreamServer
{
    // Single shared UDP client for all clients
    private readonly UdpClient _udpClient;
    private readonly int _audioPort;

    // Map remote endpoint -> clientId (learned from first packet)
    private readonly ConcurrentDictionary<IPEndPoint, string> _endpointToClient = new();

    // Track sessions for each client
    private readonly ConcurrentDictionary<string, AudioStreamSession> _sessions = new();

    // Per-receiver RTP state (unchanged)
    private readonly ConcurrentDictionary<(string clientId, uint Ssrc), ReceiverRtpState> _receiverRtpStates = new();

    private readonly FrequencyChannelManager _channelManager;
    private readonly ConcurrentDictionary<string, ClientSession> _clients;
    private readonly ILogger<AudioStreamServer> _logger;
    private readonly UdpStreamManager _udpManager;
    private CancellationTokenSource _cts = new();

    // Single receive task for ALL clients
    private Task? _receiveTask;

    // High-performance logging delegates
    private static readonly Action<ILogger, string, string, Exception?> LogAudioSessionCreated =
        LoggerMessage.Define<string, string>(
            LogLevel.Information,
            new EventId(1, nameof(CreateAudioSession)),
            "Created audio session for {DisplayName} ({ClientId})");

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
        _udpManager = new UdpStreamManager(loggerFactory.CreateLogger<UdpStreamManager>());

        // Create SINGLE shared UDP client
        _udpClient = new UdpClient(_audioPort);

        // Start SINGLE receive loop for all clients
        _receiveTask = Task.Run(() => ReceiveAudioLoop());

        _logger.LogInformation("AudioStreamServer initialized on UDP port {Port}", _audioPort);
    }

    private string GetDisplayName(string? clientId)
    {
        if (clientId == null) return "Unnamed";
        if (_clients.TryGetValue(clientId, out var session))
        {
            return !string.IsNullOrWhiteSpace(session.DisplayName) ? session.DisplayName : "Unnamed";
        }

        // unknown clientId
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
            Port = _audioPort // Same port for everyone
        };

        _sessions[clientId] = session;

        LogAudioSessionCreated(_logger, GetDisplayName(clientId), clientId, null);

        // Return the shared port to the client
        return _audioPort;
    }

    /// <summary>
    /// Single receive loop that handles all clients
    /// Demultiplexes based on remote endpoint
    /// </summary>
    private async Task ReceiveAudioLoop()
    {
        try
        {
            while (!_cts.Token.IsCancellationRequested)
            {
                var result = await _udpClient.ReceiveAsync(_cts.Token);
                var remoteEndpoint = result.RemoteEndPoint;
                
                // Figure out which client sent this packet
                // Check if we already know this endpoint
                if (_endpointToClient.TryGetValue(remoteEndpoint, out var clientId))
                {
                    // Verify client still exists
                    if (!_sessions.ContainsKey(clientId))
                    {
                        // Stale mapping - remove and re-learn from metadata
                        _endpointToClient.TryRemove(remoteEndpoint, out _);
                        clientId = null;
                    }
                }

                // Parse packet
                var (rtpPacket, metadata, audioData) = ParseRtpAudioPacket(result.Buffer, clientId ?? "unknown");

                // Skip keepalive/malformed packets
                if (rtpPacket == null || metadata == null || audioData == null)
                    continue;

                // Use metadata to identify/remap
                if (clientId == null)
                {
                    if (!string.IsNullOrEmpty(metadata.ClientId) && _sessions.ContainsKey(metadata.ClientId))
                    {
                        clientId = metadata.ClientId;
                        _endpointToClient[remoteEndpoint] = clientId;

                        LogEndpointMapped(_logger, remoteEndpoint.ToString(),
                            GetDisplayName(clientId), clientId, null);
                    }
                    else
                    {
                        // Unknown/unauthorized sender
                        if (_logger.IsEnabled(LogLevel.Debug))
                            _logger.LogDebug("Received packet from unknown client {ClientId} at {Endpoint}",
                                metadata.ClientId, remoteEndpoint);
                        continue;
                    }
                }

                // Verify the packet's metadata matches the mapped clientId
                if (metadata.ClientId != clientId)
                {
                    _logger.LogWarning(
                        "Endpoint {Endpoint} mapped to {MappedClient} but packet claims {ClaimedClient} - possible spoofing",
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
                    continue;

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
                    LogTransmittingOnFrequencies(_logger, GetDisplayName(clientId), clientId, validFrequencies.Count,
                        null);

                // Forward audio to all specified frequencies
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
            _logger.LogError(ex, "Error in shared audio receive loop");
        }
    }

    /// <summary>
    /// Parses a UDP RTP audio packet with metadata in the header extension
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
            
            if (metadata != null) return (rtpPacket, metadata, rtpPacket.Payload);
            
            if (_logger.IsEnabled(LogLevel.Warning))
                _logger.LogWarning("Failed to deserialize metadata from {DisplayName} ({ClientId})",
                    GetDisplayName(clientId), clientId);
            return (null, null, null);

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
        var metadataJson = Json.Instance.Serialize(metadata);
        var metadataBytes = Encoding.UTF8.GetBytes(metadataJson);

        // Create new RTP packet with server's sequence number
        var rtpPacket = new RtpPacket
        {
            Version = 2,
            PayloadType = originalRtpPacket.PayloadType, // Preserve payload type (Opus/PCM)
            SequenceNumber = rtpState.NextSequence, // SERVER's sequence for this receiver
            Timestamp = originalRtpPacket.Timestamp, // Preserve original timestamp for jitter calc
            Ssrc = originalRtpPacket.Ssrc, // Preserve original SSRC
            Marker = originalRtpPacket.Marker, // Currently unused but still preserve it
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
    /// Uses the single shared UDP client to send to multiple endpoints
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

                var sent = _udpManager.SendPacketAsync(
                    clientId,
                    _udpClient,
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
        if (!_sessions.TryRemove(clientId, out _)) return;

        _clients.TryGetValue(clientId, out var client);
        _udpManager.RemoveClient(clientId);

        // Remove endpoint mapping
        var staleEndpoints = _endpointToClient
            .Where(kvp => kvp.Value == clientId)
            .Select(kvp => kvp.Key)
            .ToList();

        foreach (var endpoint in staleEndpoints)
            _endpointToClient.TryRemove(endpoint, out _);

        // Remove all per-SSRC RTP states for this receiver
        foreach (var key in _receiverRtpStates.Keys.Where(k => k.clientId == clientId).ToList())
            _receiverRtpStates.TryRemove(key, out _);

        _logger.LogInformation("Session removed for {DisplayName} ({ClientId})",
            client?.DisplayName ?? "Unnamed", clientId);
    }

    public ClientStreamStats? GetClientStats(string clientId)
    {
        return _udpManager.GetStats(clientId);
    }

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

        // Wait for receive task to finish
        _receiveTask?.Wait(TimeSpan.FromSeconds(2));

        // Close the SINGLE shared UDP client
        _udpClient.Close();
        _udpClient.Dispose();

        _sessions.Clear();
        _endpointToClient.Clear();
        _receiverRtpStates.Clear();
    }
}

// Unchanged classes below
public class ReceiverRtpState
{
    public ushort NextSequence { get; set; }
    public long PacketsSent { get; set; }
}

public class ReceiverRtpStats
{
    public string ClientId { get; set; } = "";
    public uint Ssrc { get; set; }
    public long PacketsSent { get; set; }
    public ushort CurrentSequence { get; set; }
}

public class AudioStreamSession
{
    public string ClientId { get; set; } = string.Empty;
    public int Port { get; set; }
    public IPEndPoint? RemoteEndPoint { get; set; }
    public DateTime LastReceived { get; set; } = DateTime.UtcNow;
}