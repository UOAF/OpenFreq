using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using OpenFreqClient.Json;
using OpenFreqClient.Models;

namespace OpenFreqClient.Services.Telemetry;

[JsonConverter(typeof(JsonStringEnumConverter<TelemetrySeverity>))]
public enum TelemetrySeverity
{
    Debug,
    Info,
    Warning,
    Error,
    Critical
}

public interface ITelemetryEvent
{
    string EventName { get; }
    TelemetrySeverity Severity { get; }
    Dictionary<string, JsonElement> CreateAttributes();
}

public sealed record AppSessionStartedTelemetry(string Distribution) : ITelemetryEvent
{
    public string EventName => "app.session.started";
    public TelemetrySeverity Severity => TelemetrySeverity.Info;

    public Dictionary<string, JsonElement> CreateAttributes() => new()
    {
        ["distribution"] = TelemetryJsonValue.Create(Distribution)
    };
}

public sealed record AppSessionEndedTelemetry(bool Clean) : ITelemetryEvent
{
    public string EventName => "app.session.ended";
    public TelemetrySeverity Severity => TelemetrySeverity.Info;

    public Dictionary<string, JsonElement> CreateAttributes() => new()
    {
        ["clean"] = TelemetryJsonValue.Create(Clean)
    };
}

public sealed record ConsentChangedTelemetry(TelemetryConsentStatus Status, int PolicyVersion) : ITelemetryEvent
{
    public string EventName => "telemetry.consent.changed";
    public TelemetrySeverity Severity => TelemetrySeverity.Info;

    public Dictionary<string, JsonElement> CreateAttributes() => new()
    {
        ["status"] = TelemetryJsonValue.Create(Status.ToString().ToLowerInvariant()),
        ["policy_version"] = TelemetryJsonValue.Create(PolicyVersion)
    };
}

public sealed class TelemetryEnvelope
{
    public const int CurrentSchemaVersion = 1;

    public int SchemaVersion { get; init; } = CurrentSchemaVersion;
    public Guid EventRecordId { get; init; } = Guid.NewGuid();
    public required string EventName { get; init; }
    public DateTimeOffset TimestampUtc { get; init; } = DateTimeOffset.UtcNow;
    public long MonotonicMilliseconds { get; init; }
    public TelemetrySeverity Severity { get; init; }
    public string Source { get; init; } = "client";
    public required string ServiceVersion { get; init; }
    public required TelemetryPlatform Platform { get; init; }
    public required TelemetryCorrelation Correlation { get; init; }
    public TelemetryContext Context { get; init; } = new();
    public Dictionary<string, JsonElement> Attributes { get; init; } = [];
}

public sealed class TelemetryPlatform
{
    public required string OsFamily { get; init; }
    public required string OsVersion { get; init; }
    public required string Architecture { get; init; }
    public required string RuntimeVersion { get; init; }
}

public sealed class TelemetryCorrelation
{
    public required Guid InstallationId { get; init; }
    public required Guid AppSessionId { get; init; }
    public Guid? ServerRunId { get; init; }
    public string? ConnectionId { get; init; }
    public string? EventId { get; init; }
}

public sealed class TelemetryContext
{
    public string? Callsign { get; init; }
    public string? Mode { get; init; }
    public string? TheaterId { get; init; }
    public string? AircraftType { get; init; }
}

public sealed record TelemetryContextUpdate(
    string? Callsign = null,
    string? Mode = null,
    string? TheaterId = null,
    string? AircraftType = null);

public sealed record TelemetryCorrelationUpdate(
    Guid? ServerRunId = null,
    string? ConnectionId = null,
    string? EventId = null);

public sealed record TelemetryQueueStats(long Bytes, int RecordCount, DateTimeOffset? OldestRecordUtc, long DroppedRecords);

public sealed record TelemetryBatch(Guid BatchId, IReadOnlyList<TelemetryEnvelope> Records);

public sealed class TelemetryUploadBatch
{
    public int ProtocolVersion { get; init; } = 1;
    public required Guid BatchId { get; init; }
    public required List<TelemetryEnvelope> Records { get; init; }
}

public sealed class TelemetryExportManifest
{
    public int FormatVersion { get; init; } = 1;
    public required string OpenFreqVersion { get; init; }
    public DateTimeOffset ExportedAtUtc { get; init; } = DateTimeOffset.UtcNow;
    public required string Contents { get; init; }
}

internal static class TelemetryJsonValue
{
    public static JsonElement Create(object value) => value switch
    {
        string item => JsonSerializer.SerializeToElement(item, TelemetryJsonContext.Default.String),
        bool item => JsonSerializer.SerializeToElement(item, TelemetryJsonContext.Default.Boolean),
        int item => JsonSerializer.SerializeToElement(item, TelemetryJsonContext.Default.Int32),
        long item => JsonSerializer.SerializeToElement(item, TelemetryJsonContext.Default.Int64),
        double item => JsonSerializer.SerializeToElement(item, TelemetryJsonContext.Default.Double),
        Guid item => JsonSerializer.SerializeToElement(item, TelemetryJsonContext.Default.Guid),
        _ => throw new InvalidDataException($"Unsupported telemetry attribute type {value.GetType().Name}")
    };
}
