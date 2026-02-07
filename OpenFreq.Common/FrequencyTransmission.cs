using System.Text.Json.Serialization;

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
    
    /// <summary>
    /// True if this is the first packet of a transmission on this frequency
    /// </summary>
    [JsonPropertyName("begin")]
    public bool BeginMarker { get; set; }
    
    /// <summary>
    /// True if this is the last packet of a transmission on this frequency
    /// </summary>
    [JsonPropertyName("end")]
    public bool EndMarker { get; set; }

    [JsonPropertyName("position")]
    public Position? Position { get; set; }
    
    [JsonPropertyName("in3d")]
    public bool In3d { get; set; }

    public FrequencyTransmission()
    {
    }

    public FrequencyTransmission(int khz, double txPowerWatts, Position? position, bool in3d, bool beginMarker = false,
        bool endMarker = false)
    {
        Khz = khz;
        TxPowerWatts = txPowerWatts;
        BeginMarker = beginMarker;
        EndMarker = endMarker;
        Position = position;
        In3d = in3d;
    }

    public override string ToString()
    {
        var markers = "";
        if (BeginMarker) markers += "BEGIN ";
        if (EndMarker) markers += "END ";
        return $"{Khz/1000d:F3} MHz {(markers.Length > 0 ? $"[{markers.Trim()}]" : "")}";
    }
}