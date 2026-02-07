namespace OpenFreqClient.Models;

using System;

/// <summary>
/// Lightweight position and orientation data for an aircraft.
/// </summary>
public class AircraftTransform
{
    /// <summary>Longitude in degrees</summary>
    public double Longitude { get; set; }
    
    /// <summary>Latitude in degrees</summary>
    public double Latitude { get; set; }
    
    /// <summary>Altitude in meters</summary>
    public double Altitude { get; set; }
    
    public double AltitudeFt => Altitude * 3.28084;
    
    /// <summary>Roll angle in degrees</summary>
    public double Roll { get; set; }
    
    /// <summary>Pitch angle in degrees</summary>
    public double Pitch { get; set; }
    
    /// <summary>Yaw angle in degrees</summary>
    public double Yaw { get; set; }
    
    /// <summary>U coordinate (game world X position in meters)</summary>
    public double U { get; set; }
    
    /// <summary>V coordinate (game world Y position in meters)</summary>
    public double V { get; set; }
    
    /// <summary>Heading in degrees</summary>
    public double Heading { get; set; }
}

/// <summary>
/// Lightweight aircraft data - only essential fields for position tracking.
/// </summary>
public class AcmiAircraft
{
    /// <summary>Unique object ID from the ACMI stream</summary>
    public string ObjectId { get; set; } = string.Empty;
    
    /// <summary>Aircraft type (e.g., "Air+FixedWing")</summary>
    public string Type { get; set; } = string.Empty;
    
    /// <summary>Aircraft name (e.g., "F-16CM-52")</summary>
    public string Name { get; set; } = string.Empty;
    
    /// <summary>Pilot name</summary>
    public string Pilot { get; set; } = string.Empty;
    
    /// <summary>Callsign</summary>
    public string CallSign { get; set; } = string.Empty;
    
    /// <summary>Coalition (e.g., "Blue", "Red")</summary>
    public string Coalition { get; set; } = string.Empty;
    
    /// <summary>Position and orientation data</summary>
    public AircraftTransform Transform { get; set; } = new();
    
    /// <summary>Last update timestamp</summary>
    public DateTime LastUpdate { get; set; } = DateTime.UtcNow;
}

/// <summary>
/// Connection status for the ACMI client.
/// </summary>
public enum AcmiConnectionStatus
{
    Disconnected,
    Connecting,
    Connected,
    Failed
}

/// <summary>
/// Event args for connection status changes.
/// </summary>
public class AcmiConnectionEventArgs : EventArgs
{
    public AcmiConnectionStatus Status { get; set; }
    public string Message { get; set; } = string.Empty;
    public DateTime Timestamp { get; set; } = DateTime.UtcNow;
}