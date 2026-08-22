using System.IO.Compression;
using System.Net;
using System.Net.Http.Headers;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Channels;
using Microsoft.Extensions.Logging;
using OpenFreq.Common;

namespace OpenFreqServer.Telemetry;

public interface IServerTelemetry : IAsyncDisposable
{
    bool IsEnabled { get; }
    void Track(string eventName, string? connectionId = null,
        IReadOnlyDictionary<string, object?>? attributes = null,
        string severity = "Info");
}

public sealed class NullServerTelemetry : IServerTelemetry
{
    public static NullServerTelemetry Instance { get; } = new();
    public bool IsEnabled => false;
    public void Track(string eventName, string? connectionId = null,
        IReadOnlyDictionary<string, object?>? attributes = null, string severity = "Info")
    { }
    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}

/// <summary>
/// Opt-in server telemetry with a bounded local JSONL spool and idempotent HTTPS uploads.
/// It deliberately records connection IDs, never display names or network addresses.
/// </summary>
public sealed class ServerTelemetryService : IServerTelemetry
{
    private const long MaxSpoolBytes = 50L * 1024 * 1024;
    private static readonly TimeSpan MaxSpoolAge = TimeSpan.FromDays(7);
    private readonly ServerConfig _config;
    private readonly Guid _serverRunId;
    private readonly Uri _endpoint;
    private readonly string _directory;
    private readonly ILogger<ServerTelemetryService> _logger;
    private readonly HttpClient _httpClient;
    private readonly bool _ownsHttpClient;
    private readonly Channel<ServerTelemetryEnvelope> _channel = Channel.CreateBounded<ServerTelemetryEnvelope>(
        new BoundedChannelOptions(2048) { FullMode = BoundedChannelFullMode.DropWrite, SingleReader = true });
    private readonly SemaphoreSlim _fileGate = new(1, 1);
    private readonly CancellationTokenSource _cts = new();
    private readonly Task _writerTask;
    private readonly Task _uploaderTask;
    private string? _activePath;
    private string? _uploadingPath;
    private int _activeRecords;
    private bool _uploadsBlocked;
    private int _disposed;

    public bool IsEnabled => true;

    public static IServerTelemetry Create(ServerConfig config, Guid serverRunId,
        ILoggerFactory loggerFactory, string? directory = null)
    {
        if (string.IsNullOrWhiteSpace(config.TelemetryEndpoint) ||
            string.IsNullOrWhiteSpace(config.TelemetrySigningKey) ||
            Encoding.UTF8.GetByteCount(config.TelemetrySigningKey) < 32 ||
            !Uri.TryCreate(config.TelemetryEndpoint, UriKind.Absolute, out var endpoint) ||
            (endpoint.Scheme != Uri.UriSchemeHttps &&
             !(endpoint.Scheme == Uri.UriSchemeHttp && endpoint.IsLoopback)))
            return NullServerTelemetry.Instance;

        directory ??= Path.Combine(AppContext.BaseDirectory, "logs", "telemetry");
        var logger = loggerFactory.CreateLogger<ServerTelemetryService>();
        try
        {
            return new ServerTelemetryService(config, serverRunId, endpoint, directory, logger);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Server diagnostic telemetry could not be initialized");
            return NullServerTelemetry.Instance;
        }
    }

    internal ServerTelemetryService(ServerConfig config, Guid serverRunId, Uri endpoint,
        string directory, ILogger<ServerTelemetryService> logger, HttpClient? httpClient = null)
    {
        _config = config;
        _serverRunId = serverRunId;
        _endpoint = endpoint;
        _directory = directory;
        _logger = logger;
        _httpClient = httpClient ?? new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
        _ownsHttpClient = httpClient == null;
        Directory.CreateDirectory(_directory);
        _writerTask = Task.Run(WriterLoopAsync);
        _uploaderTask = Task.Run(UploaderLoopAsync);
    }

    public void Track(string eventName, string? connectionId = null,
        IReadOnlyDictionary<string, object?>? attributes = null, string severity = "Info")
    {
        if (Volatile.Read(ref _disposed) != 0) return;
        try
        {
            var safeAttributes = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
            if (attributes != null)
            {
                foreach (var (key, value) in attributes)
                    safeAttributes[key] = CreateJsonValue(value);
            }

            _channel.Writer.TryWrite(new ServerTelemetryEnvelope
            {
                EventName = eventName,
                TimestampUtc = DateTimeOffset.UtcNow,
                Severity = severity,
                ServiceVersion = OpenFreqVersion.Current,
                Platform = new ServerTelemetryPlatform
                {
                    OsFamily = OperatingSystem.IsWindows() ? "windows" :
                        OperatingSystem.IsLinux() ? "linux" :
                        OperatingSystem.IsMacOS() ? "macos" : "other",
                    OsVersion = Environment.OSVersion.Version.ToString(),
                    Architecture = RuntimeInformation.ProcessArchitecture.ToString().ToLowerInvariant(),
                    RuntimeVersion = Environment.Version.ToString()
                },
                Correlation = new ServerTelemetryCorrelation
                {
                    ServerRunId = _serverRunId,
                    ConnectionId = Truncate(connectionId, 128),
                    EventId = Truncate(_config.TelemetryEventId, 128)
                },
                Attributes = safeAttributes
            });
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Server diagnostic telemetry event was dropped");
        }
    }

