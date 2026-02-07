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
    /// Theater terrain directory (read once on connect, null if never connected)
    /// </summary>
    string? TheaterTerrainDir { get; }

    /// <summary>
    /// Polling frequency in Hz (default: 2.0 = 2 times per second)
    /// </summary>
    double PollingFrequencyHz { get; set; }

    /// <summary>
    /// Event fired when service state changes
    /// </summary>
    event EventHandler<ServiceStateChangedEventArgs>? StateChanged;

    event EventHandler<FlyingStateChangedEventArgs>? FlyingStateChanged;
}