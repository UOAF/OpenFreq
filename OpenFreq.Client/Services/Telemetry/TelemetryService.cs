using System;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using OpenFreq.Common;
using OpenFreqClient.Json;
using OpenFreqClient.Models;
using OpenFreqClient.Services.Interfaces;

namespace OpenFreqClient.Services.Telemetry;

public sealed class TelemetryService : ITelemetryService
{
    private const int ChannelCapacity = 2048;
    private readonly ITelemetrySpool _spool;
    private readonly ILogger<TelemetryService> _logger;
    private readonly System.Threading.Channels.Channel<ITelemetryEvent> _channel;
    private readonly CancellationTokenSource _cts = new();
    private readonly Task _writerTask;
    private readonly Task _uploadTask;
    private readonly HttpClient _httpClient;
    private readonly bool _ownsHttpClient;
    private readonly SemaphoreSlim _uploadSignal = new(0, 1);
    private readonly SemaphoreSlim _uploadGate = new(1, 1);
    private CancellationTokenSource _consentUploadCts = new();
    private readonly Stopwatch _stopwatch = Stopwatch.StartNew();
    private readonly Guid _appSessionId = Guid.NewGuid();
    private readonly Guid _installationId;
    private long _pending;
    private long _dropped;
    private int _enabled;
    private int _disposed;
    private int _sessionStarted;
    private TelemetryContext _context = new();
    private TelemetryCorrelationUpdate _correlation = new();
    private TelemetryCapability? _uploadCapability;
    private string? _blockedCapabilityToken;
    private string? _uploadDestination;
    private long _nextUploadUtcTicks;
    private int _consecutiveUploadFailures;

    public bool IsEnabled => Volatile.Read(ref _enabled) != 0;
    public string? UploadDestination => Volatile.Read(ref _uploadDestination);
    public event EventHandler? UploadDestinationChanged;
    public TelemetryConsentStatus ConsentStatus { get; private set; } = TelemetryConsentStatus.Unknown;

    public TelemetryService(ITelemetrySpool spool, ILogger<TelemetryService> logger, Guid? installationId = null,
        HttpClient? httpClient = null)
    {
        _spool = spool;
        _logger = logger;
        _installationId = installationId ?? LoadOrCreateInstallationId();
        _httpClient = httpClient ?? new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
        _ownsHttpClient = httpClient == null;
        _channel = System.Threading.Channels.Channel.CreateBounded<ITelemetryEvent>(new BoundedChannelOptions(ChannelCapacity)
        {
            // TryWrite must report false when full so _pending and dropped counts remain exact.
            FullMode = BoundedChannelFullMode.Wait,
            SingleReader = true,
            SingleWriter = false,
            AllowSynchronousContinuations = false
        });
        _writerTask = Task.Run(WriterLoopAsync);
        _uploadTask = Task.Run(UploadLoopAsync);
    }

    public ValueTask ConfigureConsentAsync(TelemetryConsent consent, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var changed = ConsentStatus != consent.Status;
        ConsentStatus = consent.Status;
        Volatile.Write(ref _enabled, consent.Status == TelemetryConsentStatus.Granted ? 1 : 0);

        if (IsEnabled)
        {
            if (Volatile.Read(ref _consentUploadCts).IsCancellationRequested)
            {
                var replacement = new CancellationTokenSource();
                var previous = Interlocked.Exchange(ref _consentUploadCts, replacement);
                previous.Dispose();
            }
            if (changed) Track(new ConsentChangedTelemetry(consent.Status, consent.PolicyVersion));
            if (Interlocked.Exchange(ref _sessionStarted, 1) == 0)
                Track(new AppSessionStartedTelemetry("desktop"));
        }
        else
        {
            Volatile.Read(ref _consentUploadCts).Cancel();
        }

        return ValueTask.CompletedTask;
    }

    public void Track(ITelemetryEvent telemetryEvent)
    {
        if (!IsEnabled || Volatile.Read(ref _disposed) != 0) return;
        Interlocked.Increment(ref _pending);
        if (_channel.Writer.TryWrite(telemetryEvent)) return;
        Interlocked.Decrement(ref _pending);
        Interlocked.Increment(ref _dropped);
    }

    public void UpdateContext(TelemetryContextUpdate update)
    {
        var current = Volatile.Read(ref _context);
        Volatile.Write(ref _context, new TelemetryContext
        {
            Callsign = NormalizeContextValue(update.Callsign) ?? current.Callsign,
            Mode = NormalizeContextValue(update.Mode) ?? current.Mode,
            TheaterId = NormalizeContextValue(update.TheaterId) ?? current.TheaterId,
            AircraftType = NormalizeContextValue(update.AircraftType) ?? current.AircraftType
        });
    }

