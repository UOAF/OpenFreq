using System;
using CommunityToolkit.Mvvm.ComponentModel;
using OpenFreq.Common;
using OpenFreqAudio;

namespace OpenFreq.Client.Models;

public partial class RadioStationData : ObservableObject
{
    public enum RadioStationType
    {
        BMS,
        ACMI,
        STATIONARY
    }

    [ObservableProperty]
    public partial RadioStationType Type { get; set; } = RadioStationType.STATIONARY;
    public Vector3 Vector3 { get; set; } = new(0, 0, 0);
    [ObservableProperty] public required partial RadioStationPreset Preset { get; set; }
    [ObservableProperty] public required partial double Ppm { get; set; }
    [ObservableProperty] public partial string? AcmiAircraftId { get; set; } = string.Empty;

    partial void OnPresetChanged(RadioStationPreset value)
    {
        Ppm = value.GetRandomPpm();
    }
}
