using System.Text.Json.Serialization;

namespace OpenFreq.Common;

/// <summary>
/// Metadata included in the header of each UDP audio packet
/// </summary>
public class AudioPacketMetadata
{
    [JsonPropertyName("id")]
    public string clientId  { get; set; }
   
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
    
    [JsonPropertyName("position")]
    public Position? Position { get; set; }
    
    public bool In3D { get; set; }
    
    public double TxWatts {get; set;}
    
    public int PcmDataLength { get; set; }
    
    [JsonIgnore]
    public bool HasAnyBeginMarker => Frequencies.Any(f => f.BeginMarker);
}

// Position data structure
public class Position
{
    public double X { get; set; }
    public double Y { get; set; }
    public double Z { get; set; }
    
    public Position() {}

    public Position(double x, double y, double z)
    {
        X = x;
        Y = y;
        Z = z;
    }

    public override string ToString()
    {
        return $"{X},{Y},{Z}";
    }
    
    /// <summary>
    /// Translates BMS SharedMemory coordinates (feet, origin bottom left) to Heightmap X/Y coordinates (pixels, origin top left)
    /// </summary>
    public Position ToHeightmapPosition()
    {
        // Heightmap specifications
        const int HEIGHTMAP_SIZE_PX = 32768;
        const float HEIGHTMAP_SIZE_KM = 1024f;
        
        // Calculate meters per pixel
        const float METERS_PER_PIXEL = (HEIGHTMAP_SIZE_KM * 1000f) / HEIGHTMAP_SIZE_PX;
        // = 31.25 meters/pixel

        // Calculate feet per pixel
        const float FEET_PER_METER = 3.28084f;
        const float FEET_PER_PIXEL = METERS_PER_PIXEL * FEET_PER_METER;
        // = 102.5458 feet/pixel

        var heightmapX = Y / FEET_PER_PIXEL;
        var heightmapY = HEIGHTMAP_SIZE_PX - (X / FEET_PER_PIXEL);
        return new Position(heightmapX, heightmapY, Z);
    }
}