// ReSharper disable RedundantUsingDirective

using System;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using FalconBmsDataService.Models;
using FalconBmsDataService.Services;
using FalconRadioService.Models;
using Microsoft.Extensions.Logging;
using OpenFreq.Client.NativeMethods;

namespace OpenFreq.Client.Services;

/// <summary>
/// Service for reading Falcon BMS shared memory data
/// </summary>

#if WINDOWS
public class FalconSharedMemoryService(ILogger<FalconSharedMemoryService> logger) : IFalconSharedMemoryService
{
    private System.Diagnostics.Process? _bmsProcess;

    // Shared memory area names
    private const string PRIMARY_SHARED_MEMORY = "FalconSharedMemoryArea";
    private const string STRING_SHARED_MEMORY = "FalconSharedMemoryAreaString";

    // HSI Flying bit flag
    private const uint HSI_FLYING_BIT = 0x80000000;

    // Offsets in primary shared memory structure (BMS4FlightData)
    private const int OFFSET_X = 0; // float at byte 0
    private const int OFFSET_Y = 4; // float at byte 4
    private const int OFFSET_Z = 8; // float at byte 8

    private const int OFFSET_X_DOT = 12; // float at byte 0
    private const int OFFSET_Y_DOT = 16; // float at byte 4
    private const int OFFSET_Z_DOT = 20; // float at byte 8

    private const int OFFSET_HSIBITS = 232; // hsiBits (uint) at byte 232

    // FlightData2 area (FlightData.h, default alignment). These offsets are the same from BMS 4.36 to 4.38.
    private const string FLIGHTDATA2_SHARED_MEMORY = "FalconSharedMemoryArea2";
    private const int OFFSET2_CURRENT_TIME = 68; // currentTime (int), seconds since in-game midnight
    private const int OFFSET2_VERSION_NUM = 76; // VersionNum (int)
    private const int CURRENT_TIME_MIN_VERSION = 3; // FlightData2 version that added currentTime

    // Consecutive flight data reads with the flying bit clear before we accept "not flying"
    private const int NotFlyingDebounceSamples = 3;

    // The loop ticks at 10 Hz so PTT log lines get a fresh game clock. Everything else still runs every
    // FlightDataTickDivider ticks (2 Hz): NotFlyingDebounceSamples counts those reads, and each connect
    // attempt scans the process list.
    private const double PollingFrequencyHz = 10.0;
    private const int FlightDataTickDivider = 5;

    private ServiceState _state = ServiceState.Stopped;

    private PeriodicTimer? _timer;
    private Task? _pollingTask;
    private CancellationTokenSource? _cts;

    private IntPtr _hPrimaryMemory = IntPtr.Zero;
    private IntPtr _lpPrimaryBaseAddress = IntPtr.Zero;
    private IntPtr _hStringMemory = IntPtr.Zero;
    private IntPtr _lpStringBaseAddress = IntPtr.Zero;
    private IntPtr _hFlightData2Memory = IntPtr.Zero;
    private IntPtr _lpFlightData2BaseAddress = IntPtr.Zero;

    // In-game time of day in seconds, or -1 when unknown. Not guarded by _dataLock: OpenFreqService reads it
    // while holding its signalling lock, and ChangeState raises StateChanged while holding _dataLock.
    private int _gameTimeSeconds = -1;

    private FlightPosition? _position;
    private FlightVelocity? _velocity;
    private string? _theaterTerrainDir;
    private string? _acName;
    private string? _acNctr;
    private readonly object _dataLock = new();
    private bool _disposed;
    // How many samples in a row have we not been flying (in 3D)? Used to debounce
    private int _notFlyingSamples = NotFlyingDebounceSamples;
    private readonly ILogger<FalconSharedMemoryService> _logger = logger;

    public event EventHandler<ServiceStateChangedEventArgs>? StateChanged;
    public event EventHandler<FlyingStateChangedEventArgs>? FlyingStateChanged;
    public event EventHandler<AircraftInfoChangedEventArgs>? AircraftInfoChanged;

