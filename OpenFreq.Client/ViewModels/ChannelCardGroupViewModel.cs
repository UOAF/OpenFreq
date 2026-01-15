using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CommunityToolkit.Mvvm.Messaging;
using OpenFreq.Common;
using OpenFreq.Services.Acmi;
using OpenFreqAudio;
using OpenFreqClient.Models;
using OpenFreqClient.Services;
using OpenFreqClient.Services.Interfaces;

namespace OpenFreqClient.ViewModels;

public partial class ChannelCardGroupViewModel : ViewModelBase, IDisposable
{
    [ObservableProperty] public partial string Name { get; set; }
    [ObservableProperty] public partial double TxPowerDbm { get; set; }
    [ObservableProperty] public partial double RxSensitivityDbm { get; set; }
    [ObservableProperty] public partial double AntennaElevationM { get; set; }
    [ObservableProperty] public partial Position Position { get; set; }
    [ObservableProperty] public partial string? AcmiTrackingId { get; set; }
    [ObservableProperty] public partial bool FixedPosition { get; set; }
    [ObservableProperty] public partial bool EditMode { get; set; }
    
    public record TacviewAircraftItem(string CallSign, string ObjectId)
    {
        public override string ToString() => CallSign;
    }
    
    [ObservableProperty] private ObservableCollection<TacviewAircraftItem> _tacviewFlightCallsigns = [];
    [ObservableProperty] private TacviewAircraftItem? _selectedTacviewCallsign;
    
    [ObservableProperty] public partial RadioStationPreset GroupPreset { get; set; } = RadioStationPresets.AWACS;

    private readonly IOpenFreqService _openFreqService;
    private readonly IHotkeyService _hotkeyService;
    private readonly IAcmiClientService _acmiClientService;

    [ObservableProperty] public partial ObservableCollection<ChannelCardViewModel> Channels { get; set; } = [];
    
    private readonly CancellationTokenSource? _callsignUpdateCts = new();


    public ChannelCardGroupViewModel(IOpenFreqService openFreqService, IHotkeyService hotkeyService, string name,
        double txPowerDbm, double rxSensitivityDbm, double antennaElevationM, Position position,
        string? acmiTrackingId, IAcmiClientService acmiClientService, bool fixedPosition = true, bool editMode = true)
    {
        _openFreqService = openFreqService;
        _hotkeyService = hotkeyService;
        Name = name;
        TxPowerDbm = txPowerDbm;
        RxSensitivityDbm = rxSensitivityDbm;
        AntennaElevationM = antennaElevationM;
        Position = position;
        AcmiTrackingId = acmiTrackingId;
        _acmiClientService = acmiClientService;
        FixedPosition = fixedPosition; 
        EditMode = editMode;

        // Subscribe to connection state for auto-join
        _openFreqService.ConnectionStateChanged += OnConnectionStateChanged;

        // Subscribe to frequency status changes
        _openFreqService.FrequencyStatusChanged += OnFrequencyStatusChanged;

        // Subscribe to hotkey events
        _hotkeyService.HotkeyPressed += OnHotkeyPressed;
        _hotkeyService.HotkeyReleased += OnHotkeyReleased;

        // Subscribe to channel updates for binding changes
        WeakReferenceMessenger.Default.Register<ChannelUpdatedMessage>(this, OnChannelUpdated);
        WeakReferenceMessenger.Default.Register<ChannelEnabledDisabledMessage>(this, OnChannelEnabledDisabled);
        WeakReferenceMessenger.Default.Register<ChannelDeleteRequestedMessage>(this, OnChannelDeleteRequested);

        GroupPreset.FixedPosition ??= new Position(0d, 0d, 0d);
    }

    public ChannelCardViewModel CreateChannel(double frequencyMhz, string name, Channel.ChannelType channelType,
        bool isInEditMode = true)
    {
        var channel = new ChannelCardViewModel(_hotkeyService, GroupPreset);
        channel.Name = name;
        channel.Type = channelType;
        channel.FrequencyMhz = frequencyMhz;
        channel.IsEditing = isInEditMode;

        Channels.Add(channel);
        return channel;
    }

