using System.Text.Json.Serialization;
using OpenFreqClient.Services.Telemetry;

namespace OpenFreqClient.Json;

[JsonSourceGenerationOptions(
    WriteIndented = false,
    PropertyNamingPolicy = JsonKnownNamingPolicy.SnakeCaseLower,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(TelemetryEnvelope))]
[JsonSerializable(typeof(TelemetryPlatform))]
[JsonSerializable(typeof(TelemetryCorrelation))]
[JsonSerializable(typeof(TelemetryContext))]
[JsonSerializable(typeof(TelemetryContextUpdate))]
[JsonSerializable(typeof(TelemetryCorrelationUpdate))]
[JsonSerializable(typeof(TelemetryExportManifest))]
[JsonSerializable(typeof(TelemetryUploadBatch))]
[JsonSerializable(typeof(string))]
[JsonSerializable(typeof(bool))]
[JsonSerializable(typeof(int))]
[JsonSerializable(typeof(long))]
[JsonSerializable(typeof(double))]
public partial class TelemetryJsonContext : JsonSerializerContext
{
}
