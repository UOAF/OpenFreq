using System;
using System.Collections.Generic;
using OpenFreq.Client.Models;
using OpenFreq.Common;
using OpenFreqAudio;

namespace OpenFreqClient.Models;

public class AppConfiguration
{
    public OpenFreqSettings Settings { get; set; } = new();
    public List<ChannelGroupData> ChannelGroups { get; set; } = [];
}

public class ChannelGroupData
{
    public string Name { get; set; } = string.Empty;
    public double Latitude { get; set; }
    public double Longitude { get; set; }
    public double AltitudeFt { get; set; }
    public string? AcmiTrackingId { get; set; }
    public List<ChannelData> Channels { get; set; } = [];

    public RadioStationData RadioStationData { get; set; } = new()
    {
        Preset = RadioStationPresets.AWACS,
        Vector3 = new Vector3(0, 0, 0),
        Ppm = RadioStationPresets.AWACS.GetRandomPpm()
    };
}

public class ChannelData
{
    public string? Name { get; set; }
    public int FrequencyKhz { get; set; }
    public string HotkeyCode { get; set; } = "VcUndefined";
}