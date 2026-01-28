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
    public Position? Position { get; set; }
    [ObservableProperty]
    public partial RadioStationPreset Preset { get; set; }
}