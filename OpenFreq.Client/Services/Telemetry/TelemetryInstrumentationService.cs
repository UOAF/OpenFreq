using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using FalconBmsDataService.Models;
using FalconBmsDataService.Services;
using FalconRadioService.Models;
using FalconRadioService.Services;
using OpenFreq.Client.Services.Interfaces;
using OpenFreq.Common;
using OpenFreq.Services.Acmi;
using OpenFreqClient.Models;
using OpenFreqClient.Services.Interfaces;

namespace OpenFreqClient.Services.Telemetry;

public sealed class TelemetryInstrumentationService : ILifecycleService, IDisposable
{
    private readonly ITelemetryService _telemetry;
    private readonly IFalconSharedMemoryService _falcon;
    private readonly IFalconRadioSharedMemoryService _radio;
    private readonly IIvcMonitorService _ivc;
    private readonly IAudioService _audio;
    private readonly IAcmiClientService _acmi;
    private readonly IOpenFreqService _openFreq;
    private readonly Stopwatch _mutexConflict = new();
    private CancellationTokenSource? _cts;
    private Task? _snapshotTask;
    private bool _started;

    public TelemetryInstrumentationService(ITelemetryService telemetry, IFalconSharedMemoryService falcon,
        IFalconRadioSharedMemoryService radio, IIvcMonitorService ivc, IAudioService audio,
        IAcmiClientService acmi, IOpenFreqService openFreq)
    {
        _telemetry = telemetry;
        _falcon = falcon;
        _radio = radio;
        _ivc = ivc;
        _audio = audio;
        _acmi = acmi;
        _openFreq = openFreq;
    }

    public void Start()
    {
        if (_started) return;
        _started = true;
        _falcon.StateChanged += OnFalconStateChanged;
        _falcon.FlyingStateChanged += OnFlyingStateChanged;
        _falcon.AircraftInfoChanged += OnAircraftInfoChanged;
        _radio.StateChanged += OnRadioMemoryStateChanged;
        _radio.FrequencyChanged += OnFrequencyChanged;
        _radio.VolumeChanged += OnVolumeChanged;
        _radio.PttChanged += OnPttChanged;
        _radio.PowerChanged += OnPowerChanged;
        _radio.LogbookNameChanged += OnLogbookNameChanged;
        _radio.ConnectionParametersChanged += OnConnectionParametersChanged;
        _radio.RadioClientConflict += OnMutexConflict;
        _radio.RadioClientConflictResolved += OnMutexConflictResolved;
        _ivc.IvcStatusChanged += OnIvcStatusChanged;
        _audio.PlaybackDevicesChanged += OnPlaybackDevicesChanged;
        _audio.RecordingDevicesChanged += OnRecordingDevicesChanged;
        _audio.AudioDeviceErrorOccurred += OnAudioDeviceError;
        _acmi.ConnectionStatusChanged += OnAcmiConnectionStatusChanged;
        _openFreq.ConnectionStateChanged += OnOpenFreqConnectionStateChanged;

        _cts = new CancellationTokenSource();
        _snapshotTask = Task.Run(() => SnapshotLoopAsync(_cts.Token));
    }

    public void Stop()
    {
        if (!_started) return;
        _started = false;
        _cts?.Cancel();
        try { _snapshotTask?.Wait(TimeSpan.FromSeconds(2)); } catch { }
        _cts?.Dispose();
        _cts = null;

        _falcon.StateChanged -= OnFalconStateChanged;
        _falcon.FlyingStateChanged -= OnFlyingStateChanged;
        _falcon.AircraftInfoChanged -= OnAircraftInfoChanged;
        _radio.StateChanged -= OnRadioMemoryStateChanged;
        _radio.FrequencyChanged -= OnFrequencyChanged;
        _radio.VolumeChanged -= OnVolumeChanged;
        _radio.PttChanged -= OnPttChanged;
        _radio.PowerChanged -= OnPowerChanged;
        _radio.LogbookNameChanged -= OnLogbookNameChanged;
        _radio.ConnectionParametersChanged -= OnConnectionParametersChanged;
        _radio.RadioClientConflict -= OnMutexConflict;
        _radio.RadioClientConflictResolved -= OnMutexConflictResolved;
        _ivc.IvcStatusChanged -= OnIvcStatusChanged;
        _audio.PlaybackDevicesChanged -= OnPlaybackDevicesChanged;
        _audio.RecordingDevicesChanged -= OnRecordingDevicesChanged;
        _audio.AudioDeviceErrorOccurred -= OnAudioDeviceError;
        _acmi.ConnectionStatusChanged -= OnAcmiConnectionStatusChanged;
        _openFreq.ConnectionStateChanged -= OnOpenFreqConnectionStateChanged;
    }

