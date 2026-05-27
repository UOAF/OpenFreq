using System.Text.Json.Serialization;
using OpenFreqAudio;

namespace OpenFreq.Common;

/// <summary>
/// Represents transmission state for a single frequency
/// </summary>
public class FrequencyTransmission
{
    /// <summary>
    /// Frequency in KHz
    /// </summary>
    [JsonPropertyName("khz")]
    public int Khz { get; set; }
    [JsonPropertyName("txpower")]
    public double TxPowerWatts { get; set; }

    [JsonPropertyName("position")]
    public Vector3? Position { get; set; }

    [JsonPropertyName("velocity")]
    public Vector3? Velocity { get; set; }

    [JsonPropertyName("in3d")]
    public bool In3d { get; set; }

    [JsonPropertyName("ppm")]
    public double Ppm { get; set; }

    [JsonPropertyName("ambient")]
    public AmbientNoiseType AmbientNoiseType { get; set; }

    public FrequencyTransmission()
    {
    }

    public FrequencyTransmission(int khz, double txPowerWatts, double ppm, Vector3? position, Vector3? velocity, bool in3d,
        AmbientNoiseType ambientNoiseType = AmbientNoiseType.None)
    {
        Khz = khz;
        TxPowerWatts = txPowerWatts;
        Ppm = ppm;
        Position = position;
        Velocity = velocity;
        In3d = in3d;
        AmbientNoiseType = ambientNoiseType;
    }
}
