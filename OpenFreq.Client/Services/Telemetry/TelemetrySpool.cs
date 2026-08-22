using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace OpenFreqClient.Services.Telemetry;

public interface ITelemetrySpool
{
    ValueTask EnqueueAsync(TelemetryEnvelope record, CancellationToken cancellationToken = default);
    IAsyncEnumerable<TelemetryEnvelope> ReadAllAsync(CancellationToken cancellationToken = default);
    Task<TelemetryQueueStats> GetStatsAsync(long droppedRecords, CancellationToken cancellationToken = default);
    Task DeleteAllAsync(CancellationToken cancellationToken = default);
}

public sealed class TelemetryStorageOptions
{
    public long MaximumBytes { get; init; } = 50L * 1024 * 1024;
    public long MaximumSegmentBytes { get; init; } = 1024L * 1024;
    public int MaximumRecordBytes { get; init; } = 64 * 1024;
    public TimeSpan MaximumAge { get; init; } = TimeSpan.FromDays(7);
}