    private async Task SnapshotLoopAsync(CancellationToken cancellationToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(10));
        try
        {
            while (await timer.WaitForNextTickAsync(cancellationToken))
            {
                if (!_telemetry.IsEnabled) continue;
                CapturePosition();
                CaptureAcmiHealth();
                CaptureRadioSnapshot();
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
    }

    private void CapturePosition()
    {
        if (_falcon is not { IsFlying: true, Position: { } position }) return;
        double? terrainFeet = null;
        try
        {
            var heightmap = BmsHeightmapConverter.ToHeightmap(position.X, position.Y, position.Z);
            var xMeters = heightmap.x;
            var yMeters = heightmap.y;
            terrainFeet = _openFreq.SampleTerrainElevationMeters(xMeters, yMeters) * 3.28084;
        }
        catch
        {
            // Missing or transitioning heightmaps are reported by their own event path.
        }

        _telemetry.Track(TelemetryEvents.PositionSample(position.X, position.Y, position.Z, terrainFeet, "bms"));
    }

    private void CaptureAcmiHealth()
    {
        var aircraft = _acmi.GetAllAircraft().ToList();
        var newest = aircraft.Count == 0 ? (DateTime?)null : aircraft.Max(item => item.LastUpdate);
        var age = newest.HasValue ? Math.Max(0, (DateTime.UtcNow - newest.Value).TotalSeconds) : (double?)null;
        _telemetry.Track(TelemetryEvents.AcmiHealth(aircraft.Count, age));
    }

    private void CaptureRadioSnapshot()
    {
        if (!OperatingSystem.IsWindows()) return;
        foreach (RadioType type in Enum.GetValues<RadioType>())
        {
            var channel = _radio.GetRadioChannel(type);
            if (channel == null) continue;
            _telemetry.Track(TelemetryEvents.RadioSnapshot(type, channel.Frequency, channel.RxVolume,
                channel.PttDepressed, channel.IsOn));
        }
    }

    private void OnFalconStateChanged(object? sender, ServiceStateChangedEventArgs e) =>
        _telemetry.Track(TelemetryEvents.StateChanged("bms.shared_memory.state.changed", e.OldState, e.NewState));

    private void OnRadioMemoryStateChanged(object? sender, ServiceStateChangedEventArgs e) =>
        _telemetry.Track(TelemetryEvents.StateChanged("bms.radio_memory.state.changed", e.OldState, e.NewState));

    private void OnFlyingStateChanged(object? sender, FlyingStateChangedEventArgs e)
    {
        _telemetry.UpdateContext(new TelemetryContextUpdate(Mode: e.NewFlyingState ? "game" : "lobby"));
        _telemetry.Track(TelemetryEvents.StateChanged("bms.flying_state.changed", e.OldFlyingState, e.NewFlyingState));
    }

    private void OnAircraftInfoChanged(object? sender, AircraftInfoChangedEventArgs e) =>
        _telemetry.UpdateContext(new TelemetryContextUpdate(AircraftType: e.AcName ?? e.AcNCTR));

    private void OnLogbookNameChanged(object? sender, LogbookNameChangedEventArgs e)
    {
        if (!string.IsNullOrWhiteSpace(e.NewName))
            _telemetry.UpdateContext(new TelemetryContextUpdate(Callsign: e.NewName));
    }

    private void OnConnectionParametersChanged(object? sender, ConnectionParametersChangedEventArgs e)
    {
        if (!string.IsNullOrWhiteSpace(e.NewParameters.Nickname))
            _telemetry.UpdateContext(new TelemetryContextUpdate(Callsign: e.NewParameters.Nickname));
    }

    private void OnFrequencyChanged(object? sender, RadioFrequencyChangedEventArgs e) =>
        _telemetry.Track(TelemetryEvents.RadioState("frequency_khz", e.RadioType, e.OldFrequencyKhz, e.NewFrequencyKhz));
    private void OnVolumeChanged(object? sender, RadioVolumeChangedEventArgs e) =>
        _telemetry.Track(TelemetryEvents.RadioState("raw_volume", e.RadioType, e.OldVolume, e.NewVolume));
    private void OnPttChanged(object? sender, RadioPttChangedEventArgs e) =>
        _telemetry.Track(TelemetryEvents.RadioState("ptt", e.RadioType, e.OldPtt, e.NewPtt));
    private void OnPowerChanged(object? sender, RadioPowerChangedEventArgs e) =>
        _telemetry.Track(TelemetryEvents.RadioState("power", e.RadioType, e.OldPower, e.NewPower));

    private void OnMutexConflict(object? sender, EventArgs e)
    {
        _mutexConflict.Restart();
        _telemetry.Track(TelemetryEvents.MutexConflict(true));
    }

    private void OnMutexConflictResolved(object? sender, EventArgs e)
    {
        _mutexConflict.Stop();
        _telemetry.Track(TelemetryEvents.MutexConflict(false, _mutexConflict.ElapsedMilliseconds));
    }

    private void OnIvcStatusChanged(object? sender, IvcStatusChangedEventArgs e) =>
        _telemetry.Track(TelemetryEvents.IvcState(e.IsRunning));

    private void OnPlaybackDevicesChanged(object? sender, DeviceChangedEventArgs e) =>
        _telemetry.Track(TelemetryEvents.AudioDeviceChanged("playback", e.Devices.Count, e.OldDeviceIndex,
            e.NewDeviceIndex, e.DefaultDeviceIndex, e.DeviceWasRemoved));

    private void OnRecordingDevicesChanged(object? sender, DeviceChangedEventArgs e) =>
        _telemetry.Track(TelemetryEvents.AudioDeviceChanged("recording", e.Devices.Count, e.OldDeviceIndex,
            e.NewDeviceIndex, e.DefaultDeviceIndex, e.DeviceWasRemoved));

    private void OnAudioDeviceError(object? sender, string e) =>
        _telemetry.Track(TelemetryEvents.AudioDeviceError("device_monitor"));

    private void OnAcmiConnectionStatusChanged(object? sender, AcmiConnectionEventArgs e) =>
        _telemetry.Track(TelemetryEvents.AcmiState(e.Status));

    private void OnOpenFreqConnectionStateChanged(object? sender, ConnectionStateChangedEventArgs e) =>
        _telemetry.Track(TelemetryEvents.ConnectionState(e.State, e.Reason));

    public void Dispose()
    {
        Stop();
        GC.SuppressFinalize(this);
    }
}
