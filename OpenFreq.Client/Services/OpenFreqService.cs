using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.Messaging;
using FalconBmsDataService.Models;
using FalconBmsDataService.Services;
using FalconRadioService.Models;
using FalconRadioService.Services;
using ManagedBass;
using Microsoft.Extensions.Logging;
using OpenFreq.Client.Models;
using OpenFreq.Common;
using OpenFreq.Services.Acmi;
using OpenFreqAudio;
using OpenFreqClient.Models;
using OpenFreqClient.Services.Interfaces;
using ErrorEventArgs = OpenFreq.Common.ErrorEventArgs;

namespace OpenFreqClient.Services;

/// <summary>
/// Service that integrates OpenFreqClient with BASS audio system and manages communication state
/// </summary>
public class OpenFreqService : IOpenFreqService
{
    public IOpenFreqService.OpenFreqStatus Status { get; set; } = IOpenFreqService.OpenFreqStatus.Disconnected;

    private OpenFreqRtcClient? _client;
    private int _recordHandle;
    private readonly ConcurrentDictionary<int, List<int>> _activeTransmissionsAndMutedFrequencies = new();

    private class TunedFrequencyData(RadioStationData radioStation, bool isEnabled)
    {
        public RadioStationData RadioStation { get; set; } = radioStation;
        public bool IsEnabled { get; set; } = isEnabled;
    }

    private readonly ConcurrentDictionary<int, TunedFrequencyData> _tunedFrequencies = new();


    private RadioPlayback? _playbackService;
    private readonly Lock _streamCreationLock = new();

    private DEMReader? _demReader;
    private FastPathAudioSim? _audioSim;
    private readonly IAcmiClientService _acmiClientService;
    private readonly SignalStrengthTracker _signalStrengthTracker;

    public int RecordingDeviceIndex { get; set; }
    private int _playbackDeviceIndex;

    private readonly Dictionary<string, Dictionary<int, string>>
        _peerStreams = new(); // Holds all peer streams, ordered by peer ID and frequency


    // Cache for audio params: Key is (PeerId, FrequencyKhz)
    private readonly ConcurrentDictionary<(string PeerId, int FrequencyKhz), AudioParamsCacheEntry> _audioParamsCache =
        new();

    // Pre-allocated sidetone conversion buffer — reused every recording callback (single-threaded).
    private float[] _sidetonePushBuffer = new float[4800]; // 100ms @ 48kHz, grows if needed

    // Cache duration
    private readonly TimeSpan _audioParamsCacheDuration = TimeSpan.FromMilliseconds(100);

    // Cache cleanup
    private CancellationTokenSource? _cleanupCts;

    private const float SquelchLevelOff = 0f;
    private const float SquelchLevelOn = 1f;


    public OpenFreqService(IFalconSharedMemoryService falconSharedMemoryService,
        IFalconRadioSharedMemoryService falconRadioSharedMemoryService, ILogger<OpenFreqService> logger,
        ILoggerFactory loggerFactory, IAcmiClientService acmiClientService)
    {
        _falconSharedMemoryService = falconSharedMemoryService;
        _falconRadioSharedMemoryService = falconRadioSharedMemoryService;
        _logger = logger;
        _loggerFactory = loggerFactory;
        _acmiClientService = acmiClientService;

        // Initialize signal strength tracker with callback
        _signalStrengthTracker = new SignalStrengthTracker(
            onSignalStrengthChanged: (frequencyKhz, strengthData) =>
            {
                WeakReferenceMessenger.Default.Send(
                    new SignalStrengthTracker.SignalStrengthUpdateMessage(frequencyKhz, strengthData.StrengthPercent,
                        strengthData.SnrDb));
            },
            updateIntervalMs: 100, // UI update rate
            signalTimeoutMs: 500 // How long until "no signal"
        );
    }

    public int PlaybackDeviceIndex
    {
        get => _playbackDeviceIndex;
        set
        {
            _playbackDeviceIndex = value;
            _playbackService?.ChangeOutputDevice(_playbackDeviceIndex);
        }
    }

    public int AudioParamsUpdateFrequency { get; set; }

    public bool Apply3dAudioEffects
    {
        get;
        set
        {
            field = value;
            _playbackService?.Apply3dEffects = value;
        }
    }

    public bool SidetoneEnabled
    {
        get;
        set
        {
            field = value;
            if (_playbackService != null) _playbackService.SidetoneEnabled = value;
        }
    }

    public double SidetoneVolume
    {
        get => field;
        set
        {
            field = value;
            if (_playbackService != null) _playbackService.SidetoneVolume = (float)value;
        }
    } = 0.4;

    private bool _isInitialized;

    private readonly IFalconSharedMemoryService _falconSharedMemoryService;
    private readonly IFalconRadioSharedMemoryService _falconRadioSharedMemoryService;
    private readonly ILoggerFactory _loggerFactory;
    private readonly ILogger<OpenFreqService> _logger;

    // Events for UI updates
    public IOpenFreqService.Mode OwnPositionMode { get; private set; }
    public event EventHandler<ConnectionState>? ConnectionStateChanged;
    public event EventHandler<string>? StatusMessageReceived;
    public event EventHandler<FrequencyConnectionStatusEventArgs>? FrequencyConnectionStatusChanged;
    public event EventHandler<FrequencyTransmissionStatusEventArgs>? FrequencyTransmissionStatusChanged;
    public event EventHandler<FrequencyJoinedEventArgs>? FrequencyJoined;
    public event EventHandler<PeerEventArgs>? PeerJoined;
    public event EventHandler<PeerEventArgs>? PeerLeft;
    public event EventHandler<PeerActivityEventArgs>? PeerActivityReceived;
    public event EventHandler<AllPeersStatusEventArgs>? AllPeersStatusChanged;

