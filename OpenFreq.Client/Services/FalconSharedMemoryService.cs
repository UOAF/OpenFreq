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

    private ServiceState _state = ServiceState.Stopped;
    private double _pollingFrequencyHz = 2.0;

    private PeriodicTimer? _timer;
    private Task? _pollingTask;
    private CancellationTokenSource? _cts;

    private IntPtr _hPrimaryMemory = IntPtr.Zero;
    private IntPtr _lpPrimaryBaseAddress = IntPtr.Zero;
    private IntPtr _hStringMemory = IntPtr.Zero;
    private IntPtr _lpStringBaseAddress = IntPtr.Zero;

    private FlightPosition? _position;
    private FlightVelocity? _velocity;
    private string? _theaterTerrainDir;
    private readonly object _dataLock = new();
    private bool _disposed;
    private bool _wasFlying;
    private bool _isFlying;
    private readonly ILogger<FalconSharedMemoryService> _logger = logger;

    public event EventHandler<ServiceStateChangedEventArgs>? StateChanged;
    public event EventHandler<FlyingStateChangedEventArgs>? FlyingStateChanged;

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
                return _isFlying;
        }
    }

    public string? TheaterTerrainDir
    {
        get
        {
            lock (_dataLock)
                return _theaterTerrainDir;
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

    public void Start()
    {
        lock (_dataLock)
        {
            if (_state != ServiceState.Stopped)
                throw new InvalidOperationException($"Service is already running (state: {_state})");

            ChangeState(ServiceState.Disconnected);
        }

        _cts = new CancellationTokenSource();
        var interval = TimeSpan.FromSeconds(1.0 / _pollingFrequencyHz);
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
            ChangeState(ServiceState.Stopped);
        }

        _logger.LogInformation("Stopped");
    }

    private async Task PollingLoop(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested && _timer != null)
        {
            try
            {
                await _timer.WaitForNextTickAsync(cancellationToken);

                var currentState = State;

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
            _bmsProcess = FindBmsProcess();
            if (_bmsProcess == null)
            {
                // The SHMEM is still valid but it seems BMS has crashed - just continue polling until the user restarts it
                return false;
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
            bool isFlying = (hsiBits & HSI_FLYING_BIT) != 0;

            if (isFlying != _wasFlying)
            {
                FlyingStateChanged?.Invoke(this, new FlyingStateChangedEventArgs(_wasFlying, isFlying));
            }

            _wasFlying = isFlying;

            // Update position & velocity
            lock (_dataLock)
            {
                // For some reason BMS switches x & y in shmem, correct this
                _position = new FlightPosition((int)y, (int)x, (int)z);
                _velocity = new FlightVelocity(yDot, xDot, zDot);
                _isFlying = isFlying;
            }

            return true;
        }
        catch
        {
            return false;
        }
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
    private FlightVelocity? _velocity;
    public string? TheaterTerrainDir { get; }
    public bool? IsFlying { get; }
    public double PollingFrequencyHz { get; set; }
    public event EventHandler<ServiceStateChangedEventArgs>? StateChanged;
    public event EventHandler<FlyingStateChangedEventArgs>? FlyingStateChanged;

    public void Start()
    {
    }

    public void Stop()
    {
    }
}


#endif