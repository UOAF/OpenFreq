/// <summary>
/// Converts between BMS SharedMemory coordinates (feet) and BMS Heightmap coordinates (METERS for FastPathAudioSim)
/// </summary>
public static class BmsHeightmapConverter
{
    public const double HEIGHTMAP_SIZE_METERS = 1_024_000.0;
    public const int HEIGHTMAP_SAMPLES = 32_768;

    /// <summary>
    /// Heightmap Y, in meters, of the last row, which BMS puts at 0m north.
    /// </summary>
    /// <remarks>
    /// BMS heightmap samples are fenceposts, so they span (HEIGHTMAP_SAMPLES - 1)^2 cells.
    /// For sim coordinates, BMS uses row (HEIGHTMAP_SAMPLES - 1) - north.
    /// For some reason (possible bug?) BMS maps Lat/Lon row 0 on the north edge of
    /// the heightmap, i.e., one cell north of the sim coordinates.
    /// </remarks>
    public const double LAST_ROW_Y_METERS = HEIGHTMAP_SIZE_METERS * (HEIGHTMAP_SAMPLES - 1) / HEIGHTMAP_SAMPLES;

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
        var heightmapY = LAST_ROW_Y_METERS - (y / FEET_PER_METER);
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
        var y = (LAST_ROW_Y_METERS - heightmapY) * FEET_PER_METER;
        var z = altitudeMeters * FEET_PER_METER;

        return (x, y, z);
    }
}
