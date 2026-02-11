using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CommunityToolkit.Mvvm.Messaging;
using FalconBmsDataService.Models;
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
    public Guid Id { get; } = Guid.NewGuid();
    [ObservableProperty] public partial SettingsViewModel Settings { get; set; }

    [ObservableProperty] public partial string Name { get; set; }

    [ObservableProperty] public partial bool EditMode { get; set; }

    [ObservableProperty] public partial bool IsAcmiConnected { get; set; }

    public record TacviewAircraftItem(string CallSign, string ObjectId)
    {
        public override string ToString() => CallSign;
    }

    [ObservableProperty] private ObservableCollection<TacviewAircraftItem> _tacviewFlightCallsigns = [];
    [ObservableProperty] private TacviewAircraftItem? _selectedTacviewCallsign;

    public RadioStationData RadioStationData { get; private set; }

    private readonly IOpenFreqService _openFreqService;
    private readonly IHotkeyService _hotkeyService;
    private readonly IAcmiClientService _acmiClientService;

    [ObservableProperty] public partial ObservableCollection<ChannelCardViewModel> Channels { get; set; } = [];

    private readonly CancellationTokenSource? _callsignUpdateCts = new();

    // UI Properties
    [ObservableProperty] public partial double Latitude { get; set; }
    [ObservableProperty] public partial double Longitude { get; set; }
    [ObservableProperty] public partial string LatLonInput { get; set; } = "";
    [ObservableProperty] public partial double? AltitudeInput { get; set; }
    [ObservableProperty] public partial string? CoordinateError { get; set; }
    [ObservableProperty] public partial bool HasCoordinateError { get; set; }

    private bool _isUpdatingFromInput;
    
    private MapPickerWindow? _trackingWindow;
    private CancellationTokenSource? _trackingCts;
    [ObservableProperty] public partial bool IsTracking { get; set; }

    public ChannelCardGroupViewModel(IOpenFreqService openFreqService, IHotkeyService hotkeyService,
        IAcmiClientService acmiClientService, SettingsViewModel settingsViewModel, string name,
        RadioStationPreset preset, RadioStationData.RadioStationType radioStationType, double latitude = 0, double longitude = 0, bool editMode = true)
    {
        RadioStationData = new RadioStationData
        {
            Type = radioStationType,
            Preset = preset
        };
        _openFreqService = openFreqService;
        _hotkeyService = hotkeyService;
        Settings = settingsViewModel;
        Name = name;
        Latitude = latitude;
        Longitude = longitude;
        _acmiClientService = acmiClientService;
        EditMode = editMode;

        // Subscribe to connection state for auto-join
        _openFreqService.ConnectionStateChanged += OnConnectionStateChanged;

        // Subscribe to frequency status changes
        _openFreqService.FrequencyStatusChanged += OnFrequencyStatusChanged;

        _acmiClientService.ConnectionStatusChanged += OnAcmiConnectionStatusChanged;
        

        // Subscribe to hotkey events
        _hotkeyService.HotkeyPressed += OnHotkeyPressed;
        _hotkeyService.HotkeyReleased += OnHotkeyReleased;

        // Subscribe to channel updates for binding changes
        WeakReferenceMessenger.Default.Register<ChannelUpdatedMessage>(this, OnChannelUpdated);
        WeakReferenceMessenger.Default.Register<ChannelEnabledDisabledMessage>(this, OnChannelEnabledDisabled);
        WeakReferenceMessenger.Default.Register<ChannelDeleteRequestedMessage>(this, OnChannelDeleteRequested);
    }

    private async void OnAcmiConnectionStatusChanged(object? sender, AcmiConnectionEventArgs e)
    {
        if (e.Status == AcmiConnectionStatus.Connected)
        {
            IsAcmiConnected = true;
           await UpdateTacviewCallsigns(new CancellationTokenSource().Token);
        }
        else
        {
            IsAcmiConnected = false;
        }
    }

    

    public ChannelCardViewModel CreateChannel(int frequencyKhz, string name, bool isInEditMode = true, RadioType? bmsRadioType = null)
    {
        var channel = new ChannelCardViewModel(_hotkeyService, RadioStationData, this);
        channel.Name = name;
        channel.FrequencyKhz = frequencyKhz;
        channel.IsEditing = isInEditMode;
        channel.BmsRadioType = bmsRadioType;
        if (Dispatcher.UIThread.CheckAccess())
        {
            // Already on UI thread - add directly
            Channels.Add(channel);
        }
        else
        {
            // Not on UI thread - marshal to UI thread
            Dispatcher.UIThread.Post(() => { Channels.Add(channel); });
        }

        return channel;
    }

    private void OnChannelUpdated(object recipient, ChannelUpdatedMessage message)
    {
        if (message.NeedsReconnect && _openFreqService.IsAuthenticated)
        {
            // Only leave old frequency if the channel was previously connected
            if (message.OldStatus != Channel.ChannelStatus.Disconnected)
            {
                _openFreqService.LeaveFrequencyAsync(message.OldFrequencyKhz).Wait(TimeSpan.FromMilliseconds(100));
            }

            // Always join the new frequency
            _openFreqService.JoinFrequencyAsync(message.NewFrequencyKhz, RadioStationData, message.IsEnabled)
                .Wait(TimeSpan.FromMilliseconds(100));
            
            _openFreqService.SetAudioChannel(message.NewFrequencyKhz, message.CurrentAudioChannel);
        }
    }

    private void OnConnectionStateChanged(object? sender, ConnectionState state)
    {
        // Auto connect is triggered from the ChannelCardListViewModel
        if (state == ConnectionState.Disconnected)
        {
            // Reset all channel status on disconnect
            foreach (var channel in Channels)
            {
                channel.Status = Channel.ChannelStatus.Disconnected;
            }
        }
        else if (Settings.ModeIsGci && state == ConnectionState.Connected && !IsAcmiConnected)
        {
            RadioStationData.Type = RadioStationData.RadioStationType.STATIONARY;
        }
    }

    private void OnFrequencyStatusChanged(object? sender, FrequencyStatusEventArgs e)
    {
        var channel = Channels.FirstOrDefault(c => Math.Abs(c.FrequencyKhz - e.FrequencyKhz) < 0.01);
        channel?.Status = e.Status;
    }

    private void OnChannelEnabledDisabled(object recipient, ChannelEnabledDisabledMessage message)
    {
        switch (message.Enabled)
        {
            case true:
                _openFreqService.EnableFrequency(message.FrequencyKhz);
                break;
            case false:
                _openFreqService.DisableFrequency(message.FrequencyKhz);
                break;
        }
    }

    private void OnChannelDeleteRequested(object recipient, ChannelDeleteRequestedMessage message)
    {
        var vm = Channels.FirstOrDefault(c => c.Id == message.ChannelId);

        if (vm != null)
        {
            Dispatcher.UIThread.Post(() =>
            {
                Channels.Remove(vm);
                vm.Dispose();
            });
        }

        if (_openFreqService.IsAuthenticated)
        {
            _openFreqService.LeaveFrequencyAsync(message.FrequencyKhz);
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
                    // mute all channels of the same channel type in this group when transmitting
                    var mutedFrequencies = GetAllFrequenciesOfChannelGroup(channel.Type);
                    mutedFrequencies.Remove(channel.FrequencyKhz);
                    await _openFreqService.StartTransmissionAsync(channel.FrequencyKhz, mutedFrequencies);
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
                    await _openFreqService.StopTransmissionAsync(channel.FrequencyKhz);
                }
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Error in hotkey release: {ex.Message}");
        }
    }

    public bool ChangeChannelFrequency(int oldFreqKhz, int newFreqKhz, bool setEnabled)
    {
        var oldChannel =
            Channels.FirstOrDefault(c => Math.Abs(c.FrequencyKhz - oldFreqKhz) < 0.01);
        if (oldChannel == null) return false;

        oldChannel.FrequencyKhz = newFreqKhz;
        OnChannelUpdated(this,
            new ChannelUpdatedMessage(oldChannel.Id, oldFreqKhz, newFreqKhz, oldChannel.Status, oldChannel.HotKey,
                oldChannel.HotKey, setEnabled, oldChannel.AudioChannel));

        return true;
    }

    public async Task JoinAllChannelsAsync()
    {
        foreach (var channel in Channels)
        {
            await _openFreqService.JoinFrequencyAsync(channel.FrequencyKhz, RadioStationData, channel.IsEnabled);
            _openFreqService.SetAudioChannel(channel.FrequencyKhz, channel.AudioChannel);
        }
    }

    public async Task LeaveAllChannelsAsync()
    {
        foreach (var channel in Channels)
        {
            if (channel.Status != Channel.ChannelStatus.Disconnected)
            {
                await _openFreqService.LeaveFrequencyAsync(channel.FrequencyKhz);
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
        _acmiClientService.RemoveTrackingForAircraft(oldValue?.ObjectId);
        _acmiClientService.AddTrackingForAircraft(newValue?.ObjectId);
        RadioStationData.AcmiAircraftId = newValue?.ObjectId ?? null;
    }

    [RelayCommand]
    public void AddChannel()
    {
        CreateChannel(225000, $"Channel #{Channels.Count + 1}");
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

        var xy = TheaterCoordinateConverter.LatLonToXYMeters(
            Settings.SelectedTheater,
            lat,
            lon,
            TheaterCoordinateConverter.CoordinateSystem.BMS_HEIGHTMAP_COORDINATE_SYTEM);
        RadioStationData.Position = new Position(xy.x, xy.y, RadioStationData.Position.Z);
    }

    partial void OnAltitudeInputChanged(double? value)
    {
        if (value == null) return;
        const double FEET_PER_METER = 3.28084d;
        RadioStationData.Position ??= new Position(0d, 0d, 0d);
        RadioStationData.Position.Z = value.Value / FEET_PER_METER;
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
    
    [RelayCommand]
    private async Task TrackSelectedAircraftAsync()
    {
        if (string.IsNullOrEmpty(RadioStationData.AcmiAircraftId))
            return;
        
        var aircraft = _acmiClientService.GetAircraft(RadioStationData.AcmiAircraftId);
        if (aircraft == null)
            return;
        
        // Open tracking window
        _trackingWindow = new MapPickerWindow(
            aircraft.Transform.Latitude,
            aircraft.Transform.Longitude,
            aircraft.Transform.Heading,
            Settings.SelectedTheater,
            aircraft.CallSign);
        
        IsTracking = true;
        
        // Start update task
        _trackingCts = new CancellationTokenSource();
        _ = UpdateTrackingPositionAsync(_trackingCts.Token);
        
        // Show window (non-blocking)
        var desktop = Application.Current?.ApplicationLifetime as IClassicDesktopStyleApplicationLifetime;
        if (desktop?.MainWindow != null)
        {
            _trackingWindow.Show(desktop.MainWindow);
            
            // Handle window close
            _trackingWindow.Closed += (_, _) =>
            {
                StopTracking();
            };
        }
    }
    
    private async Task UpdateTrackingPositionAsync(CancellationToken cancellationToken)
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                var aircraft = _acmiClientService.GetAircraft(RadioStationData.AcmiAircraftId ?? string.Empty);
                if (aircraft != null && _trackingWindow != null)
                {
                    // Update window with current aircraft position
                    _trackingWindow.UpdateTrackedPosition(
                        aircraft.Transform.Latitude,
                        aircraft.Transform.Longitude,
                        (aircraft.Transform.Heading + 360) % 360, // the ACMI streams sends headings as +/-180
                        aircraft.Transform.AltitudeFt);
                }
                
                // Update rate: 10 Hz (100ms)
                await Task.Delay(100, cancellationToken);
            }
        }
        catch (OperationCanceledException)
        {
            // Normal cancellation
        }
        catch (Exception ex)
        {
            // Log error
            Console.WriteLine($"Tracking error: {ex}");
        }
    }
    
    [RelayCommand]
    private void StopTracking()
    {
        IsTracking = false;
        _trackingCts?.Cancel();
        _trackingCts?.Dispose();
        _trackingCts = null;
        
        _trackingWindow?.Close();
        _trackingWindow = null;
    }
    
    public class ChannelCardGroupDeleteRequestedMessage(Guid channelCardGroupId)
    {
        public Guid ChannelCardGroupId { get; } = channelCardGroupId;
    }
    
    [RelayCommand]
    public void DeleteChannelGroup()
    {
        WeakReferenceMessenger.Default.Send(
            new ChannelCardGroupDeleteRequestedMessage(Id));
    }

    public List<int> GetAllFrequenciesOfChannelGroup(Channel.ChannelType? filterChannelType = null)
    {
        var query = Channels.AsEnumerable();

        if (filterChannelType is not null)
            query = query.Where(c => c.Type == filterChannelType);

        return query
            .Select(c => c.FrequencyKhz)
            .ToList();
    }
}