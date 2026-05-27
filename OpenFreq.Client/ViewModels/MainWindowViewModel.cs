using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Linq;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Media;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using FalconBmsDataService.Models;
using FalconBmsDataService.Services;
using FalconRadioService.Models;
using FalconRadioService.Services;
using Material.Styles.Controls;
using Microsoft.Extensions.Logging;
using OpenFreq.Client.Models;
using OpenFreq.Common;
using OpenFreq.Services.Acmi;
using OpenFreqAudio;
using OpenFreqClient.Models;
using OpenFreqClient.Services;
using OpenFreqClient.Services.Interfaces;
using OpenFreqClient.Views.Util;

namespace OpenFreqClient.ViewModels;

public partial class MainWindowViewModel : ViewModelBase, IAsyncDisposable
{
    private readonly IOpenFreqService _openFreqService;
    private readonly IHotkeyService _hotkeyService;
    private readonly IAudioService _audioService;
    private readonly IFalconRadioSharedMemoryService _falconRadioSharedMemoryService;
    private readonly IFalconSharedMemoryService _falconSharedMemoryService;
    private readonly IAcmiClientService _acmiClientService;
    private readonly IConfigurationService _configurationService;
    private readonly IIvcMonitorService _ivcMonitorService;
    private readonly ILogger<MainWindowViewModel> _logger;

    // TODO remove when done
#if DEBUG
    [ObservableProperty] public partial bool DebugMode { get; set; } = false;
#else
    [ObservableProperty] public partial bool DebugMode { get; set; } = false;
#endif
    /*********/

    [ObservableProperty] public partial ChannelCardListViewModel ChannelList { get; set; }

    [ObservableProperty] public partial SettingsViewModel Settings { get; set; }

    [ObservableProperty]
    public partial ObservableCollection<ChannelFrequencyPeerViewModel> LobbyPeerList { get; set; } = [];

    [ObservableProperty]
    public partial ObservableCollection<ChannelFrequencyPeerViewModel> GamePeerList { get; set; } = [];

    public bool HasLobbyPeers => LobbyPeerList.Count > 0;
    public bool HasGamePeers => GamePeerList.Count > 0;
    public int TotalChannels => LobbyPeerList.Concat(GamePeerList).Select(f => f.FrequencyKhz).Distinct().Count();
    public int DistinctPeers => LobbyPeerList.Concat(GamePeerList).SelectMany(freq => freq.Peers).Distinct().Count();
    public int LobbyPeerCount => LobbyPeerList.SelectMany(f => f.Peers).Select(p => p.Id).Distinct().Count();
    public int GamePeerCount => GamePeerList.SelectMany(f => f.Peers).Select(p => p.Id).Distinct().Count();

    private readonly Dictionary<(string peerId, int freqKhz), bool> _peerModes = new();
    private SortedDictionary<int, List<PeerData>> _latestAllPeers = new();

    private LocationViewModel? _subscribedLocation;
    private readonly Dictionary<ChannelCardViewModel, PropertyChangedEventHandler> _channelHandlers = new();

    [ObservableProperty] public partial bool OpenFreqConnected { get; set; }

    [ObservableProperty] public partial bool TacviewConnected { get; set; }

    [ObservableProperty] public partial bool IsPeersPanelExpanded { get; set; } = true;

    [ObservableProperty] public partial string StatusMessage { get; set; } = "Disconnected";

