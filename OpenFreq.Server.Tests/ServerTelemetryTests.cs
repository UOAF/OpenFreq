using System.Net;
using Microsoft.Extensions.Logging.Abstractions;
using OpenFreqServer;
using OpenFreqServer.Telemetry;

namespace OpenFreq.Server.Tests;

public sealed class ServerTelemetryTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), $"openfreq-server-telemetry-{Guid.NewGuid():N}");

    [Fact]
    public async Task WritesAllowlistedEnvelopeWithoutDisplayNameOrNetworkAddress()
    {
        var runId = Guid.Parse("11111111-1111-1111-1111-111111111111");
        var config = new ServerConfig
        {
            TelemetryEndpoint = "https://telemetry.example/v1/batches",
            TelemetrySigningKey = "unit-test-signing-key-at-least-32-bytes",
            TelemetryEventId = "UOAF-TEST"
        };
        using var http = new HttpClient(new NeverCalledHandler());
        await using (var telemetry = new ServerTelemetryService(config, runId,
                         new Uri(config.TelemetryEndpoint), _directory,
                         NullLogger<ServerTelemetryService>.Instance, http))
        {
            telemetry.Track("server.audio.endpoint.mapped", "connection-1",
                new Dictionary<string, object?> { ["address_family"] = "internetwork" });
        }

        var path = Assert.Single(Directory.GetFiles(_directory, "server-*.jsonl"));
        var json = await File.ReadAllTextAsync(path);
        Assert.Contains("\"event_name\":\"server.audio.endpoint.mapped\"", json, StringComparison.Ordinal);
        Assert.Contains("\"server_run_id\":\"11111111-1111-1111-1111-111111111111\"", json,
            StringComparison.Ordinal);
        Assert.Contains("\"connection_id\":\"connection-1\"", json, StringComparison.Ordinal);
        Assert.DoesNotContain("display_name", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("remote_endpoint", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(Environment.MachineName, json, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void FactoryRejectsPlaintextRemoteEndpoint()
    {
        var config = new ServerConfig
        {
            TelemetryEndpoint = "http://telemetry.example/v1/batches",
            TelemetrySigningKey = "unit-test-signing-key-at-least-32-bytes"
        };

        var telemetry = ServerTelemetryService.Create(config, Guid.NewGuid(), NullLoggerFactory.Instance, _directory);

        Assert.False(telemetry.IsEnabled);
    }

    public void Dispose()
    {
        if (Directory.Exists(_directory)) Directory.Delete(_directory, recursive: true);
    }

    private sealed class NeverCalledHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,
            CancellationToken cancellationToken) =>
            throw new InvalidOperationException("Uploader should not run during this unit test");
    }
}
