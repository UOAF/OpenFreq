using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CommunityToolkit.Mvvm.Messaging;
using FalconBmsDataService.Models;
using FalconBmsDataService.Services;
using FalconRadioService.Models;
using FalconRadioService.Services;
using Microsoft.Extensions.Logging;
using NetTopologySuite.Index.Quadtree;
using OpenFreq.Client.Models;
using OpenFreq.Common;
using OpenFreq.Services.Acmi;
using OpenFreq.Utilities;
using OpenFreqAudio;
using OpenFreqClient.Models;
using OpenFreqClient.Services.Interfaces;
using OpenFreqClient.Views.Util;
using SharpHook.Data;

namespace OpenFreqClient.ViewModels;

public partial class ChannelCardListViewModel : ViewModelBase, IDisposable
{
    private readonly IOpenFreqService _openFreqService;
    private readonly IHotkeyService _hotkeyService;
    private readonly IAcmiClientService _acmiClientService;
    private readonly ILogger<ChannelCardListViewModel> _logger;
    private readonly IFalconRadioSharedMemoryService _falconRadioSharedMemoryService;
    private readonly IFalconSharedMemoryService _falconSharedMemoryService;
    private readonly SettingsViewModel _settings;

    private const string BmsLocationName = "BMS Channels";
    public LocationViewModel? FalconLocation { get; private set; }

    private readonly Lock _channelImportLock = new();

    public SettingsViewModel Settings => _settings;

    [ObservableProperty]
    public partial LocationViewModel? SelectedLocation { get; set; }

    [ObservableProperty]
    public partial bool IsLocationPanelExpanded { get; set; } = true;

    [RelayCommand]
    private void ToggleLocationPanel() => IsLocationPanelExpanded = !IsLocationPanelExpanded;

    // This actually holds all of our Locations
    [ObservableProperty]
    public partial ObservableCollection<LocationViewModel> AllLocations { get; private set; } = [];

    // Collection used to display filtered locations (BMS or GCI mode)
    public IEnumerable<LocationViewModel> Locations =>
        _settings.ConnectionMode == IOpenFreqService.Mode.BMS
            ? AllLocations.Where(g => g.RadioStationData.Type == RadioStationData.RadioStationType.BMS)
            : AllLocations.Where(g => g.RadioStationData.Type != RadioStationData.RadioStationType.BMS);


    public ChannelCardListViewModel(IOpenFreqService openFreqService, IHotkeyService hotkeyService,
        IAcmiClientService acmiClientService, ILogger<ChannelCardListViewModel> logger,
        IFalconRadioSharedMemoryService falconRadioSharedMemoryService,
        IFalconSharedMemoryService falconSharedMemoryService, SettingsViewModel settingsViewModel)
    {
        _openFreqService = openFreqService;
        _hotkeyService = hotkeyService;
        _acmiClientService = acmiClientService;
        _logger = logger;
        _falconRadioSharedMemoryService = falconRadioSharedMemoryService;
        _falconSharedMemoryService = falconSharedMemoryService;
        _settings = settingsViewModel;
        _settings.PropertyChanged += OnSettingsChanged;

        // Subscribe to BMS Frequency update messages
        _falconRadioSharedMemoryService.ConnectionParametersChanged +=
            OnConnectionParametersChanged;
        _falconRadioSharedMemoryService.FrequencyChanged += OnBmsFrequencyChanged;
        _falconRadioSharedMemoryService.PttChanged += OnBmsPttChanged;
        _falconRadioSharedMemoryService.PowerChanged += OnRadioPowerChanged;
        _falconRadioSharedMemoryService.VolumeChanged += OnRadioVolumeChanged;
        _falconSharedMemoryService.FlyingStateChanged += OnFlyingStateChanged;
        _falconSharedMemoryService.StateChanged += OnFalconSharedMemoryStateChanged;

        _openFreqService.ConnectionStateChanged += OnOpenFreqConnectionStateChanged;
        AllLocations?.CollectionChanged += (s, e) =>
        {
            OnPropertyChanged(nameof(Locations));
            if (SelectedLocation == null)
                SelectedLocation = Locations.FirstOrDefault();
        };

        // Subscribe to transmission messages
        WeakReferenceMessenger.Default.Register<StartTransmissionMessage>(this,
            async (r, m) => await HandleStartTransmissionAsync(m));
        WeakReferenceMessenger.Default.Register<StopTransmissionMessage>(this,
            async (r, m) => await HandleStopTransmissionAsync(m));
        WeakReferenceMessenger.Default.Register<LocationViewModel.LocationDeleteRequestedMessage>(this,
            async (r, m) => await DeleteLocation(m.LocationId));
        WeakReferenceMessenger.Default.Register<ChannelPanUpdateMessage>(this,
            (r, m) => _openFreqService.SetPan(m.FrequencyKhz, m.ChannelId, m.Pan));
        WeakReferenceMessenger.Default.Register<LocationViewModel.LocationSelectionRequestedMessage>(this,
            (r, m) => SelectedLocation = Locations.FirstOrDefault(g => g.Id == m.LocationId));
    }