    public void UpdateCorrelation(TelemetryCorrelationUpdate update)
    {
        var current = Volatile.Read(ref _correlation);
        Volatile.Write(ref _correlation, new TelemetryCorrelationUpdate(
            update.ServerRunId ?? current.ServerRunId,
            update.ConnectionId ?? current.ConnectionId,
            update.EventId ?? current.EventId));
    }

    public void ConfigureUpload(TelemetryCapability? capability)
    {
        Volatile.Write(ref _uploadCapability, capability);
        var destination = capability != null && TryValidateEndpoint(capability.Endpoint, out var endpoint)
            ? endpoint.Host
            : null;
        var previousDestination = Interlocked.Exchange(ref _uploadDestination, destination);
        Volatile.Write(ref _blockedCapabilityToken, null);
        Volatile.Write(ref _nextUploadUtcTicks, 0);
        if (!string.Equals(previousDestination, destination, StringComparison.OrdinalIgnoreCase))
            UploadDestinationChanged?.Invoke(this, EventArgs.Empty);
        if (capability != null && _uploadSignal.CurrentCount == 0) _uploadSignal.Release();
    }

    public async Task FlushAsync(CancellationToken cancellationToken = default)
    {
        while (Interlocked.Read(ref _pending) > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await Task.Delay(10, cancellationToken);
        }
    }

    public Task<TelemetryQueueStats> GetQueueStatsAsync(CancellationToken cancellationToken = default) =>
        _spool.GetStatsAsync(Interlocked.Read(ref _dropped), cancellationToken);

    public async Task SendQueuedAsync(CancellationToken cancellationToken = default)
    {
        await FlushAsync(cancellationToken);
        while (await UploadOneBatchAsync(cancellationToken))
        {
        }
    }

    public async Task DeleteQueuedAsync(CancellationToken cancellationToken = default)
    {
        await FlushAsync(cancellationToken);
        await _spool.DeleteAllAsync(cancellationToken);
    }

    public async Task ExportBundleAsync(string destination, CancellationToken cancellationToken = default)
    {
        await FlushAsync(cancellationToken);
        var destinationDirectory = Path.GetDirectoryName(destination);
        if (!string.IsNullOrWhiteSpace(destinationDirectory)) Directory.CreateDirectory(destinationDirectory);

        await using var file = new FileStream(destination, FileMode.Create, FileAccess.ReadWrite, FileShare.None,
            bufferSize: 8192, FileOptions.Asynchronous);
        using var archive = new ZipArchive(file, ZipArchiveMode.Create, leaveOpen: false);

        var manifest = new TelemetryExportManifest
        {
            OpenFreqVersion = Program.Version,
            Contents = "Allowlisted structured OpenFreq diagnostic telemetry. No voice audio or passwords."
        };
        var manifestEntry = archive.CreateEntry("manifest.json", CompressionLevel.SmallestSize);
        await using (var stream = manifestEntry.Open())
        {
            await JsonSerializer.SerializeAsync(stream, manifest, TelemetryJsonContext.Default.TelemetryExportManifest,
                cancellationToken);
        }

        var telemetryEntry = archive.CreateEntry("telemetry.jsonl", CompressionLevel.SmallestSize);
        await using (var stream = telemetryEntry.Open())
        await using (var writer = new StreamWriter(stream, new UTF8Encoding(false)))
        {
            await foreach (var record in _spool.ReadAllAsync(cancellationToken))
            {
                var json = JsonSerializer.Serialize(record, TelemetryJsonContext.Default.TelemetryEnvelope);
                await writer.WriteLineAsync(json.AsMemory(), cancellationToken);
            }
        }
    }

    private async Task WriterLoopAsync()
    {
        try
        {
            await foreach (var telemetryEvent in _channel.Reader.ReadAllAsync(_cts.Token))
            {
                try
                {
                    await _spool.EnqueueAsync(CreateEnvelope(telemetryEvent), _cts.Token);
                }
                catch (OperationCanceledException) when (_cts.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception ex)
                {
                    Interlocked.Increment(ref _dropped);
                    _logger.LogWarning(ex, "Failed to spool diagnostic telemetry event {EventName}",
                        telemetryEvent.EventName);
                }
                finally
                {
                    Interlocked.Decrement(ref _pending);
                }
            }
        }
        catch (OperationCanceledException) when (_cts.IsCancellationRequested)
        {
        }
    }

