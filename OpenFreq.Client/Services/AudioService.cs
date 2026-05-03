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

    // Events to notify when devices change
    public event EventHandler<DeviceChangedEventArgs>? PlaybackDevicesChanged;
    public event EventHandler<DeviceChangedEventArgs>? RecordingDevicesChanged;

    // BASS device indices parallel to the enabled-device name lists
    private List<int> _playbackBassIndices = new();
    private List<int> _recordingBassIndices = new();

    // Device monitoring
    private CancellationTokenSource? _monitoringCts;
    private Task? _monitoringTask;
    private List<string> _lastPlaybackDevices = new();
    private List<string> _lastRecordingDevices = new();

    public void Init()
    {
        if (!Bass.Init() || !Bass.RecordInit())
        {
            throw new Exception($"Failed to initialize BASS: {Bass.LastError}");
        }

        // Get default devices
        _lastPlaybackDevices = GetPlaybackDevices();
        _lastRecordingDevices = GetRecordingDevices();

        // Start monitoring for device changes
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
        // Check playback devices
        var currentPlaybackDevices = GetPlaybackDevices();
        if (!currentPlaybackDevices.SequenceEqual(_lastPlaybackDevices))
        {
            logger.LogInformation("Playback devices changed");
            _lastPlaybackDevices = currentPlaybackDevices;
            HandlePlaybackDeviceChange();
        }

        // Check recording devices
        var currentRecordingDevices = GetRecordingDevices();
        if (!currentRecordingDevices.SequenceEqual(_lastRecordingDevices))
        {
            logger.LogInformation("Recording devices changed");
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
            logger.LogWarning("Playback device '{DeviceName}' not found, falling back to default device {DefaultDevice}",
                _selectedPlaybackDeviceName, DefaultPlaybackDevice);
            newIndex = DefaultPlaybackDevice; // list index

            if (newIndex != -1)
            {
                var deviceInfo = Bass.GetDeviceInfo(GetPlaybackBassIndex(newIndex));
                _selectedPlaybackDeviceName = deviceInfo.Name;
            }
        }

        _selectedPlaybackDeviceIndex = newIndex; // list index

        PlaybackDevicesChanged?.Invoke(this, new DeviceChangedEventArgs
        {
            Devices = devices,
            OldDeviceIndex = oldIndex,
            NewDeviceIndex = newIndex,
            DeviceWasRemoved = oldIndex != -1 && newIndex != oldIndex,
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
            logger.LogWarning("Recording device '{DeviceName}' not found, falling back to default device {DefaultDevice}",
                _selectedRecordingDeviceName, DefaultRecordingDevice);
            newIndex = DefaultRecordingDevice; // list index

            if (newIndex != -1)
            {
                var deviceInfo = Bass.RecordGetDeviceInfo(GetRecordingBassIndex(newIndex));
                _selectedRecordingDeviceName = deviceInfo.Name;
            }
        }

        _selectedRecordingDeviceIndex = newIndex; // list index

        RecordingDevicesChanged?.Invoke(this, new DeviceChangedEventArgs
        {
            Devices = devices,
            OldDeviceIndex = oldIndex,
            NewDeviceIndex = newIndex,
            DeviceWasRemoved = oldIndex != -1 && newIndex != oldIndex,
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
        List<string> deviceList = [];
        _playbackBassIndices = [];
        DefaultPlaybackDevice = -1;

        for (var i = 0; i < Bass.DeviceCount; i++)
        {
            var deviceInfo = Bass.GetDeviceInfo(i);
            if (!deviceInfo.IsEnabled) continue;
            if (deviceInfo.IsDefault)
                DefaultPlaybackDevice = deviceList.Count; // list index, not BASS index
            deviceList.Add(deviceInfo.Name);
            _playbackBassIndices.Add(i);
        }

        return deviceList;
    }

    public List<string> GetRecordingDevices()
    {
        List<string> deviceList = [];
        _recordingBassIndices = [];
        DefaultRecordingDevice = -1;

        for (var i = 0; i < Bass.RecordingDeviceCount; i++)
        {
            var deviceInfo = Bass.RecordGetDeviceInfo(i);
            if (!deviceInfo.IsEnabled) continue;
            if (deviceInfo.IsDefault)
                DefaultRecordingDevice = deviceList.Count; // list index, not BASS index
            deviceList.Add(deviceInfo.Name);
            _recordingBassIndices.Add(i);
        }

        return deviceList;
    }

    public int GetPlaybackBassIndex(int listIndex)
        => listIndex >= 0 && listIndex < _playbackBassIndices.Count ? _playbackBassIndices[listIndex] : -1;

    public int GetRecordingBassIndex(int listIndex)
        => listIndex >= 0 && listIndex < _recordingBassIndices.Count ? _recordingBassIndices[listIndex] : -1;

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