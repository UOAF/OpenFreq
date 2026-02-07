namespace OpenFreq.Utilities;

/// <summary>
/// Converts between BMS SharedMemory coordinates (feet) and BMS Heightmap coordinates (pixels)
/// </summary>
public static class BmsHeightmapConverter
{
    private const int HEIGHTMAP_SIZE_PX = 32768;
    private const double HEIGHTMAP_SIZE_KM = 1024.0;
    private const double METERS_PER_PIXEL = (HEIGHTMAP_SIZE_KM * 1000.0) / HEIGHTMAP_SIZE_PX;
    private const double FEET_PER_METER = 3.28084;
    private const double FEET_PER_PIXEL = METERS_PER_PIXEL * FEET_PER_METER;
    
    /// <summary>
    /// Converts BMS SharedMemory coordinates to Heightmap coordinates
    /// </summary>
    /// <param name="x">BMS X in feet (East/West)</param>
    /// <param name="y">BMS Y in feet (North/South)</param>
    /// <param name="z">BMS Z in feet (altitude)</param>
    /// <returns>(heightmapX, heightmapY, altitudeFeet)</returns>
    public static (double x, double y, double altitudeFeet) ToHeightmap(double x, double y, double z)
    {
        var heightmapX = x / FEET_PER_PIXEL;
        var heightmapY = HEIGHTMAP_SIZE_PX - (y / FEET_PER_PIXEL);
        
        return (heightmapX, heightmapY, z);
    }
    
    /// <summary>
    /// Converts Heightmap coordinates to BMS SharedMemory coordinates
    /// </summary>
    /// <param name="heightmapX">Heightmap X in pixels</param>
    /// <param name="heightmapY">Heightmap Y in pixels</param>
    /// <param name="altitudeFeet">Altitude in feet</param>
    /// <returns>(bmsX, bmsY, bmsZ)</returns>
    public static (double x, double y, double z) FromHeightmap(double heightmapX, double heightmapY, double altitudeFeet)
    {
        var x = heightmapX * FEET_PER_PIXEL;
        var y = (HEIGHTMAP_SIZE_PX - heightmapY) * FEET_PER_PIXEL;
        
        return (x, y, altitudeFeet);
    }
}