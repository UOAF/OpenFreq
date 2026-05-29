// ReSharper disable RedundantUsingDirective

using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using FalconBmsDataService.Models;
using FalconRadioService.Models;
using FalconRadioService.Parsers;
using FalconRadioService.Services;
using Microsoft.Extensions.Logging;
using OpenFreq.Client.NativeMethods;

namespace OpenFreq.Client.Services;

#if WINDOWS

public class FalconRadioSharedMemoryService : IFalconRadioSharedMemoryService
{
    private ServiceState _state = ServiceState.Stopped;
    private double _pollingFrequencyHz = 10.0;

    private PeriodicTimer? _rccTimer; // 3 Hz for RCC (radio data)
    private PeriodicTimer? _rcsTimer; // 1 Hz for RCS status updates
    private Task? _rccPollingTask;
    private Task? _rcsPollingTask;
    private CancellationTokenSource? _cts;

    // RCS (Status) - we CREATE this
    private IntPtr _hRcsMemory = IntPtr.Zero;
    private IntPtr _lpRcsBaseAddress = IntPtr.Zero;

    // RCC (Control) - we READ this
    private IntPtr _hRccMemory = IntPtr.Zero;
    private IntPtr _lpRccBaseAddress = IntPtr.Zero;

    // Mutex for single instance
    private IntPtr _hMutex = IntPtr.Zero;

    // Current state data
    private string? _logbookName;
    private readonly Dictionary<RadioType, RadioChannel> _radioChannels = new();
    private readonly Dictionary<RadioDeviceType, RadioDevice> _radioDevices = new();
    private ConnectionParameters? _connectionParameters;

    private ConnectionParameters? _previousConnectionParameters;
    private bool _initialReadDone = false;

    private readonly object _dataLock = new();
    private bool _disposed;
    private readonly ILogger<FalconRadioSharedMemoryService> _logger;

    // Events
    public event EventHandler<ServiceStateChangedEventArgs>? StateChanged;
    public event EventHandler<RadioFrequencyChangedEventArgs>? FrequencyChanged;
    public event EventHandler<RadioVolumeChangedEventArgs>? VolumeChanged;
    public event EventHandler<RadioPttChangedEventArgs>? PttChanged;
    public event EventHandler<RadioPowerChangedEventArgs>? PowerChanged;
    public event EventHandler<ConnectionParametersChangedEventArgs>? ConnectionParametersChanged;
    public event EventHandler<LogbookNameChangedEventArgs>? LogbookNameChanged;

    public FalconRadioSharedMemoryService(ILogger<FalconRadioSharedMemoryService> logger)
    {
        _logger = logger;
    }

    public ServiceState State
    {
        get
        {
            lock (_dataLock) return _state;
        }
    }

    public double PollingFrequencyHz
    {
        get => _pollingFrequencyHz;
        set
        {
            if (value <= 0)
                throw new ArgumentException("Polling frequency must be positive", nameof(value));
            _pollingFrequencyHz = value;
        }
    }

    public bool IsOwner => _hMutex != IntPtr.Zero;

    public string? LogbookName
    {
        get
        {
            lock (_dataLock) return _logbookName;
        }
    }

    public RadioChannel? GetRadioChannel(RadioType radioType)
    {
        lock (_dataLock)
        {
            return _radioChannels.TryGetValue(radioType, out var channel)
                ? channel.Clone()
                : null;
        }
    }

    public RadioDevice? GetRadioDevice(RadioDeviceType deviceType)
    {
        lock (_dataLock)
        {
            return _radioDevices.TryGetValue(deviceType, out var device)
                ? device.Clone()
                : null;
        }
    }

    public ConnectionParameters? ConnectionParameters
    {
        get
        {
            lock (_dataLock) return _connectionParameters?.Clone();
        }
    }

    public void SetClientStatus(ClientStatusFlags flags)
    {
        if (_lpRcsBaseAddress == IntPtr.Zero)
        {
            _logger.LogDebug("Skipping SetClientStatus - not RCS owner");
            return;
        }

        try
        {
            Marshal.WriteInt32(_lpRcsBaseAddress, (int)flags);
        }
        catch
        {
        }
    }

    public ClientStatusFlags GetClientStatus()
    {
        if (_lpRcsBaseAddress == IntPtr.Zero)
            return ClientStatusFlags.AllClear;

        try
        {
            int flags = Marshal.ReadInt32(_lpRcsBaseAddress);
            return (ClientStatusFlags)flags;
        }
        catch
        {
            return ClientStatusFlags.AllClear;
        }
    }

