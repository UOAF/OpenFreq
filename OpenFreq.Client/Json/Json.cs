using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using OpenFreq.Common;

namespace OpenFreqClient.Json;

public class Json : JsonSerializerBase
{
    public static Json Instance { get; } = new();
    
    protected override JsonSerializerOptions Options { get; } = new()
    {
        TypeInfoResolver = JsonTypeInfoResolver.Combine(
            OpenFreqJsonContext.Default,
            ClientJsonContext.Default)
    };
}