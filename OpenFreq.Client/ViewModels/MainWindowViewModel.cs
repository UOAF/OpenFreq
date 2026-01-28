using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
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
using SharpHook.Data;

namespace OpenFreqClient.ViewModels;

public partial class MainWindowViewModel : ViewModelBase, IDisposable
{
    private readonly IOpenFreqService _openFreqService;
    private readonly IHotkeyService _hotkeyService;
    private readonly IAudioService _audioService;
    private readonly IFalconRadioSharedMemoryService _falconRadioSharedMemoryService;
    private readonly IFalconSharedMemoryService _falconSharedMemoryService;
    private readonly IAcmiClientService _acmiClientService;
    private readonly IConfigurationService _configurationService;

    private readonly ILogger<MainWindowViewModel> _logger;

    // TODO remove when done
#if DEBUG
    [ObservableProperty] private bool _debugMode = true;
#else
    [ObservableProperty] private bool _debugMode = false;
#endif
    /*********/


    [ObservableProperty] private ChannelCardListViewModel _channelList;
    [ObservableProperty] private SettingsViewModel _settings;

    [ObservableProperty] private bool _openFreqConnected;
    [ObservableProperty] private bool _tacviewConnected;

    [ObservableProperty] private string _statusMessage = "Disconnected";
    [ObservableProperty] private string _peerId = String.Empty;

    [ObservableProperty] private string _connectionStatusString = String.Empty;


    // Error handling properties
    [ObservableProperty] private bool _hasError;
    [ObservableProperty] private string _errorMessage = "";
    [ObservableProperty] private ObservableCollection<string> _errorLog = new();
    [ObservableProperty] public partial bool Is3dMode { get; set; }

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
        IFalconSharedMemoryService falconSharedMemoryService)
    {
        _openFreqService = openFreqService;
        _hotkeyService = hotkeyService;
        _audioService = audioService;
        _acmiClientService = acmiClientService;
        _configurationService = configurationService;
        _logger = logger;
        _channelList = channelList;
        _settings = settings;
        _falconRadioSharedMemoryService = falconRadioSharedMemoryService;
        _falconSharedMemoryService = falconSharedMemoryService;

        // Subscribe to service events
        _openFreqService.ConnectionStateChanged += OnConnectionStateChanged;
        _openFreqService.StatusMessageReceived += OnStatusMessageReceived;
        _openFreqService.PeerActivityReceived += OnPeerActivityReceived;

        // Falcon Radio Shared Memory
        _falconRadioSharedMemoryService.ConnectionParametersChanged +=
            FalconRadioSharedMemoryServiceOnConnectionParametersChanged;
        _falconSharedMemoryService.FlyingStateChanged += OnFlyingStateChanged;

        // Load config
        _ = LoadConfigurationAsync();

        UpdateConnectionStatusString();

        _openFreqService.SetOwnPositionMode(Settings.ConnectionMode);
    }

    private void OnFlyingStateChanged(object? sender, FlyingStateChangedEventArgs e)
    {
        Is3dMode = e.NewFlyingState;
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
            _openFreqService.Initialize(Settings.GetSettings(), Settings.RecordingDeviceIndex,
                Settings.PlaybackDeviceIndex);

            // Connect to server (channels will auto-join when authenticated)
            await _openFreqService.ConnectAsync();

            if (Settings.ConnectionMode == IOpenFreqService.Mode.GCI)
            {
                _openFreqService.LoadHeightmap(Settings.HeightmapPath);
                _acmiClientService.ConnectionStatusChanged += OnTacviewConnectionStatusChanged;
                await _acmiClientService.ConnectAsync(Settings.TacviewServerAddress, Settings.TacviewServerPassword);
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

            ClearError();
        }
        catch (Exception ex)
        {
            ShowError($"Disconnect failed: {ex.Message}");
        }
    }

    private void OnTacviewConnectionStatusChanged(object? sender, AcmiConnectionEventArgs e)
    {
        UpdateConnectionStatusString();
    }


    [RelayCommand]
    private async Task ConnectToAcmiAsync()
    {
        await _acmiClientService.ConnectAsync(Settings.TacviewServerAddress, Settings.TacviewServerPassword);
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

    private void UpdateConnectionStatusString()
    {
        if (Settings.ConnectionMode == IOpenFreqService.Mode.GCI)
        {
            ConnectionStatusString =
                $"OpenFreq {_openFreqService.Status.ToString()} | Tacview {_acmiClientService.Status.ToString()}";
        }
        else
        {
            ConnectionStatusString = $"OpenFreq {_openFreqService.Status.ToString()}";
        }
    }

    // Service event handlers
    private void OnConnectionStateChanged(object? sender, ConnectionState state)
    {
        UpdateConnectionStatusString();
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
        }
        else if (state == ConnectionState.Disconnected && OpenFreqConnected)
        {
            ShowError("Lost connection to server");
        }
    }

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

    private void OnPeerActivityReceived(object? sender, PeerActivityEventArgs e)
    {
        // Could be used for a log or notifications panel
        StatusMessage = e.Message;
    }

    public void Dispose()
    {
        _ = SaveConfigurationAsync();

        ChannelList.Dispose();
        _openFreqService.Dispose();
        _hotkeyService.Dispose();
        _acmiClientService.Dispose();
        _configurationService.Dispose();

        _falconSharedMemoryService.Dispose();
        _falconRadioSharedMemoryService.Dispose();
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
            Settings.InputDeviceName = config.Settings.InputDeviceName;
            Settings.OutputDeviceName = config.Settings.OutputDeviceName;
            Settings.HeightmapPath = config.Settings.HeightmapPath;

            // Load audio settings
            Settings.LoadFromSettings(config.Settings);

            // Load channel groups
            foreach (var channelGroupData in config.ChannelGroups)
            {
                var channelGroup = ChannelList.CreateChannelGroup(channelGroupData);
                channelGroup.Latitude = channelGroupData.Latitude;
                channelGroup.Longitude = channelGroupData.Longitude;
                channelGroup.AltitudeInput = channelGroupData.AltitudeFt;
                // Load channels
                foreach (var channelData in channelGroupData.Channels)
                {
                    var channel =
                        channelGroup.CreateChannel(channelData.FrequencyKhz, channelData.Name ?? "", channelData.Type);
                    channel.IsEnabled = channelData.Enabled;
                    channel.IsEditing = false;

                    // Parse and set hotkey
                    if (Enum.TryParse<KeyCode>(channelData.HotkeyCode, out var keyCode))
                    {
                        channel.HotKey = keyCode;
                        if (keyCode != KeyCode.VcUndefined)
                        {
                            _hotkeyService.RegisterHotkey(keyCode, channel.Id);
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
                    .Where(cg => cg != ChannelList.FalconChannelGroup)
                    .Select(cg =>
                    {
                        return new ChannelGroupData
                        {
                            Name = cg.Name,
                            Latitude = cg.Latitude,
                            Longitude = cg.Longitude,
                            AltitudeFt = cg.AltitudeInput,
                            RadioStationData = cg.RadioStationData,
                            Channels = cg.Channels.Select(c => new ChannelData
                            {
                                Name = c.Name,
                                FrequencyKhz = c.FrequencyKhz,
                                Type = c.Type,
                                HotkeyCode = c.HotKey.ToString(),
                                Enabled = c.IsEnabled
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


    partial void OnIs3dModeChanged(bool value)
    {
        _openFreqService?.Apply3dAudioEffects = value;
    }

    [RelayCommand]
    private async Task Debug()
    {
        /*
        Settings.OpenFreqServerAddress = "127.0.0.1";
        await ConnectAsync();
        */
    }
}