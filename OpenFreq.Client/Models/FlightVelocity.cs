using System;

namespace FalconBmsDataService.Models;

/// <summary>
/// Represents the current velocity of the aircraft in Falcon BMS coordinates (feet/s)
/// </summary>
public class FlightVelocity
{
    /// <summary>
    /// Ownship North position (ft/s)
    /// </summary>
    public float X { get; set; }

    /// <summary>
    /// Ownship East position (ft/s)
    /// </summary>
    public float Y { get; set; }

    /// <summary>
    /// Ownship Down position (ft/s)
    /// </summary>
    public float Z { get; set; }

    /// <summary>
    /// Timestamp when this data was captured
    /// </summary>
    public DateTime Timestamp { get; set; }

    public FlightVelocity()
    {
        Timestamp = DateTime.UtcNow;
    }

    public FlightVelocity(float x, float y, float z)
    {
        X = x;
        Y = y;
        Z = z;
        Timestamp = DateTime.UtcNow;
    }
    
    public override string ToString()
    {
        return $"Velocity(X={X:F2}, Y={Y:F2}, Z={Z:F2})";
    }
}
