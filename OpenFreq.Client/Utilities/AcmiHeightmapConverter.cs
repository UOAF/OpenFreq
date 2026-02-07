using System;

namespace OpenFreq.Utilities;

/// <summary>
/// Converts between ACMI coordinates (meters) and BMS Heightmap coordinates (pixels, feet)
/// </summary>
public static class AcmiHeightmapConverter
{
    private const double THEATRE_SIZE_METERS = 1_024_000.0;
    private const int HEIGHTMAP_SIZE_PX = 32768;
    private const double METERS_TO_FEET = 3.28084;
    
    /// <summary>
    /// Converts ACMI coordinates to Heightmap coordinates
    /// </summary>
    /// <param name="u">ACMI U in meters (East/West)</param>
    /// <param name="v">ACMI V in meters (North/South)</param>
    /// <param name="altitudeMeters">ACMI altitude in meters</param>
    /// <returns>(heightmapX, heightmapY, altitudeFeet)</returns>
    public static (double x, double y, double altitudeFeet) ToHeightmap(double u, double v, double altitudeMeters)
    {
        var x = (u / THEATRE_SIZE_METERS) * HEIGHTMAP_SIZE_PX;
        var y = ((THEATRE_SIZE_METERS - v) / THEATRE_SIZE_METERS) * HEIGHTMAP_SIZE_PX;
        var altitudeFeet = altitudeMeters * METERS_TO_FEET;
        
        return (x, y, altitudeFeet);
    }
    
    /// <summary>
    /// Converts Heightmap coordinates to ACMI coordinates
    /// </summary>
    /// <param name="x">Heightmap X in pixels</param>
    /// <param name="y">Heightmap Y in pixels</param>
    /// <param name="altitudeFeet">Altitude in feet</param>
    /// <returns>(u, v, altitudeMeters)</returns>
    public static (double u, double v, double altitudeMeters) FromHeightmap(double x, double y, double altitudeFeet)
    {
        var u = (x / HEIGHTMAP_SIZE_PX) * THEATRE_SIZE_METERS;
        var v = THEATRE_SIZE_METERS - ((y / HEIGHTMAP_SIZE_PX) * THEATRE_SIZE_METERS);
        var altitudeMeters = altitudeFeet / METERS_TO_FEET;
        
        return (u, v, altitudeMeters);
    }
}