    private async void OnFalconSharedMemoryStateChanged(object? sender, ServiceStateChangedEventArgs e)
    {
        // Clean up in case the SHMEM has disconnected (BMS likely crashed)
        if (_settings.ConnectionMode != IOpenFreqService.Mode.BMS || e.NewState == ServiceState.Connected ||
            FalconLocation == null) return;
        await DeleteLocation(FalconLocation);
        FalconLocation = null;
    }

    private void OnSettingsChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(SettingsViewModel.ConnectionMode))
        {
            OnPropertyChanged(nameof(Locations));
            if (SelectedLocation == null || !Locations.Contains(SelectedLocation))
                SelectedLocation = Locations.FirstOrDefault();
        }
        else if (e.PropertyName == nameof(SettingsViewModel.BmsRadio1Pan) && FalconLocation != null)
        {
            foreach (var ch in FalconLocation.Channels
                         .Where(c => c.BmsRadioType is RadioType.Radio1 or RadioType.Guard))
                ch.Pan = _settings.BmsRadio1Pan;
        }
        else if (e.PropertyName == nameof(SettingsViewModel.BmsRadio2Pan) && FalconLocation != null)
        {
            foreach (var ch in FalconLocation.Channels
                         .Where(c => c.BmsRadioType == RadioType.Radio2))
                ch.Pan = _settings.BmsRadio2Pan;
        }
    }

    [RelayCommand]
    private void AddLocation()
    {
        var (lat, lon) = TheaterCoordinateConverter.GetCenterLatLon(_settings.SelectedTheater);
        var location = CreateLocation(
            new LocationData
            {
                Name = $"Location #{AllLocations.Count + 1}",
                Latitude = lat,
                Longitude = lon,
                AltitudeFt = 30000
            },
            editMode: true);
        SelectedLocation = location;
    }

    public void EnsureDefaultGciLocation()
    {
        if (!_settings.ModeIsGci || Locations.Any()) return;

        var (lat, lon) = TheaterCoordinateConverter.GetCenterLatLon(_settings.SelectedTheater);
        var location = CreateLocation(
            new LocationData
            {
                Name = "Default",
                Latitude = lat,
                Longitude = lon,
                AltitudeFt = 30000
            });

        var lobby1 = location.CreateChannel(1234, "BMS Lobby 1", false);
        lobby1.PttHotKey = new KeyboardBinding(KeyCode.VcF1);

        var lobby2 = location.CreateChannel(339750, "BMS Lobby 2", false);
        lobby2.PttHotKey = new KeyboardBinding(KeyCode.VcF2);

        SelectedLocation = location;
    }


    private void OnOpenFreqConnectionStateChanged(object? sender, ConnectionState e)
    {
        if (e == ConnectionState.Authenticated && !_settings.ModeIsGci)
        {
            _logger.LogDebug("OpenFreq authenticated, importing and joining BMS channels");

            _ = Task.Run(async () =>
            {
                try
                {
                    await ImportBmsRadioChannels();
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Failed to import/join BMS channels");
                }
            });
        }
        else if (e == ConnectionState.Authenticated && _settings.ModeIsGci)
        {
            foreach (var location in AllLocations)
            {
                if (location.RadioStationData.Type != RadioStationData.RadioStationType.BMS)
                {
                    location.JoinAllChannelsAsync().Wait(100);
                }
            }
        }
    }

    private static float ComputeGainFromBmsVolume(int rawVolume)
    {
        // BMS knob: 1000 = loudest, 10000 = mute (inverted scale)
        const float dxMin = 1000f;
        const float dxMax = 10000f;
        // dB range calibrated so mid-knob (~5500) gives -17 dB (14%) rather than
        // the former -37 dB (1.4%), which made any knob asymmetry catastrophic.
        const float dbMin = -40f;
        const float dbMax = 6f;

        var clampedDx = Math.Clamp(rawVolume, dxMin, dxMax);
        var t = (dxMax - clampedDx) / (dxMax - dxMin);
        var db = dbMin + t * (dbMax - dbMin);
        var gain = (float)Math.Pow(10.0, db / 20.0);
        return gain < 0.00001f ? 0f : gain;
    }

    private void OnRadioVolumeChanged(object? sender, RadioVolumeChangedEventArgs e)
    {
        if (_settings.ModeIsGci || FalconLocation == null) return;

        _logger.LogDebug($"VOLUME {e.OldVolume} -> {e.NewVolume}");

        var gain = ComputeGainFromBmsVolume(e.NewVolume);

        var channels = FalconLocation?.Channels.Where(c => c.BmsRadioType == e.RadioType).ToList();
        if (channels == null) return;
        foreach (var channel in channels)
        {
            _openFreqService.SetVolume(channel.FrequencyKhz, channel.Id, gain);
        }
    }

    private void OnRadioPowerChanged(object? sender, RadioPowerChangedEventArgs e)
    {
        if (_settings.ModeIsGci || FalconLocation == null) return;

        var channels = FalconLocation.Channels.Where(c => c.BmsRadioType == e.RadioType).ToList();
        foreach (var channel in channels)
        {
            // Skip 9999 - it's BMS's parking frequency and should never be joined
            if (channel.FrequencyKhz == IFalconRadioSharedMemoryService.BmsRadioOffFrequency)
                continue;

            // Trigger Join/Leave
            if (!e.NewPower && channel.ConnectionStatus == Channel.ChannelConnectionStatus.Connected ||
                e.NewPower && channel.ConnectionStatus != Channel.ChannelConnectionStatus.Connected)
            {
                channel.ToggleJoinLeave();
            }
        }
    }

    private async void OnConnectionParametersChanged(object? sender,
        ConnectionParametersChangedEventArgs e)
    {
        // Delete the BMS location on both TerminateClient and plain MP disconnect (ReadyToTransmit → false).
        if (e.NewParameters.TerminateClient || (e.OldParameters.ReadyToTransmit && !e.NewParameters.ReadyToTransmit))
        {
            if (FalconLocation != null)
            {
                await DeleteLocation(FalconLocation);
                FalconLocation = null;
            }

            return;
        }

        // Re-sync channel power states when BMS signals it's ready (radios may have been
        // off during AttemptingToConnect and only enabled once ReadyToTransmit is set).
        if (!e.OldParameters.ReadyToTransmit && e.NewParameters.ReadyToTransmit)
        {
            SyncBmsChannelPowerStates();
        }
    }

    private void SyncBmsChannelPowerStates()
    {
        if (FalconLocation == null) return;
        foreach (var type in Enum.GetValues<RadioType>())
        {
            var radioChannel = _falconRadioSharedMemoryService.GetRadioChannel(type);
            if (radioChannel == null) continue;
            var isPowerOn = radioChannel.IsOn &&
                            radioChannel.Frequency != IFalconRadioSharedMemoryService.BmsRadioOffFrequency;
            foreach (var channel in FalconLocation.Channels.Where(c => c.BmsRadioType == type).ToList())
            {
                if (channel.FrequencyKhz == IFalconRadioSharedMemoryService.BmsRadioOffFrequency) continue;
                var isConnected = channel.ConnectionStatus == Channel.ChannelConnectionStatus.Connected;
                if (isPowerOn != isConnected)
                    channel.ToggleJoinLeave();
            }
        }

        SyncBmsChannelVolumeStates();
    }

    private void SyncBmsChannelVolumeStates()
    {
        if (FalconLocation == null) return;
        foreach (var type in Enum.GetValues<RadioType>())
        {
            var radioChannel = _falconRadioSharedMemoryService.GetRadioChannel(type);
            if (radioChannel == null || radioChannel.RxVolume <= 0) continue;
            var gain = ComputeGainFromBmsVolume(radioChannel.RxVolume);
            foreach (var channel in FalconLocation.Channels.Where(c => c.BmsRadioType == type).ToList())
            {
                _openFreqService.SetVolume(channel.FrequencyKhz, channel.Id, gain);
            }
        }
    }

    private void OnFlyingStateChanged(object? sender, FlyingStateChangedEventArgs e)
    {
        _logger.LogDebug($"FalconSharedMemoryServiceOnFlyingStateChanged: {e.OldFlyingState} -> {e.NewFlyingState}");
        if (!e.OldFlyingState && e.NewFlyingState)
        {
            _hotkeyService.PausePttKeys();
        }
        else if (e.OldFlyingState && !e.NewFlyingState)
        {
            _hotkeyService.ResumePttKeys();
        }
    }

    private async Task ImportBmsRadioChannels()
    {
        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            lock (_channelImportLock)
            {
                if (FalconLocation == null)
                {
                    FalconLocation = CreateLocation(BmsLocationName, RadioStationPresets.FighterF16,
                        RadioStationData.RadioStationType.BMS);
                }
                else
                {
                    FalconLocation.LeaveAllChannelsAsync().Wait(300);
                    FalconLocation.Channels.Clear();
                }

                // Create the Channels
                foreach (var type in Enum.GetValues<RadioType>())
                {
                    var falconChannel = _falconRadioSharedMemoryService.GetRadioChannel(type);
                    if (falconChannel != null &&
                        FalconLocation.Channels.All(c => c.FrequencyKhz != falconChannel.Frequency))
                    {
                        var channelName = type switch
                        {
                            RadioType.Radio1 => "Radio 1",
                            RadioType.Radio2 => "Radio 2",
                            RadioType.Guard => "Guard",
                            _ => type.ToString()
                        };
                        var channel = FalconLocation.CreateChannel(falconChannel.Frequency,
                            channelName,
                            false, type);
                        channel.IsEditable = false;

                        var channelIsPowerOn = _falconRadioSharedMemoryService.GetRadioChannel(type)?.IsOn ?? false;
                        _logger.LogDebug($"CHANNEL {channel.FrequencyKhz}: {channelIsPowerOn}");
                        if (channelIsPowerOn)
                        {
                            channel.Join();
                        }
                        else
                        {
                            channel.Leave();
                        }

                        // set hotkeys and Pan from Settings
                        switch (type)
                        {
                            case RadioType.Radio2:
                                channel.PttHotKey = new KeyboardBinding(KeyCode.VcF1);
                                channel.SquelchHotKey = _settings.BmsVhfSquelchHotkey;
                                channel.Pan = _settings.BmsRadio2Pan;
                                break;
                            case RadioType.Radio1:
                                channel.PttHotKey = new KeyboardBinding(KeyCode.VcF2);
                                channel.SquelchHotKey = _settings.BmsUhfSquelchHotkey;
                                channel.Pan = _settings.BmsRadio1Pan;
                                break;
                            case RadioType.Guard:
                                channel.PttHotKey = new KeyboardBinding(KeyCode.VcF3);
                                channel.SquelchHotKey = _settings.BmsUhfSquelchHotkey;
                                channel.Pan = _settings.BmsRadio1Pan;
                                break;
                            default:
                                _logger.LogWarning("Unknown radio type: " + type);
                                break;
                        }
                    }
                }
            }

            // Apply current BMS volume levels — SyncBmsChannelPowerStates (which calls
            // SyncBmsChannelVolumeStates) only fires on ReadyToTransmit false→true transition.
            // When ReadyToTransmit stays true across a reconnect that transition never fires,
            // so we sync volumes explicitly here after the channel list is built.
            SyncBmsChannelVolumeStates();
        });
    }

    private void OnBmsPttChanged(object? sender, RadioPttChangedEventArgs e)
    {
        // Do NOT capture keys twice - for non-flying, we want to use the callbacks from our HotKey service
        if (!_hotkeyService.PttKeysPaused || _falconSharedMemoryService.IsFlying == false)
            return;

        if (FalconLocation == null)
        {
            _logger.LogWarning("Ignoring PTT: no Falcon location");
            return;
        }

        var channel = FalconLocation.Channels.FirstOrDefault(c => c.BmsRadioType == e.RadioType);
        if (channel == null || channel.ConnectionStatus == Channel.ChannelConnectionStatus.Disconnected) return;
        switch (e)
        {
            // mute all incoming transmissions from this location which have the same channel type
            case { OldPtt: false, NewPtt: true }:
                var mutedFrequencies = FalconLocation.GetAllFrequenciesOfLocation(channel.Type);
                mutedFrequencies.Remove(channel.FrequencyKhz);
                _openFreqService.StartTransmissionAsync(channel.FrequencyKhz, channel.Id, mutedFrequencies).Wait();
                break;
            case { OldPtt: true, NewPtt: false }:
                _openFreqService.StopTransmissionAsync(channel.FrequencyKhz).Wait();
                break;
        }
    }

    private void OnBmsFrequencyChanged(object? sender, RadioFrequencyChangedEventArgs e)
    {
        if (_settings.ModeIsGci) return;
        if (FalconLocation == null)
        {
            _logger.LogWarning("Unclean state: FalconLocation is null, reimporting");
            ImportBmsRadioChannels().Wait(100);
            return;
        }

        _logger.LogDebug(
            $"OnBmsFrequencyChanged: {e.OldFrequencyKhz} -> {e.NewFrequencyKhz}");


        lock (_channelImportLock)
        {
            // make sure we set the power correctly
            var channelIsPowerOn = _falconRadioSharedMemoryService.GetRadioChannel(e.RadioType)?.IsOn ?? false;

            // Try to change an existing frequency - this should be the case in 99% of the time
            if (FalconLocation.ChangeChannelFrequency(e.OldFrequencyKhz, e.NewFrequencyKhz, channelIsPowerOn))
            {
                // Explicitly join the channel that was just updated if it's powered on
                // 9999 is BMS's "radio off" parking frequency - never join it
                var updatedChannel =
                    FalconLocation.Channels.FirstOrDefault(c => c.BmsRadioType == e.RadioType);
                if (updatedChannel != null && channelIsPowerOn &&
                    e.NewFrequencyKhz != IFalconRadioSharedMemoryService.BmsRadioOffFrequency)
                {
                    _logger.LogDebug($"Explicitly joining updated channel: {e.NewFrequencyKhz}");
                    updatedChannel.Join();
                    _openFreqService.SetPan(e.NewFrequencyKhz, updatedChannel.Id, updatedChannel.Pan);
                }

                // Still make sure to join all channels - e.g. when switching back from guard mode
                foreach (var type in Enum.GetValues<RadioType>())
                {
                    var falconChannel = _falconRadioSharedMemoryService.GetRadioChannel(type);
                    if (falconChannel is not { IsOn: true }) continue;
                    if (falconChannel.Frequency == 9999) continue; // Skip parking frequency

                    foreach (var channel in FalconLocation.Channels)
                    {
                        if (channel.FrequencyKhz == falconChannel.Frequency &&
                            channel.FrequencyKhz != e.NewFrequencyKhz)
                        {
                            _logger.LogDebug($"Loop joining channel: {channel.FrequencyKhz}");
                            channel.Join();
                        }
                    }
                }

                return;
            }
        }

        // Fallback - for some reason there is no channel on the old frequency, create a new one
        lock (_channelImportLock)
        {
            _logger.LogWarning("OnBmsFrequencyChanged for an unknown frequency : {NewFrequencyKhz}", e.NewFrequencyKhz);
            var channelIsPowerOn = _falconRadioSharedMemoryService.GetRadioChannel(e.RadioType)?.IsOn ?? false;

            var newChannel = Dispatcher.UIThread.InvokeAsync(() =>
            {
                var channel = FalconLocation.CreateChannel(
                    e.NewFrequencyKhz,
                    BmsLocationName,
                    false);

                // Only join if the radio is powered on AND it's not the 9999 parking frequency
                if (channelIsPowerOn && e.NewFrequencyKhz != IFalconRadioSharedMemoryService.BmsRadioOffFrequency)
                {
                    channel.Join();
                }
                else
                {
                    channel.Leave();
                }

                return channel;
            }).GetAwaiter().GetResult();

            // Only call JoinFrequencyAsync if the radio is powered on and not 9999
            if (channelIsPowerOn && e.NewFrequencyKhz != IFalconRadioSharedMemoryService.BmsRadioOffFrequency)
            {
                JoinFrequencyAsync(newChannel.FrequencyKhz, newChannel.Id, FalconLocation.RadioStationData)
                    .Wait(TimeSpan.FromMilliseconds(500));
            }

            switch (e.RadioType)
            {
                case RadioType.Radio1:
                    newChannel.Pan = _settings.BmsRadio1Pan;
                    break;
                case RadioType.Radio2:
                    newChannel.Pan = _settings.BmsRadio2Pan;
                    break;
                case RadioType.Guard:
                    newChannel.Pan = _settings.BmsRadio1Pan;
                    break;
            }
        }
    }


    private async Task HandleStartTransmissionAsync(StartTransmissionMessage msg)
    {
        if (!_openFreqService.IsAuthenticated) return;

        try
        {
            await _openFreqService.StartTransmissionAsync(msg.FrequencyKhz, msg.ChannelId, msg.MutedRadioChannels);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Failed to start transmission: {ex.Message}");
        }
    }

    private async Task HandleStopTransmissionAsync(StopTransmissionMessage msg)
    {
        if (!_openFreqService.IsAuthenticated) return;

        try
        {
            await _openFreqService.StopTransmissionAsync(msg.FrequencyKhz);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Failed to stop transmission: {ex.Message}");
        }
    }

    public async Task JoinFrequencyAsync(int frequencyKhz, Guid slotId, RadioStationData radioStationData)
    {
        if (!_openFreqService.IsAuthenticated) return;

        try
        {
            await _openFreqService.JoinFrequencyAsync(frequencyKhz, slotId, radioStationData);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Failed to join frequency {frequencyKhz / 1000d:F3}: {ex.Message}");
        }
    }

    public async Task LeaveFrequencyAsync(int frequencyKhz, Guid slotId)
    {
        if (!_openFreqService.IsAuthenticated) return;

        try
        {
            await _openFreqService.LeaveFrequencyAsync(frequencyKhz, slotId);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Failed to leave frequency {frequencyKhz / 1000d:F3}: {ex.Message}");
        }
    }

    public async Task LeaveAllChannelsAsync()
    {
        foreach (var location in Locations)
        {
            await location.LeaveAllChannelsAsync();
        }
    }

    public LocationViewModel CreateLocation(string name, RadioStationPreset preset,
        RadioStationData.RadioStationType radioStationType,
        bool editMode = false)
    {
        var location = new LocationViewModel(_openFreqService, _hotkeyService, _acmiClientService,
            _settings, name,
            preset, radioStationType, editMode: editMode);
        AllLocations.Add(location);
        return location;
    }

    public LocationViewModel CreateLocation(LocationData locationData, bool editMode = false)
    {
        var location = new LocationViewModel(_openFreqService, _hotkeyService, _acmiClientService,
            _settings, locationData.Name, locationData.RadioStationData.Preset,
            locationData.RadioStationData.Type, locationData.Latitude, locationData.Longitude,
            locationData.AltitudeFt, editMode);
        AllLocations.Add(location);
        return location;
    }


    public async Task DeleteLocation(LocationViewModel location)
    {
        if (SelectedLocation == location)
            SelectedLocation = null;
        await location.LeaveAllChannelsAsync();
        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            AllLocations.Remove(location);
            location.Dispose();
        });
    }

    public async Task DeleteLocation(Guid locationId)
    {
        if (Locations.FirstOrDefault(cg => cg.Id == locationId) is not { } location)
            return;

        if (await ConfirmationDialogService.ShowAsync(
                title: "Confirm deletion",
                message: $"Are you sure you want to delete the Location \"{location.Name}\"?",
                cancelText: "Cancel",
                confirmText: "Delete"))
        {
            await DeleteLocation(location);
        }
    }

    public void Dispose()
    {
        _falconRadioSharedMemoryService.ConnectionParametersChanged -= OnConnectionParametersChanged;
        _falconRadioSharedMemoryService.FrequencyChanged -= OnBmsFrequencyChanged;
        _falconRadioSharedMemoryService.PttChanged -= OnBmsPttChanged;
        _falconSharedMemoryService.FlyingStateChanged -= OnFlyingStateChanged;
        _settings.PropertyChanged -= OnSettingsChanged;
    }
}