    public ChannelCardViewModel CreateChannel(Channel channel)
    {
        var viewModel = new ChannelCardViewModel(_hotkeyService, channel, GroupPreset);
        Channels.Add(viewModel);
        return viewModel;
    }

    private void OnChannelUpdated(object recipient, ChannelUpdatedMessage message)
    {
        if (message.NeedsReconnect && _openFreqService.IsAuthenticated)
        {
            // Only leave old frequency if the channel was previously connected
            if (message.OldStatus != Channel.ChannelStatus.Disconnected)
            {
                _openFreqService.LeaveFrequencyAsync(message.OldFrequencyMhz).Wait(TimeSpan.FromMilliseconds(100));
            }

            // Always join the new frequency
            _openFreqService.JoinFrequencyAsync(message.NewFrequencyMhz, GroupPreset).Wait(TimeSpan.FromMilliseconds(100));
        }
    }

    private void OnConnectionStateChanged(object? sender, ConnectionState state)
    {
        if (state == ConnectionState.Authenticated)
        {
            // Auto-join all channels when authenticated
            JoinAllChannelsAsync().Wait(TimeSpan.FromMilliseconds(500));
        }
        else if (state == ConnectionState.Disconnected)
        {
            // Reset all channel status on disconnect
            foreach (var channel in Channels)
            {
                channel.Status = Channel.ChannelStatus.Disconnected;
            }
        }
    }

    private void OnFrequencyStatusChanged(object? sender, FrequencyStatusEventArgs e)
    {
        var channel = Channels.FirstOrDefault(c => Math.Abs(c.FrequencyMhz - e.FrequencyMhz) < 0.01);
        channel?.Status = e.Status;
    }

    private void OnChannelEnabledDisabled(object recipient, ChannelEnabledDisabledMessage message)
    {
        if (_openFreqService.IsAuthenticated && message.Enabled)
        {
            _openFreqService.JoinFrequencyAsync(message.FrequencyMhz, GroupPreset).Wait(TimeSpan.FromMilliseconds(100));
        }
        else if (_openFreqService.IsAuthenticated && !message.Enabled)
        {
            _openFreqService.LeaveFrequencyAsync(message.FrequencyMhz).Wait(TimeSpan.FromMilliseconds(100));
        }

        // dont care for the rest
    }

    private void OnChannelDeleteRequested(object recipient, ChannelDeleteRequestedMessage message)
    {
        var vm = Channels.FirstOrDefault(c => c.Id == message.ChannelId);

        if (vm != null)
        {
            Channels.Remove(vm);
            vm.Dispose();
        }

        if (_openFreqService.IsAuthenticated)
        {
            _openFreqService.LeaveFrequencyAsync(message.FrequencyMhz);
        }
    }