    public ServiceState State
    {
        get
        {
            lock (_dataLock)
                return _state;
        }
    }

    public FlightPosition? Position
    {
        get
        {
            lock (_dataLock)
                return _position;
        }
    }

    public FlightVelocity? Velocity
    {
        get
        {
            lock (_dataLock)
                return _velocity;
        }
    }

    /// <summary>
    /// Indicates if the player is currently flying (from HSI Flying bit)
    /// </summary>
    public bool? IsFlying
    {
        get
        {
            if (_state != ServiceState.Connected) return null;
            lock (_dataLock)
                return _notFlyingSamples < NotFlyingDebounceSamples;
        }
    }

    public int? GameTimeSeconds => Volatile.Read(ref _gameTimeSeconds) is >= 0 and var seconds ? seconds : null;

    public string? TheaterTerrainDir
    {
        get
        {
            lock (_dataLock)
                return _theaterTerrainDir;
        }
    }

    public string? AcNCTR
    {
        get
        {
            lock (_dataLock)
                return _acNctr;
        }
    }

    public void Start()
    {
        lock (_dataLock)
        {
            if (_state != ServiceState.Stopped)
                throw new InvalidOperationException($"Service is already running (state: {_state})");

            ChangeState(ServiceState.Disconnected);
        }

        _cts = new CancellationTokenSource();
        var interval = TimeSpan.FromSeconds(1.0 / PollingFrequencyHz);
        _timer = new PeriodicTimer(interval);
        _pollingTask = Task.Run(() => PollingLoop(_cts.Token));
        _logger.LogInformation("Started");
    }

    public void Stop()
    {
        _cts?.Cancel();
        _timer?.Dispose();
        _pollingTask?.Wait(TimeSpan.FromSeconds(5));

        DisconnectFromSharedMemory();

        lock (_dataLock)
        {
            // Reset so the next Start() re-signals a false -> true transition
            _notFlyingSamples = NotFlyingDebounceSamples;
            ChangeState(ServiceState.Stopped);
        }

        _logger.LogInformation("Stopped");
    }

    private async Task PollingLoop(CancellationToken cancellationToken)
    {
        long tick = 0;
        while (!cancellationToken.IsCancellationRequested && _timer != null)
        {
            try
            {
                await _timer.WaitForNextTickAsync(cancellationToken);

                var currentState = State;

                if (currentState == ServiceState.Connected)
                    ReadGameTime();

                if (tick++ % FlightDataTickDivider != 0)
                    continue;

                if (currentState == ServiceState.Disconnected)
                {
                    // Try to connect
                    if (TryConnectToSharedMemory())
                    {
                        lock (_dataLock)
                        {
                            ChangeState(ServiceState.Connected);
                        }
                    }
                }
                else if (currentState == ServiceState.Connected)
                {
                    // Check if BMS process has exited
                    if (!IsBmsProcessRunning())
                    {
                        _logger.LogInformation("BMS process has exited");
                        DisconnectFromSharedMemory();
                        lock (_dataLock)
                        {
                            _position = null;
                            ChangeState(ServiceState.Disconnected);
                        }
                        continue;
                    }

                    // Read data
                    if (!TryReadFlightData())
                    {
                        // Connection lost
                        DisconnectFromSharedMemory();
                        lock (_dataLock)
                        {
                            _position = null;
                            ChangeState(ServiceState.Disconnected);
                        }
                    }
                }
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error in polling loop");
            }
        }
    }

