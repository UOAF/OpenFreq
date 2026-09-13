using System.Text.Json.Serialization;

namespace OpenFreqServer.Json;

[JsonSourceGenerationOptions(
    WriteIndented = false,
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(ServerConfig))]
public partial class ServerJsonContext : JsonSerializerContext
{
}