    public void AddClientStatus(ClientStatusFlags flags)
    {
        var current = GetClientStatus();
        var next = current | flags;
        SetClientStatus(next);
        if (next != current)
            _logger.LogInformation("RCS status: {Before} → {After}", current, next);
    }

    public void RemoveClientStatus(ClientStatusFlags flags)
    {
        var current = GetClientStatus();
        var next = current & ~flags;
        SetClientStatus(next);
        if (next != current)
            _logger.LogInformation("RCS status: {Before} → {After}", current, next);
    }

    public void Start()
    {
        lock (_dataLock)
        {
            if (_state != ServiceState.Stopped)
                throw new InvalidOperationException($"Service already running (state: {_state})");

            // Create mutex for single instance
            _hMutex = Win32RadioMemory.CreateMutex(
                IntPtr.Zero,
                true,
                Win32RadioMemory.RADIO_CLIENT_SEMAPHORE);

            // Check if we actually got ownership (i.e., we're the first instance)
            int error = Marshal.GetLastWin32Error();
            const int ERROR_ALREADY_EXISTS = 183;

            if (error == ERROR_ALREADY_EXISTS)
            {
                // Another radio client is already running
                _logger.LogWarning("Another radio client is already active. This instance will run in READ-ONLY mode.");

                // Clean up the mutex handle we got
                if (_hMutex != IntPtr.Zero)
                {
                    Win32RadioMemory.CloseHandle(_hMutex);
                    _hMutex = IntPtr.Zero;
                }

                // Skip RCS creation - we won't write status
                ChangeState(ServiceState.RcsCreated);
            }
            else
            {
                _logger.LogInformation("Mutex acquired — RCS owner");
                // We're the first/only instance - create RCS normally
                if (!CreateRcsSharedMemory())
                {
                    CleanupResources();
                    throw new InvalidOperationException("Failed to create RCS shared memory");
                }

                ChangeState(ServiceState.RcsCreated);
            }
        }

        _cts = new CancellationTokenSource();

        var rccInterval = TimeSpan.FromSeconds(1.0 / _pollingFrequencyHz);
        _rccTimer = new PeriodicTimer(rccInterval);
        _rccPollingTask = Task.Run(() => RccPollingLoop(_cts.Token));

        var rcsInterval = TimeSpan.FromSeconds(1.0);
        _rcsTimer = new PeriodicTimer(rcsInterval);
        _rcsPollingTask = Task.Run(() => RcsUpdateLoop(_cts.Token));

        _logger.LogInformation("Started");
    }

    public void Stop()
    {
        _cts?.Cancel();
        _rccTimer?.Dispose();
        _rcsTimer?.Dispose();

        try
        {
            _rccPollingTask?.Wait(TimeSpan.FromSeconds(5));
            _rcsPollingTask?.Wait(TimeSpan.FromSeconds(5));
        }
        catch
        {
            // dont care
        }

        CleanupResources();

        lock (_dataLock)
        {
            ChangeState(ServiceState.Stopped);
        }

        _logger.LogInformation("Stopped");
    }

    private bool CreateRcsSharedMemory()
    {
        try
        {
            _logger.LogDebug("Creating RCS shared memory...");

            _hRcsMemory = Win32RadioMemory.CreateFileMapping(
                Win32RadioMemory.INVALID_HANDLE_VALUE,
                IntPtr.Zero,
                Win32RadioMemory.PAGE_READWRITE,
                0,
                Win32RadioMemory.RCS_SIZE,
                Win32RadioMemory.FALCON_RCS_SHARED_MEMORY);

            if (_hRcsMemory == IntPtr.Zero)
            {
                var error = Marshal.GetLastWin32Error();
                _logger.LogError("CreateFileMapping failed. Error: {Error}", error);
                return false;
            }

            _lpRcsBaseAddress = Win32RadioMemory.MapViewOfFile(
                _hRcsMemory,
                Win32RadioMemory.FILE_MAP_ALL_ACCESS,
                0, 0,
                IntPtr.Zero);

            if (_lpRcsBaseAddress == IntPtr.Zero)
            {
                var error = Marshal.GetLastWin32Error();
                _logger.LogError("MapViewOfFile failed. Error: {Error}", error);
                Win32RadioMemory.CloseHandle(_hRcsMemory);
                _hRcsMemory = IntPtr.Zero;
                return false;
            }

            // Initialize RCS: clear all flags
            SetClientStatus(ClientStatusFlags.AllClear);
            _logger.LogInformation("RCS shared memory created, flags cleared");

            // Set clientactive flag
            AddClientStatus(ClientStatusFlags.ClientActive);
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Exception in CreateRcsSharedMemory");
            return false;
        }
    }

