using System;
using System.Collections.ObjectModel;
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
using OpenFreq.Services.Acmi;
using OpenFreqAudio;
using OpenFreqClient.Models;
using OpenFreqClient.Services.Interfaces;
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

    [ObservableProperty]
    public partial ObservableCollection<ChannelCardGroupViewModel> ChannelGroups { get; set; } = [];

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

        // Subscribe to BMS Frequency update messages
        _falconRadioSharedMemoryService.ConnectionParametersChanged +=
            OnConnectionParametersChanged;
        _falconRadioSharedMemoryService.FrequencyChanged += OnFrequencyChanged;
        _falconRadioSharedMemoryService.PttChanged += OnPttChanged;
        _falconRadioSharedMemoryService.PowerChanged += OnRadioPowerChanged;
        _falconRadioSharedMemoryService.VolumeChanged += OnRadioVolumeChanged;

        _falconSharedMemoryService.FlyingStateChanged += OnFlyingStateChanged;

        // Subscribe to transmission messages
        WeakReferenceMessenger.Default.Register<StartTransmissionMessage>(this,
            async (r, m) => await HandleStartTransmissionAsync(m));
        WeakReferenceMessenger.Default.Register<StopTransmissionMessage>(this,
            async (r, m) => await HandleStopTransmissionAsync(m));
    }

    private void OnRadioVolumeChanged(object? sender, RadioVolumeChangedEventArgs e)
    {
        if (FalconChannelGroup == null)
        {
            _logger.LogError("Attempting to change volume on null FalconChannelGroup");
            return;
        }

        _logger.LogDebug($"VOLUME {e.OldVolume} -> {e.NewVolume}");
        const int minBms = 1000;
        const int maxBms = 10000;

        // Invert and normalize to 0-1
        float normalized = (maxBms - e.NewVolume) / (float)(maxBms - minBms);
        // Clamp to valid range
        normalized = Math.Clamp(normalized, 0f, 1f);

        // Apply logarithmic curve (dB-like behavior)
        normalized *= normalized;

        var channels = FalconChannelGroup?.Channels.Where(c => c.Type == Channel.ToChannelType(e.RadioType)).ToList();
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

        var channels = FalconChannelGroup.Channels.Where(c => c.Type == Channel.ToChannelType(e.RadioType)).ToList();
        foreach (var channel in channels)
        {
            if (e.NewPower)
            {
                JoinFrequencyAsync(channel.FrequencyKhz, FalconChannelGroup.RadioStationData).Wait(100);
            }
            else
            {
                LeaveFrequencyAsync(channel.FrequencyKhz).Wait(100);
            }
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
        else if (e.NewParameters.ReadyToTransmit)
        {
            ImportBmsRadioChannels();
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

    private void ImportBmsRadioChannels(bool clearExisting = true)
    {
        Dispatcher.UIThread.Post(() =>
        {
            lock (_channelImportLock)
            {
                if (FalconChannelGroup == null)
                {
                    FalconChannelGroup = CreateChannelGroup(BMS_GROUP_NAME, RadioStationPresets.Fighter, RadioStationData.RadioStationType.BMS);
                }

                else if (clearExisting)
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
                            Channel.ToChannelType(type),
                            false);

                        switch (type)
                        {
                            case RadioType.VHF:
                                channel.HotKey = KeyCode.VcF1;
                                break;
                            case RadioType.UHF:
                                channel.HotKey = KeyCode.VcF2;
                                break;
                            case RadioType.GUARD:
                                channel.HotKey = KeyCode.VcF3;
                                break;
                            default:
                                // dont care
                                break;
                        }
                    }
                }

                if (_openFreqService.IsAuthenticated)
                {
                    JoinAllChannelsAsync().Wait(TimeSpan.FromSeconds(2));
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

        var channel = FalconChannelGroup.Channels.FirstOrDefault(c => c.Type == Channel.ToChannelType(e.RadioType));
        if (channel == null || channel.Status == Channel.ChannelStatus.Disconnected) return;
        switch (e)
        {
            case { OldPtt: false, NewPtt: true }:
                _openFreqService.StartTransmissionAsync(channel.FrequencyKhz, FalconChannelGroup.RadioStationData)
                    .Wait();
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
            ImportBmsRadioChannels();
            return;
        }

        _logger.LogDebug(
            $"FalconRadioSharedMemoryServiceOnFrequencyChanged: {e.OldFrequencyKhz} -> {e.NewFrequencyKhz}");

        if (FalconChannelGroup.ChangeChannelFrequency(e.OldFrequencyKhz, e.NewFrequencyKhz,
                Channel.ToChannelType(e.RadioType))) return;

        lock (_channelImportLock)
        {
            var newChannel = Dispatcher.UIThread.InvokeAsync(() =>
            {
                var channel = FalconChannelGroup.CreateChannel(
                    e.NewFrequencyKhz,
                    BMS_GROUP_NAME,
                    Channel.ToChannelType(e.RadioType),
                    false);
                return channel;
            }).GetAwaiter().GetResult();
            JoinFrequencyAsync(newChannel.FrequencyKhz, FalconChannelGroup.RadioStationData)
                .Wait(TimeSpan.FromMilliseconds(500));
        }
    }


    private async Task HandleStartTransmissionAsync(StartTransmissionMessage msg)
    {
        if (!_openFreqService.IsAuthenticated) return;

        try
        {
            await _openFreqService.StartTransmissionAsync(msg.FrequencyKhz, msg.RadioStationData);
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
            Console.WriteLine($"Failed to join frequency {frequencyKhz / 1000:F3}: {ex.Message}");
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
            Console.WriteLine($"Failed to leave frequency {frequencyKhz / 1000:F3}: {ex.Message}");
        }
    }

    public async Task JoinAllChannelsAsync()
    {
        foreach (var channelGroup in ChannelGroups)
        {
            await channelGroup.JoinAllChannelsAsync();
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
        ChannelGroups.Add(channelGroup);
        return channelGroup;
    }

    public ChannelCardGroupViewModel CreateChannelGroup(ChannelGroupData channelGroupData, bool editMode = false)
    {
        var channelGroup = new ChannelCardGroupViewModel(_openFreqService, _hotkeyService, _acmiClientService,
            _settings, channelGroupData.Name, channelGroupData.RadioStationData.Preset,
            channelGroupData.RadioStationData.Type, channelGroupData.Latitude, channelGroupData.Longitude, editMode);
        ChannelGroups.Add(channelGroup);
        return channelGroup;
    }


    public void DeleteChannelGroup(ChannelCardGroupViewModel channelGroup)
    {
        channelGroup.LeaveAllChannelsAsync().Wait(100);
        Dispatcher.UIThread.InvokeAsync(() =>
        {
            ChannelGroups.Remove(channelGroup);
            channelGroup.Dispose();
        });
    }

    public void Dispose()
    {
        _falconRadioSharedMemoryService.ConnectionParametersChanged -= OnConnectionParametersChanged;
        _falconRadioSharedMemoryService.FrequencyChanged -= OnFrequencyChanged;
        _falconRadioSharedMemoryService.PttChanged -= OnPttChanged;
        _falconSharedMemoryService.FlyingStateChanged -= OnFlyingStateChanged;
    }
}