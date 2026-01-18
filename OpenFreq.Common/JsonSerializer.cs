using System.Text.Json;

namespace OpenFreq.Common;

public abstract class JsonSerializerBase
{
    protected abstract JsonSerializerOptions Options { get; }

    public string Serialize<T>(T value) =>
        JsonSerializer.Serialize(value, Options);

    public T? Deserialize<T>(string json) =>
        JsonSerializer.Deserialize<T>(json, Options);

    public byte[] SerializeToUtf8Bytes<T>(T value) =>
        JsonSerializer.SerializeToUtf8Bytes(value, Options);

    public T? Deserialize<T>(ReadOnlySpan<byte> utf8Json) =>
        JsonSerializer.Deserialize<T>(utf8Json, Options);
}