namespace OpenFreq.Common.Signaling;

/// <summary>
/// Constants for signaling message types
/// </summary>
public static class SignalingMessageTypes
{
    public const string Authenticate = "authenticate";
    public const string Join = "join";
    public const string Leave = "leave";
    public const string Transmission = "transmission";
    public const string Success = "success";
    public const string Error = "error";
    public const string PeerJoined = "peer-joined";
    public const string PeerLeft = "peer-left";
    public const string ChannelState = "channel-state";
    public const string SetDisplayName = "set-display-name";
    public const string AllPeersStatus = "all-peers-status";
}
