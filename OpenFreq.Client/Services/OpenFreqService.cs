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

    // Keyed by (frequencyKhz, slotId) so multiple radio sets can tune the same frequency independently.
    private readonly ConcurrentDictionary<(int FreqKhz, Guid SlotId), TunedFrequencyData> _tunedSlots = new();

    // Which slotId is currently TX-ing on each frequency (needed for own-position lookup during record).
    private readonly ConcurrentDictionary<int, Guid> _activeTransmissionSlots = new();

    private bool IsAnySlotTuned(int frequencyKhz) =>
        _tunedSlots.Keys.Any(k => k.FreqKhz == frequencyKhz);

    private TunedFrequencyData? GetAnyTunedSlot(int frequencyKhz) =>
        _tunedSlots.FirstOrDefault(kvp => kvp.Key.FreqKhz == frequencyKhz).Value;


    private RadioPlayback? _playbackService;
    private readonly Lock _streamCreationLock = new();

    private DEMReader? _demReader;
    private FastPathAudioSim? _audioSim;
    private readonly IAcmiClientService _acmiClientService;
    private readonly SignalStrengthTracker _signalStrengthTracker;

    private int _recordingDeviceIndex;

    public int RecordingDeviceIndex
    {
        get => _recordingDeviceIndex;
        set
        {
            _recordingDeviceIndex = value;
            // If a recording session is already active, migrate it to the new device immediately.
            // Without this, the live recording stream keeps using the old (potentially dead) device
            // until the user stops and restarts transmission.
            if (_recordHandle != 0)
            {
                _logger.LogInformation(
                    "Recording device changed to BASS index {Index} while recording active — restarting capture",
                    value);
                RestartRecordingOnNewDevice(value);
            }
        }
    }

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

    public double MasterVolume
    {
        get => field;
        set
        {
            field = value;
            if (_playbackService != null) _playbackService.MasterVolume = (float)value;
        }
    } = 1.0;

    public double AmbientNoiseVolume
    {
        get => field;
        set
        {
            field = value;
            if (_playbackService != null) _playbackService.AmbientNoiseVolume = (float)value;
        }
    } = 1.0;

    private bool _isInitialized;

    private readonly IFalconSharedMemoryService _falconSharedMemoryService;
    private readonly IFalconRadioSharedMemoryService _falconRadioSharedMemoryService;
    private readonly ILoggerFactory _loggerFactory;
    private readonly ILogger<OpenFreqService> _logger;

    // Events for UI updates
    public IOpenFreqService.Mode OwnPositionMode { get; private set; }
    public event EventHandler<ConnectionState>? ConnectionStateChanged;
    public event EventHandler<string>? StatusMessageReceived;
    public event EventHandler<string>? AudioPlaybackErrorOccurred;
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

        // In BMS mode: use Nickname from connection params (= LogBook.Callsign(), set by BMS
        // before AttemptToConnect fires). LogbookName is from the Telemetry struct which is
        // initialised to "Wot Pilot?!" and only written after ClientReady() — too late.
        // NEVER fall back to settings.DisplayName in BMS mode — that is the manually-entered
        // GCI name. If Nickname is unavailable (ConnectionParameters reset between RCC close
        // and next poll), connect with an empty placeholder; UpdateDisplayNameAsync() will
        // push the real callsign immediately after authentication.
        var myDisplayName =
            settings.OwnPositionMode == IOpenFreqService.Mode.BMS
                ? (_falconRadioSharedMemoryService.ConnectionParameters?.Nickname ?? string.Empty)
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
        var previousPlaybackDeviceIndex = _playbackDeviceIndex;
        _playbackDeviceIndex = playbackDeviceIndex;

        if (_playbackService == null)
        {
            // First initialization: create RadioPlayback and init BASS device.
            _playbackService = new RadioPlayback(_loggerFactory, playbackDeviceIndex);
            _playbackService.UserFacingError += OnPlaybackUserFacingError;
            _playbackService.Initialize();
            _logger.LogInformation("Playback Service initialized");
        }
        else if (previousPlaybackDeviceIndex != playbackDeviceIndex)
        {
            // Device changed: switch without tearing down BASS entirely.
            _logger.LogWarning(
                "Playback device changed {Old}→{New} — calling ChangeOutputDevice",
                previousPlaybackDeviceIndex, playbackDeviceIndex);
            _playbackService.UserFacingError += OnPlaybackUserFacingError;
            _playbackService.ChangeOutputDevice(playbackDeviceIndex);
        }
        else
        {
            // Same device, same instance — streams already stopped in Shutdown(). Just re-subscribe.
            _playbackService.UserFacingError += OnPlaybackUserFacingError;
            // Guard against the master stream having been stopped during the previous session
            _playbackService.EnsureMasterStreamRunning();
        }

        _playbackService.Apply3dEffects = Apply3dAudioEffects;
        _playbackService.SidetoneEnabled = SidetoneEnabled;
        _playbackService.SidetoneVolume = (float)SidetoneVolume;
        _playbackService.AmbientNoiseVolume = (float)AmbientNoiseVolume;
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
                _playbackService.UserFacingError -= OnPlaybackUserFacingError;
                // Stop individual streams without freeing the BASS device — RadioPlayback is
                // kept alive so the next Initialize() can reuse it without Bass.Free()+Bass.Init().
                // Full StopAll() (Bass.Free) only happens on device change or app Dispose().
                var activeStreams = _playbackService.GetActiveStreams();
                foreach (var streamId in activeStreams)
                    await _playbackService.StopStream(streamId);
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

            // Unsubscribe falcon shared memory events — subscribed on every Initialize,
            // so must be unsubscribed here to prevent accumulation across reconnects.
            _falconSharedMemoryService.FlyingStateChanged -= OnFlyingStateChanged;
            _falconSharedMemoryService.StateChanged -= OnFalconStateChanged;

            _peerStreams.Clear();
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
    public async Task ConnectAsync(TimeSpan? connectTimeout = null)
    {
        if (!_isInitialized || _client == null)
        {
            throw new InvalidOperationException("Service not initialized. Call Initialize() first.");
        }

        OnStatusMessage($"Connecting to OpenFreq server {_client.ServerIp}...");
        Status = IOpenFreqService.OpenFreqStatus.Connecting;
        await _client.ConnectAsync(connectTimeout);
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
        _activeTransmissionSlots.Clear();
        _tunedSlots.Clear();

        // Stop orphaned BASS streams before clearing the tracking dict.
        // PeerLeft events may not fire on abrupt disconnects; stopping here ensures
        // RadioPlayback stays clean so it can be reused on the next connect.
        if (_playbackService != null)
        {
            foreach (var freqs in _peerStreams.Values)
                foreach (var streamId in freqs.Values)
                    await _playbackService.StopStream(streamId);
        }

        _peerStreams.Clear();
        OnStatusMessage("Disconnected from OpenFreq server");
        Status = IOpenFreqService.OpenFreqStatus.Disconnected;
    }

    public bool IsFrequencyJoined(int frequencyKhz, Guid slotId)
    {
        return _tunedSlots.ContainsKey((frequencyKhz, slotId));
    }

    /// <summary>
    /// Join a frequency channel for a specific radio slot.
    /// The signalling server is joined only on the first slot; subsequent slots on the same
    /// frequency reuse the existing server connection.
    /// </summary>
    public async Task JoinFrequencyAsync(int frequencyKhz, Guid slotId, RadioStationData radioStationData)
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

        if (_tunedSlots.ContainsKey((frequencyKhz, slotId)))
        {
            _logger.LogDebug("Not joining frequency {FrequencyKhz} slot {SlotId}, already joined", frequencyKhz, slotId);
            return;
        }

        var isFirstSlot = !IsAnySlotTuned(frequencyKhz);

        // Set up local audio state BEFORE sending the join to the server
        _tunedSlots.TryAdd((frequencyKhz, slotId), new TunedFrequencyData(radioStationData, true));
        _signalStrengthTracker.SetSquelchState(frequencyKhz, false);
        _playbackService?.TuneFrequency(frequencyKhz, slotId);

        if (isFirstSlot)
        {
            await _client.JoinFrequencyAsync(frequencyKhz);
            OnStatusMessage($"Joined frequency {frequencyKhz / 1000.0:F3} MHz");
        }

        if (!isFirstSlot)
        {
            // Frequency already active on server — synthesise the connected event for this slot only.
            OnFrequencyConnectionStatusChanged(frequencyKhz, Channel.ChannelConnectionStatus.Connected, [], slotId);
            OnStatusMessage($"Tuned frequency {frequencyKhz / 1000.0:F3} MHz (additional slot)");
        }
    }

    /// <summary>
    /// Leave a frequency channel for a specific radio slot.
    /// The signalling server is left only when the last slot leaves.
    /// </summary>
    public async Task LeaveFrequencyAsync(int frequencyKhz, Guid slotId)
    {
        if (_client == null || !_client.IsAuthenticated)
        {
            _logger.LogWarning("Not leaving frequency {FrequencyKhz}, client is not authenticated", frequencyKhz);
            return;
        }

        // Stop transmission if this slot owns the active TX on this frequency.
        if (_activeTransmissionSlots.TryGetValue(frequencyKhz, out var txSlotId) && txSlotId == slotId)
        {
            await StopTransmissionAsync(frequencyKhz);
        }

        // Disconnect this slot immediately (before server leave so UI updates promptly).
        OnFrequencyConnectionStatusChanged(frequencyKhz, Channel.ChannelConnectionStatus.Disconnected, [], slotId);

        _tunedSlots.TryRemove((frequencyKhz, slotId), out _);
        _playbackService?.UntuneFrequency(frequencyKhz, slotId);

        if (!IsAnySlotTuned(frequencyKhz))
        {
            _signalStrengthTracker.RemoveFrequency(frequencyKhz);
            await _client.LeaveFrequencyAsync(frequencyKhz);
            OnStatusMessage($"Left frequency {frequencyKhz / 1000.0:F3} MHz");
        }
    }

    /// <summary>
    /// Start transmitting on a frequency from a specific radio slot.
    /// </summary>
    public async Task StartTransmissionAsync(int frequencyKhz, Guid slotId, List<int> mutedFrequencies)
    {
        if (_client == null || _playbackService == null)
        {
            throw new InvalidOperationException("Service not initialized");
        }

        _tunedSlots.TryGetValue((frequencyKhz, slotId), out var tunedFrequencyData);
        if (!tunedFrequencyData?.IsEnabled ?? false)
        {
            _logger.LogWarning("Trying to start transmission on disabled frequency {FrequencyKhz}", frequencyKhz);
            return;
        }

        // Add to active transmissions (TX is per-frequency; only one TX per frequency at a time)
        _activeTransmissionsAndMutedFrequencies.TryAdd(frequencyKhz, mutedFrequencies);
        _activeTransmissionSlots[frequencyKhz] = slotId;
        _playbackService.AddTransmittingFrequencies(mutedFrequencies);

        // Mute the noise
        //_playbackService.SetSquelchLevel(frequency, 1.0f);

        // If this is the FIRST transmission, start recording
        if (_recordHandle == 0)
        {
            //_playbackService.SetSquelchLevel(frequency, 0.01f);
            // RecordInit: Errors.Already is fine — AudioService.Init may have already done it.
            if (!Bass.RecordInit(RecordingDeviceIndex) && Bass.LastError != Errors.Already)
            {
                var initMsg = $"Failed to initialize recording device (BASS index {RecordingDeviceIndex}): {Bass.LastError}";
                _logger.LogError("{Message}", initMsg);
                AudioPlaybackErrorOccurred?.Invoke(this, initMsg);
                _activeTransmissionsAndMutedFrequencies.Clear();
                return;
            }

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
                var startMsg = $"Failed to start recording on device (BASS index {RecordingDeviceIndex}): {Bass.LastError}";
                _logger.LogError("{Message}", startMsg);
                AudioPlaybackErrorOccurred?.Invoke(this, startMsg);
                _activeTransmissionsAndMutedFrequencies.Clear();
                return;
            }

            _client.MarkTransmitStartTime();
            if (_playbackService != null) _playbackService.SidetoneEnabled = SidetoneEnabled;

            if (!Bass.ChannelPlay(_recordHandle))
                _logger.LogWarning("ChannelPlay on record handle returned false: {Error}", Bass.LastError);
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
        _activeTransmissionSlots.TryRemove(frequencyKhz, out _);
        if (mutedFrequencies != null)
        {
            _playbackService?.RemoveTransmittingFrequencies(mutedFrequencies);
        }

        // If NO more transmissions, stop recording and sidetone
        if (_activeTransmissionsAndMutedFrequencies.IsEmpty && _recordHandle != 0)
        {
            if (!Bass.ChannelStop(_recordHandle))
            {
                _logger.LogWarning("ChannelStop on record handle {Handle} returned false: {Error}",
                    _recordHandle, Bass.LastError);
            }

            _recordHandle = 0;
            if (_playbackService != null)
            {
                _playbackService.SidetoneEnabled = SidetoneEnabled;
                _playbackService.ClearSidetone();
            }
        }

        await _client.StopTransmissionAsync(frequencyKhz, Apply3dAudioEffects);
        OnStatusMessage($"Stopped transmitting on {frequencyKhz / 1000d:F3} MHz");
    }

    public async Task NotifyModeAsync(bool is3d)
    {
        if (_client == null || !_client.IsConnected) return;
        await _client.SendModeUpdateAsync(is3d);
    }

    public async Task UpdateDisplayNameAsync(string newDisplayName)
    {
        if (_client is not { IsAuthenticated: true }) return;
        await _client.SetDisplayNameAsync(newDisplayName);
    }

    public void SetVolume(int frequencyKhz, Guid slotId, float volumeValue)
    {
        _playbackService?.SetFrequencyVolume(frequencyKhz, slotId, volumeValue);
    }

    public void SetPan(int frequencyKhz, Guid slotId, int pan)
    {
        _playbackService?.SetFrequencyPan(frequencyKhz, slotId, pan);
    }

    public void EnableFrequency(int frequencyKhz, Guid slotId)
    {
        _tunedSlots.TryGetValue((frequencyKhz, slotId), out var tunedFrequencyData);
        if (tunedFrequencyData == null) return;
        tunedFrequencyData.IsEnabled = true;
        _playbackService?.TuneFrequency(frequencyKhz, slotId);
        OnStatusMessage($"{frequencyKhz / 1000d:F3} enabled");
    }

    public void DisableFrequency(int frequencyKhz, Guid slotId)
    {
        _tunedSlots.TryGetValue((frequencyKhz, slotId), out var tunedFrequencyData);
        if (tunedFrequencyData == null) return;
        tunedFrequencyData.IsEnabled = false;
        // Only stop TX if this slot owns it and no other enabled slot remains.
        if (_activeTransmissionSlots.TryGetValue(frequencyKhz, out var txSlot) && txSlot == slotId &&
            !_tunedSlots.Any(k => k.Key.FreqKhz == frequencyKhz && k.Value.IsEnabled))
        {
            StopTransmissionAsync(frequencyKhz).Wait(50);
        }
        _playbackService?.UntuneFrequency(frequencyKhz, slotId);
        OnStatusMessage($"{frequencyKhz / 1000d:F3} disabled");
    }

    public void SetSquelch(int frequencyKhz, Guid slotId, bool isSquelchClosed)
    {
        _signalStrengthTracker.SetSquelchState(frequencyKhz, !isSquelchClosed);
        _playbackService?.SetSquelchLevel(frequencyKhz, slotId, isSquelchClosed ? 1f : 0f);
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
                _activeTransmissionSlots.TryGetValue(frequencyKhz, out var txSlotId);
                _tunedSlots.TryGetValue((frequencyKhz, txSlotId), out var radioStationData);
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

                var position = GetOwnPosition(frequencyKhz, txSlotId) ?? new Vector3(0, 0, 0);
                var velocity = GetOwnVelocity(frequencyKhz, txSlotId);

                if (RadioStationPreset.IsVHF(frequencyKhz))
                {
                    frequenciesData.Add((frequencyKhz,
                        radioStationData.RadioStation.Preset.TxPower_VHF_W, radioStationData.RadioStation.Ppm,
                        position, velocity, radioStationData.RadioStation.Preset.AmbientNoiseType));
                }
                else
                {
                    frequenciesData.Add((frequencyKhz,
                        radioStationData.RadioStation.Preset.TxPower_UHF_W, radioStationData.RadioStation.Ppm,
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

    private Vector3? GetOwnPosition(int frequencyKhz, Guid slotId)
    {
        _tunedSlots.TryGetValue((frequencyKhz, slotId), out var tunedFrequencyData);
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

    private Vector3? GetOwnVelocity(int frequencyKhz, Guid slotId)
    {
        _tunedSlots.TryGetValue((frequencyKhz, slotId), out var tunedFrequencyData);
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

    private void OnPlaybackUserFacingError(string message)
    {
        _logger.LogError("RadioPlayback user-facing error: {Message}", message);
        AudioPlaybackErrorOccurred?.Invoke(this, message);
    }

    /// <summary>
    /// Stops the active recording stream and restarts it on <paramref name="deviceIndex"/>.
    /// Called from the <see cref="RecordingDeviceIndex"/> setter when a recording is live.
    /// Safe to call with _recordHandle == 0 (no-op).
    /// </summary>
    private void RestartRecordingOnNewDevice(int deviceIndex)
    {
        if (_recordHandle == 0) return;

        // Stop current capture
        if (!Bass.ChannelStop(_recordHandle))
            _logger.LogWarning("ChannelStop on record handle {Handle} returned false: {Error}",
                _recordHandle, Bass.LastError);
        if (!Bass.StreamFree(_recordHandle))
            _logger.LogWarning("StreamFree on record handle {Handle} returned false: {Error}",
                _recordHandle, Bass.LastError);
        _recordHandle = 0;

        // Init new device — Errors.Already is fine (AudioService.Init may have done it)
        if (!Bass.RecordInit(deviceIndex) && Bass.LastError != Errors.Already)
        {
            var msg = $"Failed to initialize recording device (BASS index {deviceIndex}): {Bass.LastError}";
            _logger.LogError("{Message}", msg);
            AudioPlaybackErrorOccurred?.Invoke(this, msg);
            return;
        }

        Bass.CurrentRecordingDevice = deviceIndex;

        _recordHandle = Bass.RecordStart(
            OpenFreqRtcClient.SAMPLE_RATE, 1,
            BassFlags.RecordPause, Period: 2,
            RecordProcedure);

        if (_recordHandle == 0)
        {
            var msg = $"Failed to restart recording on device (BASS index {deviceIndex}): {Bass.LastError}";
            _logger.LogError("{Message}", msg);
            AudioPlaybackErrorOccurred?.Invoke(this, msg);
            return;
        }

        if (!Bass.ChannelPlay(_recordHandle))
        {
            var msg = $"Failed to resume recording channel on device (BASS index {deviceIndex}): {Bass.LastError}";
            _logger.LogError("{Message}", msg);
            AudioPlaybackErrorOccurred?.Invoke(this, msg);
        }
        else
        {
            _logger.LogInformation("Recording restarted on BASS device {Index}", deviceIndex);
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
            _tunedSlots.Clear();
            _activeTransmissionSlots.Clear();
        }

        ConnectionStateChanged?.Invoke(this, e.State);
    }

    private void OnClientAuthenticated(object? sender, AuthenticationEventArgs e)
    {
        OnStatusMessage($"Authenticated - Peer ID: {e.PeerId}, Audio Port: {e.AudioPort}");
        OnAllPeersStatusUpdateReceived(sender, new AllPeersStatusEventArgs(e.Peers));
        // Push current mode to server immediately so it knows our Is3d state before
        // we join any channels — prevents the AllPeersStatus broadcast on join from
        // showing us in the wrong lobby/game section.
        _ = _client?.SendModeUpdateAsync(Apply3dAudioEffects);
    }

    private void OnClientFrequencyJoined(object? sender, FrequencyJoinedEventArgs e)
    {
        FrequencyJoined?.Invoke(this, e);
        foreach (var peer in e.Peers)
            CreateAudioStreamForPeer(e.FrequencyKhz, peer.Id);

        // Update all slots tuned to this frequency to Connected.
        OnFrequencyConnectionStatusChanged(e.FrequencyKhz, Channel.ChannelConnectionStatus.Connected, e.Peers);
    }

    private void OnClientFrequencyLeft(object? sender, FrequencyLeftEventArgs e)
    {
        // Per-slot disconnection and playback untune are handled in LeaveFrequencyAsync.
        // Nothing extra needed here.
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
        OnFrequencyTransmissionStatusChanged(e.FrequencyKhz, status, Apply3dAudioEffects);
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
                    : Channel.ChannelTransmissionStatus.Idle,
            e.Is3d);
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
                _logger.LogDebug("Audio data received but not matching 3D settings - dropping");
                continue;
            }

            if (Apply3dAudioEffects && _activeTransmissionsAndMutedFrequencies.ContainsKey(frequencyTransmission.Khz))
            {
                _logger.LogDebug("Receiving transmission when we are sending - dropping");
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
                    _logger.LogWarning(
                        "Lazy-creating stream {StreamId} on {FreqMhz:F3} MHz — PeerJoined arrived after audio",
                        streamId, frequencyTransmission.Khz / 1000.0);

                    _playbackService?.StartPushStream(
                        streamId,
                        OpenFreqRtcClient.SAMPLE_RATE,
                        1,
                        audioParams);

                    // TuneFrequency is called per-slot in JoinFrequencyAsync; no action needed here.
                    if (!IsAnySlotTuned(frequencyTransmission.Khz))
                    {
                        _logger.LogWarning(
                            "Lazy stream {StreamId}: frequency {FreqMhz:F3} MHz not tuned on any slot — audio will be silenced by DSP",
                            streamId, frequencyTransmission.Khz / 1000.0);
                    }
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
        var cacheKey = (peerId, frequencyTransmission.Khz);

        // All slots on the same frequency share the same RadioStationData (position/velocity).
        // Pick any tuned slot's key for position lookup.
        var anySlotKey = _tunedSlots.Keys.FirstOrDefault(k => k.FreqKhz == frequencyTransmission.Khz);
        var ownPosition = anySlotKey != default ? GetOwnPosition(anySlotKey.FreqKhz, anySlotKey.SlotId) : null;
        var ownVelocity = anySlotKey != default ? GetOwnVelocity(anySlotKey.FreqKhz, anySlotKey.SlotId) : null;

        if (frequencyTransmission.Position == null || ownPosition == null || _audioSim == null)
        {
            // Inputs missing (e.g. a concealed frame without position).
            // Reuse the last physics result for this source+freq, even if expired.
            // Stale RF params beat full-volume no-physics audio.
            return LastKnownOrDefaultAudioParams(cacheKey, frequencyTransmission.Khz);
        }

        // Check cache
        var now = DateTime.UtcNow;

        if (_audioParamsCache.TryGetValue(cacheKey, out var cached) &&
            (now - cached.LastCalculated) < _audioParamsCacheDuration)
        {
            return cached.Params;
        }

        // Calculate — all slots on the same freq share the same RadioStationData, so any slot's data is fine.
        var receiverData = GetAnyTunedSlot(frequencyTransmission.Khz);
        if (receiverData == null)
        {
            return LastKnownOrDefaultAudioParams(cacheKey, frequencyTransmission.Khz);
        }

        var receiverSensitivityDb = RadioStationPreset.IsVHF(frequencyTransmission.Khz)
            ? receiverData.RadioStation.Preset.RxSensitivity_VHF_dBm
            : receiverData.RadioStation.Preset.RxSensitivity_UHF_dBm;

        var audioParams = _audioSim.CalculateAudioParams(
            frequencyTransmission.Position.X, frequencyTransmission.Position.Y, frequencyTransmission.Position.Z,
            ownPosition.X, ownPosition.Y, ownPosition.Z,
            frequencyTransmission.Khz, (float)frequencyTransmission.Ppm,
            frequencyTransmission.TxPowerWatts, receiverSensitivityDb,
            txAltitudeIsMSL: true, rxAltitudeIsMSL: true,
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

    // Last computed physics params for this source+freq, ignoring the cache freshness.
    // Fallback to flat default only when nothing was ever calculated.
    private AudioParams LastKnownOrDefaultAudioParams((string PeerId, int FrequencyKhz) cacheKey, int khz)
        => _audioParamsCache.TryGetValue(cacheKey, out var cached)
            ? cached.Params
            : FastPathAudioSim.GetDefaultAudioParams(khz);


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
        List<ChannelStateMessage.Peer> peers, Guid? slotId = null)
    {
        _logger.LogDebug("Frequency {FrequencyKhz}: {Status}", frequencyKhz, connectionStatus);
        FrequencyConnectionStatusChanged?.Invoke(this,
            new FrequencyConnectionStatusEventArgs(frequencyKhz, connectionStatus, peers, slotId));
    }

    private void OnFrequencyTransmissionStatusChanged(int frequencyKhz,
        Channel.ChannelTransmissionStatus transmissionStatus, bool is3d)
    {
        _logger.LogDebug("Frequency {FrequencyKhz}: {Status}", frequencyKhz, transmissionStatus);
        FrequencyTransmissionStatusChanged?.Invoke(this,
            new FrequencyTransmissionStatusEventArgs(frequencyKhz, transmissionStatus, is3d));
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
        if (_recordHandle != 0)
        {
            if (!Bass.ChannelStop(_recordHandle))
                _logger.LogWarning("ChannelStop on record handle during Dispose returned false: {Error}", Bass.LastError);
            if (!Bass.StreamFree(_recordHandle))
                _logger.LogWarning("StreamFree on record handle during Dispose returned false: {Error}", Bass.LastError);
            _recordHandle = 0;
        }
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
            _client.AllPeersStatusUpdateReceived -= OnAllPeersStatusUpdateReceived;
            _client.ErrorOccurred -= OnClientErrorOccurred;

            _client.Dispose();
        }
    }
}

// Event argument classes
public class FrequencyConnectionStatusEventArgs(
    int frequencyKhz,
    Channel.ChannelConnectionStatus connectionStatus,
    List<ChannelStateMessage.Peer> peers,
    Guid? slotId = null)
    : EventArgs
{
    public int FrequencyKhz { get; } = frequencyKhz;
    public Channel.ChannelConnectionStatus ConnectionStatus { get; } = connectionStatus;
    public List<ChannelStateMessage.Peer> Peers = peers;
    /// <summary>When set, only the channel with this Id should be updated; null means all channels on the frequency.</summary>
    public Guid? SlotId { get; } = slotId;
}

public class FrequencyTransmissionStatusEventArgs(
    int frequencyKhz,
    Channel.ChannelTransmissionStatus transmissionStatus,
    bool is3d) : EventArgs
{
    public int FrequencyKhz { get; } = frequencyKhz;
    public Channel.ChannelTransmissionStatus TransmissionStatus { get; } = transmissionStatus;
    public bool Is3d { get; } = is3d;
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
