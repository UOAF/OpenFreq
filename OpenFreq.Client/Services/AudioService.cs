using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using ManagedBass;
using Microsoft.Extensions.Logging;
using OpenFreqClient.Services.Interfaces;

namespace OpenFreqClient;

public class AudioService(ILogger<AudioService> logger) : IAudioService
{
    public int DefaultPlaybackDevice { get; private set; } = -1;
    public int DefaultRecordingDevice { get; private set; } = -1;

    // Track currently selected devices
    private int _selectedPlaybackDeviceIndex = -1;
    private int _selectedRecordingDeviceIndex = -1;
    private string? _selectedPlaybackDeviceName;
    private string? _selectedRecordingDeviceName;

    // BASS initialization state — tracked separately so each can retry independently
    private bool _playbackInitialized;
    private bool _recordingInitialized;

    // Events to notify when devices change
    public event EventHandler<DeviceChangedEventArgs>? PlaybackDevicesChanged;
    public event EventHandler<DeviceChangedEventArgs>? RecordingDevicesChanged;
    public event EventHandler<string>? AudioDeviceErrorOccurred;

    // BASS device indices parallel to the enabled-device name lists
    private List<int> _playbackBassIndices = new();
    private List<int> _recordingBassIndices = new();

    // Device monitoring
    private CancellationTokenSource? _monitoringCts;
    private Task? _monitoringTask;
    private List<string> _lastPlaybackDevices = new();
    private List<string> _lastRecordingDevices = new();

    // Guards _playbackBassIndices and _recordingBassIndices against concurrent read/write
    // between the monitoring thread (writer) and UI thread (reader via GetPlaybackBassIndex).
    private readonly object _deviceLock = new();

    public void Init()
    {
        _playbackInitialized = Bass.Init();
        if (!_playbackInitialized)
            logger.LogWarning("BASS playback init failed ({Error}), will retry when devices appear", Bass.LastError);

        _recordingInitialized = Bass.RecordInit();
        if (!_recordingInitialized)
            logger.LogWarning("BASS recording init failed ({Error}), will retry when devices appear", Bass.LastError);

        // Snapshot current device lists (may be empty when no devices present)
        _lastPlaybackDevices = GetPlaybackDevices();
        _lastRecordingDevices = GetRecordingDevices();

        // Start monitoring for device changes (also retries failed inits)
        StartDeviceMonitoring();
    }

    private void StartDeviceMonitoring()
    {
        _monitoringCts = new CancellationTokenSource();
        _monitoringTask = Task.Run(async () =>
        {
            logger.LogInformation("Device monitoring started");

            while (!_monitoringCts.Token.IsCancellationRequested)
            {
                try
                {
                    await Task.Delay(1000, _monitoringCts.Token);
                    CheckForDeviceChanges();
                }
                catch (OperationCanceledException)
                {
                    // expected
                    break;
                }
                catch (Exception ex)
                {
                    logger.LogError(ex, "Error monitoring devices");
                }
            }

            logger.LogInformation("Device monitoring stopped");
        });
    }

    private void CheckForDeviceChanges()
    {
        // Retry BASS init if it previously failed (e.g. no devices at startup)
        if (!_playbackInitialized)
        {
            _playbackInitialized = Bass.Init();
            if (_playbackInitialized)
            {
                logger.LogInformation("BASS playback init succeeded after retry");
                // Treat all current devices as newly appeared
                _lastPlaybackDevices = new List<string>();
            }
        }

        if (!_recordingInitialized)
        {
            _recordingInitialized = Bass.RecordInit();
            if (_recordingInitialized)
            {
                logger.LogInformation("BASS recording init succeeded after retry");
                _lastRecordingDevices = new List<string>();
            }
        }

        // Check playback devices
        var currentPlaybackDevices = GetPlaybackDevices();
        if (!currentPlaybackDevices.SequenceEqual(_lastPlaybackDevices))
        {
            logger.LogInformation("Playback device list changed: [{Old}] → [{New}]",
                string.Join(", ", _lastPlaybackDevices),
                string.Join(", ", currentPlaybackDevices));
            _lastPlaybackDevices = currentPlaybackDevices;
            HandlePlaybackDeviceChange();
        }

        // Check recording devices
        var currentRecordingDevices = GetRecordingDevices();
        if (!currentRecordingDevices.SequenceEqual(_lastRecordingDevices))
        {
            logger.LogInformation("Recording device list changed: [{Old}] → [{New}]",
                string.Join(", ", _lastRecordingDevices),
                string.Join(", ", currentRecordingDevices));
            _lastRecordingDevices = currentRecordingDevices;
            HandleRecordingDeviceChange();
        }
    }