    private bool TryConnectToSharedMemory()
    {
        try
        {
            // Open primary shared memory
            _hPrimaryMemory = Win32SharedMemory.OpenFileMapping(
                Win32SharedMemory.SECTION_MAP_READ,
                false,
                PRIMARY_SHARED_MEMORY);

            if (_hPrimaryMemory == IntPtr.Zero)
                return false;

            _lpPrimaryBaseAddress = Win32SharedMemory.MapViewOfFile(
                _hPrimaryMemory,
                Win32SharedMemory.SECTION_MAP_READ,
                0, 0,
                IntPtr.Zero);

            if (_lpPrimaryBaseAddress == IntPtr.Zero)
            {
                Win32SharedMemory.CloseHandle(_hPrimaryMemory);
                _hPrimaryMemory = IntPtr.Zero;
                return false;
            }

            // Find and attach to BMS process
            // This is optional - if we can't find it, we'll rely on read failures to detect disconnection
            // Reason: we want to be able to run in WINE
            _bmsProcess = FindBmsProcess();
            if (_bmsProcess == null)
            {
                _logger.LogWarning("Could not find BMS process by name - will rely on shared memory validity for connection monitoring");
            }


            // Open string shared memory
            _hStringMemory = Win32SharedMemory.OpenFileMapping(
                Win32SharedMemory.SECTION_MAP_READ,
                false,
                STRING_SHARED_MEMORY);

            if (_hStringMemory != IntPtr.Zero)
            {
                _lpStringBaseAddress = Win32SharedMemory.MapViewOfFile(
                    _hStringMemory,
                    Win32SharedMemory.SECTION_MAP_READ,
                    0, 0,
                    IntPtr.Zero);

                // Read theater terrain dir once (only on connect)
                if (_lpStringBaseAddress != IntPtr.Zero)
                {
                    var terrainDir = StringDataParser.ParseTheaterTerrainDir(_lpStringBaseAddress);

                    // This happens when BMS is not done loading yet
                    if (String.IsNullOrEmpty(terrainDir)) return false;

                    lock (_dataLock)
                    {
                        _theaterTerrainDir = terrainDir;
                    }
                }
            }

            // Optional, like the string area: it only supplies the game clock.
            _hFlightData2Memory = Win32SharedMemory.OpenFileMapping(
                Win32SharedMemory.SECTION_MAP_READ,
                false,
                FLIGHTDATA2_SHARED_MEMORY);

            if (_hFlightData2Memory != IntPtr.Zero)
            {
                _lpFlightData2BaseAddress = Win32SharedMemory.MapViewOfFile(
                    _hFlightData2Memory,
                    Win32SharedMemory.SECTION_MAP_READ,
                    0, 0,
                    IntPtr.Zero);
            }

            return true;
        }
        catch
        {
            DisconnectFromSharedMemory();
            return false;
        }
    }