    private async Task RccPollingLoop(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await _rccTimer!.WaitForNextTickAsync(ct);

                // If not yet connected to RCC shared memory, try to open it
                if (_lpRccBaseAddress == IntPtr.Zero)
                {
                    if (!TryOpenRccSharedMemory())
                        continue;

                    // Read initial data BEFORE changing state to Connected
                    // This ensures data is available when StateChanged event fires
                    _logger.LogDebug("RCC opened, reading initial data...");
                    if (!TryReadRadioData())
                    {
                        _logger.LogError("Failed to read initial RCC data, closing and retrying");
                        CloseRccSharedMemory();
                        continue;
                    }

                    _logger.LogInformation("RCC shared memory opened, initial data read");

                    // Now that we have data, change state to Connected
                    lock (_dataLock)
                    {
                        ChangeState(ServiceState.Connected);
                    }

                    // Continue to next iteration to start regular polling
                    continue;
                }

                // Regular polling: read RCC data
                if (!TryReadRadioData())
                {
                    _logger.LogInformation("RCC shared memory read failed — BMS likely closed; will retry");

                    // If read fails, RCC might have been closed by BMS
                    // Close our handle and try to reopen on next iteration
                    CloseRccSharedMemory();

                    lock (_dataLock)
                    {
                        ChangeState(ServiceState.RcsCreated);
                    }
                }
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "RCC polling error");
            }
        }
    }

    private async Task RcsUpdateLoop(CancellationToken cancellationToken)
    {
        // This loop can be used for periodic RCS updates if needed
        // For now, it's a placeholder for future status management
        while (!cancellationToken.IsCancellationRequested && _rcsTimer != null)
        {
            try
            {
                await _rcsTimer.WaitForNextTickAsync(cancellationToken);

                // Could add periodic status checks here
                // e.g., verify clientactive flag is still set
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch
            {
                // dont care
            }
        }
    }

    private bool IsFalconBmsRunning()
    {
        IntPtr hMutex = Win32RadioMemory.OpenMutex(
            Win32RadioMemory.SYNCHRONIZE,
            false,
            Win32RadioMemory.FALCON_SEMAPHORE);

        if (hMutex != IntPtr.Zero)
        {
            Win32RadioMemory.CloseHandle(hMutex);
            return true;
        }

        return false;
    }

    private bool TryOpenRccSharedMemory()
    {
        if (_lpRccBaseAddress != IntPtr.Zero)
            return true; // Already open

        try
        {
            _hRccMemory = Win32RadioMemory.OpenFileMapping(
                Win32RadioMemory.FILE_MAP_READ,
                true,
                Win32RadioMemory.FALCON_RCC_SHARED_MEMORY);

            if (_hRccMemory == IntPtr.Zero)
                return false;

            _lpRccBaseAddress = Win32RadioMemory.MapViewOfFile(
                _hRccMemory,
                Win32RadioMemory.FILE_MAP_READ,
                0, 0,
                IntPtr.Zero);

            return _lpRccBaseAddress != IntPtr.Zero;
        }
        catch
        {
            CloseRccSharedMemory();
            return false;
        }
    }

    private void CloseRccSharedMemory()
    {
        if (_lpRccBaseAddress != IntPtr.Zero)
        {
            Win32RadioMemory.UnmapViewOfFile(_lpRccBaseAddress);
            _lpRccBaseAddress = IntPtr.Zero;
        }

        if (_hRccMemory != IntPtr.Zero)
        {
            Win32RadioMemory.CloseHandle(_hRccMemory);
            _hRccMemory = IntPtr.Zero;
        }

        // Reset read state so next RCC open is treated as a fresh session.
        // Without this, if BMS restarts with the same AttemptingToConnect=true,
        // DetectConnectionParameterChanges sees no change and fires no event.
        lock (_dataLock)
        {
            _initialReadDone = false;
            _connectionParameters = null;
            _logbookName = null;
        }
    }

    private bool TryReadRadioData()
    {
        if (_lpRccBaseAddress == IntPtr.Zero)
            return false;

        try
        {
            // Read logbook name
            var logbookName = RadioControlParser.ParseLogbookName(_lpRccBaseAddress);

            // Read connection parameters
            var connParams = RadioControlParser.ParseConnectionParameters(_lpRccBaseAddress);

            // Read all radio channels
            var channels = new Dictionary<RadioType, RadioChannel>();
            foreach (RadioType radioType in Enum.GetValues<RadioType>())
            {
                channels[radioType] = RadioControlParser.ParseRadioChannel(_lpRccBaseAddress, radioType);
            }

            // Read radio devices
            var devices = new Dictionary<RadioDeviceType, RadioDevice>();
            foreach (RadioDeviceType deviceType in Enum.GetValues<RadioDeviceType>())
            {
                devices[deviceType] = RadioControlParser.ParseRadioDevice(_lpRccBaseAddress, deviceType);
            }

            // Update state and detect changes
            lock (_dataLock)
            {
                var previousLogbookName = _logbookName;
                _logbookName = logbookName;

                // "Wot Pilot?!" is the sentinel BMS writes in Telemetry::Init() before
                // ClientReady(); the real callsign arrives later via UpdatePlayerMap().
                // Don't fire the event for the placeholder — callers should use Nickname
                // from ConnectionParameters for the display name at connection time.
                const string BmsSentinel = "Wot Pilot?!";
                if (_initialReadDone &&
                    !string.IsNullOrEmpty(logbookName) &&
                    logbookName != BmsSentinel &&
                    logbookName != previousLogbookName)
                {
                    Task.Run(() => LogbookNameChanged?.Invoke(this,
                        new LogbookNameChangedEventArgs(previousLogbookName, logbookName)));
                }

                // Update channels and detect changes
                foreach (var kvp in channels)
                {
                    var radioType = kvp.Key;
                    var newChannel = kvp.Value;

                    if (_radioChannels.TryGetValue(radioType, out var oldChannel) && _initialReadDone)
                    {
                        DetectRadioChanges(oldChannel, newChannel);
                    }

                    _radioChannels[radioType] = newChannel;
                }

                // Update devices
                foreach (var kvp in devices)
                {
                    _radioDevices[kvp.Key] = kvp.Value;
                }

                // Detect connection parameter changes
                if (_connectionParameters != null && _initialReadDone)
                {
                    DetectConnectionParameterChanges(_connectionParameters, connParams);
                }
                else if (!_initialReadDone && connParams.AttemptingToConnect &&
                         !string.IsNullOrEmpty(connParams.Address))
                {
                    // Initial read with active connection request - fire event!
                    _logger.LogDebug("Initial read with active connection request - firing event");
                    var dummyOldParams = new ConnectionParameters(); // Empty old params

                    // Fire the event outside the lock
                    Task.Run(() =>
                    {
                        ConnectionParametersChanged?.Invoke(this,
                            new ConnectionParametersChangedEventArgs(dummyOldParams, connParams.Clone()));
                    });
                }
                else
                {
                    _logger.LogDebug("Skipping change detection: ConnectionParameters={HasConnectionParameters}, InitialReadDone={InitialReadDone}",
                        _connectionParameters != null, _initialReadDone);
                }

                _connectionParameters = connParams;
                _previousConnectionParameters = connParams.Clone();

                if (!_initialReadDone)
                {
                    _initialReadDone = true;
                    _logger.LogDebug("Initial read completed");
                }
            }

            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "TryReadRadioData Error: {ex.message}", ex.Message);
            return false;
        }
    }

    private void DetectRadioChanges(RadioChannel oldChannel, RadioChannel newChannel)
    {
        var radioType = oldChannel.RadioType;

        if (oldChannel.Frequency != newChannel.Frequency)
        {
            FrequencyChanged?.Invoke(this, new RadioFrequencyChangedEventArgs(
                radioType, oldChannel.Frequency, newChannel.Frequency));
        }

        if (oldChannel.RxVolume != newChannel.RxVolume)
        {
            VolumeChanged?.Invoke(this, new RadioVolumeChangedEventArgs(
                radioType, oldChannel.RxVolume, newChannel.RxVolume));
        }

        if (oldChannel.PttDepressed != newChannel.PttDepressed)
        {
            PttChanged?.Invoke(this, new RadioPttChangedEventArgs(
                radioType, oldChannel.PttDepressed, newChannel.PttDepressed));
        }

        if (oldChannel.IsOn != newChannel.IsOn)
        {
            PowerChanged?.Invoke(this, new RadioPowerChangedEventArgs(
                radioType, oldChannel.IsOn, newChannel.IsOn));
        }
    }

    private void DetectConnectionParameterChanges(ConnectionParameters oldParams, ConnectionParameters newParams)
    {
        // Check if any significant parameters changed
        if (oldParams.Address != newParams.Address ||
            oldParams.Port != newParams.Port ||
            oldParams.Password != newParams.Password ||
            oldParams.Nickname != newParams.Nickname ||
            oldParams.ReadyToTransmit != newParams.ReadyToTransmit ||
            oldParams.AttemptingToConnect != newParams.AttemptingToConnect ||
            oldParams.TerminateClient != newParams.TerminateClient)
        {
            // Fire async via Task.Run so the polling thread is not blocked while holding
            // _dataLock. The handler calls ConnectWithTimeoutAsync(...).Wait() which takes
            // up to 2.5 s — keeping that inside the lock would starve GetRadioChannel
            // callers (e.g. ImportBmsRadioChannels on the authenticated callback).
            var args = new ConnectionParametersChangedEventArgs(oldParams, newParams);
            Task.Run(() => ConnectionParametersChanged?.Invoke(this, args));
        }
    }

    private void CleanupResources()
    {
        // Clear clientactive before closing
        if (_lpRcsBaseAddress != IntPtr.Zero)
        {
            RemoveClientStatus(ClientStatusFlags.ClientActive);
        }

        CloseRccSharedMemory();

        if (_lpRcsBaseAddress != IntPtr.Zero)
        {
            Win32RadioMemory.UnmapViewOfFile(_lpRcsBaseAddress);
            _lpRcsBaseAddress = IntPtr.Zero;
        }

        if (_hRcsMemory != IntPtr.Zero)
        {
            Win32RadioMemory.CloseHandle(_hRcsMemory);
            _hRcsMemory = IntPtr.Zero;
        }

        if (_hMutex != IntPtr.Zero)
        {
            Win32RadioMemory.CloseHandle(_hMutex);
            _hMutex = IntPtr.Zero;
        }
    }

    private void ChangeState(ServiceState newState)
    {
        var oldState = _state;
        if (oldState == newState)
            return;

        _state = newState;
        _logger.LogInformation("State: {OldState} → {NewState}", oldState, newState);
        StateChanged?.Invoke(this, new ServiceStateChangedEventArgs(oldState, newState));
    }

    public void Dispose()
    {
        if (_disposed)
            return;

        Stop();
        _cts?.Dispose();
        _rccTimer?.Dispose();
        _rcsTimer?.Dispose();
        _disposed = true;
        GC.SuppressFinalize(this);
    }
}

