using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using OpenFreq.Common;

namespace OpenFreqServer.Json;

public class Json : JsonSerializerBase
{
    public static Json Instance { get; } = new();
    
    protected override JsonSerializerOptions Options { get; } = new()
    {
        TypeInfoResolver = JsonTypeInfoResolver.Combine(
            OpenFreqJsonContext.Default,
            OpenFreqServer.Json.ServerJsonContext.Default)
    };
}