    public bool IsConnected => _client?.IsConnected ?? false;
    public bool IsAuthenticated => _client?.IsAuthenticated ?? false;
    public string? PeerId => _client?.MyPeerId;
    private int _radioPlaybackInstanceId = 0;


    /// <summary>
    /// Initialize the service with server settings and audio devices
    /// </summary>
    public async Task Initialize(OpenFreqSettings settings, int recordingDeviceIndex, int playbackDeviceIndex)
    {
        if (_isInitialized)
        {
            _logger.LogDebug("Calling Shutdown from Initialize");
            await Shutdown();
        }

        var myDisplayName =
            settings.OwnPositionMode == IOpenFreqService.Mode.BMS
                ? _falconRadioSharedMemoryService.LogbookName
                : settings.DisplayName;

        // Create client with server settings
        _logger.LogDebug("Creating new client");
        _client = new OpenFreqRtcClient(_loggerFactory,
            settings.OpenFreqServerAddress, settings.OpenFreqPassword, myDisplayName);
        _logger.LogDebug("Client created: {ClientHashCode}", _client.GetHashCode());

        // Subscribe to client events
        _client.ConnectionStateChanged += OnClientConnectionStateChanged;
        _client.Authenticated += OnClientAuthenticated;
        _client.FrequencyJoined += OnClientFrequencyJoined;
        _client.FrequencyLeft += OnClientFrequencyLeft;
        _client.PeerJoined += OnClientPeerJoined;
        _client.PeerLeft += OnClientPeerLeft;
        _client.TransmissionStateChanged += OnClientTransmissionStatusChanged;
        _client.PeerTransmissionStateChanged += OnClientPeerTransmissionStatusChanged;
        _client.AudioDataReceived += OnClientAudioDataReceived;
        _client.AllPeersStatusUpdateReceived += OnAllPeersStatusUpdateReceived;
        _client.ErrorOccurred += OnClientErrorOccurred;

        RecordingDeviceIndex = recordingDeviceIndex;
        _playbackDeviceIndex = playbackDeviceIndex;

        if (_playbackService != null)
        {
            _logger.LogWarning("Disposing old RadioPlayback instance: {InstanceId}", _radioPlaybackInstanceId);
            await _playbackService.StopAll();
            if (_playbackService is IDisposable disposable)
            {
                disposable.Dispose();
            }

            _playbackService = null;
        }

        _radioPlaybackInstanceId++;
        _playbackService = new RadioPlayback(_loggerFactory, playbackDeviceIndex);
        _logger.LogWarning("Created NEW RadioPlayback instance: {InstanceId}", _radioPlaybackInstanceId);
        _playbackService.Initialize();
        _playbackService.Apply3dEffects = Apply3dAudioEffects;
        _playbackService.SidetoneVolume = (float)SidetoneVolume;
        _isInitialized = true;

        _falconSharedMemoryService.FlyingStateChanged += OnFlyingStateChanged;
        _falconSharedMemoryService.StateChanged += OnFalconStateChanged;

        // Initialize Audio Params cache cleanup
        _cleanupCts = new CancellationTokenSource();
        _ = CleanupAudioParamsCacheAsync(_cleanupCts.Token);

        OnStatusMessage("OpenFreq service initialized");
    }

    private void OnFalconStateChanged(object? sender, ServiceStateChangedEventArgs e)
    {
        if (e.NewState == ServiceState.Connected && _falconSharedMemoryService.TheaterTerrainDir != null)
        {
            var heightmapPath = Path.Join(_falconSharedMemoryService.TheaterTerrainDir, "NewTerrain", "HeightMaps",
                "HeightMap.raw");
            if (!File.Exists(heightmapPath))
            {
                _logger.LogError("Could not find heightmap path: {heightmapPath}", heightmapPath);
                return;
            }

            LoadHeightmap(heightmapPath);
        }
    }

    private void OnFlyingStateChanged(object? sender, FlyingStateChangedEventArgs e)
    {
        if (e is not { OldFlyingState: false, NewFlyingState: true }) return;
        var heightmapPath = Path.Join(_falconSharedMemoryService.TheaterTerrainDir, "NewTerrain", "HeightMaps",
            "HeightMap.raw");
        if (!File.Exists(heightmapPath))
        {
            _logger.LogError("Could not find heightmap path: {heightmapPath}", heightmapPath);
            return;
        }

        LoadHeightmap(heightmapPath);
    }

