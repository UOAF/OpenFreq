namespace OpenFreq.Common;

/// <summary>
/// Represents transmission state for a single frequency
/// </summary>
public class FrequencyTransmission
{
    /// <summary>
    /// Frequency in MHz
    /// </summary>
    public double Mhz { get; set; }
    
    public double TxPowerWatts { get; set; }
    
    /// <summary>
    /// True if this is the first packet of a transmission on this frequency
    /// </summary>
    public bool BeginMarker { get; set; }
    
    /// <summary>
    /// True if this is the last packet of a transmission on this frequency
    /// </summary>
    public bool EndMarker { get; set; }
    
    public FrequencyTransmission()
    {
    }
    
    public FrequencyTransmission(double mhz, double txPowerWatts, bool beginMarker = false, bool endMarker = false)
    {
        Mhz = mhz;
        TxPowerWatts = txPowerWatts;
        BeginMarker = beginMarker;
        EndMarker = endMarker;
    }
    
    public override string ToString()
    {
        var markers = "";
        if (BeginMarker) markers += "BEGIN ";
        if (EndMarker) markers += "END ";
        return $"{Mhz:F1} MHz {(markers.Length > 0 ? $"[{markers.Trim()}]" : "")}";
    }
}