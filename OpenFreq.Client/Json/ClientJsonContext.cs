using System.Text.Json.Serialization;
using OpenFreqClient.Models;

namespace OpenFreqClient.Json;

[JsonSourceGenerationOptions(
    WriteIndented = false,
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(AppConfiguration))]
[JsonSerializable(typeof(OpenFreqAudio.RadioStationPreset))]
public partial class ClientJsonContext : JsonSerializerContext
{
}