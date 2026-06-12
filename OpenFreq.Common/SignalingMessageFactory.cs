using System.Text.Json;
using System.Text.Json.Serialization.Metadata;

namespace OpenFreq.Common.Signaling;

public static class SignalingMessageFactory
{
    public static SignalingMessage CreateAuthenticate(string password, string? displayName, string? version = null)
    {
        return new SignalingMessage
        {
            Type = SignalingMessageTypes.Authenticate,
            Payload = JsonSerializer.SerializeToElement(
                new AuthenticateMessage { Password = password, DisplayName = displayName, Version = version },
                OpenFreqJsonContext.Default.AuthenticateMessage)
        };
    }

    public static SignalingMessage CreateJoin(int frequencyKhz)
    {
        return new SignalingMessage
        {
            Type = SignalingMessageTypes.Join,
            Payload = JsonSerializer.SerializeToElement(
                new JoinChannelMessage { FrequencyKhz = frequencyKhz },
                OpenFreqJsonContext.Default.JoinChannelMessage)
        };
    }

    public static SignalingMessage CreateLeave(int frequencyKhz)
    {
        return new SignalingMessage
        {
            Type = SignalingMessageTypes.Leave,
            Payload = JsonSerializer.SerializeToElement(
                new LeaveChannelMessage { FrequencyKhz = frequencyKhz },
                OpenFreqJsonContext.Default.LeaveChannelMessage)
        };
    }

    public static SignalingMessage CreateTransmission(int frequencyKhz, bool transmitting, bool is3d)
    {
        return new SignalingMessage
        {
            Type = SignalingMessageTypes.Transmission,
            Payload = JsonSerializer.SerializeToElement(
                new AudioTransmissionMessage
                {
                    FrequencyKhz = frequencyKhz,
                    Transmitting = transmitting,
                    Is3d = is3d
                },
                OpenFreqJsonContext.Default.AudioTransmissionMessage)
        };
    }

    public static SignalingMessage CreateSuccess(string message, SortedDictionary<int, List<PeerData>> peers, string? peerId = null, int? audioPort = null, bool opusEnabled = true)
    {
        return new SignalingMessage
        {
            Type = SignalingMessageTypes.Success,
            Payload = JsonSerializer.SerializeToElement(
                new SuccessMessage
                {
                    Message = message,
                    PeerId = peerId,
                    AudioPort = audioPort,
                    OpusCompressionEnabled = opusEnabled,
                    FrequenciesPeers = peers
                },
                OpenFreqJsonContext.Default.SuccessMessage)
        };
    }

    public static SignalingMessage CreateError(string error, string? serverVersion = null)
    {
        return new SignalingMessage
        {
            Type = SignalingMessageTypes.Error,
            Payload = JsonSerializer.SerializeToElement(
                new ErrorMessage { Error = error, ServerVersion = serverVersion },
                OpenFreqJsonContext.Default.ErrorMessage)
        };
    }

    public static SignalingMessage CreatePeerJoined(string peerId, string? peerDisplayName, int frequencyKhz)
    {
        return new SignalingMessage
        {
            Type = SignalingMessageTypes.PeerJoined,
            Payload = JsonSerializer.SerializeToElement(
                new PeerJoinedMessage
                {
                    PeerId = peerId,
                    PeerDisplayName = peerDisplayName ?? "Unnamed",
                    FrequencyKhz = frequencyKhz
                },
                OpenFreqJsonContext.Default.PeerJoinedMessage)
        };
    }

    public static SignalingMessage CreatePeerLeft(string peerId, int frequencyKhz)
    {
        return new SignalingMessage
        {
            Type = SignalingMessageTypes.PeerLeft,
            Payload = JsonSerializer.SerializeToElement(
                new PeerLeftMessage
                {
                    PeerId = peerId,
                    FrequencyKhz = frequencyKhz
                },
                OpenFreqJsonContext.Default.PeerLeftMessage)
        };
    }

    public static SignalingMessage CreateSetDisplayName(string displayName)
    {
        return new SignalingMessage
        {
            Type = SignalingMessageTypes.SetDisplayName,
            Payload = JsonSerializer.SerializeToElement(
                new DisplayNameMessage { DisplayName = displayName },
                OpenFreqJsonContext.Default.DisplayNameMessage)
        };
    }

    public static SignalingMessage CreateTransmissionEvent(string peerId, int frequencyKhz, bool transmitting, bool is3d)
    {
        return new SignalingMessage
        {
            Type = SignalingMessageTypes.Transmission,
            Payload = JsonSerializer.SerializeToElement(
                new TransmissionEventMessage
                {
                    PeerId = peerId,
                    FrequencyKhz = frequencyKhz,
                    Transmitting = transmitting,
                    Is3d = is3d
                },
                OpenFreqJsonContext.Default.TransmissionEventMessage)
        };
    }

    public static SignalingMessage CreateChannelState(int frequencyKhz, List<ChannelStateMessage.Peer> peers)
    {
        return new SignalingMessage
        {
            Type = SignalingMessageTypes.ChannelState,
            Payload = JsonSerializer.SerializeToElement(
                new ChannelStateMessage
                {
                    FrequencyKhz = frequencyKhz,
                    Peers = peers
                },
                OpenFreqJsonContext.Default.ChannelStateMessage)
        };
    }

    public static SignalingMessage CreateAllPeersStatusMessage(SortedDictionary<int, List<PeerData>> allPeersStatus)
    {
        return new SignalingMessage
        {
            Type = SignalingMessageTypes.AllPeersStatus,
            Payload = JsonSerializer.SerializeToElement(
                new AllPeersStatusMessage()
                {
                    FrequenciesPeers = allPeersStatus
                },
                OpenFreqJsonContext.Default.AllPeersStatusMessage)
        };
    }

    public static SignalingMessage CreateModeUpdate(bool is3d)
    {
        return new SignalingMessage
        {
            Type = SignalingMessageTypes.ModeUpdate,
            Payload = JsonSerializer.SerializeToElement(
                new ModeUpdateMessage { Is3d = is3d },
                OpenFreqJsonContext.Default.ModeUpdateMessage)
        };
    }

    public static T? DeserializePayload<T>(JsonElement? payload) where T : class
    {
        if (payload == null)
            return null;

        var typeInfo = (JsonTypeInfo<T>)OpenFreqJsonContext.Default.GetTypeInfo(typeof(T))!;
        return payload.Value.Deserialize(typeInfo);
    }
}