    private async Task UploadLoopAsync()
    {
        try
        {
            while (!_cts.IsCancellationRequested)
            {
                await _uploadSignal.WaitAsync(TimeSpan.FromSeconds(60), _cts.Token);
                if (_cts.IsCancellationRequested) break;
                if (DateTimeOffset.UtcNow.UtcTicks < Volatile.Read(ref _nextUploadUtcTicks)) continue;
                await FlushAsync(_cts.Token);
                await UploadOneBatchAsync(_cts.Token);
            }
        }
        catch (OperationCanceledException) when (_cts.IsCancellationRequested)
        {
        }
    }

    private async Task<bool> UploadOneBatchAsync(CancellationToken cancellationToken)
    {
        await _uploadGate.WaitAsync(cancellationToken);
        try
        {
            return await UploadOneBatchCoreAsync(cancellationToken);
        }
        finally
        {
            _uploadGate.Release();
        }
    }

    private async Task<bool> UploadOneBatchCoreAsync(CancellationToken cancellationToken)
    {
        var capability = Volatile.Read(ref _uploadCapability);
        if (!IsEnabled || capability == null ||
            capability.ExpiresAtUtc is { } expiry && expiry <= DateTimeOffset.UtcNow)
            return false;
        if (string.Equals(capability.Token, Volatile.Read(ref _blockedCapabilityToken), StringComparison.Ordinal))
            return false;
        if (!TryValidateEndpoint(capability.Endpoint, out var endpoint)) return false;

        using var uploadCts = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken, Volatile.Read(ref _consentUploadCts).Token);
        var uploadToken = uploadCts.Token;

        var batch = await _spool.ClaimBatchAsync(uploadToken);
        if (batch == null) return false;

        try
        {
            var payload = new TelemetryUploadBatch
            {
                BatchId = batch.BatchId,
                Records = [.. batch.Records]
            };
            await using var compressed = new MemoryStream();
            await using (var gzip = new GZipStream(compressed, CompressionLevel.Fastest, leaveOpen: true))
            {
                await JsonSerializer.SerializeAsync(gzip, payload,
                    TelemetryJsonContext.Default.TelemetryUploadBatch, uploadToken);
            }
            compressed.Position = 0;

            using var request = new HttpRequestMessage(HttpMethod.Post, endpoint);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", capability.Token);
            request.Headers.Add("X-OpenFreq-Batch-Id", batch.BatchId.ToString("D"));
            request.Content = new StreamContent(compressed);
            request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
            request.Content.Headers.ContentEncoding.Add("gzip");

            using var response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead,
                uploadToken);
            if (response.IsSuccessStatusCode || response.StatusCode == HttpStatusCode.Conflict)
            {
                await _spool.AcknowledgeAsync(batch.BatchId, uploadToken);
                _consecutiveUploadFailures = 0;
                Volatile.Write(ref _nextUploadUtcTicks, 0);
                return true;
            }

