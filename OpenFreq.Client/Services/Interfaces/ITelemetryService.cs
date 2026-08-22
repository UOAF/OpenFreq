using System;
using System.Threading;
using System.Threading.Tasks;
using OpenFreq.Common;
using OpenFreqClient.Models;
using OpenFreqClient.Services.Telemetry;

namespace OpenFreqClient.Services.Interfaces;

public interface ITelemetryService : IAsyncDisposable
{
    event EventHandler? UploadDestinationChanged;
    bool IsEnabled { get; }
    string? UploadDestination { get; }
    TelemetryConsentStatus ConsentStatus { get; }

    ValueTask ConfigureConsentAsync(TelemetryConsent consent, CancellationToken cancellationToken = default);
    void UpdateContext(TelemetryContextUpdate update);
    void UpdateCorrelation(TelemetryCorrelationUpdate update);
    void ConfigureUpload(TelemetryCapability? capability);
    void Track(ITelemetryEvent telemetryEvent);
    Task FlushAsync(CancellationToken cancellationToken = default);
    Task SendQueuedAsync(CancellationToken cancellationToken = default);
    Task<TelemetryQueueStats> GetQueueStatsAsync(CancellationToken cancellationToken = default);
    Task DeleteQueuedAsync(CancellationToken cancellationToken = default);
    Task ExportBundleAsync(string destination, CancellationToken cancellationToken = default);
}
