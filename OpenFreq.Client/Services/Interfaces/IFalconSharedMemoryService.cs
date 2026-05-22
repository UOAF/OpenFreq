using System;
using FalconBmsDataService.Models;
using FalconRadioService.Models;
using OpenFreq.Client.Services.Interfaces;
using OpenFreq.Common;

namespace FalconBmsDataService.Services;

/// <summary>
/// Interface for the Falcon BMS shared memory service
/// </summary>
public interface IFalconSharedMemoryService : IDisposable, ILifecycleService
{
    /// <summary>
    /// Current state of the service
    /// </summary>
    ServiceState State { get; }

    /// <summary>
    /// Current flight position (null if disconnected)
    /// </summary>
    FlightPosition? Position { get; }
    
    /// <summary>
    /// Current flight velocity (null if disconnected)
    /// </summary>
    FlightVelocity? Velocity { get; }

    /// <summary>
    /// Theater terrain directory (read once on connect, null if never connected)
    /// </summary>
    string? TheaterTerrainDir { get; }

    /// <summary>
    /// Current aircraft name, e.g. "F-16C Block 52+" (read once on connect, null if never connected)
    /// </summary>
    string? AcName { get; }

    /// <summary>
    /// Current aircraft NCTR string (read once on connect, null if never connected)
    /// </summary>
    string? AcNCTR { get; }

    /// <summary>
    /// 3D Status (null if disconnected)
    /// </summary>
    bool? IsFlying { get; }

    /// <summary>
    /// Polling frequency in Hz (default: 2.0 = 2 times per second)
    /// </summary>
    double PollingFrequencyHz { get; set; }

    /// <summary>
    /// Event fired when service state changes
    /// </summary>
    event EventHandler<ServiceStateChangedEventArgs>? StateChanged;

    event EventHandler<FlyingStateChangedEventArgs>? FlyingStateChanged;

    /// <summary>
    /// Fired when AcName/AcNCTR become available (once per connect)
    /// </summary>
    event EventHandler<AircraftInfoChangedEventArgs>? AircraftInfoChanged;
}