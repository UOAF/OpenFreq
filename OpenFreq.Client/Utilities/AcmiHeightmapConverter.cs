/// <summary>
/// Converts between ACMI coordinates (meters) and BMS Heightmap coordinates (METERS for FastPathAudioSim)
/// </summary>
public static class AcmiHeightmapConverter
{
    private const double THEATRE_SIZE_METERS = 1_024_000.0;
    
    /// <summary>
    /// Converts ACMI coordinates to Heightmap coordinates (in METERS for FastPathAudioSim)
    /// </summary>
    /// <param name="u">ACMI U in meters (East/West)</param>
    /// <param name="v">ACMI V in meters (North/South)</param>
    /// <param name="altitudeMeters">ACMI altitude in meters</param>
    /// <returns>(heightmapX, heightmapY, altitudeMeters) - all in METERS</returns>
    public static (double x, double y, double altitudeMeters) ToHeightmap(double u, double v, double altitudeMeters)
    {
        // ACMI coordinates are already in meters, just need Y-flip
        var x = u;
        var y = THEATRE_SIZE_METERS - v;
        
        return (x, y, altitudeMeters);
    }
    
    /// <summary>
    /// Converts Heightmap coordinates to ACMI coordinates
    /// </summary>
    /// <param name="x">Heightmap X in METERS</param>
    /// <param name="y">Heightmap Y in METERS</param>
    /// <param name="altitudeMeters">Altitude in meters</param>
    /// <returns>(u, v, altitudeMeters) - all in METERS</returns>
    public static (double u, double v, double altitudeMeters) FromHeightmap(double x, double y, double altitudeMeters)
    {
        var u = x;
        var v = THEATRE_SIZE_METERS - y;
        
        return (u, v, altitudeMeters);
    }
}