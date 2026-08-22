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
            files = GetSegmentFiles().ToList();
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

    public async Task<TelemetryQueueStats> GetStatsAsync(long droppedRecords,
        CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var files = GetSegmentFiles().Select(path => new FileInfo(path)).ToList();
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
            foreach (var file in GetSegmentFiles()) File.Delete(file);
            _activeSegment = null;
        }
        finally
        {
            _gate.Release();
        }
    }

    private string GetWritableSegment(int incomingBytes)
    {
        if (_activeSegment != null && File.Exists(_activeSegment) &&
            new FileInfo(_activeSegment).Length + incomingBytes <= _options.MaximumSegmentBytes)
            return _activeSegment;

        _activeSegment = Path.Combine(
            _directory,
            $"telemetry-{DateTimeOffset.UtcNow:yyyyMMddTHHmmssfff}-{Interlocked.Increment(ref _segmentSequence):D4}.jsonl");
        return _activeSegment;
    }

    private async Task PruneExpiredAndOversizedAsync(int incomingBytes, CancellationToken cancellationToken)
    {
        var cutoff = DateTime.UtcNow - _options.MaximumAge;
        var files = GetSegmentFiles().Select(path => new FileInfo(path)).OrderBy(file => file.CreationTimeUtc).ToList();
        foreach (var file in files.Where(file => file.CreationTimeUtc < cutoff).ToList())
        {
            cancellationToken.ThrowIfCancellationRequested();
            file.Delete();
            files.Remove(file);
            if (string.Equals(_activeSegment, file.FullName, StringComparison.OrdinalIgnoreCase))
                _activeSegment = null;
        }

        var totalBytes = files.Sum(file => file.Length);
        foreach (var file in files)
        {
            if (totalBytes + incomingBytes <= _options.MaximumBytes) break;
            cancellationToken.ThrowIfCancellationRequested();
            totalBytes -= file.Length;
            file.Delete();
            if (string.Equals(_activeSegment, file.FullName, StringComparison.OrdinalIgnoreCase))
                _activeSegment = null;
        }

        await Task.CompletedTask;
    }

    private IEnumerable<string> GetSegmentFiles() =>
        Directory.EnumerateFiles(_directory, "telemetry-*.jsonl").OrderBy(path => path, StringComparer.Ordinal);

    public void Dispose() => _gate.Dispose();
}
