using System;
using System.Collections.Generic;
using System.Text.Json;

namespace OpenFreqClient.Services.Telemetry;

/// <summary>
/// Central allowlist for hosted diagnostic event names and fields. Application components use
/// these factories instead of forwarding arbitrary log messages or objects to telemetry.
/// </summary>
public static class TelemetryEvents
{
    public static ITelemetryEvent StateChanged(string eventName, object oldState, object newState) =>
        Create(eventName, ("old_state", oldState.ToString()), ("new_state", newState.ToString()));

    public static ITelemetryEvent ConnectionState(object state, object reason) =>
        Create("connection.state.changed", ("state", state.ToString()), ("reason", reason.ToString()));

    public static ITelemetryEvent IvcState(bool running) =>
        Create("ivc.process.state.changed", ("running", running));

    public static ITelemetryEvent MutexConflict(bool active, long? durationMilliseconds = null) =>
        Create("bms.radio_mutex.conflict",
            ("active", active),
            ("duration_ms", durationMilliseconds));

    public static ITelemetryEvent RadioState(string property, object radio, object oldValue, object newValue) =>
        Create("radio.state.changed",
            ("property", property),
            ("radio", radio.ToString()),
            ("old_value", oldValue.ToString()),
            ("new_value", newValue.ToString()));

    public static ITelemetryEvent RadioSnapshot(object radio, int frequencyKhz, int rawVolume, bool ptt, bool power) =>
        Create("radio.state.snapshot",
            ("radio", radio.ToString()),
            ("frequency_khz", frequencyKhz),
            ("raw_volume", rawVolume),
            ("ptt", ptt),
            ("power", power));

    public static ITelemetryEvent AudioDeviceChanged(string direction, int count, int oldIndex, int newIndex,
        int defaultIndex, bool removed) =>
        Create("audio.device.inventory.changed",
            ("direction", direction),
            ("enabled_device_count", count),
            ("old_index", oldIndex),
            ("new_index", newIndex),
            ("default_index", defaultIndex),
            ("selected_is_default", newIndex == defaultIndex),
            ("device_removed", removed));

    public static ITelemetryEvent AudioDeviceError(string component) =>
        Create("audio.device.error", TelemetrySeverity.Error, ("component", component));

    public static ITelemetryEvent AudioCaptureState(string state, int deviceIndex, string? errorCode = null) =>
        Create("audio.capture.state.changed", errorCode == null ? TelemetrySeverity.Info : TelemetrySeverity.Error,
            ("state", state),
            ("device_index", deviceIndex),
            ("error_code", errorCode));

    public static ITelemetryEvent AudioCaptureHealth(long callbacks, long samples, double rmsDbfs, double peakDbfs,
        int activeFrequencies) =>
        Create("audio.capture.health",
            ("callbacks", callbacks),
            ("samples", samples),
            ("rms_dbfs", rmsDbfs),
            ("peak_dbfs", peakDbfs),
            ("active_frequency_count", activeFrequencies));

    public static ITelemetryEvent TransmissionState(Guid transmissionId, string state, int frequencyKhz,
        Guid slotId, bool is3d) =>
        Create("radio.transmission.state.changed",
            ("transmission_id", transmissionId),
            ("state", state),
            ("frequency_khz", frequencyKhz),
            ("slot_id", slotId),
            ("is_3d", is3d));

    public static ITelemetryEvent TransmissionHealth(Guid transmissionId, int frequencyKhz, long durationMilliseconds,
        long captureCallbacks, long capturedSamples, long sendCalls) =>
        Create("radio.transmission.health",
            ("transmission_id", transmissionId),
            ("frequency_khz", frequencyKhz),
            ("duration_ms", durationMilliseconds),
            ("capture_callbacks", captureCallbacks),
            ("captured_samples", capturedSamples),
            ("send_calls", sendCalls));

    public static ITelemetryEvent AcmiState(object status) =>
        Create("acmi.connection.state.changed", ("status", status.ToString()));

    public static ITelemetryEvent AcmiHealth(int aircraftCount, double? newestFrameAgeSeconds) =>
        Create("acmi.frame.health",
            ("aircraft_count", aircraftCount),
            ("newest_frame_age_seconds", newestFrameAgeSeconds));

    public static ITelemetryEvent PositionSample(int xFeet, int yFeet, int zDownFeet, double? terrainElevationFeet,
        string source) =>
        Create("position.sample",
            ("source", source),
            ("bms_x_ft", xFeet),
            ("bms_y_ft", yFeet),
            ("bms_z_down_ft", zDownFeet),
            ("altitude_msl_ft", -zDownFeet),
            ("terrain_elevation_ft", terrainElevationFeet),
            ("altitude_agl_ft", terrainElevationFeet.HasValue ? -zDownFeet - terrainElevationFeet.Value : null));

    public static ITelemetryEvent PropagationSample(string peerId, int frequencyKhz,
        double txX, double txY, double txZ, double rxX, double rxY, double rxZ,
        double receivedDb, double snrDb, double dropoutRate, double deepFadeRate) =>
        Create("propagation.calculation.sample",
            ("peer_id", peerId),
            ("frequency_khz", frequencyKhz),
            ("tx_x_m", txX), ("tx_y_m", txY), ("tx_z_m", txZ),
            ("rx_x_m", rxX), ("rx_y_m", rxY), ("rx_z_m", rxZ),
            ("received_db", receivedDb),
            ("snr_db", snrDb),
            ("dropout_rate", dropoutRate),
            ("deep_fade_rate", deepFadeRate));

    public static ITelemetryEvent HeightmapLoad(bool success, string theaterId, int width, int height,
        int bytesPerSample, string? errorCode = null) =>
        Create("heightmap.load.result", success ? TelemetrySeverity.Info : TelemetrySeverity.Error,
            ("success", success),
            ("theater_id", theaterId),
            ("width", width),
            ("height", height),
            ("bytes_per_sample", bytesPerSample),
            ("error_code", errorCode));

    private static ITelemetryEvent Create(string name, params (string Name, object? Value)[] attributes) =>
        Create(name, TelemetrySeverity.Info, attributes);

    private static ITelemetryEvent Create(string name, TelemetrySeverity severity,
        params (string Name, object? Value)[] attributes)
    {
        var values = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        foreach (var (attributeName, value) in attributes)
        {
            if (value == null) continue;
            values[attributeName] = JsonSerializer.SerializeToElement(value, value.GetType());
        }
        return new AllowlistedTelemetryEvent(name, severity, values);
    }

    private sealed record AllowlistedTelemetryEvent(
        string EventName,
        TelemetrySeverity Severity,
        Dictionary<string, JsonElement> Attributes) : ITelemetryEvent
    {
        public Dictionary<string, JsonElement> CreateAttributes() => Attributes;
    }
}
