using System.IO.Compression;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using OpenFreqClient.Json;
using OpenFreqClient.Models;
using OpenFreqClient.Services.Telemetry;

namespace OpenFreq.Client.Tests;

public sealed class TelemetryTests : IDisposable
{
    private readonly string _directory = Path.Combine(
        Path.GetTempPath(), "openfreq-telemetry-tests", Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task SpoolRoundTripsAllowlistedEnvelope()
    {
        using var spool = new FileTelemetrySpool(_directory);
        var expected = CreateEnvelope("audio.capture.health");

        await spool.EnqueueAsync(expected);

        var actual = new List<TelemetryEnvelope>();
        await foreach (var item in spool.ReadAllAsync()) actual.Add(item);

        var record = Assert.Single(actual);
        Assert.Equal(expected.EventRecordId, record.EventRecordId);
        Assert.Equal("audio.capture.health", record.EventName);
        Assert.Equal("VIPR11", record.Context.Callsign);
        Assert.Equal(305000, record.Attributes["frequency_khz"].GetInt32());
    }

    [Fact]
    public async Task SpoolRejectsOversizedRecord()
    {
        using var spool = new FileTelemetrySpool(_directory, new TelemetryStorageOptions
        {
            MaximumRecordBytes = 256,
            MaximumBytes = 4096,
            MaximumSegmentBytes = 1024
        });
        var envelope = CreateEnvelope("test.oversized");
        envelope.Attributes["large"] = JsonSerializer.SerializeToElement(new string('x', 1024));

        await Assert.ThrowsAsync<InvalidDataException>(async () => await spool.EnqueueAsync(envelope));
    }

    [Fact]
    public async Task DisabledServiceDoesNotCollectAndGrantedServiceExportsZip()
    {
        using var spool = new FileTelemetrySpool(_directory);
        await using var service = new TelemetryService(
            spool,
            NullLogger<TelemetryService>.Instance,
            Guid.Parse("11111111-1111-1111-1111-111111111111"));

        service.Track(new AppSessionStartedTelemetry("ignored"));
        await service.FlushAsync();
        Assert.Equal(0, (await service.GetQueueStatsAsync()).RecordCount);

        await service.ConfigureConsentAsync(new TelemetryConsent
        {
            Status = TelemetryConsentStatus.Granted,
            PolicyVersion = TelemetryConsent.CurrentPolicyVersion,
            RecordedAtUtc = DateTimeOffset.UtcNow
        });
        await service.FlushAsync();

        var destination = Path.Combine(_directory, "diagnostics.zip");
        await service.ExportBundleAsync(destination);

        using var archive = ZipFile.OpenRead(destination);
        Assert.NotNull(archive.GetEntry("manifest.json"));
        var telemetry = Assert.IsType<ZipArchiveEntry>(archive.GetEntry("telemetry.jsonl"));
        using var reader = new StreamReader(telemetry.Open(), Encoding.UTF8);
        var lines = (await reader.ReadToEndAsync()).Split('\n', StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal(2, lines.Length);
        Assert.Contains(lines, line => line.Contains("telemetry.consent.changed", StringComparison.Ordinal));
        Assert.Contains(lines, line => line.Contains("app.session.started", StringComparison.Ordinal));
    }

    [Fact]
    public async Task DeleteQueuedRemovesAllSegments()
    {
        using var spool = new FileTelemetrySpool(_directory);
        await spool.EnqueueAsync(CreateEnvelope("test.event"));

        await spool.DeleteAllAsync();

        Assert.Equal(0, (await spool.GetStatsAsync(0)).RecordCount);
    }

    private static TelemetryEnvelope CreateEnvelope(string eventName) => new()
    {
        EventName = eventName,
        ServiceVersion = "test",
        Severity = TelemetrySeverity.Info,
        Platform = new TelemetryPlatform
        {
            OsFamily = "windows",
            OsVersion = "test",
            Architecture = "x64",
            RuntimeVersion = "test"
        },
        Correlation = new TelemetryCorrelation
        {
            InstallationId = Guid.Parse("11111111-1111-1111-1111-111111111111"),
            AppSessionId = Guid.Parse("22222222-2222-2222-2222-222222222222")
        },
        Context = new TelemetryContext { Callsign = "VIPR11" },
        Attributes = new Dictionary<string, JsonElement>
        {
            ["frequency_khz"] = JsonSerializer.SerializeToElement(305000)
        }
    };

    public void Dispose()
    {
        if (!Directory.Exists(_directory)) return;
        Directory.Delete(_directory, recursive: true);
    }
}
