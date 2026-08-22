using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace OpenFreqServer;

public static class TelemetryTokenIssuer
{
    public static IssuedTelemetryToken? TryIssue(ServerConfig config, Guid serverRunId,
        DateTimeOffset? now = null)
    {
        if (string.IsNullOrWhiteSpace(config.TelemetryEndpoint) ||
            string.IsNullOrWhiteSpace(config.TelemetrySigningKey))
            return null;

        if (!Uri.TryCreate(config.TelemetryEndpoint, UriKind.Absolute, out var endpoint) ||
            endpoint.Scheme is not ("https" or "http"))
            return null;

        var issuedAt = now ?? DateTimeOffset.UtcNow;
        var expiresAt = issuedAt.AddHours(6);
        var payload = new TelemetryTokenPayload
        {
            ServerRunId = serverRunId,
            EventId = config.TelemetryEventId,
            IssuedAtUnixSeconds = issuedAt.ToUnixTimeSeconds(),
            ExpiresAtUnixSeconds = expiresAt.ToUnixTimeSeconds()
        };
        var payloadJson = JsonSerializer.Serialize(payload, TelemetryTokenJsonContext.Default.TelemetryTokenPayload);
        var payloadEncoded = Base64UrlEncode(Encoding.UTF8.GetBytes(payloadJson));
        var signature = HMACSHA256.HashData(
            Encoding.UTF8.GetBytes(config.TelemetrySigningKey),
            Encoding.ASCII.GetBytes(payloadEncoded));
        return new IssuedTelemetryToken($"{payloadEncoded}.{Base64UrlEncode(signature)}", expiresAt);
    }

    private static string Base64UrlEncode(ReadOnlySpan<byte> data) =>
        Convert.ToBase64String(data).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}

public sealed record IssuedTelemetryToken(string Token, DateTimeOffset ExpiresAtUtc);

public sealed class TelemetryTokenPayload
{
    public Guid ServerRunId { get; init; }
    public string? EventId { get; init; }
    public long IssuedAtUnixSeconds { get; init; }
    public long ExpiresAtUnixSeconds { get; init; }
}

[JsonSerializable(typeof(TelemetryTokenPayload))]
internal partial class TelemetryTokenJsonContext : JsonSerializerContext
{
}