            if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden or
                HttpStatusCode.NotFound)
            {
                await _spool.ReleaseAsync(batch.BatchId, uploadToken);
                Volatile.Write(ref _blockedCapabilityToken, capability.Token);
                _logger.LogWarning("Diagnostic telemetry capability was rejected with HTTP {StatusCode}",
                    (int)response.StatusCode);
                return false;
            }

            if ((int)response.StatusCode is >= 400 and < 500 &&
                response.StatusCode is not (HttpStatusCode.RequestTimeout or HttpStatusCode.TooManyRequests))
            {
                await _spool.RejectAsync(batch.BatchId, uploadToken);
                _logger.LogWarning("Diagnostic telemetry batch was quarantined after HTTP {StatusCode}",
                    (int)response.StatusCode);
                return true;
            }

            await _spool.ReleaseAsync(batch.BatchId, uploadToken);
            var retryAfter = response.Headers.RetryAfter?.Delta ??
                             response.Headers.RetryAfter?.Date - DateTimeOffset.UtcNow;
            RegisterUploadFailure(retryAfter is { } value && value > TimeSpan.Zero ? value : null);
            _logger.LogWarning("Diagnostic telemetry upload failed with HTTP {StatusCode}",
                (int)response.StatusCode);
            return false;
        }
        catch (OperationCanceledException)
        {
            await _spool.ReleaseAsync(batch.BatchId, CancellationToken.None);
            if (cancellationToken.IsCancellationRequested) throw;
            return false;
        }
        catch (Exception ex)
        {
            await _spool.ReleaseAsync(batch.BatchId, CancellationToken.None);
            RegisterUploadFailure(null);
            _logger.LogWarning(ex, "Diagnostic telemetry upload failed");
            return false;
        }
    }

    private void RegisterUploadFailure(TimeSpan? retryAfter)
    {
        _consecutiveUploadFailures = Math.Min(_consecutiveUploadFailures + 1, 8);
        var exponentialSeconds = Math.Min(900, Math.Pow(2, _consecutiveUploadFailures) * 5);
        var delay = retryAfter ?? TimeSpan.FromSeconds(exponentialSeconds + Random.Shared.NextDouble() * 5);
        Volatile.Write(ref _nextUploadUtcTicks, (DateTimeOffset.UtcNow + delay).UtcTicks);
    }

    private static bool TryValidateEndpoint(string value, out Uri endpoint)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out endpoint!) ||
            endpoint.Scheme is not ("https" or "http")) return false;
        return endpoint.Scheme == "https" || endpoint.IsLoopback;
    }

    private static string? NormalizeContextValue(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var clean = new string(value.Trim().Where(ch => !char.IsControl(ch)).ToArray());
        if (clean.Length == 0) return null;
        return clean[..Math.Min(clean.Length, 128)];
    }

    private TelemetryEnvelope CreateEnvelope(ITelemetryEvent telemetryEvent) => new()
    {
        EventName = telemetryEvent.EventName,
        MonotonicMilliseconds = _stopwatch.ElapsedMilliseconds,
        Severity = telemetryEvent.Severity,
        ServiceVersion = Program.Version,
        Platform = new TelemetryPlatform
        {
            OsFamily = GetOsFamily(),
            OsVersion = Environment.OSVersion.VersionString,
            Architecture = RuntimeInformation.ProcessArchitecture.ToString().ToLowerInvariant(),
            RuntimeVersion = RuntimeInformation.FrameworkDescription
        },
        Correlation = CreateCorrelation(),
        Context = Volatile.Read(ref _context),
        Attributes = telemetryEvent.CreateAttributes()
    };

    private TelemetryCorrelation CreateCorrelation()
    {
        var current = Volatile.Read(ref _correlation);
        return new TelemetryCorrelation
        {
            InstallationId = _installationId,
            AppSessionId = _appSessionId,
            ServerRunId = current.ServerRunId,
            ConnectionId = current.ConnectionId,
            EventId = current.EventId
        };
    }

    private Guid LoadOrCreateInstallationId()
    {
        var telemetryDirectory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "OpenFreq", "telemetry");
        Directory.CreateDirectory(telemetryDirectory);
        var idPath = Path.Combine(telemetryDirectory, "installation-id");
        try
        {
            if (File.Exists(idPath) && Guid.TryParse(File.ReadAllText(idPath), out var existing)) return existing;
            var created = Guid.NewGuid();
            File.WriteAllText(idPath, created.ToString("D"));
            return created;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not persist telemetry installation ID; using an ephemeral ID");
            return Guid.NewGuid();
        }
    }

    private static string GetOsFamily()
    {
        if (OperatingSystem.IsWindows()) return "windows";
        if (OperatingSystem.IsLinux()) return "linux";
        if (OperatingSystem.IsMacOS()) return "macos";
        return "unknown";
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        if (IsEnabled && Volatile.Read(ref _sessionStarted) != 0)
        {
            // Bypass Track's disposed guard so the final marker is queued.
            Interlocked.Increment(ref _pending);
            if (!_channel.Writer.TryWrite(new AppSessionEndedTelemetry(true)))
                Interlocked.Decrement(ref _pending);
        }

        _channel.Writer.TryComplete();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        try
        {
            await FlushAsync(timeout.Token);
            await _writerTask.WaitAsync(timeout.Token);
        }
        catch (OperationCanceledException)
        {
            _cts.Cancel();
        }
        finally
        {
            _cts.Cancel();
            try { await _uploadTask; } catch (OperationCanceledException) { }
            _cts.Dispose();
            _uploadSignal.Dispose();
            _uploadGate.Dispose();
            _consentUploadCts.Dispose();
            if (_ownsHttpClient) _httpClient.Dispose();
            if (_spool is IDisposable disposable) disposable.Dispose();
        }
    }
}
