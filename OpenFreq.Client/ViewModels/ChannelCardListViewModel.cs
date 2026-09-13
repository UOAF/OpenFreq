using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
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
    public LocationViewModel? FalconLocation { get; internal set; }

    private readonly Lock _channelImportLock = new();

    private readonly Lock _bmsPttLock = new();
    private Task _bmsPttTail = Task.CompletedTask;

    // Last-logged BMS volume signature — dedupes the volume diagnostic so it only
    // logs when raw/gain values actually change (no per-poll spam).
    private string? _lastVolumeDiagSignature;

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

    // Single global ACMI callsign list shared across all LocationViewModels
    public ObservableCollection<LocationViewModel.TacviewAircraftItem> GlobalTacviewCallsigns { get; } = [];
    private CancellationTokenSource? _callsignUpdateCts;

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
        _acmiClientService.ConnectionStatusChanged += OnAcmiConnectionStatusChangedForCallsigns;

        // Sync initial ACMI state in case already connected before this VM was created
        if (_acmiClientService.Status == AcmiConnectionStatus.Connected)
            StartCallsignPolling();

        AllLocations.CollectionChanged += OnAllLocationsChanged;

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

    private void OnAllLocationsChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        OnPropertyChanged(nameof(Locations));
        if (SelectedLocation == null)
            SelectedLocation = Locations.FirstOrDefault();
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


    private void OnOpenFreqConnectionStateChanged(object? sender, ConnectionStateChangedEventArgs args)
    {
        var e = args.State;
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
        // BMS knob: 0 = loudest, 10000 = mute (inverted scale).
        const double dxMin = 0f;
        const double dxMax = 10000f;

        const double dbMin = -40f;
        const double dbMax = 6f;

        double clampedDx = Math.Clamp(rawVolume, dxMin, dxMax);
        double t = (dxMax - clampedDx) / (dxMax - dxMin);
        double db = dbMin + t * (dbMax - dbMin);
        return (float)Math.Pow(10.0, db / 20.0);
    }

    private void OnRadioVolumeChanged(object? sender, RadioVolumeChangedEventArgs e)
    {
        if (_settings.ModeIsGci || FalconLocation == null) return;

        _logger.LogDebug($"VOLUME {e.OldVolume} -> {e.NewVolume}");

        var gain = ComputeGainFromBmsVolume(e.NewVolume);

        // REMOVE ME WHEN UHF/VHF LOUDNESS BUG FIXED
        LogBmsVolumeDiagnostics("knob change");
        // *****************************************
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

        LogBmsVolumeDiagnostics("sync");
    }

    /// <summary>
    /// Diagnostic for the COM1/COM2 volume-mismatch issue: logs every BMS radio's raw RxVolume and the gain it maps to
    /// </summary>
    private void LogBmsVolumeDiagnostics(string trigger)
    {
        if (_settings.ModeIsGci) return;

        var parts = new List<string>();
        foreach (var type in Enum.GetValues<RadioType>())
        {
            var radioChannel = _falconRadioSharedMemoryService.GetRadioChannel(type);
            if (radioChannel == null) continue;
            var gain = ComputeGainFromBmsVolume(radioChannel.RxVolume);
            parts.Add($"{type} raw={radioChannel.RxVolume} gain={gain:F3}");
        }

        var signature = string.Join(" | ", parts);
        if (signature == _lastVolumeDiagSignature) return;
        _lastVolumeDiagSignature = signature;

        _logger.LogInformation("BMS volume map ({Trigger}): {Volumes}", trigger, signature);
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
                            case RadioType.Radio1:
                                channel.PttHotKey = new KeyboardBinding(KeyCode.VcF1);
                                channel.SquelchHotKey = _settings.BmsUhfSquelchHotkey;
                                channel.Pan = _settings.BmsRadio1Pan;
                                break;
                            case RadioType.Radio2:
                                channel.PttHotKey = new KeyboardBinding(KeyCode.VcF2);
                                channel.SquelchHotKey = _settings.BmsVhfSquelchHotkey;
                                channel.Pan = _settings.BmsRadio2Pan;
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

        // Read the frequency now. A retune can change it before the queued start runs.
        var frequencyKhz = channel.FrequencyKhz;
        var slotId = channel.Id;
        switch (e)
        {
            case { OldPtt: false, NewPtt: true }:
                QueueBmsPtt(e.RadioType, "start", () => _openFreqService.StartTransmissionAsync(frequencyKhz, slotId));
                break;
            case { OldPtt: true, NewPtt: false }:
                QueueBmsPtt(e.RadioType, "stop", () => _openFreqService.StopTransmissionAsync(slotId));
                break;
        }
    }

    /// <summary>
    /// Runs BMS PTT changes on the thread pool, one at a time, in the order that the RCC polling loop saw them.
    /// If the polling loop waited for each websocket send, it would read the next PTT change late.
    /// </summary>
    /// <remarks>
    /// Each change waits until the previous change is complete, not only until it has started. The RTC client raises
    /// <see cref="IRtcClient.TransmissionStateChanged"/> after each send completes. If a start and a stop overlap,
    /// those events can arrive in the wrong order, and the card then stays in the transmitting status.
    /// </remarks>
    private void QueueBmsPtt(RadioType radioType, string action, Func<Task> change)
    {
        lock (_bmsPttLock)
        {
            var previous = _bmsPttTail;
            _bmsPttTail = Task.Run(async () =>
            {
                // This never throws, because each change catches its own exception.
                await previous;
                try
                {
                    await change();
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "BMS PTT {Action} on {RadioType} failed", action, radioType);
                }
            });
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
            if (FalconLocation.ChangeChannelFrequency(e.OldFrequencyKhz, e.NewFrequencyKhz))
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
        _logger.LogWarning("OnBmsFrequencyChanged for an unknown frequency : {NewFrequencyKhz}", e.NewFrequencyKhz);
        var radioIsOn = _falconRadioSharedMemoryService.GetRadioChannel(e.RadioType)?.IsOn ?? false;

        // Post rather than wait. This runs on the RCC polling thread, and blocking it on the UI thread while holding
        // _channelImportLock deadlocks with ImportBmsRadioChannels, which takes that lock on the UI thread.
        Dispatcher.UIThread.Post(() =>
        {
            lock (_channelImportLock)
            {
                if (FalconLocation == null) return;

                var channel = FalconLocation.CreateChannel(
                    e.NewFrequencyKhz,
                    BmsLocationName,
                    false);

                // Only join if the radio is powered on AND it's not the 9999 parking frequency
                if (radioIsOn && e.NewFrequencyKhz != IFalconRadioSharedMemoryService.BmsRadioOffFrequency)
                {
                    channel.Join();
                }
                else
                {
                    channel.Leave();
                }

                switch (e.RadioType)
                {
                    case RadioType.Radio1:
                        channel.Pan = _settings.BmsRadio1Pan;
                        break;
                    case RadioType.Radio2:
                        channel.Pan = _settings.BmsRadio2Pan;
                        break;
                    case RadioType.Guard:
                        channel.Pan = _settings.BmsRadio1Pan;
                        break;
                }
            }
        });
    }


    private async Task HandleStartTransmissionAsync(StartTransmissionMessage msg)
    {
        if (!_openFreqService.IsAuthenticated) return;

        try
        {
            await _openFreqService.StartTransmissionAsync(msg.FrequencyKhz, msg.ChannelId);
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
            await _openFreqService.StopTransmissionAsync(msg.ChannelId);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Failed to stop transmission: {ex.Message}");
        }
    }

    private void OnAcmiConnectionStatusChangedForCallsigns(object? sender, AcmiConnectionEventArgs e)
    {
        if (e.Status == AcmiConnectionStatus.Connected)
            StartCallsignPolling();
        else
            StopCallsignPolling();
    }

    private void StartCallsignPolling()
    {
        StopCallsignPolling();
        _callsignUpdateCts = new CancellationTokenSource();
        _ = PollCallsignsAsync(_callsignUpdateCts.Token);
    }

    private void StopCallsignPolling()
    {
        _callsignUpdateCts?.Cancel();
        _callsignUpdateCts?.Dispose();
        _callsignUpdateCts = null;
        Dispatcher.UIThread.Post(() => GlobalTacviewCallsigns.Clear());
    }

    private async Task PollCallsignsAsync(CancellationToken cancellationToken)
    {
        while (_acmiClientService.Status == AcmiConnectionStatus.Connected
               && !cancellationToken.IsCancellationRequested)
        {
            var currentAircraft = _acmiClientService.GetAllAircraft()
                .Select(ac => new LocationViewModel.TacviewAircraftItem(ac.CallSign, ac.ObjectId))
                .ToList();

            var currentIds = currentAircraft.Select(a => a.ObjectId).ToHashSet();

            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                // Remove stale
                for (int i = GlobalTacviewCallsigns.Count - 1; i >= 0; i--)
                {
                    if (!currentIds.Contains(GlobalTacviewCallsigns[i].ObjectId))
                        GlobalTacviewCallsigns.RemoveAt(i);
                }

                // Add new
                var existingIds = GlobalTacviewCallsigns.Select(a => a.ObjectId).ToHashSet();
                foreach (var aircraft in currentAircraft)
                {
                    if (aircraft.CallSign == string.Empty || existingIds.Contains(aircraft.ObjectId))
                        continue;

                    // Insert in sorted position by CallSign
                    var insertAt = 0;
                    while (insertAt < GlobalTacviewCallsigns.Count
                           && string.Compare(GlobalTacviewCallsigns[insertAt].CallSign,
                               aircraft.CallSign, StringComparison.OrdinalIgnoreCase) <= 0)
                        insertAt++;
                    GlobalTacviewCallsigns.Insert(insertAt, aircraft);
                }
            });

            try
            {
                await Task.Delay(1000, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    public LocationViewModel CreateLocation(string name, RadioStationPreset preset,
        RadioStationData.RadioStationType radioStationType,
        bool editMode = false)
    {
        var location = new LocationViewModel(_openFreqService, _hotkeyService, _acmiClientService,
            _settings, name, preset, radioStationType, GlobalTacviewCallsigns, editMode: editMode);
        AllLocations.Add(location);
        return location;
    }

    public LocationViewModel CreateLocation(LocationData locationData, bool editMode = false)
    {
        var location = new LocationViewModel(_openFreqService, _hotkeyService, _acmiClientService,
            _settings, locationData.Name, locationData.RadioStationData.Preset,
            locationData.RadioStationData.Type, GlobalTacviewCallsigns,
            locationData.Latitude, locationData.Longitude, locationData.AltitudeFt, editMode);
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
        _falconRadioSharedMemoryService.PowerChanged -= OnRadioPowerChanged;
        _falconRadioSharedMemoryService.VolumeChanged -= OnRadioVolumeChanged;
        _falconSharedMemoryService.FlyingStateChanged -= OnFlyingStateChanged;
        _falconSharedMemoryService.StateChanged -= OnFalconSharedMemoryStateChanged;
        _openFreqService.ConnectionStateChanged -= OnOpenFreqConnectionStateChanged;
        AllLocations.CollectionChanged -= OnAllLocationsChanged;
        _settings.PropertyChanged -= OnSettingsChanged;
        _acmiClientService.ConnectionStatusChanged -= OnAcmiConnectionStatusChangedForCallsigns;
        _callsignUpdateCts?.Cancel();
        _callsignUpdateCts?.Dispose();
    }
}
