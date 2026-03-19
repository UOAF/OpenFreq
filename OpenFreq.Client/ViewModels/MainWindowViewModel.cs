using System;
using System.Collections.ObjectModel;
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
using SharpHook.Data;

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
    [NotifyPropertyChangedFor(nameof(TotalChannels), nameof(DistinctPeers))]
    public partial ObservableCollection<ChannelFrequencyPeerViewModel> PeerList { get; set; } = [];

    public int TotalChannels => PeerList.Count;
    public int DistinctPeers => PeerList.SelectMany(freq => freq.Peers).Distinct().Count();

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

        // Falcon Radio Shared Memory
        _falconRadioSharedMemoryService.ConnectionParametersChanged +=
            FalconRadioSharedMemoryServiceOnConnectionParametersChanged;
        _falconSharedMemoryService.FlyingStateChanged += OnFlyingStateChanged;
        _falconSharedMemoryService.StateChanged += OnFalconSharedMemoryStateChanged;

        // IVC Monitor
        _ivcMonitorService.IvcStatusChanged += OnIvcStatusChanged;
        // manually start it so we can be sure to get a notification if its already running
        _ivcMonitorService.Start();

        // Load config
        _ = LoadConfigurationAsync();

        _openFreqService.SetOwnPositionMode(Settings.ConnectionMode);
    }

    private void OnAllPeersChanged(object? sender, AllPeersStatusEventArgs e)
    {
        Dispatcher.UIThread.Post(() =>
        {
            PeerList.Clear();
            foreach (var frequency in e.AllPeers.Keys)
            {
                ObservableCollection<ChannelPeerViewModel> peers = [];
                foreach (var peer in e.AllPeers[frequency])
                {
                    peers.Add(new ChannelPeerViewModel(peer.Id, peer.Name,
                        peer.Status == PeerData.PeerStatus.Transmitting, peer.Id == _openFreqService.PeerId));
                }

                PeerList.Add(new ChannelFrequencyPeerViewModel(frequency, peers));
            }

            OnPropertyChanged(nameof(DistinctPeers));
        });
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
            await _openFreqService.Initialize(Settings.GetSettings(), Settings.RecordingDeviceIndex,
                Settings.PlaybackDeviceIndex);

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
        else if (state == ConnectionState.Disconnected && OpenFreqConnected)
        {
            ShowError("Lost connection to server");
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
            var capturedKey = await _hotkeyService.CaptureNextKeyAsync();
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
            var capturedKey = await _hotkeyService.CaptureNextKeyAsync();
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
        // This isn't ideal performance-wise, but we don't have too many peers and there is no ObservableDictionary
        foreach (var peer in PeerList
                     .Where(f => f.FrequencyKhz == e.FrequencyKhz)
                     .SelectMany(f => f.Peers)
                     .Where(p => p.Id == e.PeerData.Id))
        {
            // Only show transmitting if the client is in the same mode as we are
            peer.IsTransmitting =
                (Settings.Is3dMode == e.Is3d) && e.PeerData.Status == PeerData.PeerStatus.Transmitting;
        }
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

            if (Enum.TryParse<KeyCode>(config.Settings.BmsSquelchVhfHotkeyCode, out var vhfSquelchHotkey))
            {
                Settings.BmsVhfSquelchHotkey = vhfSquelchHotkey;
            }

            if (Enum.TryParse<KeyCode>(config.Settings.BmsSquelchUhfHotkeyCode, out var uhfSquelchHotkey))
            {
                Settings.BmsUhfSquelchHotkey = uhfSquelchHotkey;
            }

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

                    // Parse and set PTT hotkey
                    if (Enum.TryParse<KeyCode>(channelData.HotkeyCode, out var keyCode))
                    {
                        channel.PttHotKey = keyCode;
                        if (keyCode != KeyCode.VcUndefined)
                        {
                            _hotkeyService.RegisterHotkey(IHotkeyService.HotkeyType.Ptt, keyCode, channel.Id);
                        }
                    }
                }
            }
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
                                HotkeyCode = c.PttHotKey.ToString(),
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