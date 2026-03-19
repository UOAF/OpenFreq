using System.Text.Json;
using System.Text.Json.Serialization;

namespace OpenFreq.Common;

/// <summary>
/// Base signaling message wrapper
/// </summary>
public class SignalingMessage
{
    [JsonPropertyName("type")] public string Type { get; set; } = string.Empty;

    [JsonPropertyName("payload")] public JsonElement? Payload { get; set; }
}

/// <summary>
/// Authentication request message
/// </summary>
public class AuthenticateMessage
{
    [JsonPropertyName("password")] public string? Password { get; set; }
    [JsonPropertyName("displayName")] public string? DisplayName { get; set; }
}

/// <summary>
/// Join frequency channel request
/// </summary>
public class JoinChannelMessage
{
    [JsonPropertyName("frequency")] public int FrequencyKhz { get; set; }
}

/// <summary>
/// Leave frequency channel request
/// </summary>
public class LeaveChannelMessage
{
    [JsonPropertyName("frequency")] public int FrequencyKhz { get; set; }
}

/// <summary>
/// Audio transmission state change message
/// </summary>
public class AudioTransmissionMessage
{
    [JsonPropertyName("frequency")] public int FrequencyKhz { get; set; }

    [JsonPropertyName("transmitting")] public bool Transmitting { get; set; }
    
    [JsonPropertyName("3d")] public bool Is3d { get; set; } 
}

/// <summary>
/// Error response message
/// </summary>
public class ErrorMessage
{
    [JsonPropertyName("error")] public string Error { get; set; } = string.Empty;
}

/// <summary>
/// Success response message
/// </summary>
public class SuccessMessage
{
    [JsonPropertyName("message")] public string Message { get; set; } = string.Empty;

    [JsonPropertyName("peerId")] public string? PeerId { get; set; }

    [JsonPropertyName("audioPort")] public int? AudioPort { get; set; }
    
    [JsonPropertyName("opusCompression")] public bool OpusCompressionEnabled { get; set; }
    
    [JsonPropertyName("frequencies")] public Dictionary<int, List<PeerData>> FrequenciesPeers { get; set; } = [];

}

/// <summary>
/// Peer joined channel notification
/// </summary>
public class PeerJoinedMessage
{
    [JsonPropertyName("peerId")] public string PeerId { get; set; } = string.Empty;
    [JsonPropertyName("peerDisplayName")] public string PeerDisplayName { get; set; } = string.Empty;

    [JsonPropertyName("frequency")] public int FrequencyKhz { get; set; }
}

/// <summary>
/// Peer left channel notification
/// </summary>
public class PeerLeftMessage
{
    [JsonPropertyName("peerId")] public string PeerId { get; set; } = string.Empty;

    [JsonPropertyName("frequency")] public int FrequencyKhz { get; set; }
}

/// <summary>
/// Transmission state change notification from peer
/// </summary>
public class TransmissionEventMessage
{
    [JsonPropertyName("peerId")] public string PeerId { get; set; } = string.Empty;
    [JsonPropertyName("peerDisplayName")] public string PeerDisplayName { get; set; } = string.Empty;
    [JsonPropertyName("transmitting")] public bool Transmitting { get; set; }
    [JsonPropertyName("frequency")] public int FrequencyKhz { get; set; }
    [JsonPropertyName("3d")] public bool Is3d { get; set; }
}

/// <summary>
/// Channel state with list of peers
/// </summary>
public class ChannelStateMessage
{
    [JsonPropertyName("frequency")] public int FrequencyKhz { get; set; }

    [JsonPropertyName("peers")] public List<Peer> Peers { get; set; } = [];

    public class Peer
    {
        [JsonPropertyName("id")] public string Id { get; set; } = string.Empty;
        [JsonPropertyName("displayname")] public string DisplayName { get; set; } = string.Empty;

        public Peer(string id, string displayName)
        {
            Id = id;
            DisplayName = displayName;
        }
    }
}

/// <summary>
/// Update display name
/// </summary>
public class DisplayNameMessage
{
    [JsonPropertyName("displayname")] public string DisplayName { get; set; }  = string.Empty;
}


/// <summary>
/// Peer list has changed
/// </summary>
public class AllPeersStatusMessage
{
    [JsonPropertyName("frequencies")] public Dictionary<int, List<PeerData>> FrequenciesPeers { get; set; } = [];
}