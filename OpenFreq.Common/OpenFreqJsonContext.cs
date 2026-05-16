using System.Text.Json.Serialization;
using OpenFreq.Common.Signaling;

namespace OpenFreq.Common;

[JsonSourceGenerationOptions(
    WriteIndented = false,
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(SignalingMessage))]
[JsonSerializable(typeof(AudioPacketMetadata))]
[JsonSerializable(typeof(FrequencyTransmission))]
[JsonSerializable(typeof(AuthenticateMessage))]
[JsonSerializable(typeof(JoinChannelMessage))]
[JsonSerializable(typeof(LeaveChannelMessage))]
[JsonSerializable(typeof(AudioTransmissionMessage))]
[JsonSerializable(typeof(SuccessMessage))]
[JsonSerializable(typeof(ErrorMessage))]
[JsonSerializable(typeof(PeerJoinedMessage))]
[JsonSerializable(typeof(PeerLeftMessage))]
[JsonSerializable(typeof(TransmissionEventMessage))]
[JsonSerializable(typeof(ChannelStateMessage))]
[JsonSerializable(typeof(DisplayNameMessage))]
[JsonSerializable(typeof(AllPeersStatusMessage))]
[JsonSerializable(typeof(ModeUpdateMessage))]

public partial class OpenFreqJsonContext : JsonSerializerContext
{
}