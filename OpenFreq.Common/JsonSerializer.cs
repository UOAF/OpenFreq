using System.Text.Json;
using System.Text.Json.Serialization.Metadata;

namespace OpenFreq.Common;

public abstract class JsonSerializerBase
{
    protected abstract JsonSerializerOptions Options { get; }

    public string Serialize<T>(T value)
    {
        var typeInfo = (JsonTypeInfo<T>)Options.GetTypeInfo(typeof(T));
        return JsonSerializer.Serialize(value, typeInfo);
    }

    public T? Deserialize<T>(string json)
    {
        var typeInfo = (JsonTypeInfo<T>)Options.GetTypeInfo(typeof(T));
        return JsonSerializer.Deserialize(json, typeInfo);
    }

    public byte[] SerializeToUtf8Bytes<T>(T value)
    {
        var typeInfo = (JsonTypeInfo<T>)Options.GetTypeInfo(typeof(T));
        return JsonSerializer.SerializeToUtf8Bytes(value, typeInfo);
    }

    public T? Deserialize<T>(ReadOnlySpan<byte> utf8Json)
    {
        var typeInfo = (JsonTypeInfo<T>)Options.GetTypeInfo(typeof(T));
        return JsonSerializer.Deserialize(utf8Json, typeInfo);
    }
}
