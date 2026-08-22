using System;
using System.Threading;
using System.Threading.Tasks;
using OpenFreqClient.Models;
using OpenFreqClient.Services.Telemetry;

namespace OpenFreqClient.Services.Interfaces;

public interface ITelemetryService : IAsyncDisposable
{
    bool IsEnabled { get; }
    TelemetryConsentStatus ConsentStatus { get; }

    ValueTask ConfigureConsentAsync(TelemetryConsent consent, CancellationToken cancellationToken = default);
    void Track(ITelemetryEvent telemetryEvent);
    Task FlushAsync(CancellationToken cancellationToken = default);
    Task<TelemetryQueueStats> GetQueueStatsAsync(CancellationToken cancellationToken = default);
    Task DeleteQueuedAsync(CancellationToken cancellationToken = default);
    Task ExportBundleAsync(string destination, CancellationToken cancellationToken = default);
}