#else
[SuppressMessage("ReSharper", "UnassignedGetOnlyAutoProperty")]
[SuppressMessage("ReSharper", "ReturnTypeCanBeNotNullable")]

// Stub implementation for non-Windows platforms
public class FalconRadioSharedMemoryService : IFalconRadioSharedMemoryService
{
    public ServiceState State { get; }
    public double PollingFrequencyHz { get; set; }
    public bool IsOwner => false;
    public string? LogbookName { get; }
    public RadioChannel? GetRadioChannel(RadioType radioType)
    {
        throw new NotImplementedException();
    }

    public RadioDevice? GetRadioDevice(RadioDeviceType deviceType)
    {
        throw new NotImplementedException();
    }

    public ConnectionParameters? ConnectionParameters { get; }
    public ClientStatusFlags GetClientStatus()
    {
        throw new NotImplementedException();
    }

    public void SetClientStatus(ClientStatusFlags flags)
    {
        throw new NotImplementedException();
    }

    public void AddClientStatus(ClientStatusFlags flags)
    {
        throw new NotImplementedException();
    }

    public void RemoveClientStatus(ClientStatusFlags flags)
    {
        throw new NotImplementedException();
    }

#pragma warning disable CS0067
    public event EventHandler<ServiceStateChangedEventArgs>? StateChanged;
    public event EventHandler<RadioFrequencyChangedEventArgs>? FrequencyChanged;
    public event EventHandler<RadioVolumeChangedEventArgs>? VolumeChanged;
    public event EventHandler<RadioPttChangedEventArgs>? PttChanged;
    public event EventHandler<RadioPowerChangedEventArgs>? PowerChanged;
    public event EventHandler<ConnectionParametersChangedEventArgs>? ConnectionParametersChanged;
    public event EventHandler<LogbookNameChangedEventArgs>? LogbookNameChanged;
#pragma warning restore CS0067

    public void Start()
    {
    }

    public void Stop()
    {
    }

    public void Dispose()
    {
    }
}
#endif