    private void HandlePlaybackDeviceChange()
    {
        var devices = GetPlaybackDevices();
        var oldIndex = _selectedPlaybackDeviceIndex;
        var newIndex = _selectedPlaybackDeviceIndex;

        // Try to find the previously selected device by name
        if (!string.IsNullOrEmpty(_selectedPlaybackDeviceName))
        {
            newIndex = FindDeviceIndexByName(_selectedPlaybackDeviceName, isRecording: false);
        }

        // If device was removed or not found, fall back to default
        if (newIndex == -1)
        {
            logger.LogWarning(
                "Playback device '{DeviceName}' not found after device change, falling back to default (list index {DefaultDevice})",
                _selectedPlaybackDeviceName, DefaultPlaybackDevice);
            newIndex = DefaultPlaybackDevice;

            if (newIndex != -1)
            {
                var deviceInfo = Bass.GetDeviceInfo(GetPlaybackBassIndex(newIndex));
                _selectedPlaybackDeviceName = deviceInfo.Name;
                logger.LogInformation("Playback fallback device: '{FallbackName}' (list index {Index})",
                    _selectedPlaybackDeviceName, newIndex);
            }
            else
            {
                var msg = "No valid audio output device available — all playback devices were removed. Please connect an audio device.";
                logger.LogError("{Message}", msg);
                AudioDeviceErrorOccurred?.Invoke(this, msg);
            }
        }

        // DeviceWasRemoved: true when the physical device changed, even if list index stayed same.
        // Consumers MUST force a BASS device switch in this case regardless of index equality.
        bool deviceWasRemoved = oldIndex != -1 && !string.Equals(
            _selectedPlaybackDeviceName,
            oldIndex >= 0 && oldIndex < devices.Count ? devices[oldIndex] : null,
            StringComparison.Ordinal);

        _selectedPlaybackDeviceIndex = newIndex;

        logger.LogInformation(
            "Playback device resolved: list index {Old} → {New}, DeviceWasRemoved={Removed}",
            oldIndex, newIndex, deviceWasRemoved);

        PlaybackDevicesChanged?.Invoke(this, new DeviceChangedEventArgs
        {
            Devices = devices,
            OldDeviceIndex = oldIndex,
            NewDeviceIndex = newIndex,
            DeviceWasRemoved = deviceWasRemoved,
            DefaultDeviceIndex = DefaultPlaybackDevice
        });
    }

    private void HandleRecordingDeviceChange()
    {
        var devices = GetRecordingDevices();
        var oldIndex = _selectedRecordingDeviceIndex;
        var newIndex = _selectedRecordingDeviceIndex;

        // Try to find the previously selected device by name
        if (!string.IsNullOrEmpty(_selectedRecordingDeviceName))
        {
            newIndex = FindDeviceIndexByName(_selectedRecordingDeviceName, isRecording: true);
        }

        // If device was removed or not found, fall back to default
        if (newIndex == -1)
        {
            logger.LogWarning(
                "Recording device '{DeviceName}' not found after device change, falling back to default (list index {DefaultDevice})",
                _selectedRecordingDeviceName, DefaultRecordingDevice);
            newIndex = DefaultRecordingDevice;

            if (newIndex != -1)
            {
                var deviceInfo = Bass.RecordGetDeviceInfo(GetRecordingBassIndex(newIndex));
                _selectedRecordingDeviceName = deviceInfo.Name;
                logger.LogInformation("Recording fallback device: '{FallbackName}' (list index {Index})",
                    _selectedRecordingDeviceName, newIndex);
            }
            else
            {
                var msg = "No valid audio input device available — all recording devices were removed. Please connect a microphone.";
                logger.LogError("{Message}", msg);
                AudioDeviceErrorOccurred?.Invoke(this, msg);
            }
        }

        bool deviceWasRemoved = oldIndex != -1 && !string.Equals(
            _selectedRecordingDeviceName,
            oldIndex >= 0 && oldIndex < devices.Count ? devices[oldIndex] : null,
            StringComparison.Ordinal);

        _selectedRecordingDeviceIndex = newIndex;

        logger.LogInformation(
            "Recording device resolved: list index {Old} → {New}, DeviceWasRemoved={Removed}",
            oldIndex, newIndex, deviceWasRemoved);

        RecordingDevicesChanged?.Invoke(this, new DeviceChangedEventArgs
        {
            Devices = devices,
            OldDeviceIndex = oldIndex,
            NewDeviceIndex = newIndex,
            DeviceWasRemoved = deviceWasRemoved,
            DefaultDeviceIndex = DefaultRecordingDevice
        });
    }

    private int FindDeviceIndexByName(string deviceName, bool isRecording)
    {
        int listIndex = 0;
        if (isRecording)
        {
            for (var i = 0; i < Bass.RecordingDeviceCount; i++)
            {
                var deviceInfo = Bass.RecordGetDeviceInfo(i);
                if (!deviceInfo.IsEnabled) continue;
                if (deviceInfo.Name == deviceName) return listIndex;
                listIndex++;
            }
        }
        else
        {
            for (var i = 0; i < Bass.DeviceCount; i++)
            {
                var deviceInfo = Bass.GetDeviceInfo(i);
                if (!deviceInfo.IsEnabled) continue;
                if (deviceInfo.Name == deviceName) return listIndex;
                listIndex++;
            }
        }

        return -1;
    }