    public async Task Shutdown()
    {
        _logger.LogDebug("Shutdown called - IsInitialized: {IsInitialized}", _isInitialized);
        if (!_isInitialized) return;

        try
        {
            if (_playbackService != null)
            {
                await _playbackService.StopAll();
                _playbackService = null;
            }

            if (_client != null)
            {
                // Unsubscribe from events before disposing
                _client.ConnectionStateChanged -= OnClientConnectionStateChanged;
                _client.Authenticated -= OnClientAuthenticated;
                _client.FrequencyJoined -= OnClientFrequencyJoined;
                _client.FrequencyLeft -= OnClientFrequencyLeft;
                _client.PeerJoined -= OnClientPeerJoined;
                _client.PeerLeft -= OnClientPeerLeft;
                _client.TransmissionStateChanged -= OnClientTransmissionStatusChanged;
                _client.PeerTransmissionStateChanged -= OnClientPeerTransmissionStatusChanged;
                _client.AudioDataReceived -= OnClientAudioDataReceived;
                _client.ErrorOccurred -= OnClientErrorOccurred;

                _logger.LogDebug("Disconnecting client: {ClientHashCode}", _client.GetHashCode());
                await _client.DisconnectAsync();
                Status = IOpenFreqService.OpenFreqStatus.Disconnected;
                _logger.LogDebug("Disposing client: {ClientHashCode}", _client.GetHashCode());
                _client.Dispose();
                _client = null;
            }

            _isInitialized = false;
            _logger.LogDebug("Shutdown complete");
        }
        catch (Exception e)
        {
            _logger.LogError(e, "Shutdown exception");
            throw;
        }
    }

    /// <summary>
    /// Connect to the OpenFreq server
    /// </summary>
    public async Task ConnectAsync()
    {
        if (!_isInitialized || _client == null)
        {
            throw new InvalidOperationException("Service not initialized. Call Initialize() first.");
        }

        OnStatusMessage($"Connecting to OpenFreq server {_client.ServerIp}...");
        Status = IOpenFreqService.OpenFreqStatus.Connecting;
        await _client.ConnectAsync();
    }

    /// <summary>
    /// Disconnect from the server
    /// </summary>
    public async Task DisconnectAsync()
    {
        if (_client == null) return;

        // Stop all transmissions
        foreach (var frequencyKhz in _activeTransmissionsAndMutedFrequencies.Keys)
        {
            await StopTransmissionAsync(frequencyKhz);
        }

        await _client.DisconnectAsync();
        _activeTransmissionsAndMutedFrequencies.Clear();
        _tunedFrequencies.Clear();
        OnStatusMessage("Disconnected from OpenFreq server");
        Status = IOpenFreqService.OpenFreqStatus.Disconnected;
    }

    public bool IsFrequencyJoined(int frequencyKhz)
    {
        return _tunedFrequencies.ContainsKey(frequencyKhz);
    }

    /// <summary>
    /// Join a frequency channel
    /// </summary>
    public async Task JoinFrequencyAsync(int frequencyKhz, RadioStationData radioStationData)
    {
        if (_client == null || !_client.IsAuthenticated)
        {
            _logger.LogWarning("Not joining frequency {FrequencyKhz}, client is not authenticated", frequencyKhz);
            return;
        }

        if (radioStationData == null)
        {
            _logger.LogWarning("Not joining frequency {FrequencyKhz}, RadioStationData is null", frequencyKhz);
            return;
        }

        if (_tunedFrequencies.ContainsKey(frequencyKhz))
        {
            _logger.LogWarning("Not joining frequency {FrequencyKhz}, client is already joined", frequencyKhz);
            return;
        }

        await _client.JoinFrequencyAsync(frequencyKhz);
        OnStatusMessage($"Joined frequency {frequencyKhz / 1000.0:F3} MHz");

        _tunedFrequencies.TryAdd(frequencyKhz, new TunedFrequencyData(radioStationData, true));
        _signalStrengthTracker.SetSquelchState(frequencyKhz, false);
        _playbackService?.TuneFrequency(frequencyKhz);

        // TODO
        //_playbackService.SetSquelchLevel(frequencyKhz, 0.1f);
    }

    /// <summary>
    /// Leave a frequency channel
    /// </summary>
    public async Task LeaveFrequencyAsync(int frequencyKhz)
    {
        if (_client == null || !_client.IsAuthenticated)
        {
            _logger.LogWarning("Not leaving frequency {FrequencyKhz}, client is not authenticated", frequencyKhz);
            return;
        }

        // Stop transmission if active
        await StopTransmissionAsync(frequencyKhz);

        _tunedFrequencies.Remove(frequencyKhz, out _);
        _signalStrengthTracker.RemoveFrequency(frequencyKhz);

        await _client.LeaveFrequencyAsync(frequencyKhz);
        OnStatusMessage($"Left frequency {frequencyKhz / 1000.0:F3} MHz");
    }

    /// <summary>
    /// Start transmitting on a frequency
    /// </summary>
    public async Task StartTransmissionAsync(int frequencyKhz, List<int> mutedFrequencies)
    {
        if (_client == null || _playbackService == null)
        {
            throw new InvalidOperationException("Service not initialized");
        }

        _tunedFrequencies.TryGetValue(frequencyKhz, out var tunedFrequencyData);
        if (!tunedFrequencyData?.IsEnabled ?? false)
        {
            _logger.LogWarning("Trying to start transmission on disabled frequency {FrequencyKhz}", frequencyKhz);
            return;
        }

        // Add to active transmissions
        _activeTransmissionsAndMutedFrequencies.TryAdd(frequencyKhz, mutedFrequencies);
        _playbackService.AddTransmittingFrequencies(mutedFrequencies);

        // Mute the noise
        //_playbackService.SetSquelchLevel(frequency, 1.0f);

        // If this is the FIRST transmission, start recording
        if (_recordHandle == 0)
        {
            //_playbackService.SetSquelchLevel(frequency, 0.01f);
            Bass.RecordInit(RecordingDeviceIndex);
            Bass.CurrentRecordingDevice = RecordingDeviceIndex;

            _logger.LogDebug("RecordingDeviceIndex set to {RecordingDeviceIndex}", RecordingDeviceIndex);

            _recordHandle = Bass.RecordStart(
                OpenFreqRtcClient.SAMPLE_RATE,
                1,
                BassFlags.RecordPause,
                Period: 2,
                RecordProcedure);

            if (_recordHandle == 0)
            {
                _activeTransmissionsAndMutedFrequencies.Clear();
                OnStatusMessage($"Failed to start recording: {Bass.LastError}");
                return;
            }
            _client.MarkTransmitStartTime();
            if (_playbackService != null) _playbackService.SidetoneEnabled = true;
            Bass.ChannelPlay(_recordHandle);
        }

        await _client.StartTransmissionAsync(frequencyKhz, Apply3dAudioEffects);
        OnStatusMessage($"Transmitting on {frequencyKhz / 1000d:F3}");
    }

