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

    private readonly string BMS_GROUP_NAME = "BMS Channels";
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
        _falconRadioSharedMemoryService.FrequencyChanged += OnFrequencyChanged;
        _falconRadioSharedMemoryService.PttChanged += OnPttChanged;
        _falconRadioSharedMemoryService.PowerChanged += OnRadioPowerChanged;
        _falconRadioSharedMemoryService.VolumeChanged += OnRadioVolumeChanged;
        _falconSharedMemoryService.FlyingStateChanged += OnFlyingStateChanged;

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
                    _logger.LogDebug("BMS channels imported, joining...");
                    if (FalconChannelGroup != null)
                    {
                        await FalconChannelGroup.JoinAllChannelsAsync();
                    }
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

        // BMS dB scale
        const float dbMin = -6.0f; // +6dB boost at DX=0
        const float dbMax = 40.0f; // -40dB attenuation at DX=10000
        const float dxMin = 0.0f; // BMS formula uses full 0-10000 internally
        const float dxMax = 10000.0f;

        // Convert DX value to dB (matching BMS RADIOVOLUMERESCALE_DX_TO_DB)
        var dB = ((e.NewVolume - dxMin) * (dbMax - dbMin) / (dxMax - dxMin)) + dbMin;

        // Negate for attenuation (matching BMS sprintf line: -vol)
        var attenuationDb = -dB;

        // Convert dB to linear amplitude: amplitude = 10^(dB/20)
        var amplitude = MathF.Pow(10.0f, attenuationDb / 20.0f);

        // Allow boost up to +6dB like BMS does
        var normalized = Math.Clamp(amplitude, 0f, 2f);

        var channels = FalconChannelGroup?.Channels.Where(c => c.BmsRadioType == e.RadioType).ToList();
        if (channels != null)
            foreach (var channel in channels)
            {
                _openFreqService.SetVolume(channel.FrequencyKhz, normalized);
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
            channel.IsEnabled = e.NewPower;
        }
    }

    private void OnConnectionParametersChanged(object? sender,
        ConnectionParametersChangedEventArgs e)
    {
        if (e.NewParameters.TerminateClient)
        {
            if (FalconChannelGroup == null) return;
            DeleteChannelGroup(FalconChannelGroup);
            FalconChannelGroup = null;
        }
    }

    private void OnFlyingStateChanged(object? sender, FlyingStateChangedEventArgs e)
    {
        _logger.LogDebug($"FalconSharedMemoryServiceOnFlyingStateChanged: {e.OldFlyingState} -> {e.NewFlyingState}");
        if (!e.OldFlyingState && e.NewFlyingState)
        {
            _hotkeyService.Pause();
        }
        else if (e.OldFlyingState && !e.NewFlyingState)
        {
            _hotkeyService.Resume();
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
                    FalconChannelGroup = CreateChannelGroup(BMS_GROUP_NAME, RadioStationPresets.Fighter,
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
                        var channelIsPowerOn = _falconRadioSharedMemoryService.GetRadioChannel(type)?.IsOn ?? false;
                        channel.IsEnabled = channelIsPowerOn;


                        // set hotkeys and AudioChannel from Settings
                        switch (type)
                        {
                            case RadioType.VHF:
                                channel.HotKey = KeyCode.VcF1;
                                channel.AudioChannel = _settings.BmsVhfAudioChannel;
                                break;
                            case RadioType.UHF:
                                channel.HotKey = KeyCode.VcF2;
                                channel.AudioChannel = _settings.BmsUhfAudioChannel;
                                break;
                            case RadioType.GUARD:
                                channel.HotKey = KeyCode.VcF2;
                                channel.AudioChannel = _settings.BmsUhfAudioChannel;
                                break;
                        }
                    }
                }
            }
        });
    }

    private void OnPttChanged(object? sender, RadioPttChangedEventArgs e)
    {
        if (FalconChannelGroup == null)
        {
            _logger.LogWarning("Ignoring PTT: no Falcon channel group");
            return;
        }

        var channel = FalconChannelGroup.Channels.FirstOrDefault(c => c.BmsRadioType == e.RadioType);
        if (channel == null || channel.Status == Channel.ChannelStatus.Disconnected) return;
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

    private void OnFrequencyChanged(object? sender, RadioFrequencyChangedEventArgs e)
    {
        if (FalconChannelGroup == null)
        {
            _logger.LogWarning("Unclean state: _falconChannelGroup is null, reimporting");
            ImportBmsRadioChannels().Wait(100);
            return;
        }

        _logger.LogDebug(
            $"FalconRadioSharedMemoryServiceOnFrequencyChanged: {e.OldFrequencyKhz} -> {e.NewFrequencyKhz}");


        // make sure we set the power correctly
        var channelIsPowerOn = _falconRadioSharedMemoryService.GetRadioChannel(e.RadioType)?.IsOn ?? false;
        if (FalconChannelGroup.ChangeChannelFrequency(e.OldFrequencyKhz, e.NewFrequencyKhz, channelIsPowerOn))
        {
            // we still need to join all other channels in case we can resolve a previous double-join (e.g. with switching to GRD)
            FalconChannelGroup.JoinAllChannelsAsync().Wait(100);
            return;
        }

        lock (_channelImportLock)
        {
            var newChannel = Dispatcher.UIThread.InvokeAsync(() =>
            {
                var channel = FalconChannelGroup.CreateChannel(
                    e.NewFrequencyKhz,
                    BMS_GROUP_NAME,
                    false);

                channel.IsEnabled = channelIsPowerOn;
                return channel;
            }).GetAwaiter().GetResult();
            JoinFrequencyAsync(newChannel.FrequencyKhz, FalconChannelGroup.RadioStationData, newChannel.IsEnabled)
                .Wait(TimeSpan.FromMilliseconds(500));

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

    public async Task JoinFrequencyAsync(int frequencyKhz, RadioStationData radioStationData, bool isEnabled)
    {
        if (!_openFreqService.IsAuthenticated) return;

        try
        {
            await _openFreqService.JoinFrequencyAsync(frequencyKhz, radioStationData, isEnabled);
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
            channelGroupData.RadioStationData.Type, channelGroupData.Latitude, channelGroupData.Longitude, editMode);
        AllChannelGroups.Add(channelGroup);
        return channelGroup;
    }


    public void DeleteChannelGroup(ChannelCardGroupViewModel channelGroup)
    {
        channelGroup.LeaveAllChannelsAsync().Wait(100);
        Dispatcher.UIThread.InvokeAsync(() =>
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
            DeleteChannelGroup(channelCardGroupViewModel);
        }
    }

    public void Dispose()
    {
        _falconRadioSharedMemoryService.ConnectionParametersChanged -= OnConnectionParametersChanged;
        _falconRadioSharedMemoryService.FrequencyChanged -= OnFrequencyChanged;
        _falconRadioSharedMemoryService.PttChanged -= OnPttChanged;
        _falconSharedMemoryService.FlyingStateChanged -= OnFlyingStateChanged;
        _settings.PropertyChanged -= OnSettingsChanged;
    }
}