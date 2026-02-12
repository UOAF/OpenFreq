using System.Text.Json.Serialization;

namespace OpenFreq.Common;

/// <summary>
/// Metadata included in the header of each UDP audio packet
/// </summary>
public class AudioPacketMetadata
{
    [JsonPropertyName("id")]
    public required string ClientId  { get; set; }
   
    /// <summary>
    /// Frequencies being transmitted on with per-frequency markers
    /// </summary>
    [JsonPropertyName("frequencies")]
    public List<FrequencyTransmission> Frequencies { get; set; } = new();
    
    /// <summary>
    /// Optional timestamp for debugging/monitoring
    /// </summary>
    [JsonPropertyName("capture_timestamp")]
    public long CaptureTimestamp { get; set; }
    
    [JsonPropertyName("send_timestamp")]
    public long SendTimestamp { get; set; }
    
    [JsonPropertyName("server_send_timestamp")]
    public long ServerSendTimestamp { get; set; }
    
    [JsonIgnore]
    public bool HasAnyBeginMarker => Frequencies.Any(f => f.BeginMarker);
}

// Position data structure
public class Position
{
    [JsonPropertyName("x")]
    public double X { get; set; }
    [JsonPropertyName("y")]
    public double Y { get; set; }
    [JsonPropertyName("z")]
    public double Z { get; set; }
    
    public Position() {}

    public Position(double x, double y, double z)
    {
        X = x;
        Y = y;
        Z = z;
    }

    public Position((double, double, double) data)
    {
        X = data.Item1;
        Y = data.Item2;
        Z = data.Item3;
    }

    public override string ToString()
    {
        return $"{X},{Y},{Z}";
    }
}