    private System.Diagnostics.Process? FindBmsProcess()
    {
        try
        {
            var processes = System.Diagnostics.Process.GetProcessesByName("Falcon BMS");
            if (processes.Length > 0)
            {
                var process = processes[0];
                // Dispose the rest
                for (int i = 1; i < processes.Length; i++)
                {
                    processes[i].Dispose();
                }
                return process;
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to find BMS process");
        }

        return null;
    }

    private bool IsBmsProcessRunning()
    {
        if (_bmsProcess == null)
            return true; // Don't know, so assume it's running

        try
        {
            // HasExited throws if process handle is invalid
            return !_bmsProcess.HasExited;
        }
        catch
        {
            return false;
        }
    }

    private void DisconnectFromSharedMemory()
    {
        if (_lpPrimaryBaseAddress != IntPtr.Zero)
        {
            Win32SharedMemory.UnmapViewOfFile(_lpPrimaryBaseAddress);
            _lpPrimaryBaseAddress = IntPtr.Zero;
        }

        if (_hPrimaryMemory != IntPtr.Zero)
        {
            Win32SharedMemory.CloseHandle(_hPrimaryMemory);
            _hPrimaryMemory = IntPtr.Zero;
        }

        if (_lpStringBaseAddress != IntPtr.Zero)
        {
            Win32SharedMemory.UnmapViewOfFile(_lpStringBaseAddress);
            _lpStringBaseAddress = IntPtr.Zero;
        }

        if (_hStringMemory != IntPtr.Zero)
        {
            Win32SharedMemory.CloseHandle(_hStringMemory);
            _hStringMemory = IntPtr.Zero;
        }

        if (_lpFlightData2BaseAddress != IntPtr.Zero)
        {
            Win32SharedMemory.UnmapViewOfFile(_lpFlightData2BaseAddress);
            _lpFlightData2BaseAddress = IntPtr.Zero;
        }

        if (_hFlightData2Memory != IntPtr.Zero)
        {
            Win32SharedMemory.CloseHandle(_hFlightData2Memory);
            _hFlightData2Memory = IntPtr.Zero;
        }

        Volatile.Write(ref _gameTimeSeconds, -1);

        if (_bmsProcess != null)
        {
            _bmsProcess.Dispose();
            _bmsProcess = null;
        }
    }

    private bool TryReadFlightData()
    {
        if (_lpPrimaryBaseAddress == IntPtr.Zero)
            return false;

        try
        {
            // Read x, y, z (floats at offsets 0, 4, 8)
            float x = BitConverter.ToSingle(ReadBytes(_lpPrimaryBaseAddress, OFFSET_X, 4), 0);
            float y = BitConverter.ToSingle(ReadBytes(_lpPrimaryBaseAddress, OFFSET_Y, 4), 0);

            // For some reason, the BMS altitude is inverted
            float z = BitConverter.ToSingle(ReadBytes(_lpPrimaryBaseAddress, OFFSET_Z, 4), 0) * -1;

            // Read the velocity
            float xDot = BitConverter.ToSingle(ReadBytes(_lpPrimaryBaseAddress, OFFSET_X_DOT, 4), 0);
            float yDot = BitConverter.ToSingle(ReadBytes(_lpPrimaryBaseAddress, OFFSET_Y_DOT, 4), 0);

            // Inverted
            float zDot = BitConverter.ToSingle(ReadBytes(_lpPrimaryBaseAddress, OFFSET_Z_DOT, 4), 0) * -1;

            // Read hsiBits
            uint hsiBits = BitConverter.ToUInt32(ReadBytes(_lpPrimaryBaseAddress, OFFSET_HSIBITS, 4), 0);
            bool rawIsFlying = (hsiBits & HSI_FLYING_BIT) != 0;

            bool isFlying, wasFlying;
            // Lock not strictly needed since reads and writes of int are atomic in C#,
            // nor do we need atomic read-modify-write here since we're the only writer,
            // but to match the style of everything else:
            lock (_dataLock)
            {
                // Debounce the flying -> not-flying state:
                wasFlying = _notFlyingSamples < NotFlyingDebounceSamples;
                _notFlyingSamples = rawIsFlying ? 0 : Math.Min(_notFlyingSamples + 1, NotFlyingDebounceSamples);
                isFlying = _notFlyingSamples < NotFlyingDebounceSamples;
            }

            if (isFlying != wasFlying)
            {
                _logger.LogInformation("Flying state changed: {Old} -> {New}", wasFlying, isFlying);
                try
                {
                    FlyingStateChanged?.Invoke(this, new FlyingStateChangedEventArgs(wasFlying, isFlying));
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "FlyingStateChanged subscriber threw");
                }
            }

            // Poll AcName/AcNCTR — can change while BMS is running
            bool aircraftInfoChanged = false;
            string? newAcName = null;
            string? newAcNctr = null;
            if (_lpStringBaseAddress != IntPtr.Zero)
            {
                var (acName, acNctr) = StringDataParser.ParseAircraftInfo(_lpStringBaseAddress);
                lock (_dataLock)
                {
                    if (acName != _acName || acNctr != _acNctr)
                    {
                        _acName = acName;
                        _acNctr = acNctr;
                        aircraftInfoChanged = true;
                        newAcName = acName;
                        newAcNctr = acNctr;
                    }
                }
            }

            // Update position & velocity before notifying, so a throwing subscriber cannot
            // leave the cached state stale.
            lock (_dataLock)
            {
                // For some reason BMS switches x & y in shmem, correct this
                _position = new FlightPosition((int)y, (int)x, (int)z);
                _velocity = new FlightVelocity(yDot, xDot, zDot);
            }

            if (aircraftInfoChanged)
            {
                try
                {
                    AircraftInfoChanged?.Invoke(this, new AircraftInfoChangedEventArgs(newAcName, newAcNctr));
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "AircraftInfoChanged subscriber threw");
                }
            }

            return true;
        }
        catch (Exception ex)
        {
            // Genuine shared memory read failure - the caller tears down and re-maps.
            _logger.LogWarning(ex, "Failed to read flight data from shared memory");
            return false;
        }
    }

    // FlightData2 is optional, and only has currentTime from version 3 on. BMS updates it only in 3D and clears the
    // Flying bit when the player leaves, so outside 3D currentTime is 0 or frozen at the last flight, and isn't
    // reported. This uses the raw bit: the debounced state holds "flying" for a few reads after BMS leaves 3D.
    private void ReadGameTime()
    {
        var gameTimeSeconds = -1;
        if (_lpPrimaryBaseAddress != IntPtr.Zero && _lpFlightData2BaseAddress != IntPtr.Zero &&
            (BitConverter.ToUInt32(ReadBytes(_lpPrimaryBaseAddress, OFFSET_HSIBITS, 4), 0) & HSI_FLYING_BIT) != 0 &&
            BitConverter.ToInt32(ReadBytes(_lpFlightData2BaseAddress, OFFSET2_VERSION_NUM, 4), 0) >= CURRENT_TIME_MIN_VERSION)
        {
            int currentTime = BitConverter.ToInt32(ReadBytes(_lpFlightData2BaseAddress, OFFSET2_CURRENT_TIME, 4), 0);
            if (currentTime is >= 0 and <= 24 * 60 * 60)
                gameTimeSeconds = currentTime % (24 * 60 * 60);
        }

        Volatile.Write(ref _gameTimeSeconds, gameTimeSeconds);
    }

    private static byte[] ReadBytes(IntPtr baseAddress, int offset, int count)
    {
        byte[] buffer = new byte[count];
        Marshal.Copy(baseAddress + offset, buffer, 0, count);
        return buffer;
    }

    private void ChangeState(ServiceState newState)
    {
        var oldState = _state;
        if (oldState == newState)
            return;

        _state = newState;
        _logger.LogInformation("State: {Old} -> {New}", oldState, newState);
        StateChanged?.Invoke(this, new ServiceStateChangedEventArgs(oldState, newState));
    }

    public void Dispose()
    {
        if (_disposed)
            return;

        Stop();
        _cts?.Dispose();
        _timer?.Dispose();
        _disposed = true;
        GC.SuppressFinalize(this);
    }
}
#else
// Stub for non-Windows platforms
[SuppressMessage("ReSharper", "UnassignedGetOnlyAutoProperty")]
public class FalconSharedMemoryService : IFalconSharedMemoryService
{
    public void Dispose()
    {
    }

    public ServiceState State { get; }
    public FlightPosition? Position { get; }
    public FlightVelocity? Velocity { get; }
    public string? TheaterTerrainDir { get; }
    public string? AcNCTR { get; }
    public bool? IsFlying { get; }
    public int? GameTimeSeconds { get; }
#pragma warning disable CS0067
    public event EventHandler<ServiceStateChangedEventArgs>? StateChanged;
    public event EventHandler<FlyingStateChangedEventArgs>? FlyingStateChanged;
    public event EventHandler<AircraftInfoChangedEventArgs>? AircraftInfoChanged;
#pragma warning restore CS0067

    public void Start()
    {
    }

    public void Stop()
    {
    }
}


#endif
