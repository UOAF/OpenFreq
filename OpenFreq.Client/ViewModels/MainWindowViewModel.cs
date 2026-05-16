using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Linq;
using System.Threading.Tasks;
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
using OpenFreq.Common;
using OpenFreq.Services.Acmi;
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

    [ObservableProperty] public partial ObservableCollection<ChannelFrequencyPeerViewModel> LobbyPeerList { get; set; } = [];
    [ObservableProperty] public partial ObservableCollection<ChannelFrequencyPeerViewModel> GamePeerList { get; set; } = [];

    public bool HasLobbyPeers => LobbyPeerList.Count > 0;
    public bool HasGamePeers => GamePeerList.Count > 0;
    public int TotalChannels => LobbyPeerList.Concat(GamePeerList).Select(f => f.FrequencyKhz).Distinct().Count();
    public int DistinctPeers => LobbyPeerList.Concat(GamePeerList).SelectMany(freq => freq.Peers).Distinct().Count();
    public int LobbyPeerCount => LobbyPeerList.SelectMany(f => f.Peers).Select(p => p.Id).Distinct().Count();
    public int GamePeerCount => GamePeerList.SelectMany(f => f.Peers).Select(p => p.Id).Distinct().Count();

    private readonly Dictionary<(string peerId, int freqKhz), bool> _peerModes = new();
    private SortedDictionary<int, List<PeerData>> _latestAllPeers = new();

    private ChannelCardGroupViewModel? _subscribedGroup;
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

        // Falcon Radio Shared Memory
        _falconRadioSharedMemoryService.ConnectionParametersChanged +=
            FalconRadioSharedMemoryServiceOnConnectionParametersChanged;
        _falconSharedMemoryService.FlyingStateChanged += OnFlyingStateChanged;
        _falconSharedMemoryService.StateChanged += OnFalconSharedMemoryStateChanged;

        // IVC Monitor
        _ivcMonitorService.IvcStatusChanged += OnIvcStatusChanged;
        // manually start it so we can be sure to get a notification if its already running
        _ivcMonitorService.Start();

        LobbyPeerList.CollectionChanged += (_, _) =>
        {
            OnPropertyChanged(nameof(HasLobbyPeers));
            OnPropertyChanged(nameof(LobbyPeerCount));
            OnPropertyChanged(nameof(DistinctPeers));
            OnPropertyChanged(nameof(TotalChannels));
        };
        GamePeerList.CollectionChanged += (_, _) =>
        {
            OnPropertyChanged(nameof(HasGamePeers));
            OnPropertyChanged(nameof(GamePeerCount));
            OnPropertyChanged(nameof(DistinctPeers));
            OnPropertyChanged(nameof(TotalChannels));
        };
        Settings.PropertyChanged += OnSettingsPropertyChanged;
        ChannelList.PropertyChanged += OnChannelListPropertyChanged;
        UpdateGroupSubscription();

        // Load config
        _ = LoadConfigurationAsync();

        _openFreqService.SetOwnPositionMode(Settings.ConnectionMode);
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
                newLobby.Add(new ChannelFrequencyPeerViewModel(frequency, lobbyPeers, JoinFrequencyFromPeerList, false, !is3dMode && !IsFrequencyAlreadyConnected(frequency)));
            if (gamePeers.Count > 0)
                newGame.Add(new ChannelFrequencyPeerViewModel(frequency, gamePeers, JoinFrequencyFromPeerList, true, is3dMode && !IsFrequencyAlreadyConnected(frequency)));
        }

        LobbyPeerList.Clear();
        foreach (var e in newLobby) LobbyPeerList.Add(e);
        GamePeerList.Clear();
        foreach (var e in newGame) GamePeerList.Add(e);
    }

    private void JoinFrequencyFromPeerList(int frequencyKhz)
    {
        var group = ChannelList.SelectedGroup;
        if (group == null || group.IsBmsGroup) return;

        var existing = group.Channels.FirstOrDefault(c => c.FrequencyKhz == frequencyKhz);
        if (existing == null)
        {
            var channel = group.CreateChannel(frequencyKhz, $"{frequencyKhz / 1000d:F3} MHz", false);
            channel.Join();
        }
        else if (existing.ConnectionStatus != Channel.ChannelConnectionStatus.Connected)
        {
            existing.Join();
        }
    }

    private bool IsFrequencyAlreadyConnected(int frequencyKhz)
    {
        var group = ChannelList.SelectedGroup;
        return group?.Channels.Any(c => c.FrequencyKhz == frequencyKhz &&
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
        if (e.PropertyName != nameof(ChannelCardListViewModel.SelectedGroup)) return;
        UpdateGroupSubscription();
        Dispatcher.UIThread.Post(UpdateCanJoin);
    }

    private void UpdateGroupSubscription()
    {
        if (_subscribedGroup != null)
        {
            _subscribedGroup.Channels.CollectionChanged -= OnSelectedGroupChannelsChanged;
            foreach (var (ch, handler) in _channelHandlers)
                ch.PropertyChanged -= handler;
            _channelHandlers.Clear();
        }

        _subscribedGroup = ChannelList.SelectedGroup;

        if (_subscribedGroup != null)
        {
            _subscribedGroup.Channels.CollectionChanged += OnSelectedGroupChannelsChanged;
            foreach (var ch in _subscribedGroup.Channels)
                SubscribeToChannelStatus(ch);
        }
    }

    private void OnSelectedGroupChannelsChanged(object? sender, NotifyCollectionChangedEventArgs e)
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
            _falconRadioSharedMemoryService.AddClientStatus(ClientStatusFlags.TryingToConnect);
            Settings.OpenFreqPassword = e.NewParameters.Password;
            Settings.OpenFreqServerAddress = e.NewParameters.Address + ":" + e.NewParameters.Port;
            _ = ConnectAsync().Wait(TimeSpan.FromSeconds(3));

            if (_openFreqService.IsConnected)
            {
                _falconRadioSharedMemoryService.AddClientStatus(ClientStatusFlags.Connected);
                Settings.Is3dMode = _falconSharedMemoryService.IsFlying ?? false;
            }
            else
            {
                _falconRadioSharedMemoryService.AddClientStatus(ClientStatusFlags.ConnectionFail);
            }

            _falconRadioSharedMemoryService.RemoveClientStatus(ClientStatusFlags.TryingToConnect);
        }

        // BMS wants us to disconnect but not exit
        if (e.OldParameters.ReadyToTransmit && !e.NewParameters.ReadyToTransmit)
        {
            _falconRadioSharedMemoryService.RemoveClientStatus(ClientStatusFlags.Connected);
            _ = DisconnectAsync().Wait(TimeSpan.FromMilliseconds(500));
        }

        // Nickname changed - this is usually triggered AFTER a successful connection
        if (e.OldParameters.Nickname != e.NewParameters.Nickname && e.NewParameters.ReadyToTransmit)
        {
            _openFreqService.UpdateDisplayNameAsync(e.NewParameters.Nickname);
        }
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
        }
        else if (state == ConnectionState.Disconnected)
        {
            ShowError("Lost connection to server");
            SettingsDrawerOpened = true;
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
            ChannelList.FalconChannelGroup?.UpdateUhfHotkey(capturedKey);
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
            ChannelList.FalconChannelGroup?.UpdateVhfHotkey(capturedKey);
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
        // Check if message contains error indicators
        if (message.Contains("error", StringComparison.OrdinalIgnoreCase) ||
            message.Contains("failed", StringComparison.OrdinalIgnoreCase))
        {
            ShowError(message);
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
                peer.IsTransmitting = (Settings.Is3dMode == e.Is3d) && e.PeerData.Status == PeerData.PeerStatus.Transmitting;
            }
        });
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

            // Load channel groups
            foreach (var channelGroupData in config.ChannelGroups)
            {
                var channelGroup = ChannelList.CreateChannelGroup(channelGroupData);
                channelGroup.Latitude = channelGroupData.Latitude;
                channelGroup.Longitude = channelGroupData.Longitude;
                channelGroup.AltitudeFeet = channelGroupData.AltitudeFt;
                // Load channels
                foreach (var channelData in channelGroupData.Channels)
                {
                    var channel =
                        channelGroup.CreateChannel(channelData.FrequencyKhz, channelData.Name ?? "");
                    channel.IsEditing = false;

                    // Set PTT hotkey
                    channel.PttHotKey = channelData.Hotkey;
                    if (channel.PttHotKey != null)
                    {
                        _hotkeyService.RegisterHotkey(IHotkeyService.HotkeyType.Ptt, channel.PttHotKey, channel.Id);
                    }
                }
            }

            ChannelList.EnsureDefaultGciGroup();
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
                ChannelGroups = ChannelList.ChannelGroups
                    .Where(cg => !cg.Equals(ChannelList.FalconChannelGroup))
                    .Select(cg =>
                    {
                        return new ChannelGroupData
                        {
                            Name = cg.Name,
                            Latitude = cg.Latitude,
                            Longitude = cg.Longitude,
                            AltitudeFt = cg.AltitudeFeet,
                            RadioStationData = cg.RadioStationData,
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
        Settings.PropertyChanged -= OnSettingsPropertyChanged;
        ChannelList.PropertyChanged -= OnChannelListPropertyChanged;
        if (_subscribedGroup != null)
        {
            _subscribedGroup.Channels.CollectionChanged -= OnSelectedGroupChannelsChanged;
            foreach (var (ch, handler) in _channelHandlers)
                ch.PropertyChanged -= handler;
            _channelHandlers.Clear();
        }

        _falconRadioSharedMemoryService.ConnectionParametersChanged -=
            FalconRadioSharedMemoryServiceOnConnectionParametersChanged;
        _falconSharedMemoryService.FlyingStateChanged -= OnFlyingStateChanged;

        await DisconnectAsync();
        ChannelList.Dispose();
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