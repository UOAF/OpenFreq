/// <summary>
/// Converts between BMS SharedMemory coordinates (feet) and BMS Heightmap coordinates (METERS for FastPathAudioSim)
/// </summary>
public static class BmsHeightmapConverter
{
    private const double HEIGHTMAP_SIZE_METERS = 1_024_000.0;
    private const double FEET_PER_METER = 3.28084;

    /// <summary>
    /// Converts BMS SharedMemory coordinates to Heightmap coordinates (in METERS for FastPathAudioSim)
    /// </summary>
    /// <param name="x">BMS X in feet (East/West)</param>
    /// <param name="y">BMS Y in feet (North/South)</param>
    /// <param name="z">BMS Z in feet (altitude)</param>
    /// <returns>(heightmapX, heightmapY, altitudeMeters) - all in METERS</returns>
    public static (double x, double y, double altitudeMeters) ToHeightmap(double x, double y, double z)
    {
        // Convert feet to meters
        var heightmapX = x / FEET_PER_METER;
        var heightmapY = HEIGHTMAP_SIZE_METERS - (y / FEET_PER_METER);
        var altitudeMeters = z / FEET_PER_METER;

        return (heightmapX, heightmapY, altitudeMeters);
    }

    /// <summary>
    /// Converts Heightmap coordinates to BMS SharedMemory coordinates
    /// </summary>
    /// <param name="heightmapX">Heightmap X in METERS</param>
    /// <param name="heightmapY">Heightmap Y in METERS</param>
    /// <param name="altitudeMeters">Altitude in meters</param>
    /// <returns>(bmsX, bmsY, bmsZ) - all in FEET</returns>
    public static (double x, double y, double z) FromHeightmap(double heightmapX, double heightmapY, double altitudeMeters)
    {
        var x = heightmapX * FEET_PER_METER;
        var y = (HEIGHTMAP_SIZE_METERS - heightmapY) * FEET_PER_METER;
        var z = altitudeMeters * FEET_PER_METER;

        return (x, y, z);
    }
}
