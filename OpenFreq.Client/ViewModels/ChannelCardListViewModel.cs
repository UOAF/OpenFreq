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
using OpenFreq.Common;
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

    private readonly string BMS_GROUP_NAME = "BMS Channels";
    public ChannelCardGroupViewModel? FalconChannelGroup { get; private set; }

    private readonly Lock _channelImportLock = new();

    [ObservableProperty]
    public partial ObservableCollection<ChannelCardGroupViewModel> ChannelGroups { get; set; } = [];

    public ChannelCardListViewModel(IOpenFreqService openFreqService, IHotkeyService hotkeyService,
        IAcmiClientService acmiClientService, ILogger<ChannelCardListViewModel> logger,
        IFalconRadioSharedMemoryService falconRadioSharedMemoryService,
        IFalconSharedMemoryService falconSharedMemoryService)
    {
        _openFreqService = openFreqService;
        _hotkeyService = hotkeyService;
        _acmiClientService = acmiClientService;
        _logger = logger;
        _falconRadioSharedMemoryService = falconRadioSharedMemoryService;
        _falconSharedMemoryService = falconSharedMemoryService;

        // Subscribe to BMS Frequency update messages
        _falconRadioSharedMemoryService.ConnectionParametersChanged +=
            OnConnectionParametersChanged;
        _falconRadioSharedMemoryService.FrequencyChanged += OnFrequencyChanged;
        _falconRadioSharedMemoryService.PttChanged += OnPttChanged;
        _falconSharedMemoryService.FlyingStateChanged += OnFlyingStateChanged;


        // Subscribe to transmission messages
        WeakReferenceMessenger.Default.Register<StartTransmissionMessage>(this,
            async (r, m) => await HandleStartTransmissionAsync(m));
        WeakReferenceMessenger.Default.Register<StopTransmissionMessage>(this,
            async (r, m) => await HandleStopTransmissionAsync(m));
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
                    FalconChannelGroup = CreateChannelGroup(BMS_GROUP_NAME, RadioStationPresets.Fighter, true);
                }

                else if (clearExisting)
                {
                    FalconChannelGroup.LeaveAllChannelsAsync().Wait(300);
                    FalconChannelGroup.Channels.Clear();
                }

                foreach (var type in Enum.GetValues<RadioType>())
                {
                    var falconChannel = _falconRadioSharedMemoryService.GetRadioChannel(type);
                    if (falconChannel != null && !FalconChannelGroup.Channels.Any(c =>
                            Math.Abs(c.FrequencyMhz - falconChannel.Frequency / 1000d) < 0.1d))
                    {
                        var channel = FalconChannelGroup.CreateChannel(falconChannel.Frequency / 1000d,
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
                _openFreqService.StartTransmissionAsync(channel.FrequencyMhz, FalconChannelGroup.GroupPreset).Wait();
                break;
            case { OldPtt: true, NewPtt: false }:
                _openFreqService.StopTransmissionAsync(channel.FrequencyMhz).Wait();
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

        if (FalconChannelGroup.ChangeChannelFrequency(e.OldFrequencyKhz / 1000d, e.NewFrequencyKhz / 1000d,
                Channel.ToChannelType(e.RadioType))) return;

        lock (_channelImportLock)
        {
            var newChannel = Dispatcher.UIThread.InvokeAsync(() =>
            {
                return FalconChannelGroup.CreateChannel(
                    e.NewFrequencyKhz / 1000d,
                    BMS_GROUP_NAME,
                    Channel.ToChannelType(e.RadioType),
                    false);
            }).GetAwaiter().GetResult();
            JoinFrequencyAsync(newChannel.FrequencyMhz, FalconChannelGroup.GroupPreset)
                .Wait(TimeSpan.FromMilliseconds(500));
        }
    }


    private async Task HandleStartTransmissionAsync(StartTransmissionMessage msg)
    {
        if (!_openFreqService.IsAuthenticated) return;

        try
        {
            await _openFreqService.StartTransmissionAsync(msg.FrequencyMhz, msg.stationPreset);
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
            await _openFreqService.StopTransmissionAsync(msg.FrequencyMhz);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Failed to stop transmission: {ex.Message}");
        }
    }

    public async Task JoinFrequencyAsync(double frequencyMhz, RadioStationPreset preset)
    {
        if (!_openFreqService.IsAuthenticated) return;

        try
        {
            await _openFreqService.JoinFrequencyAsync(frequencyMhz, preset);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Failed to join frequency {frequencyMhz}: {ex.Message}");
        }
    }

    public async Task LeaveFrequencyAsync(double frequency)
    {
        if (!_openFreqService.IsAuthenticated) return;

        try
        {
            await _openFreqService.LeaveFrequencyAsync(frequency);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Failed to leave frequency {frequency}: {ex.Message}");
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

    public ChannelCardGroupViewModel CreateChannelGroup(string name, RadioStationPreset preset, bool isBmsGroup = false,
        bool editMode = false)
    {
        var channelGroup = new ChannelCardGroupViewModel(_openFreqService, _hotkeyService, _acmiClientService, name,
            preset, isBmsGroup, editMode);
        ChannelGroups.Add(channelGroup);
        return channelGroup;
    }

    public ChannelCardGroupViewModel CreateChannelGroup(ChannelGroupData channelGroupData, bool isBmsGroup = false,
        bool editMode = false)
    {
        return CreateChannelGroup(channelGroupData.Name, channelGroupData.Preset, isBmsGroup, editMode);
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