using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using OpenFreqServer;

namespace OpenFreq.Server.Tests;

public class TelemetryTokenIssuerTests
{
    [Fact]
    public void MissingConfigurationDoesNotIssueToken()
    {
        Assert.Null(TelemetryTokenIssuer.TryIssue(new ServerConfig(), Guid.NewGuid()));
    }

    [Fact]
    public void IssuedTokenHasValidSignatureAndCorrelation()
    {
        const string key = "test-signing-key-with-enough-entropy-32";
        var runId = Guid.NewGuid();
        var now = DateTimeOffset.FromUnixTimeSeconds(1_800_000_000);
        var issued = TelemetryTokenIssuer.TryIssue(new ServerConfig
        {
            TelemetryEndpoint = "https://telemetry.example/v1/batches",
            TelemetrySigningKey = key,
            TelemetryEventId = "UOAF-TEST"
        }, runId, now);

        Assert.NotNull(issued);
        var parts = issued.Token.Split('.');
        Assert.Equal(2, parts.Length);
        var expectedSignature = HMACSHA256.HashData(
            Encoding.UTF8.GetBytes(key), Encoding.ASCII.GetBytes(parts[0]));
        Assert.Equal(expectedSignature, Decode(parts[1]));

        using var payload = JsonDocument.Parse(Decode(parts[0]));
        Assert.Equal(runId, payload.RootElement.GetProperty("ServerRunId").GetGuid());
        Assert.Equal("UOAF-TEST", payload.RootElement.GetProperty("EventId").GetString());
        Assert.Equal(now.AddHours(6), issued.ExpiresAtUtc);
    }

    [Theory]
    [InlineData("http://telemetry.example/v1/batches", "test-signing-key-with-enough-entropy-32")]
    [InlineData("https://telemetry.example/v1/batches", "too-short")]
    public void InsecureConfigurationDoesNotIssueToken(string endpoint, string key)
    {
        Assert.Null(TelemetryTokenIssuer.TryIssue(new ServerConfig
        {
            TelemetryEndpoint = endpoint,
            TelemetrySigningKey = key
        }, Guid.NewGuid()));
    }

    private static byte[] Decode(string value)
    {
        value = value.Replace('-', '+').Replace('_', '/');
        value = value.PadRight(value.Length + ((4 - value.Length % 4) % 4), '=');
        return Convert.FromBase64String(value);
    }
}
