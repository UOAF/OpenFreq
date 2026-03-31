using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
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

    private const string BmsGroupName = "BMS Channels";
    public ChannelCardGroupViewModel? FalconChannelGroup { get; private set; }

    private readonly Lock _channelImportLock = new();

    // This actually holds all of our ChannelGroups
    [ObservableProperty]
    public partial ObservableCollection<ChannelCardGroupViewModel> AllChannelGroups { get; private set; } = [];

    // Collection used to display filtered channel groups (BMS or GCI mode)
    public IEnumerable<ChannelCardGroupViewModel> ChannelGroups =>
        _settings.ConnectionMode == IOpenFreqService.Mode.BMS
            ? AllChannelGroups.Where(g => g.RadioStationData.Type == RadioStationData.RadioStationType.BMS)
            : AllChannelGroups.Where(g => g.RadioStationData.Type != RadioStationData.RadioStationType.BMS);


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
        AllChannelGroups?.CollectionChanged += (s, e) => OnPropertyChanged(nameof(ChannelGroups));

        // Subscribe to transmission messages
        WeakReferenceMessenger.Default.Register<StartTransmissionMessage>(this,
            async (r, m) => await HandleStartTransmissionAsync(m));
        WeakReferenceMessenger.Default.Register<StopTransmissionMessage>(this,
            async (r, m) => await HandleStopTransmissionAsync(m));
        WeakReferenceMessenger.Default.Register<ChannelCardGroupViewModel.ChannelCardGroupDeleteRequestedMessage>(this,
            async (r, m) => await DeleteChannelGroup(m.ChannelCardGroupId));
        WeakReferenceMessenger.Default.Register<ChannelAudioChannelUpdateMessage>(this,
            (r, m) => _openFreqService.SetAudioChannel(m.FrequencyKhz, m.AudioChannel));
    }

    private async void OnFalconSharedMemoryStateChanged(object? sender, ServiceStateChangedEventArgs e)
    {
        // Clean up in case the SHMEM has disconnected (BMS likely crashed)
        if (_settings.ConnectionMode != IOpenFreqService.Mode.BMS || e.NewState == ServiceState.Connected ||
            FalconChannelGroup == null) return;
        await DeleteChannelGroup(FalconChannelGroup);
        FalconChannelGroup = null;
    }

    private void OnSettingsChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(SettingsViewModel.ConnectionMode))
        {
            OnPropertyChanged(nameof(ChannelGroups));
        }
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
            foreach (var channelGroup in AllChannelGroups)
            {
                if (channelGroup.RadioStationData.Type != RadioStationData.RadioStationType.BMS)
                {
                    channelGroup.JoinAllChannelsAsync().Wait(100);
                }
            }
        }
    }

    private void OnRadioVolumeChanged(object? sender, RadioVolumeChangedEventArgs e)
    {
        if (FalconChannelGroup == null)
        {
            _logger.LogError("Attempting to change volume on null FalconChannelGroup");
            return;
        }

        _logger.LogDebug($"VOLUME {e.OldVolume} -> {e.NewVolume}");

        const float dxMin = 1000f; // loudest position
        const float dxMax = 10000f; // mute position

        const float dbMin = -80f; // silence
        const float dbMax = 6f; // 2x boost

        // Clamp input
        var clampedDx = Math.Clamp(e.NewVolume, dxMin, dxMax);

        // Invert and normalize knob position (0..1)
        var t = (dxMax - clampedDx) / (dxMax - dxMin);

        // Convert to dB
        var db = dbMin + t * (dbMax - dbMin);

        // Convert dB → linear gain
        var gain = (float)Math.Pow(10.0f, db / 20.0f);

        // Prevent denormals / tiny noise
        if (gain < 0.00001f)
            gain = 0f;

        var channels = FalconChannelGroup?.Channels.Where(c => c.BmsRadioType == e.RadioType).ToList();
        if (channels == null) return;
        foreach (var channel in channels)
        {
            _openFreqService.SetVolume(channel.FrequencyKhz, gain);
        }
    }

    private void OnRadioPowerChanged(object? sender, RadioPowerChangedEventArgs e)
    {
        if (FalconChannelGroup == null)
        {
            _logger.LogError("Attempting to change power on null FalconChannelGroup");
            return;
        }

        var channels = FalconChannelGroup.Channels.Where(c => c.BmsRadioType == e.RadioType).ToList();
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
        if (e.NewParameters.TerminateClient)
        {
            if (FalconChannelGroup == null) return;
            await DeleteChannelGroup(FalconChannelGroup);
            FalconChannelGroup = null;
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
                if (FalconChannelGroup == null)
                {
                    FalconChannelGroup = CreateChannelGroup(BmsGroupName, RadioStationPresets.Fighter,
                        RadioStationData.RadioStationType.BMS);
                }
                else
                {
                    FalconChannelGroup.LeaveAllChannelsAsync().Wait(300);
                    FalconChannelGroup.Channels.Clear();
                }

                foreach (var type in Enum.GetValues<RadioType>())
                {
                    var falconChannel = _falconRadioSharedMemoryService.GetRadioChannel(type);
                    if (falconChannel != null &&
                        FalconChannelGroup.Channels.All(c => c.FrequencyKhz != falconChannel.Frequency))
                    {
                        var channel = FalconChannelGroup.CreateChannel(falconChannel.Frequency,
                            "BMS Channel " + type,
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

                        // set hotkeys and AudioChannel from Settings
                        switch (type)
                        {
                            case RadioType.VHF:
                                channel.PttHotKey = new KeyboardBinding(KeyCode.VcF1);
                                channel.SquelchHotKey = _settings.BmsVhfSquelchHotkey;
                                channel.AudioChannel = _settings.BmsVhfAudioChannel;
                                break;
                            case RadioType.UHF:
                                channel.PttHotKey = new KeyboardBinding(KeyCode.VcF2);
                                channel.SquelchHotKey = _settings.BmsUhfSquelchHotkey;
                                channel.AudioChannel = _settings.BmsUhfAudioChannel;
                                break;
                            case RadioType.GUARD:
                                channel.PttHotKey = new KeyboardBinding(KeyCode.VcF3);
                                channel.SquelchHotKey = _settings.BmsUhfSquelchHotkey;
                                channel.AudioChannel = _settings.BmsUhfAudioChannel;
                                break;
                            default:
                                _logger.LogWarning("Unknown radio type: " + type);
                                break;
                        }
                    }
                }
            }
        });
    }

    private void OnBmsPttChanged(object? sender, RadioPttChangedEventArgs e)
    {
        // Do NOT capture keys twice - for non-flying, we want to use the callbacks from our HotKey service
        if (!_hotkeyService.PttKeysPaused ||_falconSharedMemoryService.IsFlying == false)
            return;
        
        if (FalconChannelGroup == null)
        {
            _logger.LogWarning("Ignoring PTT: no Falcon channel group");
            return;
        }

        var channel = FalconChannelGroup.Channels.FirstOrDefault(c => c.BmsRadioType == e.RadioType);
        if (channel == null || channel.ConnectionStatus == Channel.ChannelConnectionStatus.Disconnected) return;
        switch (e)
        {
            // mute all incoming transmissions from this group which have the same channel type
            case { OldPtt: false, NewPtt: true }:
                var mutedFrequencies = FalconChannelGroup.GetAllFrequenciesOfChannelGroup(channel.Type);
                mutedFrequencies.Remove(channel.FrequencyKhz);
                _openFreqService.StartTransmissionAsync(channel.FrequencyKhz, mutedFrequencies).Wait();
                break;
            case { OldPtt: true, NewPtt: false }:
                _openFreqService.StopTransmissionAsync(channel.FrequencyKhz).Wait();
                break;
        }
    }

    private void OnBmsFrequencyChanged(object? sender, RadioFrequencyChangedEventArgs e)
    {
        if (FalconChannelGroup == null)
        {
            _logger.LogWarning("Unclean state: _falconChannelGroup is null, reimporting");
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
            if (FalconChannelGroup.ChangeChannelFrequency(e.OldFrequencyKhz, e.NewFrequencyKhz, channelIsPowerOn))
            {
                // Explicitly join the channel that was just updated if it's powered on
                // 9999 is BMS's "radio off" parking frequency - never join it
                var updatedChannel =
                    FalconChannelGroup.Channels.FirstOrDefault(c => c.FrequencyKhz == e.NewFrequencyKhz);
                if (updatedChannel != null && channelIsPowerOn &&
                    e.NewFrequencyKhz != IFalconRadioSharedMemoryService.BmsRadioOffFrequency)
                {
                    _logger.LogDebug($"Explicitly joining updated channel: {e.NewFrequencyKhz}");
                    updatedChannel.Join();
                }

                // Still make sure to join all channels - e.g. when switching back from guard mode
                foreach (var type in Enum.GetValues<RadioType>())
                {
                    var falconChannel = _falconRadioSharedMemoryService.GetRadioChannel(type);
                    if (falconChannel is not { IsOn: true }) continue;
                    if (falconChannel.Frequency == 9999) continue; // Skip parking frequency

                    foreach (var channel in FalconChannelGroup.Channels)
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
                var channel = FalconChannelGroup.CreateChannel(
                    e.NewFrequencyKhz,
                    BmsGroupName,
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
                JoinFrequencyAsync(newChannel.FrequencyKhz, FalconChannelGroup.RadioStationData)
                    .Wait(TimeSpan.FromMilliseconds(500));
            }

            switch (e.RadioType)
            {
                case RadioType.UHF:
                    newChannel.AudioChannel = _settings.BmsUhfAudioChannel;
                    break;
                case RadioType.VHF:
                    newChannel.AudioChannel = _settings.BmsVhfAudioChannel;
                    break;
                case RadioType.GUARD:
                    newChannel.AudioChannel = _settings.BmsUhfAudioChannel;
                    break;
            }
        }
    }


    private async Task HandleStartTransmissionAsync(StartTransmissionMessage msg)
    {
        if (!_openFreqService.IsAuthenticated) return;

        try
        {
            await _openFreqService.StartTransmissionAsync(msg.FrequencyKhz, msg.MutedRadioChannels);
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

    public async Task JoinFrequencyAsync(int frequencyKhz, RadioStationData radioStationData)
    {
        if (!_openFreqService.IsAuthenticated) return;

        try
        {
            await _openFreqService.JoinFrequencyAsync(frequencyKhz, radioStationData);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Failed to join frequency {frequencyKhz / 1000d:F3}: {ex.Message}");
        }
    }

    public async Task LeaveFrequencyAsync(int frequencyKhz)
    {
        if (!_openFreqService.IsAuthenticated) return;

        try
        {
            await _openFreqService.LeaveFrequencyAsync(frequencyKhz);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Failed to leave frequency {frequencyKhz / 1000d:F3}: {ex.Message}");
        }
    }

    public async Task LeaveAllChannelsAsync()
    {
        foreach (var channelGroup in ChannelGroups)
        {
            await channelGroup.LeaveAllChannelsAsync();
        }
    }

    public ChannelCardGroupViewModel CreateChannelGroup(string name, RadioStationPreset preset,
        RadioStationData.RadioStationType radioStationType,
        bool editMode = false)
    {
        var channelGroup = new ChannelCardGroupViewModel(_openFreqService, _hotkeyService, _acmiClientService,
            _settings, name,
            preset, radioStationType, editMode: editMode);
        AllChannelGroups.Add(channelGroup);
        return channelGroup;
    }

    public ChannelCardGroupViewModel CreateChannelGroup(ChannelGroupData channelGroupData, bool editMode = false)
    {
        var channelGroup = new ChannelCardGroupViewModel(_openFreqService, _hotkeyService, _acmiClientService,
            _settings, channelGroupData.Name, channelGroupData.RadioStationData.Preset,
            channelGroupData.RadioStationData.Type, channelGroupData.Latitude, channelGroupData.Longitude,
            channelGroupData.AltitudeFt, editMode);
        AllChannelGroups.Add(channelGroup);
        return channelGroup;
    }


    public async Task DeleteChannelGroup(ChannelCardGroupViewModel channelGroup)
    {
        await channelGroup.LeaveAllChannelsAsync();
        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            AllChannelGroups.Remove(channelGroup);
            channelGroup.Dispose();
        });
    }

    public async Task DeleteChannelGroup(Guid channelGroupId)
    {
        if (ChannelGroups.FirstOrDefault(cg => cg.Id == channelGroupId) is not { } channelCardGroupViewModel)
            return;

        if (await ConfirmationDialogService.ShowAsync(
                title: "Confirm deletion",
                message: $"Are you sure you want to delete the Channel Group \"{channelCardGroupViewModel.Name}\"?",
                cancelText: "Cancel",
                confirmText: "Delete"))
        {
            await DeleteChannelGroup(channelCardGroupViewModel);
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