    private async void OnHotkeyPressed(object? sender, HotkeyPressedEventArgs e)
    {
        try
        {
            foreach (var channelId in e.ChannelIds)
            {
                var channel = Channels.FirstOrDefault(c => c.Id == channelId);
                if (channel != null && channel.Status != Channel.ChannelStatus.Disconnected && !channel.IsEditing)
                {
                    await _openFreqService.StartTransmissionAsync(channel.FrequencyMhz, GroupPreset);
                }
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Error in hotkey press: {ex.Message}");
        }
    }

    private async void OnHotkeyReleased(object? sender, HotkeyReleasedEventArgs e)
    {
        try
        {
            foreach (var channelId in e.ChannelIds)
            {
                var channel = Channels.FirstOrDefault(c => c.Id == channelId);
                if (channel != null && channel.Status != Channel.ChannelStatus.Disconnected)
                {
                    await _openFreqService.StopTransmissionAsync(channel.FrequencyMhz);
                }
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Error in hotkey release: {ex.Message}");
        }
    }

    public bool ChangeChannelFrequency(double oldFreqMhz, double newFreqMhz, Channel.ChannelType newChannelType)
    {
        var oldChannel =
            Channels.FirstOrDefault(c => Math.Abs(c.FrequencyMhz - oldFreqMhz) < 0.01);
        if (oldChannel == null) return false;

        oldChannel.FrequencyMhz = oldFreqMhz;
        OnChannelUpdated(this,
            new ChannelUpdatedMessage(oldChannel.Id, oldFreqMhz, newFreqMhz,
                oldChannel.Type, newChannelType, oldChannel.Status, oldChannel.HotKey,
                oldChannel.HotKey));

        return true;
    }

    public async Task JoinAllChannelsAsync()
    {
        foreach (var channel in Channels)
        {
            await _openFreqService.JoinFrequencyAsync(channel.FrequencyMhz, GroupPreset);
        }
    }

    public async Task LeaveAllChannelsAsync()
    {
        foreach (var channel in Channels)
        {
            if (channel.Status != Channel.ChannelStatus.Disconnected)
            {
                await _openFreqService.LeaveFrequencyAsync(channel.FrequencyMhz);
            }
        }
    }

    public void Dispose()
    {
        _hotkeyService.HotkeyPressed -= OnHotkeyPressed;
        _hotkeyService.HotkeyReleased -= OnHotkeyReleased;
        _openFreqService.FrequencyStatusChanged -= OnFrequencyStatusChanged;
        _openFreqService.ConnectionStateChanged -= OnConnectionStateChanged;
        
        _callsignUpdateCts?.Cancel();
        _callsignUpdateCts?.Dispose();

        WeakReferenceMessenger.Default.Unregister<ChannelUpdatedMessage>(this);
        WeakReferenceMessenger.Default.Unregister<StartTransmissionMessage>(this);
        WeakReferenceMessenger.Default.Unregister<StopTransmissionMessage>(this);
    }
    
    partial void OnSelectedTacviewCallsignChanged(TacviewAircraftItem? oldValue, TacviewAircraftItem? newValue)
    {
        _acmiClientService.RemoveTrackingForAircraft(oldValue.ObjectId);
        _acmiClientService.AddTrackingForAircraft(newValue.ObjectId);
    }

    [RelayCommand]
    public void AddChannel()
    {
    }

    [RelayCommand]
    public void JoinAll()
    {
    }

    [RelayCommand]
    public void ToggleEditing()
    {
        EditMode =  !EditMode;
    }
    
    [RelayCommand]
    private async Task UpdateTacviewCallsigns(CancellationToken cancellationToken)
    {
        while (_acmiClientService.Status == AcmiConnectionStatus.Connected
               && !cancellationToken.IsCancellationRequested)
        {
            if (SelectedTacviewCallsign != null)
            {
                var aircraft = _acmiClientService.GetAircraft(SelectedTacviewCallsign.ObjectId);
            }

            var currentAircraft = _acmiClientService.GetAllAircraft()
                .Select(ac => new TacviewAircraftItem(ac.CallSign, ac.ObjectId))
                .ToList();

            if (currentAircraft.Count > 0)
            {
                _callsignUpdateCts?.Cancel(false);
                
            }

            // Incremental update
            var currentIds = currentAircraft.Select(a => a.ObjectId).ToHashSet();

            // Remove items no longer present
            for (int i = TacviewFlightCallsigns.Count - 1; i >= 0; i--)
            {
                if (!currentIds.Contains(TacviewFlightCallsigns[i].ObjectId))
                {
                    TacviewFlightCallsigns.RemoveAt(i);
                }
            }

            // Add new items
            var existingIds = TacviewFlightCallsigns.Select(a => a.ObjectId).ToHashSet();
            foreach (var aircraft in currentAircraft)
            {
                if (aircraft.CallSign != string.Empty && !existingIds.Contains(aircraft.ObjectId))
                {
                    TacviewFlightCallsigns.Add(aircraft);
                }
            }

            try
            {
                await Task.Delay(1000, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                // Expected when cancellation is requested
                break;
            }
        }
    }

    partial void OnGroupPresetChanged(RadioStationPreset value)
    {
        foreach (var channelCardViewModel in Channels)
        {
            channelCardViewModel.Preset = value;
        }
    }
}