using System;
using System.Collections.Generic;

namespace OpenFreqClient.Services.Interfaces;

public interface IAudioService: IAsyncDisposable
{
    public List<string> GetPlaybackDevices();
    public List<string> GetRecordingDevices();

    public event EventHandler<DeviceChangedEventArgs>? PlaybackDevicesChanged;
    public event EventHandler<DeviceChangedEventArgs>? RecordingDevicesChanged;

    /// <summary>
    /// Fired when device monitoring encounters a user-facing error (device init failure, no valid device, ...)
    /// </summary>
    public event EventHandler<string>? AudioDeviceErrorOccurred;

    public void Init();

    /// <summary>List index (0-based position among enabled devices) of the system default playback device.</summary>
    public int DefaultPlaybackDevice { get; }
    /// <summary>List index (0-based position among enabled devices) of the system default recording device.</summary>
    public int DefaultRecordingDevice { get; }

    /// <summary>Convert a list index (ComboBox selection) to the underlying BASS device index.</summary>
    public int GetPlaybackBassIndex(int listIndex);
    /// <summary>Convert a list index (ComboBox selection) to the underlying BASS recording device index.</summary>
    public int GetRecordingBassIndex(int listIndex);
}