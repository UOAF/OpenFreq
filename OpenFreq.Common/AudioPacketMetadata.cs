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
    
    [JsonPropertyName("3d")]
    public bool In3D { get; set; }
    
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

    public override string ToString()
    {
        return $"{X},{Y},{Z}";
    }
    
    /// <summary>
    /// Translates BMS SharedMemory coordinates (feet, origin bottom left) to Heightmap X/Y coordinates (pixels, origin top left)
    /// </summary>
    public Position ToHeightmapPosition()
    {
        Console.Out.WriteLine($"RAW POSITION: {X},{Y},{Z}");
    
        const int HEIGHTMAP_SIZE_PX = 32768;
        const double HEIGHTMAP_SIZE_KM = 1024.0;
        const double METERS_PER_PIXEL = (HEIGHTMAP_SIZE_KM * 1000.0) / HEIGHTMAP_SIZE_PX;
        const double FEET_PER_METER = 3.28084d;
        const double FEET_PER_PIXEL = METERS_PER_PIXEL * FEET_PER_METER;

        // BMS: origin bottom-left
        // Heightmap: origin top-left
    
        double heightmapX = X / FEET_PER_PIXEL;
    
        // Swap & Flip: BMS X (North from bottom) → Heightmap Y (from top)
        double heightmapY = HEIGHTMAP_SIZE_PX - (Y / FEET_PER_PIXEL);
    
        Console.Out.WriteLine($"HEIGHTMAP POSITION: {heightmapX},{heightmapY}");
        return new Position(heightmapX, heightmapY, Z);
    }
}