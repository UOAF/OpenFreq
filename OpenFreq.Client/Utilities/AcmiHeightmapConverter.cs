using System;
using OpenFreq.Common;

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

    /// <summary>
    /// Converts aircraft true airspeed and orientation into a 3D velocity vector in world coordinates.
    /// </summary>
    /// <param name="airSpeedMach">Air Speed in Mach</param>
    /// <param name="altitudeMslMeters">Altitude above mean sea level in meters</param>
    /// <param name="pitchDeg">Pitch angle in degrees (positive = nose up)</param>
    /// <param name="yawDeg">Yaw/heading angle in degrees (clockwise from true north: 0°=N, 90°=E, 180°=S, 270°=W)</param>
    /// <returns>Velocity vector (North, East, Up) in m/s</returns>
    public static Vector3 GetVelocityVector(
        double airSpeedMach,
        double altitudeMslMeters,
        double pitchDeg,
        double yawDeg)
    {
        var trueAirspeedMps = CalculateTAS(airSpeedMach, altitudeMslMeters);
        var pitch = pitchDeg * Math.PI / 180.0;
        var yaw = yawDeg * Math.PI / 180.0;

        // Horizontal velocity component
        var vHorizontal = trueAirspeedMps * Math.Cos(pitch);

        // Decompose horizontal into North and East
        var vNorth = vHorizontal * Math.Cos(yaw);
        var vEast = vHorizontal * Math.Sin(yaw);

        // Vertical component (positive up)
        var vUp = trueAirspeedMps * Math.Sin(pitch);

        return new Vector3(vNorth, vEast, vUp);
    }

    /// <summary>
    /// Calculates True Airspeed from Mach number and altitude
    /// </summary>
    /// <param name="mach">Mach number</param>
    /// <param name="altitudeMeters">Altitude in meters MSL</param>
    /// <returns>True airspeed in meters per second</returns>
    public static double CalculateTAS(double mach, double altitudeMeters)
    {
        // ISA temperature at altitude (troposphere up to 11km)
        const double seaLevelTemp = 288.15; // Kelvin
        const double lapseRate = 0.0065; // K/m
        const double tropoPauseAlt = 11000.0; // meters

        double temperature;
        if (altitudeMeters <= tropoPauseAlt)
        {
            // Troposphere: temperature decreases with altitude
            temperature = seaLevelTemp - lapseRate * altitudeMeters;
        }
        else
        {
            // Stratosphere: constant temperature
            temperature = seaLevelTemp - lapseRate * tropoPauseAlt;
        }

        // Speed of sound: a = sqrt(γ × R × T)
        // where γ = 1.4 (ratio of specific heats for air)
        //       R = 287.05 (specific gas constant for air, J/(kg·K))
        const double gamma = 1.4;
        const double R = 287.05;

        double speedOfSound = Math.Sqrt(gamma * R * temperature);

        // TAS = Mach × speed of sound
        return mach * speedOfSound;
    }
}
