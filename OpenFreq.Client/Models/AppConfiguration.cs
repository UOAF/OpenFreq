using System;
using System.Collections.Generic;
using OpenFreq.Common;
using OpenFreqAudio;

namespace OpenFreqClient.Models;

public class AppConfiguration
{
    public OpenFreqSettings Settings { get; set; } = new();
    public List<ChannelGroupData> ChannelGroups { get; set; } = new();
}

public class ChannelGroupData
{
    public string Name { get; set; } = String.Empty;
    public double TxPowerDbm { get; set; }
    public double RxSensitivityDbm { get; set; }
    public double AntennaElevationM { get; set; }
    public Position? Position { get; set; }
    public string? AcmiTrackingId { get; set; }
    public List<ChannelData> Channels { get; set; } = new();
    public RadioStationPreset Preset { get; set; } = RadioStationPresets.AWACS;
}

public class ChannelData
{
    public string? Name { get; set; }
    public int FrequencyKhz { get; set; }  // KHz
    public Channel.ChannelType Type { get; set; }
    public string HotkeyCode { get; set; } = "VcUndefined";
    public bool Enabled { get; set; }
}