    /// <summary>
    /// Stop transmitting on a frequency
    /// </summary>
    public async Task StopTransmissionAsync(int frequencyKhz)
    {
        if (_client == null) return;

        // Remove from active transmissions
        _activeTransmissionsAndMutedFrequencies.TryRemove(frequencyKhz, out var mutedFrequencies);
        if (mutedFrequencies != null)
        {
            _playbackService?.RemoveTransmittingFrequencies(mutedFrequencies);
        }

        // If NO more transmissions, stop recording and sidetone
        if (_activeTransmissionsAndMutedFrequencies.IsEmpty && _recordHandle != 0)
        {
            Bass.ChannelStop(_recordHandle);
            Bass.StreamFree(_recordHandle);
            _recordHandle = 0;
            if (_playbackService != null)
            {
                _playbackService.SidetoneEnabled = false;
                _playbackService.ClearSidetone();
            }
        }

        await _client.StopTransmissionAsync(frequencyKhz, Apply3dAudioEffects);
        OnStatusMessage($"Stopped transmitting on {frequencyKhz / 1000d:F3} MHz");
    }

    public async Task UpdateDisplayNameAsync(string newDisplayName)
    {
        if (_client is not { IsAuthenticated: true }) return;
        await _client.SetDisplayNameAsync(newDisplayName);
    }

    public void SetVolume(int frequencyKhz, float volumeValue)
    {
        _playbackService?.SetFrequencyVolume(frequencyKhz, volumeValue);
    }

    public void SetPan(int frequencyKhz, int pan)
    {
        _playbackService?.SetFrequencyPan(frequencyKhz, pan);
    }

    public void EnableFrequency(int frequencyKhz)
    {
        _tunedFrequencies.TryGetValue(frequencyKhz, out var tunedFrequencyData);
        tunedFrequencyData?.IsEnabled = true;
        _playbackService?.TuneFrequency(frequencyKhz);
        OnStatusMessage($"{frequencyKhz / 1000d:F3} enabled");
    }

    public void DisableFrequency(int frequencyKhz)
    {
        _tunedFrequencies.TryGetValue(frequencyKhz, out var tunedFrequencyData);
        tunedFrequencyData?.IsEnabled = false;
        StopTransmissionAsync(frequencyKhz).Wait(50);
        _playbackService?.UntuneFrequency(frequencyKhz);
        OnStatusMessage($"{frequencyKhz / 1000d:F3} disabled");
    }

    public void SetSquelch(int frequencyKhz, bool isSquelchClosed)
    {
        _tunedFrequencies.TryGetValue(frequencyKhz, out var tunedFrequencyData);
        if (tunedFrequencyData == null) return;

        _signalStrengthTracker.SetSquelchState(frequencyKhz, !isSquelchClosed);
        _playbackService?.SetSquelchLevel(frequencyKhz, isSquelchClosed ? SquelchLevelOn : SquelchLevelOff);
    }

    public void SetOwnPositionMode(IOpenFreqService.Mode newMode)
    {
        if (newMode == OwnPositionMode) return;

        switch (newMode)
        {
            case IOpenFreqService.Mode.BMS:
            {
                _acmiClientService.Stop();
                if (_falconSharedMemoryService.State == ServiceState.Stopped)
                {
                    _falconSharedMemoryService.Start();
                }

                break;
            }
            case IOpenFreqService.Mode.GCI:
                _falconSharedMemoryService.Stop();
                // TODO
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(newMode), newMode, null);
        }

