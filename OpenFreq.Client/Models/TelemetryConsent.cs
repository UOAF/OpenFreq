using System;

namespace OpenFreqClient.Models;

public enum TelemetryConsentStatus
{
    Unknown,
    Granted,
    Declined
}

public sealed class TelemetryConsent
{
    public const int CurrentPolicyVersion = 1;

    public TelemetryConsentStatus Status { get; set; } = TelemetryConsentStatus.Unknown;
    public int PolicyVersion { get; set; } = CurrentPolicyVersion;
    public DateTimeOffset? RecordedAtUtc { get; set; }
}
