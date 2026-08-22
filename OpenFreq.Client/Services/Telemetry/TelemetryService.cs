using System;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
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
    private readonly Stopwatch _stopwatch = Stopwatch.StartNew();
    private readonly Guid _appSessionId = Guid.NewGuid();
    private readonly Guid _installationId;
    private long _pending;
    private long _dropped;
    private int _disposed;
    private int _sessionStarted;
    private TelemetryContext _context = new();
    private TelemetryCorrelationUpdate _correlation = new();
    private TelemetryCapability? _uploadCapability;

    public bool IsEnabled { get; private set; }
    public TelemetryConsentStatus ConsentStatus { get; private set; } = TelemetryConsentStatus.Unknown;

    public TelemetryService(ITelemetrySpool spool, ILogger<TelemetryService> logger, Guid? installationId = null)
    {
        _spool = spool;
        _logger = logger;
        _installationId = installationId ?? LoadOrCreateInstallationId();
        _channel = System.Threading.Channels.Channel.CreateBounded<ITelemetryEvent>(new BoundedChannelOptions(ChannelCapacity)
        {
            FullMode = BoundedChannelFullMode.DropWrite,
            SingleReader = true,
            SingleWriter = false,
            AllowSynchronousContinuations = false
        });
        _writerTask = Task.Run(WriterLoopAsync);
    }

    public ValueTask ConfigureConsentAsync(TelemetryConsent consent, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var changed = ConsentStatus != consent.Status;
        ConsentStatus = consent.Status;
        IsEnabled = consent.Status == TelemetryConsentStatus.Granted;

        if (IsEnabled)
        {
            if (changed) Track(new ConsentChangedTelemetry(consent.Status, consent.PolicyVersion));
            if (Interlocked.Exchange(ref _sessionStarted, 1) == 0)
                Track(new AppSessionStartedTelemetry("desktop"));
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
            Callsign = update.Callsign ?? current.Callsign,
            Mode = update.Mode ?? current.Mode,
            TheaterId = update.TheaterId ?? current.TheaterId,
            AircraftType = update.AircraftType ?? current.AircraftType
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

    public void ConfigureUpload(TelemetryCapability? capability) =>
        Volatile.Write(ref _uploadCapability, capability);

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
            _cts.Dispose();
            if (_spool is IDisposable disposable) disposable.Dispose();
        }
    }
}