        OwnPositionMode = newMode;
    }

    /// <summary>
    /// Check if currently transmitting on a frequency
    /// </summary>
    public bool IsTransmitting(int frequency)
    {
        return _activeTransmissionsAndMutedFrequencies.ContainsKey(frequency);
    }

    /// <summary>
    /// Load heightmap for terrain-aware RF calculations
    /// </summary>
    public void LoadHeightmap(string path, int width = 32768, int height = 32768, int bytesPerSample = 2)
    {
        _demReader?.Dispose();
        _demReader = new DEMReader(path, width, height, bytesPerSample);
        _audioSim = new FastPathAudioSim(_demReader, 0, 0, 20, _loggerFactory.CreateLogger<FastPathAudioSim>());
        OnStatusMessage($"Heightmap loaded: {path}");
        _logger.LogDebug($"Heightmap loaded: {path}");
    }

    private bool RecordProcedure(int handle, IntPtr buffer, int length, IntPtr user)
    {
        // If not transmitting on any frequency, skip
        if (_activeTransmissionsAndMutedFrequencies.Count == 0)
            return true;

        try
        {
            // Handle first packet - BASS accumulates audio during initialization
            // Calculate expected size for 20ms at 48kHz, mono, 16-bit
            // 48000 samples/sec ÷ 50 = 960 samples per 20ms
            // 960 samples × 2 bytes/sample × 1 channel = 1920 bytes
            int expectedBytes = (OpenFreqRtcClient.SAMPLE_RATE / 50) * 2;

            // TODO: I dont think we need this anymore with the shorter dsp updates. Deactivated for now
            expectedBytes = 10000;

            if (length > expectedBytes)
            {
                _logger.LogWarning("Recording packet oversized: {Length} bytes, truncating to {ExpectedBytes}",
                    length, expectedBytes);
                // Option 1: Only use the LAST 20ms (most recent audio)
                buffer = IntPtr.Add(buffer, length - expectedBytes);
                length = expectedBytes;

                // Option 2: Skip first packet entirely
                //_isFirstPacket = false;
                //return true;
            }

            // Copy audio data once
            short[] audioData = new short[length / 2];
            Marshal.Copy(buffer, audioData, 0, audioData.Length);

            // Sidetone: feed mic back to speaker with no extra buffering.
            // Use pre-allocated buffer to avoid GC allocation on the hot audio path.
            if (_playbackService is { SidetoneEnabled: true })
            {
                int n = audioData.Length;
                if (n > _sidetonePushBuffer.Length)
                    _sidetonePushBuffer = new float[n * 2];
                for (int i = 0; i < n; i++)
                    _sidetonePushBuffer[i] = audioData[i] / (float)short.MaxValue;
                _playbackService.PushSidetone(_sidetonePushBuffer.AsSpan()[..n]);
            }

            // Send to ALL active frequencies
            var frequenciesData =
                new List<(int frequencyKhz, double txPowerWatts, double ppm, Vector3? position, Vector3? velocity,
                    AmbientNoiseType ambientNoiseType)>();

            // List of frequencies that got disabled in the meantime
            var disabledFrequencies = new List<int>();
            foreach (var transmission in _activeTransmissionsAndMutedFrequencies)
            {
                var frequencyKhz = transmission.Key;
                _tunedFrequencies.TryGetValue(frequencyKhz, out var radioStationData);
                if (radioStationData == null)
                {
                    _logger.LogError($"Frequency {frequencyKhz} has no RadioStationData");
                    continue;
                }

                if (!radioStationData.IsEnabled)
                {
                    _logger.LogWarning($"Recording on frequency {frequencyKhz} which is not active");
                    disabledFrequencies.Add(frequencyKhz);
                    continue;
                }

                var position = GetOwnPosition(frequencyKhz) ?? new Vector3(0, 0, 0);
                var velocity = GetOwnVelocity(frequencyKhz);

                if (RadioStationPreset.IsVHF(frequencyKhz))
                {
                    frequenciesData.Add((frequencyKhz,
                        radioStationData.RadioStation.Preset.TxPower_VHF_W, radioStationData.RadioStation.Ppm,
                        position, velocity, radioStationData.RadioStation.Preset.AmbientNoiseType));
                }
                else
                {
                    frequenciesData.Add((frequencyKhz,
                        radioStationData.RadioStation.Preset.TxPower_VHF_W, radioStationData.RadioStation.Ppm,
                        position, velocity, radioStationData.RadioStation.Preset.AmbientNoiseType));
                }
            }

            _client?.SendAudio(audioData, frequenciesData, Apply3dAudioEffects);

            // Clean up any frequencies which might have been disabled in the meantime
            foreach (var disabledFrequency in disabledFrequencies)
            {
                _activeTransmissionsAndMutedFrequencies.TryRemove(disabledFrequency, out _);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError("Error sending audio: {ExMessage}", ex.Message);
            OnStatusMessage($"Error sending audio: {ex.Message}");
        }

        return true;
    }

    private Vector3? GetOwnPosition(int frequencyKhz)
    {
        _tunedFrequencies.TryGetValue(frequencyKhz, out var tunedFrequencyData);
        if (tunedFrequencyData == null)
        {
            return null;
        }

        switch (tunedFrequencyData.RadioStation.Type)
        {
            case RadioStationData.RadioStationType.BMS:
                if (_falconSharedMemoryService.State != ServiceState.Connected ||
                    _falconSharedMemoryService.Position == null) return null;

                return new Vector3(BmsHeightmapConverter.ToHeightmap(_falconSharedMemoryService.Position.X,
                    _falconSharedMemoryService.Position.Y, _falconSharedMemoryService.Position.Z));

            case RadioStationData.RadioStationType.STATIONARY:
                var position = tunedFrequencyData.RadioStation.Vector3;
                return position == null
                    ? null
                    : new Vector3(position.X, position.Y,
                        position.Z + tunedFrequencyData.RadioStation.Preset.AntennaElevation_m);

            case RadioStationData.RadioStationType.ACMI:
                var acmiAircraftId = tunedFrequencyData.RadioStation.AcmiAircraftId;
                if (acmiAircraftId == null) return null;

                var aircraft = _acmiClientService.GetAircraft(acmiAircraftId);
                if (aircraft == null) return null;
                return new Vector3(AcmiHeightmapConverter.ToHeightmap(aircraft.Transform.U, aircraft.Transform.V,
                    aircraft.Transform.Altitude + tunedFrequencyData.RadioStation.Preset.AntennaElevation_m));
            default:
                return null;
        }
    }

    private Vector3? GetOwnVelocity(int frequencyKhz)
    {
        _tunedFrequencies.TryGetValue(frequencyKhz, out var tunedFrequencyData);
        if (tunedFrequencyData == null)
        {
            return null;
        }

        switch (tunedFrequencyData.RadioStation.Type)
        {
            case RadioStationData.RadioStationType.BMS:
                if (_falconSharedMemoryService.State != ServiceState.Connected ||
                    _falconSharedMemoryService.Velocity == null) return null;

                // BMS velocity is stored as (East, North, Up) in ft/s
                // Convert to (North, East, Up) in m/s to match heightmap/ACMI convention
                const double feetToMeters = 0.3048;
                var bmsVel = _falconSharedMemoryService.Velocity;

                return new Vector3(
                    bmsVel.Y * feetToMeters, // North (swap Y to first component)
                    bmsVel.X * feetToMeters, // East (swap X to second component)
                    bmsVel.Z * feetToMeters // Up (Z already inverted to Up in service)
                );

            case RadioStationData.RadioStationType.STATIONARY:
                // we are stationary, duh
                return null;

            case RadioStationData.RadioStationType.ACMI:
                var acmiAircraftId = tunedFrequencyData.RadioStation.AcmiAircraftId;
                if (acmiAircraftId == null) return null;

                var aircraft = _acmiClientService.GetAircraft(acmiAircraftId);
                if (aircraft == null) return null;

                return AcmiHeightmapConverter.GetVelocityVector(aircraft.Mach, aircraft.Transform.Altitude,
                    aircraft.Transform.Pitch,
                    aircraft.Transform.Yaw);
            default:
                return null;
        }
    }

    // Client event handlers
    private void OnClientConnectionStateChanged(object? sender, ConnectionStateChangedEventArgs e)
    {
        Status = e.State switch
        {
            ConnectionState.Connected => IOpenFreqService.OpenFreqStatus.Connected,
            ConnectionState.Disconnected => IOpenFreqService.OpenFreqStatus.Disconnected,
            ConnectionState.Connecting => IOpenFreqService.OpenFreqStatus.Connecting,
            ConnectionState.Authenticated => IOpenFreqService.OpenFreqStatus.Authenticated,
            _ => Status
        };

        if (e.State == ConnectionState.Disconnected)
        {
            _tunedFrequencies.Clear();
        }

        ConnectionStateChanged?.Invoke(this, e.State);
    }

    private void OnClientAuthenticated(object? sender, AuthenticationEventArgs e)
    {
        OnStatusMessage($"Authenticated - Peer ID: {e.PeerId}, Audio Port: {e.AudioPort}");
        OnAllPeersStatusUpdateReceived(sender, new AllPeersStatusEventArgs(e.Peers));
    }

    private void OnClientFrequencyJoined(object? sender, FrequencyJoinedEventArgs e)
    {
        FrequencyJoined?.Invoke(this, e);
        _playbackService?.TuneFrequency(e.FrequencyKhz);
        foreach (var peer in e.Peers)
        {
            CreateAudioStreamForPeer(e.FrequencyKhz, peer.Id);
        }

        OnFrequencyConnectionStatusChanged(e.FrequencyKhz, Channel.ChannelConnectionStatus.Connected, e.Peers);
    }

    private void OnClientFrequencyLeft(object? sender, FrequencyLeftEventArgs e)
    {
        _playbackService?.UntuneFrequency(e.FrequencyKhz);
        _signalStrengthTracker.RemoveFrequency(e.FrequencyKhz);
        OnFrequencyConnectionStatusChanged(e.FrequencyKhz, Channel.ChannelConnectionStatus.Disconnected, []);
    }

    private void OnClientPeerJoined(object? sender, PeerEventArgs e)
    {
        CreateAudioStreamForPeer(e.FrequencyKhz, e.PeerId);
        PeerJoined?.Invoke(this, e);
    }

    private void CreateAudioStreamForPeer(int frequencyKhz, string peerId)
    {
        // Create stream for this peer-frequency combination
        string streamId = GetStreamId(peerId, frequencyKhz);

        // Start with default params (will update when we get position data)
        var audioParams = FastPathAudioSim.GetDefaultAudioParams(frequencyKhz);

        _playbackService?.StartPushStream(
            streamId,
            OpenFreqRtcClient.SAMPLE_RATE,
            1,
            audioParams
        );

        // Track it
        if (!_peerStreams.ContainsKey(peerId))
            _peerStreams[peerId] = new Dictionary<int, string>();
        _peerStreams[peerId][frequencyKhz] = streamId;
    }

    private static string GetStreamId(string peerId, double frequencyKhz)
    {
        return peerId + ":" + frequencyKhz;
    }

    private void OnClientPeerLeft(object? sender, PeerEventArgs e)
    {
        // Remove stream when peer leaves
        if (_peerStreams.TryGetValue(e.PeerId, out var freqs))
        {
            if (freqs.TryGetValue(e.FrequencyKhz, out var streamId))
            {
                _playbackService?.StopStream(streamId);
                freqs.Remove(e.FrequencyKhz);
            }
        }

        PeerLeft?.Invoke(this, e);
    }

    private void OnClientTransmissionStatusChanged(object? sender, TransmissionStateEventArgs e)
    {
        var status = e.IsTransmitting
            ? Channel.ChannelTransmissionStatus.Transmitting
            : Channel.ChannelTransmissionStatus.Idle;
        OnFrequencyTransmissionStatusChanged(e.FrequencyKhz, status);
    }

    private void OnClientPeerTransmissionStatusChanged(object? sender, PeerTransmissionEventArgs e)
    {
        OnPeerActivity(this,
            new PeerActivityEventArgs(e.FrequencyKhz,
                new PeerData(e.PeerId, e.PeerDisplayName,
                    e.IsTransmitting ? PeerData.PeerStatus.Transmitting : PeerData.PeerStatus.Receiving), e.Is3d));

        // Own TX is authoritative: don't let peer state overwrite Transmitting in subscribers
        OnFrequencyTransmissionStatusChanged(e.FrequencyKhz,
            _activeTransmissionsAndMutedFrequencies.ContainsKey(e.FrequencyKhz)
                ? Channel.ChannelTransmissionStatus.Transmitting
                : e.IsTransmitting
                    ? Channel.ChannelTransmissionStatus.Receiving
                    : Channel.ChannelTransmissionStatus.Idle);
    }

    /// <summary>
    /// Route received audio to playback service with RF effects
    /// </summary>
    /// <summary>
    /// Route received audio to playback service with RF effects
    /// </summary>
    private void OnClientAudioDataReceived(object? sender, AudioDataEventArgs e)
    {
        if (e.AudioData.Length == 0)
        {
            _logger.LogWarning("Audio data received with 0 size");
            return;
        }

        if (e.Metadata.Frequencies.Count == 0)
        {
            _logger.LogWarning("Audio data received without frequencies, dropping");
            return;
        }

        foreach (var frequencyTransmission in e.Metadata.Frequencies)
        {
            if (frequencyTransmission.In3d != Apply3dAudioEffects)
            {
                _logger.LogTrace("Audio data received but not matching 3D settings - dropping");
                continue;
            }

            if (Apply3dAudioEffects && _activeTransmissionsAndMutedFrequencies.ContainsKey(frequencyTransmission.Khz))
            {
                _logger.LogTrace("Receiving transmission when we are sending - dropping");
                continue;
            }

            var streamId = GetStreamId(e.PeerId, frequencyTransmission.Khz);

            // Calculate audio params - always sync when 3D enabled, default otherwise
            var audioParams = Apply3dAudioEffects
                ? CalculateAudioParamsSync(frequencyTransmission, e.PeerId)
                : FastPathAudioSim.GetDefaultAudioParams(frequencyTransmission.Khz);

            lock (_streamCreationLock)
            {
                var streamExists = _playbackService?.IsStreamActive(streamId) ?? false;

                if (!streamExists)
                {
                    _logger.LogDebug(
                        $"Creating new stream: {streamId}, SR={OpenFreqRtcClient.SAMPLE_RATE}");

                    _playbackService?.StartPushStream(
                        streamId,
                        OpenFreqRtcClient.SAMPLE_RATE,
                        1,
                        audioParams);
                }
                else
                {
                    // Update existing stream params
                    _playbackService?.UpdateStreamParams(streamId, audioParams);
                }
            }

            var ambientNoiseType = Apply3dAudioEffects ? frequencyTransmission.AmbientNoiseType : AmbientNoiseType.None;

            // Update signal strength tracking for 3D audio
            if (Apply3dAudioEffects)
            {
                _signalStrengthTracker.UpdateSignalStrength(audioParams.RadioFrequencyKHz, audioParams);
            }

            // Push audio data immediately
            _playbackService?.PushAudioData(streamId, e.AudioData, ambientNoiseType);
        }
    }

    private AudioParams CalculateAudioParamsSync(FrequencyTransmission frequencyTransmission, string peerId)
    {
        var ownPosition = GetOwnPosition(frequencyTransmission.Khz);
        var ownVelocity = GetOwnVelocity(frequencyTransmission.Khz);

        if (frequencyTransmission.Position == null || ownPosition == null || _audioSim == null)
        {
            return FastPathAudioSim.GetDefaultAudioParams(frequencyTransmission.Khz);
        }

        // Check cache
        var cacheKey = (peerId, frequencyTransmission.Khz);
        var now = DateTime.UtcNow;

        if (_audioParamsCache.TryGetValue(cacheKey, out var cached) &&
            (now - cached.LastCalculated) < _audioParamsCacheDuration)
        {
            return cached.Params;
        }

        // Calculate
        _tunedFrequencies.TryGetValue(frequencyTransmission.Khz, out var receiverData);
        if (receiverData == null)
        {
            return FastPathAudioSim.GetDefaultAudioParams(frequencyTransmission.Khz);
        }

        var receiverSensitivityDb = RadioStationPreset.IsVHF(frequencyTransmission.Khz)
            ? receiverData.RadioStation.Preset.RxSensitivity_VHF_dBm
            : receiverData.RadioStation.Preset.RxSensitivity_UHF_dBm;

        var audioParams = _audioSim.CalculateAudioParams(
            frequencyTransmission.Position.X, frequencyTransmission.Position.Y, frequencyTransmission.Position.Z,
            ownPosition.X, ownPosition.Y, ownPosition.Z,
            frequencyTransmission.Khz, (float)frequencyTransmission.Ppm,
            frequencyTransmission.TxPowerWatts, receiverSensitivityDb,
            txVelocity: frequencyTransmission.Velocity?.ToTuple(),
            rxVelocity: ownVelocity?.ToTuple()
        );

        // Update cache
        _audioParamsCache[cacheKey] = new AudioParamsCacheEntry
        {
            Params = audioParams,
            LastCalculated = now
        };

#if DEBUG
        _logger.LogDebug("Calculated audio params {AudioParams}", audioParams);
#endif

        return audioParams;
    }


    // Periodical Cache cleanup
    private async Task CleanupAudioParamsCacheAsync(CancellationToken cancellationToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(30));
        try
        {
            while (await timer.WaitForNextTickAsync(cancellationToken))
            {
                var cutoff = DateTime.UtcNow - TimeSpan.FromSeconds(30);
                var keysToRemove = _audioParamsCache
                    .Where(kvp => kvp.Value.LastCalculated < cutoff)
                    .Select(kvp => kvp.Key)
                    .ToList();

                foreach (var key in keysToRemove)
                {
                    _audioParamsCache.TryRemove(key, out _);
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Expected when stopping
        }
    }

    private void OnClientErrorOccurred(object? sender, ErrorEventArgs e)
    {
        _logger.LogError(e.ErrorMessage);
        OnStatusMessage($"Error: {e.ErrorMessage}");
    }

    // Event raising methods
    private void OnStatusMessage(string message) =>
        StatusMessageReceived?.Invoke(this, message);

    private void OnFrequencyConnectionStatusChanged(int frequencyKhz, Channel.ChannelConnectionStatus connectionStatus,
        List<ChannelStateMessage.Peer> peers)
    {
        _logger.LogDebug("Frequency {FrequencyKhz}: {Status}", frequencyKhz, connectionStatus);
        FrequencyConnectionStatusChanged?.Invoke(this,
            new FrequencyConnectionStatusEventArgs(frequencyKhz, connectionStatus, peers));
    }

    private void OnFrequencyTransmissionStatusChanged(int frequencyKhz,
        Channel.ChannelTransmissionStatus transmissionStatus)
    {
        _logger.LogDebug("Frequency {FrequencyKhz}: {Status}", frequencyKhz, transmissionStatus);
        FrequencyTransmissionStatusChanged?.Invoke(this,
            new FrequencyTransmissionStatusEventArgs(frequencyKhz, transmissionStatus));
    }

    private void OnPeerActivity(object? sender, PeerActivityEventArgs args) =>
        PeerActivityReceived?.Invoke(this, args);

    private void OnAllPeersStatusUpdateReceived(object? sender, AllPeersStatusEventArgs args) =>
        AllPeersStatusChanged?.Invoke(this, args);

    public void Dispose()
    {
        // Stop the cache cleanup
        _cleanupCts?.Cancel();
        _cleanupCts?.Dispose();

        // Stop all transmissions and free recording handle
        Bass.ChannelStop(_recordHandle);
        Bass.StreamFree(_recordHandle);

        _recordHandle = 0;
        _activeTransmissionsAndMutedFrequencies.Clear();

        _playbackService?.StopAll();
        _demReader?.Dispose();

        _falconSharedMemoryService.FlyingStateChanged -= OnFlyingStateChanged;
        _falconSharedMemoryService.StateChanged -= OnFalconStateChanged;

        // Unsubscribe from client events before disposing
        if (_client != null)
        {
            _client.ConnectionStateChanged -= OnClientConnectionStateChanged;
            _client.Authenticated -= OnClientAuthenticated;
            _client.FrequencyJoined -= OnClientFrequencyJoined;
            _client.FrequencyLeft -= OnClientFrequencyLeft;
            _client.PeerJoined -= OnClientPeerJoined;
            _client.PeerLeft -= OnClientPeerLeft;
            _client.TransmissionStateChanged -= OnClientTransmissionStatusChanged;
            _client.PeerTransmissionStateChanged -= OnClientPeerTransmissionStatusChanged;
            _client.AudioDataReceived -= OnClientAudioDataReceived;
            _client.ErrorOccurred -= OnClientErrorOccurred;

            _client.Dispose();
        }
    }
}

// Event argument classes
public class FrequencyConnectionStatusEventArgs(
    int frequencyKhz,
    Channel.ChannelConnectionStatus connectionStatus,
    List<ChannelStateMessage.Peer> peers)
    : EventArgs
{
    public int FrequencyKhz { get; } = frequencyKhz;
    public Channel.ChannelConnectionStatus ConnectionStatus { get; } = connectionStatus;
    public List<ChannelStateMessage.Peer> Peers = peers;
}

public class FrequencyTransmissionStatusEventArgs(
    int frequencyKhz,
    Channel.ChannelTransmissionStatus transmissionStatus) : EventArgs
{
    public int FrequencyKhz { get; } = frequencyKhz;
    public Channel.ChannelTransmissionStatus TransmissionStatus { get; } = transmissionStatus;
}

public class PeerActivityEventArgs(int frequencyKhz, PeerData peerData, bool is3d) : EventArgs
{
    public int FrequencyKhz { get; } = frequencyKhz;
    public PeerData PeerData { get; } = peerData;
    public bool Is3d { get; } = is3d;
}

// AudioParams Cache
internal class AudioParamsCacheEntry
{
    public required AudioParams Params { get; set; }
    public DateTime LastCalculated { get; set; }
}