using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using OpenFreqClient.Json;

namespace OpenFreqClient.Services.Telemetry;

public sealed class FileTelemetrySpool : ITelemetrySpool, IDisposable
{
    private readonly string _directory;
    private readonly TelemetryStorageOptions _options;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private int _segmentSequence;
    private string? _activeSegment;
    private int _activeSegmentRecords;
    private readonly Dictionary<Guid, string> _claimedFiles = [];

    public FileTelemetrySpool(string directory, TelemetryStorageOptions? options = null)
    {
        _directory = directory;
        _options = options ?? new TelemetryStorageOptions();
        Directory.CreateDirectory(_directory);
    }

    public async ValueTask EnqueueAsync(TelemetryEnvelope record, CancellationToken cancellationToken = default)
    {
        var json = JsonSerializer.Serialize(record, TelemetryJsonContext.Default.TelemetryEnvelope);
        var recordBytes = Encoding.UTF8.GetByteCount(json) + 1;
        if (recordBytes > _options.MaximumRecordBytes)
            throw new InvalidDataException($"Telemetry record exceeds {_options.MaximumRecordBytes} bytes");

        await _gate.WaitAsync(cancellationToken);
        try
        {
            await PruneExpiredAndOversizedAsync(recordBytes, cancellationToken);
            var segment = GetWritableSegment(recordBytes);
            await using var stream = new FileStream(
                segment,
                FileMode.Append,
                FileAccess.Write,
                FileShare.Read,
                bufferSize: 4096,
                FileOptions.Asynchronous | FileOptions.WriteThrough);
            await using var writer = new StreamWriter(stream, new UTF8Encoding(false));
            await writer.WriteLineAsync(json.AsMemory(), cancellationToken);
            _activeSegmentRecords++;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async IAsyncEnumerable<TelemetryEnvelope> ReadAllAsync(
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        List<string> files;
        await _gate.WaitAsync(cancellationToken);
        try
        {
            files = GetDataFiles().ToList();
        }
        finally
        {
            _gate.Release();
        }

        foreach (var file in files)
        {
            await using var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite,
                bufferSize: 4096, FileOptions.Asynchronous | FileOptions.SequentialScan);
            using var reader = new StreamReader(stream, Encoding.UTF8);
            while (await reader.ReadLineAsync(cancellationToken) is { } line)
            {
                if (string.IsNullOrWhiteSpace(line)) continue;
                TelemetryEnvelope? record;
                try
                {
                    record = JsonSerializer.Deserialize(line, TelemetryJsonContext.Default.TelemetryEnvelope);
                }
                catch (JsonException)
                {
                    // A process may have terminated in the middle of its final append. Skip only
                    // that corrupt line so preceding crash evidence remains exportable.
                    continue;
                }

                if (record != null) yield return record;
            }
        }
    }

    public async Task<TelemetryBatch?> ClaimBatchAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var existingClaim = Directory.EnumerateFiles(_directory, "upload-*.jsonl")
                .OrderBy(path => path, StringComparer.Ordinal)
                .FirstOrDefault(path => TryGetBatchId(path, out var id) && !_claimedFiles.ContainsKey(id));

            Guid batchId;
            string claimedPath;
            if (existingClaim != null && TryGetBatchId(existingClaim, out batchId))
            {
                claimedPath = existingClaim;
            }
            else
            {
                _activeSegment = null;
                _activeSegmentRecords = 0;
                var source = GetSegmentFiles().FirstOrDefault();
                if (source == null) return null;
                batchId = Guid.NewGuid();
                claimedPath = Path.Combine(_directory, $"upload-{batchId:N}.jsonl");
                File.Move(source, claimedPath);
            }

            var records = await ReadFileAsync(claimedPath, cancellationToken);
            if (records.Count == 0)
            {
                File.Delete(claimedPath);
                return null;
            }

            _claimedFiles[batchId] = claimedPath;
            return new TelemetryBatch(batchId, records);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task AcknowledgeAsync(Guid batchId, CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var path = ResolveClaimedPath(batchId);
            if (path != null && File.Exists(path)) File.Delete(path);
            _claimedFiles.Remove(batchId);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task ReleaseAsync(Guid batchId, CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            // Leave the upload segment named with its batch ID so a retry uses the
            // same idempotency key, including after an application restart.
            _claimedFiles.Remove(batchId);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task RejectAsync(Guid batchId, CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var path = ResolveClaimedPath(batchId);
            if (path != null && File.Exists(path))
                File.Move(path, Path.Combine(_directory, $"rejected-{batchId:N}.jsonl"), overwrite: true);
            _claimedFiles.Remove(batchId);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<TelemetryQueueStats> GetStatsAsync(long droppedRecords,
        CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var files = GetDataFiles().Select(path => new FileInfo(path)).ToList();
            var bytes = files.Sum(file => file.Length);
            var oldest = files.Count == 0
                ? (DateTimeOffset?)null
                : files.Min(file => new DateTimeOffset(file.CreationTimeUtc, TimeSpan.Zero));
            var count = 0;
            foreach (var file in files)
            {
                using var stream = file.Open(FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                using var reader = new StreamReader(stream, Encoding.UTF8);
                while (await reader.ReadLineAsync(cancellationToken) != null) count++;
            }

            return new TelemetryQueueStats(bytes, count, oldest, droppedRecords);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task DeleteAllAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            foreach (var file in GetDataFiles()) File.Delete(file);
            _claimedFiles.Clear();
            _activeSegment = null;
            _activeSegmentRecords = 0;
        }
        finally
        {
            _gate.Release();
        }
    }

    private string GetWritableSegment(int incomingBytes)
    {
        if (_activeSegment != null && File.Exists(_activeSegment) &&
            _activeSegmentRecords < _options.MaximumSegmentRecords &&
            new FileInfo(_activeSegment).Length + incomingBytes <= _options.MaximumSegmentBytes)
            return _activeSegment;

        _activeSegment = Path.Combine(
            _directory,
            $"telemetry-{DateTimeOffset.UtcNow:yyyyMMddTHHmmssfff}-{Interlocked.Increment(ref _segmentSequence):D4}.jsonl");
        _activeSegmentRecords = 0;
        return _activeSegment;
    }

    private async Task PruneExpiredAndOversizedAsync(int incomingBytes, CancellationToken cancellationToken)
    {
        var cutoff = DateTime.UtcNow - _options.MaximumAge;
        var files = GetDataFiles().Select(path => new FileInfo(path)).OrderBy(file => file.CreationTimeUtc).ToList();
        foreach (var file in files.Where(file => file.CreationTimeUtc < cutoff && !IsClaimed(file.FullName)).ToList())
        {
            cancellationToken.ThrowIfCancellationRequested();
            file.Delete();
            files.Remove(file);
            if (string.Equals(_activeSegment, file.FullName, StringComparison.OrdinalIgnoreCase))
            {
                _activeSegment = null;
                _activeSegmentRecords = 0;
            }
        }

        var totalBytes = files.Sum(file => file.Length);
        foreach (var file in files)
        {
            if (totalBytes + incomingBytes <= _options.MaximumBytes) break;
            if (IsClaimed(file.FullName)) continue;
            cancellationToken.ThrowIfCancellationRequested();
            totalBytes -= file.Length;
            file.Delete();
            if (string.Equals(_activeSegment, file.FullName, StringComparison.OrdinalIgnoreCase))
            {
                _activeSegment = null;
                _activeSegmentRecords = 0;
            }
        }

        if (totalBytes + incomingBytes > _options.MaximumBytes)
            throw new InvalidDataException("Telemetry spool is full while an upload batch is in flight");

        await Task.CompletedTask;
    }

    private bool IsClaimed(string path) =>
        _claimedFiles.Values.Contains(path, StringComparer.OrdinalIgnoreCase);

    private IEnumerable<string> GetSegmentFiles() =>
        Directory.EnumerateFiles(_directory, "telemetry-*.jsonl").OrderBy(path => path, StringComparer.Ordinal);

    private IEnumerable<string> GetDataFiles() =>
        GetSegmentFiles()
            .Concat(Directory.EnumerateFiles(_directory, "upload-*.jsonl"))
            .Concat(Directory.EnumerateFiles(_directory, "rejected-*.jsonl"))
            .OrderBy(path => path, StringComparer.Ordinal);

    private async Task<List<TelemetryEnvelope>> ReadFileAsync(string path, CancellationToken cancellationToken)
    {
        var records = new List<TelemetryEnvelope>();
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read,
            bufferSize: 4096, FileOptions.Asynchronous | FileOptions.SequentialScan);
        using var reader = new StreamReader(stream, Encoding.UTF8);
        while (await reader.ReadLineAsync(cancellationToken) is { } line)
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            try
            {
                var record = JsonSerializer.Deserialize(line, TelemetryJsonContext.Default.TelemetryEnvelope);
                if (record != null) records.Add(record);
            }
            catch (JsonException)
            {
            }
        }
        return records;
    }

    private string? ResolveClaimedPath(Guid batchId)
    {
        if (_claimedFiles.TryGetValue(batchId, out var tracked)) return tracked;
        var path = Path.Combine(_directory, $"upload-{batchId:N}.jsonl");
        return File.Exists(path) ? path : null;
    }

    private static bool TryGetBatchId(string path, out Guid batchId)
    {
        batchId = Guid.Empty;
        var name = Path.GetFileNameWithoutExtension(path);
        return name.StartsWith("upload-", StringComparison.Ordinal) && Guid.TryParseExact(name[7..], "N", out batchId);
    }

    public void Dispose() => _gate.Dispose();
}
