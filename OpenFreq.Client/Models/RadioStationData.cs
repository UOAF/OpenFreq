using OpenFreq.Common;
using OpenFreqAudio;

namespace OpenFreq.Client.Models;

public class RadioStationData
{
    public enum RadioStationType
    {
        BMS,
        ACMI,
        STATIONARY
    }
    public RadioStationType Type { get; set; }
    public Position? Position { get; set; }
    public double Frequency { get; set; }
    public RadioStationPreset Preset { get; set; } = RadioStationPresets.Fighter;
}