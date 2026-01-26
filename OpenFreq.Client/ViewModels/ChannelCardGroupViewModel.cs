using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Net.Mime;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CommunityToolkit.Mvvm.Messaging;
using OpenFreq.Client.Models;
using OpenFreq.Common;
using OpenFreq.Services.Acmi;
using OpenFreq.Utilities;
using OpenFreqAudio;
using OpenFreqClient.Models;
using OpenFreqClient.Services;
using OpenFreqClient.Services.Interfaces;
using OpenFreqClient.Views;

namespace OpenFreqClient.ViewModels;

public partial class ChannelCardGroupViewModel : ViewModelBase, IDisposable
{
    public SettingsViewModel Settings { get; }

    [ObservableProperty] public partial string Name { get; set; }
    [ObservableProperty] public partial bool UseFixedPosition { get; set; }
    [ObservableProperty] public partial bool EditMode { get; set; }

    public record TacviewAircraftItem(string CallSign, string ObjectId)
    {
        public override string ToString() => CallSign;
    }

    [ObservableProperty] private ObservableCollection<TacviewAircraftItem> _tacviewFlightCallsigns = [];
    [ObservableProperty] private TacviewAircraftItem? _selectedTacviewCallsign;
    
    public RadioStationData RadioStationData { get; } = new();
    [ObservableProperty] public partial RadioStationPreset GroupPreset { get; set; }

    private readonly IOpenFreqService _openFreqService;
    private readonly IHotkeyService _hotkeyService;
    private readonly IAcmiClientService _acmiClientService;

    [ObservableProperty] public partial ObservableCollection<ChannelCardViewModel> Channels { get; set; } = [];

    private readonly CancellationTokenSource? _callsignUpdateCts = new();
    [ObservableProperty] public partial bool IsBmsGroup { get; set; }

    [ObservableProperty] public partial double Latitude { get; set; }
    [ObservableProperty] public partial double Longitude { get; set; }
    [ObservableProperty] public partial string LatLonInput { get; set; } = "";
    [ObservableProperty] public partial double AltitudeInput { get; set; } = 0d;
    [ObservableProperty] public partial string? CoordinateError { get; set; }
    [ObservableProperty] public partial bool HasCoordinateError { get; set; }

    private bool _isUpdatingFromInput = false;


    public ChannelCardGroupViewModel(IOpenFreqService openFreqService, IHotkeyService hotkeyService,
        IAcmiClientService acmiClientService, SettingsViewModel settingsViewModel, string name,
        RadioStationPreset preset, bool isBmsGroup, bool editMode = true)
    {
        _openFreqService = openFreqService;
        _hotkeyService = hotkeyService;
        Settings = settingsViewModel;
        Name = name;
        GroupPreset = preset;
        RadioStationData.Preset = preset;
        _acmiClientService = acmiClientService;
        IsBmsGroup = isBmsGroup;
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

        if (isBmsGroup)
        {
            RadioStationData.Type = RadioStationData.RadioStationType.BMS;
        }
        else
        {
            // For non-BMS groups, default to stationary
            RadioStationData.Type = RadioStationData.RadioStationType.STATIONARY;
            // Initialize position for stationary radios
            RadioStationData.Position = new Position(0d, 0d, 0d);
        }
    }

    public ChannelCardViewModel CreateChannel(double frequencyMhz, string name, Channel.ChannelType channelType,
        bool isInEditMode = true)
    {
        var channel = new ChannelCardViewModel(_hotkeyService, RadioStationData);
        channel.Name = name;
        channel.Type = channelType;
        channel.FrequencyMhz = frequencyMhz;
        channel.IsEditing = isInEditMode;

        Channels.Add(channel);
        return channel;
    }

    public ChannelCardViewModel CreateChannel(Channel channel)
    {
        var viewModel = new ChannelCardViewModel(_hotkeyService, channel, RadioStationData);
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
            _openFreqService.JoinFrequencyAsync(message.NewFrequencyMhz, RadioStationData)
                .Wait(TimeSpan.FromMilliseconds(100));
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
            _openFreqService.JoinFrequencyAsync(message.FrequencyMhz, RadioStationData)
                .Wait(TimeSpan.FromMilliseconds(100));
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
                    await _openFreqService.StartTransmissionAsync(channel.FrequencyMhz, RadioStationData);
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

        oldChannel.FrequencyMhz = newFreqMhz;
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
            await _openFreqService.JoinFrequencyAsync(channel.FrequencyMhz, RadioStationData);
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
        EditMode = !EditMode;
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

    partial void OnLatLonInputChanged(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            CoordinateError = null;
            HasCoordinateError = false;
            return;
        }

        var parts = value.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length != 2)
        {
            CoordinateError = "Please enter both latitude and longitude separated by a space";
            HasCoordinateError = true;
            return;
        }

        if (!double.TryParse(parts[0], out var lat) ||
            !double.TryParse(parts[1], out var lon))
        {
            CoordinateError = "Invalid coordinate format. Please enter valid numbers";
            HasCoordinateError = true;
            return;
        }

        // Round to 5 decimal places
        lat = Math.Round(lat, 5);
        lon = Math.Round(lon, 5);

        // Check theater bounds
        if (!TheaterCoordinateConverter.IsWithinTheaterBounds(Settings.SelectedTheater, lat, lon))
        {
            CoordinateError = "Coordinates are outside the theater bounds";
            HasCoordinateError = true;
            return;
        }

        // All validation passed
        CoordinateError = null;
        HasCoordinateError = false;

        _isUpdatingFromInput = true;
        Latitude = lat;
        Longitude = lon;
        _isUpdatingFromInput = false;
    }

    partial void OnLatitudeChanged(double value)
    {
        UpdatePosition(value, Longitude);
        if (!_isUpdatingFromInput)
        {
            LatLonInput = $"{value:F5} {Longitude:F5}";
        }
    }

    partial void OnLongitudeChanged(double value)
    {
        UpdatePosition(Latitude, value);
        if (!_isUpdatingFromInput)
        {
            LatLonInput = $"{Latitude:F5} {value:F5}";
        }
    }

    private void UpdatePosition(double lat, double lon)
    {
        if (RadioStationData.Position == null)
        {
            RadioStationData.Position = new Position(0d, 0d, 0d);
        }
        var xy = TheaterCoordinateConverter.LatLonToXY(
            Settings.SelectedTheater,
            lat,
            lon,
            TheaterCoordinateConverter.CoordinateSystem.BMS_HEIGHTMAP_COORDINATE_SYTEM);
        RadioStationData.Position.X = xy.x;
        RadioStationData.Position.Y = xy.y;
    }

    partial void OnGroupPresetChanged(RadioStationPreset value)
    {
        RadioStationData.Preset = value;
    }

    partial void OnAltitudeInputChanged(double value)
    {
        const double FEET_PER_METER = 3.28084d;
        RadioStationData.Position ??= new Position(0d, 0d, 0d);
        RadioStationData.Position.Z = value / FEET_PER_METER;
    }
    
    [RelayCommand]
    private async Task OpenMapPickerAsync()
    {
        var window = new MapPickerWindow(Latitude, Longitude, Settings.SelectedTheater);
    
        var result = await window.ShowDialog<(double lat, double lon)?>(
            (Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop
                ? desktop.MainWindow
                : null) ?? throw new InvalidOperationException());
    
        if (result.HasValue)
        {
            Latitude = result.Value.lat;
            Longitude = result.Value.lon;
        }
    }
}