    [ObservableProperty] public partial string PeerId { get; set; } = String.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(OpenFreqStatusColor))]
    public partial ConnectionState OpenFreqConnectionState { get; set; } = ConnectionState.Disconnected;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(TacviewStatusColor))]
    public partial AcmiConnectionStatus AcmiConnectionStatus { get; set; } = AcmiConnectionStatus.Disconnected;

    [ObservableProperty] public partial bool SettingsDrawerOpened { get; set; } = true;

    // Error handling properties
    [ObservableProperty] public partial bool HasError { get; set; }

    [ObservableProperty] public partial string ErrorMessage { get; set; } = "";

    [ObservableProperty] private partial ObservableCollection<string> ErrorLog { get; set; } = [];
    [ObservableProperty] public partial bool IvcWarning { get; set; }

    public string AppVersion { get; } = Program.Version;

    public Color OpenFreqStatusColor => OpenFreqConnectionState switch
    {
        ConnectionState.Connected => Color.Parse("#4CAF50"), // Material Green 500
        ConnectionState.Connecting => Color.Parse("#FF9800"), // Material Orange 500
        ConnectionState.Disconnected => Color.Parse("#9E9E9E"), // Material Grey 500
        _ => Color.Parse("#9E9E9E")
    };

    public Color TacviewStatusColor => AcmiConnectionStatus switch
    {
        AcmiConnectionStatus.Connected => Color.Parse("#4CAF50"),
        AcmiConnectionStatus.Connecting => Color.Parse("#FF9800"),
        AcmiConnectionStatus.Disconnected => Color.Parse("#9E9E9E"),
        AcmiConnectionStatus.Failed => Color.Parse("#F44336"), // Material Red 500
        _ => Color.Parse("#9E9E9E")
    };


    public ColorZoneMode AppBarColorZone =>
        (OpenFreqConnected && TacviewConnected) ? ColorZoneMode.PrimaryMid : ColorZoneMode.Accent;


    public MainWindowViewModel(
        IOpenFreqService openFreqService,
        IHotkeyService hotkeyService,
        IAudioService audioService,
        IAcmiClientService acmiClientService,
        IConfigurationService configurationService,
        ILogger<MainWindowViewModel> logger,
        ChannelCardListViewModel channelList,
        SettingsViewModel settings, IFalconRadioSharedMemoryService falconRadioSharedMemoryService,
        IFalconSharedMemoryService falconSharedMemoryService, IIvcMonitorService ivcMonitorService)
    {
        _openFreqService = openFreqService;
        _hotkeyService = hotkeyService;
        _audioService = audioService;
        _acmiClientService = acmiClientService;
        _configurationService = configurationService;
        _logger = logger;
        ChannelList = channelList;
        Settings = settings;
        _falconRadioSharedMemoryService = falconRadioSharedMemoryService;
        _falconSharedMemoryService = falconSharedMemoryService;
        _ivcMonitorService = ivcMonitorService;

        // Subscribe to service events
        _openFreqService.ConnectionStateChanged += OnConnectionStateChanged;
        _openFreqService.StatusMessageReceived += OnStatusMessageReceived;
        _openFreqService.PeerActivityReceived += OnPeerActivityReceived;
        _openFreqService.AllPeersStatusChanged += OnAllPeersChanged;
        _openFreqService.FrequencyTransmissionStatusChanged += OnFrequencyTransmissionStatusChanged;
        _openFreqService.AudioPlaybackErrorOccurred += OnAudioErrorOccurred;
        _audioService.AudioDeviceErrorOccurred += OnAudioErrorOccurred;

        // Falcon Radio Shared Memory
        _falconRadioSharedMemoryService.ConnectionParametersChanged +=
            FalconRadioSharedMemoryServiceOnConnectionParametersChanged;
        _falconRadioSharedMemoryService.LogbookNameChanged += OnLogbookNameChanged;
        _falconSharedMemoryService.FlyingStateChanged += OnFlyingStateChanged;
        _falconSharedMemoryService.StateChanged += OnFalconSharedMemoryStateChanged;
        _falconSharedMemoryService.AircraftInfoChanged += OnAircraftInfoChanged;

        // IVC Monitor
        _ivcMonitorService.IvcStatusChanged += OnIvcStatusChanged;
        // manually start it so we can be sure to get a notification if its already running
        _ivcMonitorService.Start();

        LobbyPeerList.CollectionChanged += OnLobbyPeerListChanged;
        GamePeerList.CollectionChanged += OnGamePeerListChanged;
        Settings.PropertyChanged += OnSettingsPropertyChanged;
        ChannelList.PropertyChanged += OnChannelListPropertyChanged;
        UpdateLocationSubscription();

        // Load config
        _ = LoadConfigurationAsync();

        _openFreqService.SetOwnPositionMode(Settings.ConnectionMode);
    }

    private void OnAircraftInfoChanged(object? sender, AircraftInfoChangedEventArgs e)
    {
        _logger.LogDebug("BMS Aircraft info changed: {nctr} to preset {name}", e.AcNCTR, e.AcName);
        // If we are in 3d, the SHMEM AcName and AcNCTR fields are now populated.
        
        var preset = RadioStationPresets.GetPresetByBmsAircraftNctr(_falconSharedMemoryService.AcNCTR);
        foreach (var channelGroup in ChannelList.Locations)
        {
            channelGroup.RadioStationData.Preset = preset;
            _logger.LogDebug("BMS Aircraft info changed: switching ChannelGroup {channelGroup} to preset {preset}", channelGroup.RadioStationData, channelGroup.RadioStationData.Preset.Name);
        }
    }

    private void OnAllPeersChanged(object? sender, AllPeersStatusEventArgs e)
    {
        _latestAllPeers = e.AllPeers;
        // Sync _peerModes from the authoritative server snapshot so late-joining
        // clients get the correct lobby/game section for all existing peers.
        foreach (var (frequency, peers) in e.AllPeers)
        foreach (var peer in peers)
            _peerModes[(peer.Id, frequency)] = peer.Is3d;
        Dispatcher.UIThread.Post(RebuildPeerLists);
    }

    private void RebuildPeerLists()
    {
        bool is3dMode = Settings.Is3dMode;
        var newLobby = new List<ChannelFrequencyPeerViewModel>();
        var newGame = new List<ChannelFrequencyPeerViewModel>();

        foreach (var (frequency, peerDatas) in _latestAllPeers)
        {
            ObservableCollection<ChannelPeerViewModel> lobbyPeers = [];
            ObservableCollection<ChannelPeerViewModel> gamePeers = [];

            foreach (var peer in peerDatas)
            {
                bool is3d = _peerModes.TryGetValue((peer.Id, frequency), out var mode) && mode;
                bool isTransmitting = (is3dMode == is3d) && peer.Status == PeerData.PeerStatus.Transmitting;
                var vm = new ChannelPeerViewModel(peer.Id, peer.Name ?? string.Empty, isTransmitting,
                    peer.Id == _openFreqService.PeerId);
                if (is3d) gamePeers.Add(vm);
                else lobbyPeers.Add(vm);
            }

            if (lobbyPeers.Count > 0)
                newLobby.Add(new ChannelFrequencyPeerViewModel(frequency, lobbyPeers, JoinFrequencyFromPeerList, false,
                    !is3dMode && !IsFrequencyAlreadyConnected(frequency)));
            if (gamePeers.Count > 0)
                newGame.Add(new ChannelFrequencyPeerViewModel(frequency, gamePeers, JoinFrequencyFromPeerList, true,
                    is3dMode && !IsFrequencyAlreadyConnected(frequency)));
        }

        LobbyPeerList.Clear();
        foreach (var e in newLobby) LobbyPeerList.Add(e);
        GamePeerList.Clear();
        foreach (var e in newGame) GamePeerList.Add(e);
    }

    private void JoinFrequencyFromPeerList(int frequencyKhz)
    {
        var location = ChannelList.SelectedLocation;
        if (location == null || location.IsBmsLocation) return;

        var existing = location.Channels.FirstOrDefault(c => c.FrequencyKhz == frequencyKhz);
        if (existing == null)
        {
            var channel = location.CreateChannel(frequencyKhz, $"{frequencyKhz / 1000d:F3} MHz", false);
            channel.Join();
        }
        else if (existing.ConnectionStatus != Channel.ChannelConnectionStatus.Connected)
        {
            existing.Join();
        }
    }

    private bool IsFrequencyAlreadyConnected(int frequencyKhz)
    {
        var location = ChannelList.SelectedLocation;
        return location?.Channels.Any(c => c.FrequencyKhz == frequencyKhz &&
                                        c.ConnectionStatus == Channel.ChannelConnectionStatus.Connected) == true;
    }

    private void UpdateCanJoin()
    {
        bool is3dMode = Settings.Is3dMode;
        foreach (var entry in LobbyPeerList)
            entry.CanJoin = !is3dMode && !IsFrequencyAlreadyConnected(entry.FrequencyKhz);
        foreach (var entry in GamePeerList)
            entry.CanJoin = is3dMode && !IsFrequencyAlreadyConnected(entry.FrequencyKhz);
    }

    private void OnChannelListPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(ChannelCardListViewModel.SelectedLocation)) return;
        UpdateLocationSubscription();
        Dispatcher.UIThread.Post(UpdateCanJoin);
    }

    private void UpdateLocationSubscription()
    {
        if (_subscribedLocation != null)
        {
            _subscribedLocation.Channels.CollectionChanged -= OnSelectedLocationChannelsChanged;
            foreach (var (ch, handler) in _channelHandlers)
                ch.PropertyChanged -= handler;
            _channelHandlers.Clear();
        }

        _subscribedLocation = ChannelList.SelectedLocation;

        if (_subscribedLocation != null)
        {
            _subscribedLocation.Channels.CollectionChanged += OnSelectedLocationChannelsChanged;
            foreach (var ch in _subscribedLocation.Channels)
                SubscribeToChannelStatus(ch);
        }
    }

    private void OnSelectedLocationChannelsChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.NewItems != null)
            foreach (ChannelCardViewModel ch in e.NewItems)
                SubscribeToChannelStatus(ch);
        if (e.OldItems != null)
            foreach (ChannelCardViewModel ch in e.OldItems)
                UnsubscribeFromChannelStatus(ch);
        Dispatcher.UIThread.Post(UpdateCanJoin);
    }

    private void SubscribeToChannelStatus(ChannelCardViewModel ch)
    {
        PropertyChangedEventHandler handler = (_, args) =>
        {
            if (args.PropertyName == nameof(ChannelCardViewModel.ConnectionStatus))
                Dispatcher.UIThread.Post(UpdateCanJoin);
        };
        _channelHandlers[ch] = handler;
        ch.PropertyChanged += handler;
    }

    private void UnsubscribeFromChannelStatus(ChannelCardViewModel ch)
    {
        if (!_channelHandlers.TryGetValue(ch, out var handler)) return;
        ch.PropertyChanged -= handler;
        _channelHandlers.Remove(ch);
    }

    private async void OnFalconSharedMemoryStateChanged(object? sender, ServiceStateChangedEventArgs e)
    {
        if (e.OldState != ServiceState.Connected) return;
        if (Settings.ConnectionMode != IOpenFreqService.Mode.BMS) return;
        Settings.Is3dMode = _falconSharedMemoryService.IsFlying ?? false;
        await DisconnectAsync();
    }

    private async void OnIvcStatusChanged(object? sender, IvcStatusChangedEventArgs ivcStatusChangedEventArgs)
    {
        try
        {
            await Dispatcher.UIThread.InvokeAsync(async () =>
            {
                IvcWarning = ivcStatusChangedEventArgs.IsRunning;

                if (!IvcWarning) return;

                if (!await ConfirmationDialogService.ShowAsync(
                        title: "IVC Client detected",
                        message: "The BMS IVC Client seems to be running.\n" +
                                 "OpenFreq will not work in BMS mode.\n" +
                                 "\n" +
                                 "Kill the IVC process?",
                        cancelText: "Cancel",
                        confirmText: "Kill IVC")) return;

                try
                {
                    _ivcMonitorService.KillIvc();
                }
                catch (Exception exception)
                {
                    _logger.LogError("Failed to kill IVC: {Exception}", exception.ToString());
                }
            });
        }
        catch (Exception e)
        {
            _logger.LogError("{ToString}", e.ToString());
        }
    }

    private void OnFlyingStateChanged(object? sender, FlyingStateChangedEventArgs e)
    {
        Settings.Is3dMode = e.NewFlyingState;
    }

    private void FalconRadioSharedMemoryServiceOnConnectionParametersChanged(object? sender,
        ConnectionParametersChangedEventArgs e)
    {
        _logger.LogDebug($"FalconRadioSharedMemoryServiceOnConnectionParametersChanged: {e.NewParameters}");

        // BMS wants us to close the client
        if (e.NewParameters.TerminateClient)
        {
            _falconRadioSharedMemoryService.AddClientStatus(ClientStatusFlags.ExitReceived);
            _falconRadioSharedMemoryService.RemoveClientStatus(ClientStatusFlags.Connected);
            _ = DisconnectAsync().Wait(TimeSpan.FromMilliseconds(500));
            return;
        }

        // BMS wants us to connect
        if (e.NewParameters.AttemptingToConnect)
        {
            // Clear ALL error flags (ConnectionFail, BadPassword, HostUnknown, etc.).
            // Clearing only ConnectionFail leaves e.g. BadPassword set from a previous attempt.
            // BMS VoiceDoLogic checks ClientHasAnError() on every frame: any stale error flag
            // causes SetBlockingError(true) → VoiceDoLogic returns immediately → SetChannelsFor3d()
            // is never called → RCC is never updated → OpenFreq reads stale frequencies/power states.
            _falconRadioSharedMemoryService.RemoveClientStatus(ClientStatusFlags.ErrorMask);
            _falconRadioSharedMemoryService.AddClientStatus(ClientStatusFlags.ClientActive);
            _falconRadioSharedMemoryService.AddClientStatus(ClientStatusFlags.TryingToConnect);
            Settings.OpenFreqPassword = e.NewParameters.Password;
            Settings.OpenFreqServerAddress = e.NewParameters.Address + ":" + e.NewParameters.Port;
            var failFlag = ClientStatusFlags.ConnectionFail;
            try { ConnectWithTimeoutAsync(TimeSpan.FromSeconds(2)).Wait(TimeSpan.FromSeconds(2.5)); }
            catch (AggregateException ae)
            {
                var flat = ae.Flatten();
                if (flat.InnerExceptions.Any(ex => ex is AuthenticationException))
                    failFlag = ClientStatusFlags.BadPassword;
                else if (flat.InnerExceptions.Any(IsHostUnknownException))
                    failFlag = ClientStatusFlags.HostUnknown;
            }

            if (_openFreqService.IsConnected)
            {
                // Ensure all error flags are gone before setting Connected so BMS never
                // sees ClientHasAnError()=true alongside Connected in the same frame.
                _falconRadioSharedMemoryService.RemoveClientStatus(ClientStatusFlags.ErrorMask);
                _falconRadioSharedMemoryService.AddClientStatus(ClientStatusFlags.Connected);
                Settings.Is3dMode = _falconSharedMemoryService.IsFlying ?? false;
                // Prefer Nickname (set by BMS at StartExternalVoice time = LogBook.Callsign()).
                // LogbookName is from the Telemetry struct which BMS initialises to "Wot Pilot?!"
                // and only overwrites after ClientReady() — i.e. after this connection completes.
                var bestName = !string.IsNullOrEmpty(e.NewParameters.Nickname)
                    ? e.NewParameters.Nickname
                    : _falconRadioSharedMemoryService.LogbookName;
                if (!string.IsNullOrEmpty(bestName))
                    _openFreqService.UpdateDisplayNameAsync(bestName);
            }
            else
            {
                _falconRadioSharedMemoryService.AddClientStatus(failFlag);
                _falconRadioSharedMemoryService.RemoveClientStatus(ClientStatusFlags.ClientActive);
            }

            _falconRadioSharedMemoryService.RemoveClientStatus(ClientStatusFlags.TryingToConnect);
        }

        // BMS wants us to disconnect but not exit
        if (e.OldParameters.ReadyToTransmit && !e.NewParameters.ReadyToTransmit)
        {
            _falconRadioSharedMemoryService.RemoveClientStatus(ClientStatusFlags.Connected);
            _ = DisconnectAsync().Wait(TimeSpan.FromMilliseconds(500));
        }

        // Update display name when ReadyToTransmit becomes true or nickname changes mid-session.
        // Use LogbookName as fallback if Nickname is empty (same priority order as AttemptingToConnect).
        // Never fall through to Settings.DisplayName — in BMS mode that is the GCI name.
        if (e.NewParameters.ReadyToTransmit &&
            (!e.OldParameters.ReadyToTransmit || e.OldParameters.Nickname != e.NewParameters.Nickname))
        {
            var name = !string.IsNullOrEmpty(e.NewParameters.Nickname)
                ? e.NewParameters.Nickname
                : _falconRadioSharedMemoryService.LogbookName;
            if (!string.IsNullOrEmpty(name))
                _openFreqService.UpdateDisplayNameAsync(name);
        }
    }

    private void OnLogbookNameChanged(object? sender, LogbookNameChangedEventArgs e)
    {
        // BMS initialises Telemetry::m_logbookName to "Wot Pilot?!" before the session
        // is established; the real callsign is only written after ClientReady().
        // Ignore the sentinel so we never push the placeholder as a display name.
        const string BmsSentinel = "Wot Pilot?!";
        if (!string.IsNullOrEmpty(e.NewName) && e.NewName != BmsSentinel)
            _openFreqService.UpdateDisplayNameAsync(e.NewName);
    }


    partial void OnOpenFreqConnectedChanged(bool value)
    {
        OnPropertyChanged(nameof(AppBarColorZone));
    }

    [RelayCommand]
    private async Task ConnectAsync()
    {
        try
        {
            // Gracefully handle if already connected
            if (_openFreqService.IsConnected)
            {
                return;
            }

            // Validate settings before connecting
            if (string.IsNullOrWhiteSpace(Settings.OpenFreqServerAddress))
            {
                ShowError("Server address is not configured. Please check Settings.");
                return;
            }

            // Initialize service with settings
            await _openFreqService.Initialize(Settings.GetSettings(),
                _audioService.GetRecordingBassIndex(Settings.RecordingDeviceIndex),
                _audioService.GetPlaybackBassIndex(Settings.PlaybackDeviceIndex));

            // Connect to server (channels will auto-join when authenticated)
            await _openFreqService.ConnectAsync();

            if (Settings.ConnectionMode == IOpenFreqService.Mode.GCI)
            {
                _openFreqService.LoadHeightmap(Settings.HeightmapPath);

                if (!string.IsNullOrEmpty(Settings.TacviewServerAddress))
                {
                    _acmiClientService.ConnectionStatusChanged += OnTacviewConnectionStatusChanged;
                    await _acmiClientService.ConnectAsync(Settings.TacviewServerAddress,
                        Settings.TacviewServerPassword);
                }
            }

            ClearError();
        }
        catch (InvalidOperationException ex)
        {
            ShowError($"Configuration error: {ex.Message}");
        }
        catch (TimeoutException)
        {
            ShowError("Connection timeout. Please check server address and network connection.");
        }
        catch (Exception ex)
        {
            ShowError($"Connection failed: {ex.Message}");
        }
    }

    private static bool IsHostUnknownException(Exception ex) => ex switch
    {
        SocketException se => se.SocketErrorCode is SocketError.HostNotFound
            or SocketError.HostUnreachable
            or SocketError.NetworkUnreachable,
        _ => ex.InnerException != null && IsHostUnknownException(ex.InnerException)
    };

    private async Task ConnectWithTimeoutAsync(TimeSpan connectTimeout)
    {
        if (_openFreqService.IsConnected) return;
        if (string.IsNullOrWhiteSpace(Settings.OpenFreqServerAddress)) return;

        await _openFreqService.Initialize(Settings.GetSettings(),
            _audioService.GetRecordingBassIndex(Settings.RecordingDeviceIndex),
            _audioService.GetPlaybackBassIndex(Settings.PlaybackDeviceIndex));

        await _openFreqService.ConnectAsync(connectTimeout);
    }

    [RelayCommand]
    private async Task DisconnectAsync()
    {
        try
        {
            // Gracefully handle if already disconnected
            if (!_openFreqService.IsConnected)
            {
                return;
            }

            await _openFreqService.DisconnectAsync();

            if (_acmiClientService.Status == AcmiConnectionStatus.Connected ||
                _acmiClientService.Status == AcmiConnectionStatus.Connecting)
            {
                await _acmiClientService.DisconnectAsync();
            }

            _acmiClientService.CancelConnectionAttempts();
            ClearError();
        }
        catch (Exception ex)
        {
            ShowError($"Disconnect failed: {ex.Message}");
        }
    }

    private void OnTacviewConnectionStatusChanged(object? sender, AcmiConnectionEventArgs e)
    {
        AcmiConnectionStatus = e.Status;
    }


    [RelayCommand]
    private void ClearError()
    {
        HasError = false;
        ErrorMessage = "";
    }

    private void ShowError(string message)
    {
        _logger.LogError(message);
        HasError = true;
        ErrorMessage = message;
        StatusMessage = $"⚠️ {message}";

        // Add to error log with timestamp
        var logEntry = $"[{DateTime.Now:HH:mm:ss}] {message}";
        ErrorLog.Insert(0, logEntry);

        // Keep only last 50 errors
        while (ErrorLog.Count > 50)
        {
            ErrorLog.RemoveAt(ErrorLog.Count - 1);
        }
    }

    /// <summary>
    /// Shared handler for audio errors from both <see cref="IOpenFreqService.AudioPlaybackErrorOccurred"/>
    /// (RadioPlayback / BASS) and <see cref="IAudioService.AudioDeviceErrorOccurred"/> (device monitoring).
    /// Marshals to the UI thread — callers may fire from background threads.
    /// </summary>
    private void OnAudioErrorOccurred(object? sender, string message)
    {
        Dispatcher.UIThread.Post(() => ShowError(message));
    }

    // Service event handlers
    private void OnConnectionStateChanged(object? sender, ConnectionState state)
    {
        OpenFreqConnectionState = state;
        OpenFreqConnected = state == ConnectionState.Connected || state == ConnectionState.Authenticated;
        StatusMessage = state switch
        {
            ConnectionState.Disconnected => "Disconnected",
            ConnectionState.Connecting => "Connecting...",
            ConnectionState.Connected => "Connected",
            ConnectionState.Authenticated => "Authenticated",
            _ => "Unknown"
        };

        if (state == ConnectionState.Authenticated)
        {
            PeerId = _openFreqService.PeerId ?? "";
            ClearError();
            SettingsDrawerOpened = false;
            
            if (Settings is { ModeIsGci: false, MinimizeOnConnect: true })
            {
                Dispatcher.UIThread.Post(() =>
                {
                    var window = ((IClassicDesktopStyleApplicationLifetime)Application.Current!.ApplicationLifetime!)
                        .MainWindow!;
                    window.WindowState = WindowState.Minimized;
                });
            }
        }
        else if (state == ConnectionState.Disconnected)
        {
            // This handler fires from the WebSocket receive background thread.
            // ShowError mutates ErrorLog (ObservableCollection) which must happen
            // on the UI thread — consolidate all UI work into one Post.
            Dispatcher.UIThread.Post(() =>
            {
                // Only show the generic disconnect message when no more-specific error
                // (e.g. bad password, auth failure) is already being displayed.
                // Auth-failure errors are set via OnStatusMessageReceived before the
                // WebSocket close event arrives (~100 ms earlier in practice), so
                // HasError is already true when we get here and the overwrite is skipped.
                if (!HasError)
                    ShowError("Lost connection to server");
                SettingsDrawerOpened = true;

                var window = ((IClassicDesktopStyleApplicationLifetime)Application.Current!.ApplicationLifetime!)
                    .MainWindow!;
                if (window.WindowState == WindowState.Minimized)
                    window.WindowState = WindowState.Normal;
            });
        }
    }

    [RelayCommand]
    private void ToggleSettingsDrawer()
    {
        SettingsDrawerOpened = !SettingsDrawerOpened;
    }

    [RelayCommand]
    private async Task BeginCaptureUhfSquelchHotkeyAsync()
    {
        IsCapturingHotkey = true;
        try
        {
            var capturedKey = await _hotkeyService.CaptureNextHotkeyAsync();
            Settings.BmsUhfSquelchHotkey = capturedKey;
            ChannelList.FalconLocation?.UpdateUhfHotkey(capturedKey);
        }
        catch (OperationCanceledException)
        {
            // Capture was cancelled
        }
        finally
        {
            IsCapturingHotkey = false;
        }
    }

    [RelayCommand]
    private async Task BeginCaptureVhfSquelchHotkeyAsync()
    {
        IsCapturingHotkey = true;
        try
        {
            var capturedKey = await _hotkeyService.CaptureNextHotkeyAsync();
            Settings.BmsVhfSquelchHotkey = capturedKey;
            ChannelList.FalconLocation?.UpdateVhfHotkey(capturedKey);
        }
        catch (OperationCanceledException)
        {
            // Capture was cancelled
        }
        finally
        {
            IsCapturingHotkey = false;
        }
    }

    public bool IsCapturingHotkey { get; set; }


    private void OnStatusMessageReceived(object? sender, string message)
    {
        // Fires from background threads (WebSocket receive, audio callbacks).
        // ShowError mutates ErrorLog (ObservableCollection) — must be on UI thread.
        if (message.Contains("error", StringComparison.OrdinalIgnoreCase) ||
            message.Contains("failed", StringComparison.OrdinalIgnoreCase))
        {
            Dispatcher.UIThread.Post(() => ShowError(message));
        }
        else
        {
            StatusMessage = message;
            ClearError();
        }
    }

    [RelayCommand]
    private void TogglePeersPanel()
    {
        IsPeersPanelExpanded = !IsPeersPanelExpanded;
    }

    private void OnPeerActivityReceived(object? sender, PeerActivityEventArgs e)
    {
        bool? prevMode = _peerModes.TryGetValue((e.PeerData.Id, e.FrequencyKhz), out var m) ? m : null;
        _peerModes[(e.PeerData.Id, e.FrequencyKhz)] = e.Is3d;

        // Update status in snapshot so a subsequent RebuildPeerLists call uses fresh status
        if (_latestAllPeers.TryGetValue(e.FrequencyKhz, out var peerList))
        {
            var snapPeer = peerList.FirstOrDefault(p => p.Id == e.PeerData.Id);
            if (snapPeer != null) snapPeer.Status = e.PeerData.Status;
        }

        bool modeChanged = prevMode == null ? e.Is3d : prevMode != e.Is3d;

        Dispatcher.UIThread.Post(() =>
        {
            if (modeChanged)
            {
                RebuildPeerLists();
                return;
            }

            foreach (var peer in LobbyPeerList.Concat(GamePeerList)
                         .Where(f => f.FrequencyKhz == e.FrequencyKhz)
                         .SelectMany(f => f.Peers)
                         .Where(p => p.Id == e.PeerData.Id))
            {
                peer.IsTransmitting = (Settings.Is3dMode == e.Is3d) &&
                                      e.PeerData.Status == PeerData.PeerStatus.Transmitting;
            }
        });
    }

    private void OnLobbyPeerListChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        OnPropertyChanged(nameof(HasLobbyPeers));
        OnPropertyChanged(nameof(LobbyPeerCount));
        OnPropertyChanged(nameof(DistinctPeers));
        OnPropertyChanged(nameof(TotalChannels));
    }

    private void OnGamePeerListChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        OnPropertyChanged(nameof(HasGamePeers));
        OnPropertyChanged(nameof(GamePeerCount));
        OnPropertyChanged(nameof(DistinctPeers));
        OnPropertyChanged(nameof(TotalChannels));
    }

    private void OnSettingsPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(Settings.Is3dMode)) return;
        Dispatcher.UIThread.Post(UpdateModeDependent);
    }

    private void UpdateModeDependent()
    {
        bool is3d = Settings.Is3dMode;
        string? ownId = _openFreqService.PeerId;

        // Stamp own player's mode into _peerModes for every frequency they appear on,
        // so RebuildPeerLists places them in the correct section.
        if (!string.IsNullOrEmpty(ownId))
        {
            foreach (var frequency in _latestAllPeers.Keys)
                _peerModes[(ownId, frequency)] = is3d;
        }

        RebuildPeerLists();
    }

    private void OnFrequencyTransmissionStatusChanged(object? sender, FrequencyTransmissionStatusEventArgs e)
    {
        // Only interested in channels we are transmitting or idling in
        if (e.TransmissionStatus == Channel.ChannelTransmissionStatus.Receiving) return;

        Dispatcher.UIThread.Post(() =>
        {
            foreach (var peer in LobbyPeerList.Concat(GamePeerList)
                         .Where(f => f.FrequencyKhz == e.FrequencyKhz)
                         .SelectMany(f => f.Peers)
                         .Where(p => p.Id == _openFreqService.PeerId))
            {
                peer.IsTransmitting = e.TransmissionStatus == Channel.ChannelTransmissionStatus.Transmitting;
            }
        });
    }

    private async Task LoadConfigurationAsync()
    {
        try
        {
            var config = await _configurationService.LoadConfigurationAsync();

            // Load settings
            Settings.OpenFreqServerAddress = config.Settings.OpenFreqServerAddress;
            Settings.OpenFreqPassword = config.Settings.OpenFreqPassword;
            Settings.ConnectionMode = config.Settings.OwnPositionMode;
            Settings.DisplayName = config.Settings.DisplayName;
            Settings.InputDeviceName = config.Settings.InputDeviceName;
            Settings.OutputDeviceName = config.Settings.OutputDeviceName;
            Settings.HeightmapPath = config.Settings.HeightmapPath;

            Settings.BmsVhfSquelchHotkey = config.Settings.BmsSquelchVhfHotkey;
            Settings.BmsUhfSquelchHotkey = config.Settings.BmsSquelchUhfHotkey;

            // Load audio settings
            Settings.LoadFromSettings(config.Settings);

            // Load locations
            foreach (var locationData in config.Locations)
            {
                var location = ChannelList.CreateLocation(locationData);
                location.Latitude = locationData.Latitude;
                location.Longitude = locationData.Longitude;
                location.AltitudeFeet = locationData.AltitudeFt;
                // Load channels
                foreach (var channelData in locationData.Channels)
                {
                    var channel =
                        location.CreateChannel(channelData.FrequencyKhz, channelData.Name ?? "");
                    channel.IsEditing = false;

                    // Set PTT hotkey
                    channel.PttHotKey = channelData.Hotkey;
                    if (channel.PttHotKey != null)
                    {
                        _hotkeyService.RegisterHotkey(IHotkeyService.HotkeyType.Ptt, channel.PttHotKey, channel.Id);
                    }
                }
            }

            ChannelList.EnsureDefaultGciLocation();
        }
        catch (Exception ex)
        {
            ShowError($"Failed to load configuration: {ex.Message}");
        }
    }

    public async Task SaveConfigurationAsync()
    {
        try
        {
            var config = new AppConfiguration
            {
                Settings = Settings.GetSettings(),
                Locations = ChannelList.Locations
                    .Where(cg => !cg.Equals(ChannelList.FalconLocation))
                    .Select(cg =>
                    {
                        return new LocationData
                        {
                            Name = cg.Name,
                            Latitude = cg.Latitude,
                            Longitude = cg.Longitude,
                            AltitudeFt = cg.AltitudeFeet,
                            RadioStationData = new RadioStationData
                            {
                                Type = RadioStationData.RadioStationType.STATIONARY,
                                Preset = cg.RadioStationData.Preset,
                                Ppm = cg.RadioStationData.Ppm,
                            },
                            Channels = cg.Channels.Select(c => new ChannelData
                            {
                                Name = c.Name,
                                FrequencyKhz = c.FrequencyKhz,
                                Hotkey = c.PttHotKey,
                            }).ToList()
                        };
                    }).ToList()
            };

            await _configurationService.SaveConfigurationAsync(config);
        }
        catch (Exception ex)
        {
            ShowError($"Failed to save configuration: {ex.Message}");
        }
    }


    [RelayCommand]
    private Task Debug()
    {
        return Task.CompletedTask;
        /*
        Settings.OpenFreqServerAddress = "127.0.0.1";
        await ConnectAsync();
        */
    }

    public async ValueTask DisposeAsync()
    {
        await SaveConfigurationAsync();

        _openFreqService.ConnectionStateChanged -= OnConnectionStateChanged;
        _openFreqService.StatusMessageReceived -= OnStatusMessageReceived;
        _openFreqService.PeerActivityReceived -= OnPeerActivityReceived;
        _openFreqService.AllPeersStatusChanged -= OnAllPeersChanged;
        _openFreqService.FrequencyTransmissionStatusChanged -= OnFrequencyTransmissionStatusChanged;
        _openFreqService.AudioPlaybackErrorOccurred -= OnAudioErrorOccurred;
        _audioService.AudioDeviceErrorOccurred -= OnAudioErrorOccurred;
        _ivcMonitorService.IvcStatusChanged -= OnIvcStatusChanged;
        _falconSharedMemoryService.StateChanged -= OnFalconSharedMemoryStateChanged;
        LobbyPeerList.CollectionChanged -= OnLobbyPeerListChanged;
        GamePeerList.CollectionChanged -= OnGamePeerListChanged;
        Settings.PropertyChanged -= OnSettingsPropertyChanged;
        ChannelList.PropertyChanged -= OnChannelListPropertyChanged;
        if (_subscribedLocation != null)
        {
            _subscribedLocation.Channels.CollectionChanged -= OnSelectedLocationChannelsChanged;
            foreach (var (ch, handler) in _channelHandlers)
                ch.PropertyChanged -= handler;
            _channelHandlers.Clear();
        }

        _falconRadioSharedMemoryService.ConnectionParametersChanged -=
            FalconRadioSharedMemoryServiceOnConnectionParametersChanged;
        _falconRadioSharedMemoryService.LogbookNameChanged -= OnLogbookNameChanged;
        _falconSharedMemoryService.FlyingStateChanged -= OnFlyingStateChanged;
        _falconSharedMemoryService.AircraftInfoChanged -= OnAircraftInfoChanged;
        
        await DisconnectAsync();
        ChannelList.Dispose();
        Settings.Dispose();
        _openFreqService.Dispose();
        _hotkeyService.Dispose();
        _acmiClientService.Dispose();
        _configurationService.Dispose();

        _falconSharedMemoryService.Dispose();
        _falconRadioSharedMemoryService.Dispose();
        await _audioService.DisposeAsync();
        await _ivcMonitorService.DisposeAsync();
    }
}