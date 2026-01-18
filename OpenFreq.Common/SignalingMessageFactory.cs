using System.Text.Json;

namespace OpenFreq.Common.Signaling;

/// <summary>
/// Helper class for creating and serializing signaling messages
/// </summary>
public static class SignalingMessageFactory
{
    private static readonly JsonSerializerOptions SerializerOptions = OpenFreqJsonContext.Default.Options;

    /// <summary>
    /// Create an authentication message
    /// </summary>
    public static SignalingMessage CreateAuthenticate(string password)
    {
        return new SignalingMessage
        {
            Type = SignalingMessageTypes.Authenticate,
            Payload = JsonSerializer.SerializeToElement(new AuthenticateMessage { Password = password }, SerializerOptions)
        };
    }

    /// <summary>
    /// Create a join channel message
    /// </summary>
    public static SignalingMessage CreateJoin(double frequencyMhz)
    {
        return new SignalingMessage
        {
            Type = SignalingMessageTypes.Join,
            Payload = JsonSerializer.SerializeToElement(new JoinChannelMessage { FrequencyMhz = frequencyMhz }, SerializerOptions)
        };
    }

    /// <summary>
    /// Create a leave channel message
    /// </summary>
    public static SignalingMessage CreateLeave(double frequencyMhz)
    {
        return new SignalingMessage
        {
            Type = SignalingMessageTypes.Leave,
            Payload = JsonSerializer.SerializeToElement(new LeaveChannelMessage { FrequencyMhz = frequencyMhz }, SerializerOptions)
        };
    }

    /// <summary>
    /// Create a transmission state message
    /// </summary>
    public static SignalingMessage CreateTransmission(double frequency, bool transmitting)
    {
        return new SignalingMessage
        {
            Type = SignalingMessageTypes.Transmission,
            Payload = JsonSerializer.SerializeToElement(new AudioTransmissionMessage 
            { 
                FrequencyMhz = frequency, 
                Transmitting = transmitting 
            }, SerializerOptions)
        };
    }

    /// <summary>
    /// Create a success response message
    /// </summary>
    public static SignalingMessage CreateSuccess(string message, string? peerId = null, int? audioPort = null, bool opusEnabled = true)
    {
        return new SignalingMessage
        {
            Type = SignalingMessageTypes.Success,
            Payload = JsonSerializer.SerializeToElement(new SuccessMessage 
            { 
                Message = message, 
                PeerId = peerId, 
                AudioPort = audioPort,
                OpusCompressionEnabled = opusEnabled
            }, SerializerOptions)
        };
    }

    /// <summary>
    /// Create an error response message
    /// </summary>
    public static SignalingMessage CreateError(string error)
    {
        return new SignalingMessage
        {
            Type = SignalingMessageTypes.Error,
            Payload = JsonSerializer.SerializeToElement(new ErrorMessage { Error = error }, SerializerOptions)
        };
    }

    /// <summary>
    /// Create a peer joined notification
    /// </summary>
    public static SignalingMessage CreatePeerJoined(string peerId, double frequencyMhz)
    {
        return new SignalingMessage
        {
            Type = SignalingMessageTypes.PeerJoined,
            Payload = JsonSerializer.SerializeToElement(new PeerJoinedMessage 
            { 
                PeerId = peerId, 
                FrequencyMhz = frequencyMhz 
            }, SerializerOptions)
        };
    }

    /// <summary>
    /// Create a peer left notification
    /// </summary>
    public static SignalingMessage CreatePeerLeft(string peerId, double frequencyMhz)
    {
        return new SignalingMessage
        {
            Type = SignalingMessageTypes.PeerLeft,
            Payload = JsonSerializer.SerializeToElement(new PeerLeftMessage 
            { 
                PeerId = peerId, 
                FrequencyMhz = frequencyMhz 
            }, SerializerOptions)
        };
    }

    /// <summary>
    /// Create a transmission event notification
    /// </summary>
    public static SignalingMessage CreateTransmissionEvent(string peerId, double frequencyMhz, bool transmitting)
    {
        return new SignalingMessage
        {
            Type = SignalingMessageTypes.Transmission,
            Payload = JsonSerializer.SerializeToElement(new TransmissionEventMessage 
            { 
                PeerId = peerId, 
                FrequencyMhz = frequencyMhz, 
                Transmitting = transmitting 
            }, SerializerOptions)
        };
    }

    /// <summary>
    /// Create a channel state message
    /// </summary>
    public static SignalingMessage CreateChannelState(double frequencyMhz, List<string> peers)
    {
        return new SignalingMessage
        {
            Type = SignalingMessageTypes.ChannelState,
            Payload = JsonSerializer.SerializeToElement(new ChannelStateMessage 
            { 
                FrequencyMhz = frequencyMhz, 
                Peers = peers 
            }, SerializerOptions)
        };
    }

    /// <summary>
    /// Deserialize a payload to a specific type
    /// </summary>
    public static T? DeserializePayload<T>(JsonElement? payload) where T : class
    {
        return payload?.Deserialize<T>(SerializerOptions);
    }
}
