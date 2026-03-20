using System.Text.Json.Serialization;
using OpenFreqClient.Models;
using OpenFreqClient.ViewModels;

namespace OpenFreqClient.Json;

[JsonSourceGenerationOptions(
    WriteIndented = false,
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(AppConfiguration))]
[JsonSerializable(typeof(OpenFreqAudio.RadioStationPreset))]
[JsonSerializable(typeof(MapPickerViewModel.NominatimResult))]
public partial class ClientJsonContext : JsonSerializerContext
{
}