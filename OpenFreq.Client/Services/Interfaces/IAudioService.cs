using System;
using System.Collections.Generic;

namespace OpenFreqClient.Services.Interfaces;

public interface IAudioService: IAsyncDisposable
{
    public List<string> GetPlaybackDevices();
    public List<string> GetRecordingDevices();

    public event EventHandler<DeviceChangedEventArgs>? PlaybackDevicesChanged;
    public event EventHandler<DeviceChangedEventArgs>? RecordingDevicesChanged;
    
    public void Init();
    public int DefaultPlaybackDevice { get; }
    public int DefaultRecordingDevice { get; }
}