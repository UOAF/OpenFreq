using System;
using System.Collections.Generic;
using ManagedBass;
using OpenFreqClient.Services.Interfaces;

namespace OpenFreqClient;

public class AudioService : IAudioService
{
    public int DefaultPlaybackDevice { get; set; } = -1;
    public int DefaultRecordingDevice { get; set; } = -1;

    public void Init()
    {
        if (!Bass.Init() || !Bass.RecordInit())
        {
            throw new Exception($"Failed to initialize BASS: {Bass.LastError}");
        }

        // Get default devices
        GetPlaybackDevices();
        GetRecordingDevices();
    }

    public List<string> GetPlaybackDevices()
    {
        List<string> deviceList = [];
        for (var i = 0; i < Bass.DeviceCount; i++)
        {
            var deviceInfo = Bass.GetDeviceInfo(i);
            if (!deviceInfo.IsEnabled) continue;
            deviceList.Add(deviceInfo.Name);
            if (deviceInfo.IsDefault)
                DefaultPlaybackDevice = i;
        }

        return deviceList;
    }

    public List<string> GetRecordingDevices()
    {
        List<string> deviceList = [];
        for (var i = 0; i < Bass.RecordingDeviceCount; i++)
        {
            var deviceInfo = Bass.RecordGetDeviceInfo(i);
            if (!deviceInfo.IsEnabled) continue;
            deviceList.Add(deviceInfo.Name);
            if (deviceInfo.IsDefault)
                DefaultRecordingDevice = i;

        }

        return deviceList;
    }


    public void Dispose()
    {
        Bass.Free();
    }
}