    private async Task WriterLoopAsync()
    {
        try
        {
            await foreach (var envelope in _channel.Reader.ReadAllAsync())
            {
                var line = JsonSerializer.Serialize(envelope, ServerTelemetryJsonContext.Default.ServerTelemetryEnvelope);
                await _fileGate.WaitAsync();
                try
                {
                    if (_activePath != null && (_activeRecords >= 500 ||
                        new FileInfo(_activePath).Length + Encoding.UTF8.GetByteCount(line) + 1 > 512 * 1024))
                    {
                        _activePath = null;
                        _activeRecords = 0;
                    }
                    _activePath ??= Path.Combine(_directory,
                        $"server-{DateTimeOffset.UtcNow:yyyyMMddTHHmmssfff}-{_serverRunId:N}.jsonl");
                    await File.AppendAllTextAsync(_activePath, line + Environment.NewLine, Encoding.UTF8);
                    _activeRecords++;
                    PruneSpool();
                }
                finally
                {
                    _fileGate.Release();
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Server diagnostic telemetry writer stopped");
        }
    }

    private async Task UploaderLoopAsync()
    {
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(5), _cts.Token);
            using var timer = new PeriodicTimer(TimeSpan.FromSeconds(60));
            do
            {
                await UploadOneAsync(_cts.Token);
            } while (await timer.WaitForNextTickAsync(_cts.Token));
        }
        catch (OperationCanceledException) when (_cts.IsCancellationRequested) { }
    }

