using System.IO.Compression;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using OpenFreq.Common;
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

    [Fact]
    public async Task UpdatedContextAndCorrelationAreCapturedWithoutMachineIdentity()
    {
        using var spool = new FileTelemetrySpool(_directory);
        await using var service = new TelemetryService(
            spool,
            NullLogger<TelemetryService>.Instance,
            Guid.Parse("11111111-1111-1111-1111-111111111111"));
        await service.ConfigureConsentAsync(new TelemetryConsent { Status = TelemetryConsentStatus.Granted });
        service.UpdateContext(new TelemetryContextUpdate(
            Callsign: "VIPR11", Mode: "game", TheaterId: "balkans", AircraftType: "F-16CM-50"));
        service.UpdateCorrelation(new TelemetryCorrelationUpdate(
            Guid.Parse("33333333-3333-3333-3333-333333333333"), "peer-42", "UOAF-TEST"));
        service.Track(TelemetryEvents.PositionSample(100, 200, -3000, 500, "bms"));
        await service.FlushAsync();

        var records = new List<TelemetryEnvelope>();
        await foreach (var item in spool.ReadAllAsync()) records.Add(item);
        var position = Assert.Single(records, item => item.EventName == "position.sample");
        Assert.Equal("VIPR11", position.Context.Callsign);
        Assert.Equal("balkans", position.Context.TheaterId);
        Assert.Equal("peer-42", position.Correlation.ConnectionId);
        Assert.Equal("UOAF-TEST", position.Correlation.EventId);
        var json = JsonSerializer.Serialize(position, TelemetryJsonContext.Default.TelemetryEnvelope);
        Assert.DoesNotContain(Environment.UserName, json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(Environment.MachineName, json, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ClaimReleasePreservesBatchIdAndAcknowledgeDeletesIt()
    {
        using var spool = new FileTelemetrySpool(_directory);
        await spool.EnqueueAsync(CreateEnvelope("test.claim"));

        var claimed = await spool.ClaimBatchAsync();
        Assert.NotNull(claimed);
        await spool.ReleaseAsync(claimed.BatchId);
        var retried = await spool.ClaimBatchAsync();

        Assert.NotNull(retried);
        Assert.Equal(claimed.BatchId, retried.BatchId);
        Assert.Single(retried.Records);
        await spool.AcknowledgeAsync(retried.BatchId);
        Assert.Equal(0, (await spool.GetStatsAsync(0)).RecordCount);
    }

    [Fact]
    public async Task SegmentRecordLimitProducesIngestionSizedBatches()
    {
        using var spool = new FileTelemetrySpool(_directory, new TelemetryStorageOptions
        {
            MaximumBytes = 1024 * 1024,
            MaximumSegmentBytes = 1024 * 1024,
            MaximumSegmentRecords = 2
        });
        await spool.EnqueueAsync(CreateEnvelope("test.one"));
        await spool.EnqueueAsync(CreateEnvelope("test.two"));
        await spool.EnqueueAsync(CreateEnvelope("test.three"));

        var first = await spool.ClaimBatchAsync();
        Assert.NotNull(first);
        Assert.Equal(2, first.Records.Count);
        await spool.AcknowledgeAsync(first.BatchId);
        var second = await spool.ClaimBatchAsync();
        Assert.NotNull(second);
        Assert.Single(second.Records);
    }

    [Fact]
    public async Task ReleasedUploadSegmentsRemainInsideTheTotalSpoolBound()
    {
        var firstRecord = CreateEnvelope("test.first");
        firstRecord.Attributes["padding"] = JsonSerializer.SerializeToElement(new string('x', 300));
        var oneRecordBytes = Encoding.UTF8.GetByteCount(
            JsonSerializer.Serialize(firstRecord, TelemetryJsonContext.Default.TelemetryEnvelope)) + 1;
        using var spool = new FileTelemetrySpool(_directory, new TelemetryStorageOptions
        {
            MaximumBytes = oneRecordBytes + 100,
            MaximumSegmentBytes = oneRecordBytes + 100,
            MaximumRecordBytes = oneRecordBytes + 100
        });
        await spool.EnqueueAsync(firstRecord);
        var released = await spool.ClaimBatchAsync();
        Assert.NotNull(released);
        await spool.ReleaseAsync(released.BatchId);

        await spool.EnqueueAsync(CreateEnvelope("test.second"));

        var records = new List<TelemetryEnvelope>();
        await foreach (var record in spool.ReadAllAsync()) records.Add(record);
        Assert.Single(records);
        Assert.Equal("test.second", records[0].EventName);
    }

    [Fact]
    public async Task SendQueuedUploadsGzipBatchAndAcknowledgesIt()
    {
        var handler = new CapturingHandler();
        using var httpClient = new HttpClient(handler);
        using var spool = new FileTelemetrySpool(_directory);
        await using var service = new TelemetryService(
            spool,
            NullLogger<TelemetryService>.Instance,
            Guid.Parse("11111111-1111-1111-1111-111111111111"),
            httpClient);
        service.ConfigureUpload(new TelemetryCapability(
            "https://telemetry.example/v1/batches", "signed-token", DateTimeOffset.UtcNow.AddHours(1)));
        await service.ConfigureConsentAsync(new TelemetryConsent { Status = TelemetryConsentStatus.Granted });
        service.Track(TelemetryEvents.IvcState(true));
        await service.SendQueuedAsync();
        for (var i = 0; i < 50 && handler.CallCount == 0; i++) await Task.Delay(10);

        Assert.Equal(1, handler.CallCount);
        Assert.Equal("Bearer", handler.AuthorizationScheme);
        Assert.Equal("signed-token", handler.AuthorizationParameter);
        Assert.NotNull(handler.DecompressedBody);
        Assert.Contains("ivc.process.state.changed", handler.DecompressedBody, StringComparison.Ordinal);
        Assert.Equal(0, (await service.GetQueueStatsAsync()).RecordCount);
    }

    [Fact]
    public async Task WithdrawingConsentCancelsAnInFlightUploadAndKeepsTheBatch()
    {
        var handler = new BlockingHandler();
        using var httpClient = new HttpClient(handler);
        using var spool = new FileTelemetrySpool(_directory);
        await using var service = new TelemetryService(
            spool,
            NullLogger<TelemetryService>.Instance,
            Guid.Parse("11111111-1111-1111-1111-111111111111"),
            httpClient);
        await service.ConfigureConsentAsync(new TelemetryConsent { Status = TelemetryConsentStatus.Granted });
        service.Track(TelemetryEvents.IvcState(true));
        await service.FlushAsync();
        service.ConfigureUpload(new TelemetryCapability(
            "https://telemetry.example/v1/batches", "signed-token", DateTimeOffset.UtcNow.AddHours(1)));
        await handler.Started.Task.WaitAsync(TimeSpan.FromSeconds(2));

        await service.ConfigureConsentAsync(new TelemetryConsent { Status = TelemetryConsentStatus.Declined });
        await handler.Cancelled.Task.WaitAsync(TimeSpan.FromSeconds(2));

        Assert.True((await service.GetQueueStatsAsync()).RecordCount > 0);
    }

    [Fact]
    public async Task PermanentSchemaFailureQuarantinesInsteadOfRetryingForever()
    {
        var handler = new CapturingHandler { ResponseStatusCode = HttpStatusCode.BadRequest };
        using var httpClient = new HttpClient(handler);
        using var spool = new FileTelemetrySpool(_directory);
        await using var service = new TelemetryService(spool, NullLogger<TelemetryService>.Instance,
            Guid.NewGuid(), httpClient);
        await service.ConfigureConsentAsync(new TelemetryConsent { Status = TelemetryConsentStatus.Granted });
        service.ConfigureUpload(new TelemetryCapability(
            "https://telemetry.example/v1/batches", "signed-token", DateTimeOffset.UtcNow.AddHours(1)));
        service.Track(TelemetryEvents.IvcState(true));

        await service.SendQueuedAsync();
        await service.SendQueuedAsync();

        Assert.Equal(1, handler.CallCount);
        Assert.True((await service.GetQueueStatsAsync()).RecordCount > 0);
        Assert.Single(Directory.GetFiles(_directory, "rejected-*.jsonl"));
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

    private sealed class CapturingHandler : HttpMessageHandler
    {
        public int CallCount { get; private set; }
        public string? AuthorizationScheme { get; private set; }
        public string? AuthorizationParameter { get; private set; }
        public string? DecompressedBody { get; private set; }
        public HttpStatusCode ResponseStatusCode { get; init; } = HttpStatusCode.Accepted;

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            CallCount++;
            AuthorizationScheme = request.Headers.Authorization?.Scheme;
            AuthorizationParameter = request.Headers.Authorization?.Parameter;
            var bytes = await request.Content!.ReadAsByteArrayAsync(cancellationToken);
            await using var compressed = new MemoryStream(bytes);
            await using var gzip = new GZipStream(compressed, CompressionMode.Decompress);
            using var reader = new StreamReader(gzip, Encoding.UTF8);
            DecompressedBody = await reader.ReadToEndAsync(cancellationToken);
            return new HttpResponseMessage(ResponseStatusCode);
        }
    }

    private sealed class BlockingHandler : HttpMessageHandler
    {
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Cancelled { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            Started.TrySetResult();
            try
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                return new HttpResponseMessage(HttpStatusCode.Accepted);
            }
            catch (OperationCanceledException)
            {
                Cancelled.TrySetResult();
                throw;
            }
        }
    }
}
