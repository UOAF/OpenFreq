using System.Text.Json;
using System.Text.Json.Serialization.Metadata;

namespace OpenFreq.Common.Signaling;

public static class SignalingMessageFactory
{
    public static SignalingMessage CreateAuthenticate(string password)
    {
        return new SignalingMessage
        {
            Type = SignalingMessageTypes.Authenticate,
            Payload = JsonSerializer.SerializeToElement(
                new AuthenticateMessage { Password = password }, 
                OpenFreqJsonContext.Default.AuthenticateMessage)
        };
    }

    public static SignalingMessage CreateJoin(double frequencyMhz)
    {
        return new SignalingMessage
        {
            Type = SignalingMessageTypes.Join,
            Payload = JsonSerializer.SerializeToElement(
                new JoinChannelMessage { FrequencyMhz = frequencyMhz }, 
                OpenFreqJsonContext.Default.JoinChannelMessage)
        };
    }

    public static SignalingMessage CreateLeave(double frequencyMhz)
    {
        return new SignalingMessage
        {
            Type = SignalingMessageTypes.Leave,
            Payload = JsonSerializer.SerializeToElement(
                new LeaveChannelMessage { FrequencyMhz = frequencyMhz }, 
                OpenFreqJsonContext.Default.LeaveChannelMessage)
        };
    }

    public static SignalingMessage CreateTransmission(double frequency, bool transmitting)
    {
        return new SignalingMessage
        {
            Type = SignalingMessageTypes.Transmission,
            Payload = JsonSerializer.SerializeToElement(
                new AudioTransmissionMessage 
                { 
                    FrequencyMhz = frequency, 
                    Transmitting = transmitting 
                }, 
                OpenFreqJsonContext.Default.AudioTransmissionMessage)
        };
    }

    public static SignalingMessage CreateSuccess(string message, string? peerId = null, int? audioPort = null, bool opusEnabled = true)
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
                    OpusCompressionEnabled = opusEnabled
                }, 
                OpenFreqJsonContext.Default.SuccessMessage)
        };
    }

    public static SignalingMessage CreateError(string error)
    {
        return new SignalingMessage
        {
            Type = SignalingMessageTypes.Error,
            Payload = JsonSerializer.SerializeToElement(
                new ErrorMessage { Error = error }, 
                OpenFreqJsonContext.Default.ErrorMessage)
        };
    }

    public static SignalingMessage CreatePeerJoined(string peerId, double frequencyMhz)
    {
        return new SignalingMessage
        {
            Type = SignalingMessageTypes.PeerJoined,
            Payload = JsonSerializer.SerializeToElement(
                new PeerJoinedMessage 
                { 
                    PeerId = peerId, 
                    FrequencyMhz = frequencyMhz 
                }, 
                OpenFreqJsonContext.Default.PeerJoinedMessage)
        };
    }

    public static SignalingMessage CreatePeerLeft(string peerId, double frequencyMhz)
    {
        return new SignalingMessage
        {
            Type = SignalingMessageTypes.PeerLeft,
            Payload = JsonSerializer.SerializeToElement(
                new PeerLeftMessage 
                { 
                    PeerId = peerId, 
                    FrequencyMhz = frequencyMhz 
                }, 
                OpenFreqJsonContext.Default.PeerLeftMessage)
        };
    }

    public static SignalingMessage CreateTransmissionEvent(string peerId, double frequencyMhz, bool transmitting)
    {
        return new SignalingMessage
        {
            Type = SignalingMessageTypes.Transmission,
            Payload = JsonSerializer.SerializeToElement(
                new TransmissionEventMessage 
                { 
                    PeerId = peerId, 
                    FrequencyMhz = frequencyMhz, 
                    Transmitting = transmitting 
                }, 
                OpenFreqJsonContext.Default.TransmissionEventMessage)
        };
    }

    public static SignalingMessage CreateChannelState(double frequencyMhz, List<string> peers)
    {
        return new SignalingMessage
        {
            Type = SignalingMessageTypes.ChannelState,
            Payload = JsonSerializer.SerializeToElement(
                new ChannelStateMessage 
                { 
                    FrequencyMhz = frequencyMhz, 
                    Peers = peers 
                }, 
                OpenFreqJsonContext.Default.ChannelStateMessage)
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