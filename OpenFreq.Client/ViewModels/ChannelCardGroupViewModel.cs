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
using FalconRadioService.Services;
using OpenFreq.Client.Models;
using OpenFreq.Common;
using OpenFreq.Services.Acmi;
using OpenFreq.Utilities;
using OpenFreqAudio;
using OpenFreqClient.Models;
using OpenFreqClient.Services;
using OpenFreqClient.Services.Interfaces;
using OpenFreqClient.Views;
using SharpHook.Data;

namespace OpenFreqClient.ViewModels;

public partial class ChannelCardGroupViewModel : ViewModelBase, IDisposable
{
    public Guid Id { get; } = Guid.NewGuid();
    public SettingsViewModel Settings { get; }

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
    [ObservableProperty] public partial double AltitudeFeet { get; set; }
    [ObservableProperty] public partial string? CoordinateError { get; set; }
    [ObservableProperty] public partial bool HasCoordinateError { get; set; }

    private const double FeetPerMeter = 3.28084d;
    private bool _isUpdatingPosition;

    private MapPickerWindow? _trackingWindow;
    private CancellationTokenSource? _trackingCts;
    [ObservableProperty] public partial bool IsTracking { get; set; }
    public bool IsBmsGroup => RadioStationData.Type == RadioStationData.RadioStationType.BMS;

    public ChannelCardGroupViewModel(IOpenFreqService openFreqService, IHotkeyService hotkeyService,
        IAcmiClientService acmiClientService, SettingsViewModel settingsViewModel, string name,
        RadioStationPreset preset, RadioStationData.RadioStationType radioStationType, double latitude = 0,
        double longitude = 0, double altitudeFeet = 0, bool editMode = true)
    {
        RadioStationData = new RadioStationData
        {
            Type = radioStationType,
            Preset = preset,
            Ppm = preset.GetRandomPpm()
        };
        _openFreqService = openFreqService;
        _hotkeyService = hotkeyService;
        Settings = settingsViewModel;
        Name = name;
        Latitude = latitude;
        Longitude = longitude;
        AltitudeFeet = altitudeFeet;
        _acmiClientService = acmiClientService;
        EditMode = editMode;

        // Subscribe to connection state for auto-join
        _openFreqService.ConnectionStateChanged += OnConnectionStateChanged;

        // Subscribe to frequency status changes
        _openFreqService.FrequencyConnectionStatusChanged += OnFrequencyConnectionStatusChanged;
        _openFreqService.FrequencyTransmissionStatusChanged += OnFrequencyTransmissionStatusChanged;

        _acmiClientService.ConnectionStatusChanged += OnAcmiConnectionStatusChanged;


        // Subscribe to hotkey events
        _hotkeyService.HotkeyPressed += OnHotkeyPressed;
        _hotkeyService.HotkeyReleased += OnHotkeyReleased;

        // Subscribe to channel updates for binding changes
        WeakReferenceMessenger.Default.Register<ChannelUpdatedMessage>(this, OnChannelUpdated);
        WeakReferenceMessenger.Default.Register<ChannelJoinLeaveRequestedMessage>(this, OnChannelJoinLeaveRequested);
        WeakReferenceMessenger.Default.Register<SquelchEnabledDisabledMessage>(this, OnSquelchEnabledDisabled);
        WeakReferenceMessenger.Default.Register<ChannelDeleteRequestedMessage>(this, OnChannelDeleteRequested);

        // Only update position if valid coordinates provided
        if (latitude != 0 || longitude != 0)
        {
            UpdateRadioStationPosition();
        }
    }

