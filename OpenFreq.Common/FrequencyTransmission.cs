using System.Text.Json.Serialization;

namespace OpenFreq.Common;

/// <summary>
/// Represents transmission state for a single frequency
/// </summary>
public class FrequencyTransmission
{
    /// <summary>
    /// Frequency in MHz
    /// </summary>
    [JsonPropertyName("mhz")]
    public double Mhz { get; set; }
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

    public FrequencyTransmission()
    {
    }

    public FrequencyTransmission(double mhz, double txPowerWatts, Position position, bool beginMarker = false,
        bool endMarker = false)
    {
        Mhz = mhz;
        TxPowerWatts = txPowerWatts;
        BeginMarker = beginMarker;
        EndMarker = endMarker;
        Position = position;
    }

    public override string ToString()
    {
        var markers = "";
        if (BeginMarker) markers += "BEGIN ";
        if (EndMarker) markers += "END ";
        return $"{Mhz:F1} MHz {(markers.Length > 0 ? $"[{markers.Trim()}]" : "")}";
    }
}