    public void SetSelectedPlaybackDevice(int deviceIndex)
    {
        if (deviceIndex >= 0 && deviceIndex < Bass.DeviceCount)
        {
            var deviceInfo = Bass.GetDeviceInfo(deviceIndex);
            if (deviceInfo.IsEnabled)
            {
                _selectedPlaybackDeviceIndex = deviceIndex;
                _selectedPlaybackDeviceName = deviceInfo.Name;
                logger.LogInformation("Selected playback device: {DeviceIndex} - {DeviceName}",
                    deviceIndex, deviceInfo.Name);
            }
        }
    }

    public void SetSelectedRecordingDevice(int deviceIndex)
    {
        if (deviceIndex >= 0 && deviceIndex < Bass.RecordingDeviceCount)
        {
            var deviceInfo = Bass.RecordGetDeviceInfo(deviceIndex);
            if (deviceInfo.IsEnabled)
            {
                _selectedRecordingDeviceIndex = deviceIndex;
                _selectedRecordingDeviceName = deviceInfo.Name;
                logger.LogInformation("Selected recording device: {DeviceIndex} - {DeviceName}",
                    deviceIndex, deviceInfo.Name);
            }
        }
    }

    public int GetSelectedPlaybackDeviceIndex() => _selectedPlaybackDeviceIndex;
    public int GetSelectedRecordingDeviceIndex() => _selectedRecordingDeviceIndex;

    public List<string> GetPlaybackDevices()
    {
        if (!_playbackInitialized)
        {
            lock (_deviceLock) { _playbackBassIndices = new List<int>(); DefaultPlaybackDevice = -1; }
            return new List<string>();
        }

        // Build into locals first, then swap atomically under lock.
        // This prevents GetPlaybackBassIndex (called from UI thread) from reading a
        // partially-populated list while the monitoring thread is rebuilding it.
        var deviceList = new List<string>();
        var bassIndices = new List<int>();
        int defaultIndex = -1;

        for (var i = 0; i < Bass.DeviceCount; i++)
        {
            var deviceInfo = Bass.GetDeviceInfo(i);
            if (!deviceInfo.IsEnabled) continue;
            if (deviceInfo.IsDefault)
                defaultIndex = deviceList.Count; // list index, not BASS index
            deviceList.Add(deviceInfo.Name);
            bassIndices.Add(i);
        }

        lock (_deviceLock)
        {
            _playbackBassIndices = bassIndices;
            DefaultPlaybackDevice = defaultIndex;
        }

        return deviceList;
    }

    public List<string> GetRecordingDevices()
    {
        if (!_recordingInitialized)
        {
            lock (_deviceLock) { _recordingBassIndices = new List<int>(); DefaultRecordingDevice = -1; }
            return new List<string>();
        }

        var deviceList = new List<string>();
        var bassIndices = new List<int>();
        int defaultIndex = -1;

        for (var i = 0; i < Bass.RecordingDeviceCount; i++)
        {
            var deviceInfo = Bass.RecordGetDeviceInfo(i);
            if (!deviceInfo.IsEnabled) continue;
            if (deviceInfo.IsDefault)
                defaultIndex = deviceList.Count; // list index, not BASS index
            deviceList.Add(deviceInfo.Name);
            bassIndices.Add(i);
        }

        lock (_deviceLock)
        {
            _recordingBassIndices = bassIndices;
            DefaultRecordingDevice = defaultIndex;
        }

        return deviceList;
    }

    public int GetPlaybackBassIndex(int listIndex)
    {
        lock (_deviceLock)
        {
            return listIndex >= 0 && listIndex < _playbackBassIndices.Count
                ? _playbackBassIndices[listIndex]
                : -1;
        }
    }

    public int GetRecordingBassIndex(int listIndex)
    {
        lock (_deviceLock)
        {
            return listIndex >= 0 && listIndex < _recordingBassIndices.Count
                ? _recordingBassIndices[listIndex]
                : -1;
        }
    }

    public async ValueTask DisposeAsync()
    {
        // Stop device monitoring
        _monitoringCts?.Cancel();

        if (_monitoringTask != null)
        {
            await _monitoringTask;
        }

        _monitoringCts?.Dispose();
        Bass.Free();
    }
}

public class DeviceChangedEventArgs : EventArgs
{
    public required List<string> Devices { get; init; }
    public required int OldDeviceIndex { get; init; }
    public required int NewDeviceIndex { get; init; }
    public required bool DeviceWasRemoved { get; init; }
    public required int DefaultDeviceIndex { get; init; }
}