    private async void OnChannelJoinLeaveRequested(object recipient, ChannelJoinLeaveRequestedMessage message)
    {
        if (!_openFreqService.IsAuthenticated) return;
        if (message.Join)
        {
            await _openFreqService.JoinFrequencyAsync(message.FrequencyKhz, message.RadioStationData);
        }
        else
        {
            await _openFreqService.LeaveFrequencyAsync(message.FrequencyKhz);
        }
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


    public ChannelCardViewModel CreateChannel(int frequencyKhz, string name, bool isInEditMode = true,
        RadioType? bmsRadioType = null)
    {
        var channel = new ChannelCardViewModel(_openFreqService, _hotkeyService, name, frequencyKhz, isInEditMode, RadioStationData, this,
            Settings);
        channel.Name = name;
        channel.FrequencyKhz = frequencyKhz;
        channel.IsEditing = isInEditMode;
        channel.BmsRadioType = bmsRadioType;
        
        // 9999 is BMS's "radio off" parking frequency - always ensure it's disconnected
        if (frequencyKhz == IFalconRadioSharedMemoryService.BmsRadioOffFrequency)
        {
            channel.ConnectionStatus = Channel.ChannelConnectionStatus.Disconnected;
        }
        
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

    private async void OnChannelUpdated(object recipient, ChannelUpdatedMessage message)
    {
        if (!_openFreqService.IsAuthenticated) return;
        
        // Always leave the old frequency first
        await _openFreqService.LeaveFrequencyAsync(message.OldFrequencyKhz);
        
        if (!message.IsBmsChannel)
        {
            // For non-BMS channels, immediately join the new frequency
            await _openFreqService.JoinFrequencyAsync(message.NewFrequencyKhz, RadioStationData);
            _openFreqService.SetAudioChannel(message.NewFrequencyKhz, message.CurrentAudioChannel);
        }
        // For BMS channels, the join will be handled by OnBmsFrequencyChanged after checking power state
    }

    private void OnConnectionStateChanged(object? sender, ConnectionState state)
    {
        // Auto connect is triggered from the ChannelCardListViewModel
        if (state == ConnectionState.Disconnected)
        {
            // Reset all channel status on disconnect
            foreach (var channel in Channels)
            {
                channel.ConnectionStatus = Channel.ChannelConnectionStatus.Disconnected;
            }
        }
        else if (Settings.ModeIsGci && state == ConnectionState.Connected && !IsAcmiConnected)
        {
            RadioStationData.Type = RadioStationData.RadioStationType.STATIONARY;
        }
    }

    private void OnFrequencyConnectionStatusChanged(object? sender, FrequencyConnectionStatusEventArgs e)
    {
        var channel = Channels.FirstOrDefault(c => c.FrequencyKhz == e.FrequencyKhz);
        if (channel == null) return;
        
        // 9999 is BMS's "radio off" parking frequency - always keep it disconnected
        if (e.FrequencyKhz == IFalconRadioSharedMemoryService.BmsRadioOffFrequency)
        {
            channel.ConnectionStatus = Channel.ChannelConnectionStatus.Disconnected;
        }
        else
        {
            channel.ConnectionStatus = e.ConnectionStatus;
        }
    }
    
    private void OnFrequencyTransmissionStatusChanged(object? sender, FrequencyTransmissionStatusEventArgs e)
    {
        var channel = Channels.FirstOrDefault(c => c.FrequencyKhz == e.FrequencyKhz);
        channel?.TransmissionStatus = e.TransmissionStatus;
    }
    
    

    private void OnSquelchEnabledDisabled(object recipient, SquelchEnabledDisabledMessage message)
    {
        _openFreqService.SetSquelch(message.FrequencyKhz, message.SquelchEnabled);
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

                if (e.Type == IHotkeyService.HotkeyType.Ptt)
                {
                    if (channel != null && channel.ConnectionStatus != Channel.ChannelConnectionStatus.Disconnected &&
                        !channel.IsEditing)
                    {
                        // mute all channels of the same channel type in this group when transmitting
                        var mutedFrequencies = GetAllFrequenciesOfChannelGroup(channel.Type);
                        mutedFrequencies.Remove(channel.FrequencyKhz);
                        await _openFreqService.StartTransmissionAsync(channel.FrequencyKhz, mutedFrequencies);
                    }
                }
                else if (e.Type == IHotkeyService.HotkeyType.SquelchToggle)
                {
                    if (channel != null && channel.ConnectionStatus != Channel.ChannelConnectionStatus.Disconnected && Settings.Is3dMode)
                    {
                        channel.ToggleSquelch();
                    }
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
                if (channel != null && channel.ConnectionStatus != Channel.ChannelConnectionStatus.Disconnected)
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

    public bool ChangeChannelFrequency(int oldFreqKhz, int newFreqKhz, bool joined)
    {
        var oldChannel =
            Channels.FirstOrDefault(c => c.FrequencyKhz == oldFreqKhz);
        if (oldChannel == null)
        {
            // TODO Log warning
            return false;
        }

        oldChannel.FrequencyKhz = newFreqKhz;
        
        // 9999 is BMS's "radio off" parking frequency - always ensure it's disconnected
        if (newFreqKhz == IFalconRadioSharedMemoryService.BmsRadioOffFrequency)
        {
            oldChannel.ConnectionStatus = Channel.ChannelConnectionStatus.Disconnected;
        }
        
        OnChannelUpdated(this,
            new ChannelUpdatedMessage(oldChannel.Id, oldFreqKhz, newFreqKhz, oldChannel.ConnectionStatus,
                oldChannel.AudioChannel, true));
        
        // Note: Join will be handled by OnBmsFrequencyChanged which explicitly joins for non-9999 frequencies
        
        return true;
    }

    public async Task JoinAllChannelsAsync()
    {
        foreach (var channel in Channels)
        {
            await _openFreqService.JoinFrequencyAsync(channel.FrequencyKhz, RadioStationData);
            _openFreqService.SetAudioChannel(channel.FrequencyKhz, channel.AudioChannel);
        }
    }

    public async Task LeaveAllChannelsAsync()
    {
        foreach (var channel in Channels)
        {
            if (channel.ConnectionStatus != Channel.ChannelConnectionStatus.Disconnected)
            {
                await _openFreqService.LeaveFrequencyAsync(channel.FrequencyKhz);
            }
        }
    }

    public void Dispose()
    {
        foreach (var channel in Channels)
        {
            channel.Dispose();
        }

        Channels.Clear();

        _hotkeyService.HotkeyPressed -= OnHotkeyPressed;
        _hotkeyService.HotkeyReleased -= OnHotkeyReleased;
        _openFreqService.FrequencyConnectionStatusChanged -= OnFrequencyConnectionStatusChanged;
        _openFreqService.FrequencyTransmissionStatusChanged -= OnFrequencyTransmissionStatusChanged;
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
        EditMode = false;
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

    partial void OnLatitudeChanged(double value)
    {
        ValidateAndUpdatePosition();
    }

    partial void OnLongitudeChanged(double value)
    {
        ValidateAndUpdatePosition();
    }

    partial void OnAltitudeFeetChanged(double value)
    {
        ValidateAndUpdatePosition();
    }

    private void ValidateAndUpdatePosition()
    {
        if (_isUpdatingPosition) return;

        _isUpdatingPosition = true;

        try
        {
            // Validate coordinates
            if (!TheaterCoordinateConverter.IsWithinTheaterBounds(Settings.SelectedTheater, Latitude, Longitude))
            {
                CoordinateError = "Coordinates are outside the theater bounds";
                HasCoordinateError = true;
                return;
            }

            // Clear errors
            CoordinateError = null;
            HasCoordinateError = false;

            // Update RadioStationData
            UpdateRadioStationPosition();
        }
        finally
        {
            _isUpdatingPosition = false;
        }
    }

    private void UpdateRadioStationPosition()
    {
        var xy = TheaterCoordinateConverter.LatLonToXYMeters(
            Settings.SelectedTheater,
            Latitude,
            Longitude,
            TheaterCoordinateConverter.CoordinateSystem.BMS_HEIGHTMAP_COORDINATE_SYTEM);

        RadioStationData.Vector3 = new Vector3(
            xy.x,
            xy.y,
            AltitudeFeet / FeetPerMeter);
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
    private Task TrackSelectedAircraftAsync()
    {
        if (string.IsNullOrEmpty(RadioStationData.AcmiAircraftId))
            return Task.CompletedTask;

        var aircraft = _acmiClientService.GetAircraft(RadioStationData.AcmiAircraftId);
        if (aircraft == null)
            return Task.CompletedTask;

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
            _trackingWindow.Closed += (_, _) => { StopTracking(); };
        }

        return Task.CompletedTask;
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

    protected bool Equals(ChannelCardGroupViewModel other)
    {
        return Id.Equals(other.Id);
    }

    public override bool Equals(object? obj)
    {
        if (obj is null) return false;
        if (ReferenceEquals(this, obj)) return true;
        if (obj.GetType() != GetType()) return false;
        return Equals((ChannelCardGroupViewModel)obj);
    }

    public override int GetHashCode()
    {
        return Id.GetHashCode();
    }

    public void UpdateVhfHotkey(KeyCode capturedKey)
    {
        foreach (var channel in Channels)
        {
            if (channel.BmsRadioType == RadioType.VHF)
            {
                channel.SquelchHotKey = capturedKey;
            }
        }
    }
    
    public void UpdateUhfHotkey(KeyCode capturedKey)
    {
        foreach (var channel in Channels)
        {
            if (channel.BmsRadioType is RadioType.UHF or RadioType.GUARD)
            {
                channel.SquelchHotKey = capturedKey;
            }
        }
    }
}