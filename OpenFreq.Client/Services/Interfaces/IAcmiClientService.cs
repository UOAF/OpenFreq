using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using OpenFreq.Client.Services.Interfaces;
using OpenFreqClient.Models;

namespace OpenFreq.Services.Acmi;

public interface IAcmiClientService : IDisposable, ILifecycleService
{
    /// <summary>Fired when connection status changes</summary>
    event EventHandler<AcmiConnectionEventArgs>? ConnectionStatusChanged;

    /// <summary>Fired when connection is established</summary>
    event EventHandler<AcmiConnectionEventArgs>? Connected;

    /// <summary>Fired when connection is lost</summary>
    event EventHandler<AcmiConnectionEventArgs>? ConnectionLost;

    /// <summary>Connects to the ACMI server</summary>
    Task<bool> ConnectAsync(string connectionString, string password = "", int maxRetries = -1);

    void CancelConnectionAttempts();

    /// <summary>Disconnects from the ACMI server</summary>
    Task DisconnectAsync();

    /// <summary>Gets an aircraft by its object ID</summary>
    AcmiAircraft? GetAircraft(string objectId);

    /// <summary>Gets all aircraft currently tracked</summary>
    IEnumerable<AcmiAircraft> GetAllAircraft();
    
    /// <summary>
    /// Adds an object ID which will be tracked.
    /// Only this aircraft will trigger the TrackedAircraftTransformUpdated event.
    /// Pass null to stop tracking.
    /// </summary>
    void AddTrackingForAircraft(string? objectId);
    void RemoveTrackingForAircraft(string? objectId);
    
    
    public AcmiConnectionStatus Status { get; }
}

/// <summary>
/// Event args for tracked aircraft transform updates
/// </summary>
public class AircraftTransformEventArgs : EventArgs
{
    /// <summary>The aircraft's object ID</summary>
    public string ObjectId { get; set; } = string.Empty;
    
    /// <summary>The aircraft's current transform data</summary>
    public AircraftTransform Transform { get; set; } = new();
    
    /// <summary>When this update occurred</summary>
    public DateTime Timestamp { get; set; }
}