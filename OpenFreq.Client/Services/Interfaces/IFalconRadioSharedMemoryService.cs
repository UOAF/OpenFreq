using System;
using FalconBmsDataService.Models;
using FalconRadioService.Models;
using OpenFreq.Client.Services.Interfaces;

namespace FalconRadioService.Services;

public interface IFalconRadioSharedMemoryService : IDisposable, ILifecycleService
{
    // Service state
    ServiceState State { get; }
    double PollingFrequencyHz { get; set; }

    /// <summary>
    /// True when this instance owns the radio-client mutex and has created the RCS shared memory.
    /// False in read-only mode (another client, e.g. IVC, grabbed the mutex first).
    /// </summary>
    bool IsOwner { get; }

    // Current data (thread-safe)
    string? LogbookName { get; }
    RadioChannel? GetRadioChannel(RadioType radioType);
    RadioDevice? GetRadioDevice(RadioDeviceType deviceType);
    ConnectionParameters? ConnectionParameters { get; }

    // Client status management
    ClientStatusFlags GetClientStatus();
    void SetClientStatus(ClientStatusFlags flags);
    void AddClientStatus(ClientStatusFlags flags);
    void RemoveClientStatus(ClientStatusFlags flags);

    // State change events
    event EventHandler<ServiceStateChangedEventArgs>? StateChanged;

    // Radio change events (fired only on subsequent changes)
    event EventHandler<RadioFrequencyChangedEventArgs>? FrequencyChanged;
    event EventHandler<RadioVolumeChangedEventArgs>? VolumeChanged;
    event EventHandler<RadioPttChangedEventArgs>? PttChanged;
    event EventHandler<RadioPowerChangedEventArgs>? PowerChanged;

    // Connection parameter changes
    event EventHandler<ConnectionParametersChangedEventArgs>? ConnectionParametersChanged;

    // Logbook name change (mPlayerMap[0].LogBookName — populated once in-game)
    event EventHandler<LogbookNameChangedEventArgs>? LogbookNameChanged;

    // Constant
    public const int BmsRadioOffFrequency = 9999;
}