    private async Task UploadOneAsync(CancellationToken cancellationToken)
    {
        if (_uploadsBlocked) return;
        (Guid BatchId, string Path)? claim = null;
        await _fileGate.WaitAsync(cancellationToken);
        try
        {
            var existing = Directory.EnumerateFiles(_directory, "upload-*.jsonl")
                .OrderBy(path => path, StringComparer.Ordinal).FirstOrDefault();
            if (existing != null && TryBatchId(existing, out var existingId))
            {
                claim = (existingId, existing);
            }
            else
            {
                var source = Directory.EnumerateFiles(_directory, "server-*.jsonl")
                    .OrderBy(path => path, StringComparer.Ordinal).FirstOrDefault();
                if (source == null) return;
                if (source == _activePath)
                {
                    _activePath = null;
                    _activeRecords = 0;
                }
                var id = Guid.NewGuid();
                var target = Path.Combine(_directory, $"upload-{id:N}.jsonl");
                File.Move(source, target);
                claim = (id, target);
            }
            _uploadingPath = claim.Value.Path;
        }
        finally
        {
            _fileGate.Release();
        }

        try
        {
            var records = new List<ServerTelemetryEnvelope>();
            foreach (var line in await File.ReadAllLinesAsync(claim.Value.Path, cancellationToken))
            {
                if (string.IsNullOrWhiteSpace(line)) continue;
                var record = JsonSerializer.Deserialize(line,
                    ServerTelemetryJsonContext.Default.ServerTelemetryEnvelope);
                if (record != null) records.Add(record);
            }
            if (records.Count == 0)
            {
                File.Delete(claim.Value.Path);
                return;
            }

            var token = TelemetryTokenIssuer.TryIssue(_config, _serverRunId);
            if (token == null) return;
            var batch = new ServerTelemetryBatch { BatchId = claim.Value.BatchId, Records = records };
            await using var body = new MemoryStream();
            await using (var gzip = new GZipStream(body, CompressionLevel.Fastest, leaveOpen: true))
                await JsonSerializer.SerializeAsync(gzip, batch,
                    ServerTelemetryJsonContext.Default.ServerTelemetryBatch, cancellationToken);
            body.Position = 0;
            using var request = new HttpRequestMessage(HttpMethod.Post, _endpoint);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token.Token);
            request.Headers.Add("X-OpenFreq-Batch-Id", claim.Value.BatchId.ToString("D"));
            request.Content = new StreamContent(body);
            request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
            request.Content.Headers.ContentEncoding.Add("gzip");
            using var response = await _httpClient.SendAsync(request,
                HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            if (response.IsSuccessStatusCode || response.StatusCode == HttpStatusCode.Conflict)
                File.Delete(claim.Value.Path);
            else if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden or
                     HttpStatusCode.NotFound)
                _uploadsBlocked = true;
            else if ((int)response.StatusCode is >= 400 and < 500 &&
                     response.StatusCode is not (HttpStatusCode.RequestTimeout or HttpStatusCode.TooManyRequests))
                File.Move(claim.Value.Path,
                    Path.Combine(_directory, $"rejected-{claim.Value.BatchId:N}.jsonl"), overwrite: true);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Server diagnostic telemetry upload failed");
        }
        finally
        {
            await _fileGate.WaitAsync(CancellationToken.None);
            try
            {
                if (string.Equals(_uploadingPath, claim.Value.Path, StringComparison.OrdinalIgnoreCase))
                    _uploadingPath = null;
            }
            finally
            {
                _fileGate.Release();
            }
        }
    }

    private void PruneSpool()
    {
        var cutoff = DateTime.UtcNow - MaxSpoolAge;
        var files = Directory.EnumerateFiles(_directory, "*.jsonl")
            .Select(path => new FileInfo(path)).OrderBy(file => file.LastWriteTimeUtc).ToList();
        foreach (var file in files.Where(file => file.LastWriteTimeUtc < cutoff &&
                                                file.FullName != _uploadingPath)) file.Delete();
        var remaining = files.Where(file => file.Exists).ToList();
        var total = remaining.Sum(file => file.Length);
        foreach (var file in remaining)
        {
            if (total <= MaxSpoolBytes) break;
            if (file.FullName == _activePath || file.FullName == _uploadingPath) continue;
            total -= file.Length;
            file.Delete();
        }
    }

    private static bool TryBatchId(string path, out Guid id)
    {
        id = Guid.Empty;
        var name = Path.GetFileNameWithoutExtension(path);
        return name.StartsWith("upload-", StringComparison.Ordinal) &&
               Guid.TryParseExact(name[7..], "N", out id);
    }

    private static string? Truncate(string? value, int length) =>
        string.IsNullOrEmpty(value) ? value : value[..Math.Min(value.Length, length)];

    private static JsonElement CreateJsonValue(object? value) => value switch
    {
        null => JsonSerializer.SerializeToElement<string?>(null, ServerTelemetryJsonContext.Default.String),
        string item => JsonSerializer.SerializeToElement(item, ServerTelemetryJsonContext.Default.String),
        bool item => JsonSerializer.SerializeToElement(item, ServerTelemetryJsonContext.Default.Boolean),
        int item => JsonSerializer.SerializeToElement(item, ServerTelemetryJsonContext.Default.Int32),
        long item => JsonSerializer.SerializeToElement(item, ServerTelemetryJsonContext.Default.Int64),
        double item => JsonSerializer.SerializeToElement(item, ServerTelemetryJsonContext.Default.Double),
        _ => throw new InvalidDataException($"Unsupported telemetry attribute type {value.GetType().Name}")
    };

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        _channel.Writer.TryComplete();
        await _writerTask;
        _cts.Cancel();
        try { await _uploaderTask; } catch (OperationCanceledException) { }
        if (_ownsHttpClient) _httpClient.Dispose();
        _cts.Dispose();
        _fileGate.Dispose();
    }
}

public sealed class ServerTelemetryEnvelope
{
    public int SchemaVersion { get; init; } = 1;
    public Guid EventRecordId { get; init; } = Guid.NewGuid();
    public required string EventName { get; init; }
    public DateTimeOffset TimestampUtc { get; init; }
    public long MonotonicMilliseconds { get; init; } = Environment.TickCount64;
    public required string Severity { get; init; }
    public string Source { get; init; } = "server";
    public required string ServiceVersion { get; init; }
    public required ServerTelemetryPlatform Platform { get; init; }
    public required ServerTelemetryCorrelation Correlation { get; init; }
    public Dictionary<string, string> Context { get; init; } = [];
    public Dictionary<string, JsonElement> Attributes { get; init; } = [];
}

public sealed class ServerTelemetryPlatform
{
    public required string OsFamily { get; init; }
    public required string OsVersion { get; init; }
    public required string Architecture { get; init; }
    public required string RuntimeVersion { get; init; }
}

public sealed class ServerTelemetryCorrelation
{
    public Guid ServerRunId { get; init; }
    public string? ConnectionId { get; init; }
    public string? EventId { get; init; }
}

public sealed class ServerTelemetryBatch
{
    public int ProtocolVersion { get; init; } = 1;
    public Guid BatchId { get; init; }
    public required List<ServerTelemetryEnvelope> Records { get; init; }
}

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.SnakeCaseLower,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(ServerTelemetryEnvelope))]
[JsonSerializable(typeof(ServerTelemetryBatch))]
[JsonSerializable(typeof(string))]
[JsonSerializable(typeof(bool))]
[JsonSerializable(typeof(int))]
[JsonSerializable(typeof(long))]
[JsonSerializable(typeof(double))]
internal partial class ServerTelemetryJsonContext : JsonSerializerContext;
