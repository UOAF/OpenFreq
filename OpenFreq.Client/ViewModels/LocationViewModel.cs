using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Media;
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

namespace OpenFreqClient.ViewModels;

public partial class LocationViewModel : ViewModelBase, IDisposable
{
    public Guid Id { get; } = Guid.NewGuid();
    public SettingsViewModel Settings { get; }


    [ObservableProperty] public partial string Name { get; set; }
    [ObservableProperty] public partial IBrush AccentBrush { get; set; } = new SolidColorBrush(Color.Parse("#4CAF50"));

    partial void OnNameChanged(string value) => AccentBrush = MaterialColorUtil.GetAccentBrush(value);

    [ObservableProperty] public partial bool EditMode { get; set; }

    [ObservableProperty] public partial bool IsAcmiConnected { get; set; }

    public record TacviewAircraftItem(string CallSign, string ObjectId)
    {
        public override string ToString() => CallSign;
    }

    // Shared global list owned by ChannelCardListViewModel — same instance across all locations
    public ObservableCollection<TacviewAircraftItem> TacviewFlightCallsigns { get; private set; } = null!;
    [ObservableProperty] private TacviewAircraftItem? _selectedTacviewCallsign;

    public RadioStationData RadioStationData { get; private set; }

    private readonly IOpenFreqService _openFreqService;
    private readonly IHotkeyService _hotkeyService;
    private readonly IAcmiClientService _acmiClientService;

    [ObservableProperty] public partial ObservableCollection<ChannelCardViewModel> Channels { get; set; } = [];

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
    public bool IsBmsLocation => RadioStationData.Type == RadioStationData.RadioStationType.BMS;

    [ObservableProperty] public partial bool AnyChannelTransmitting { get; set; }
    [ObservableProperty] public partial bool AnyChannelReceiving { get; set; }

    public LocationViewModel(IOpenFreqService openFreqService, IHotkeyService hotkeyService,
        IAcmiClientService acmiClientService, SettingsViewModel settingsViewModel, string name,
        RadioStationPreset preset, RadioStationData.RadioStationType radioStationType,
        ObservableCollection<TacviewAircraftItem> globalTacviewCallsigns,
        double latitude = 0, double longitude = 0, double altitudeFeet = 0, bool editMode = true)
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
        TacviewFlightCallsigns = globalTacviewCallsigns;

        // Subscribe to connection state for auto-join
        _openFreqService.ConnectionStateChanged += OnConnectionStateChanged;

        // Subscribe to frequency status changes
        _openFreqService.FrequencyConnectionStatusChanged += OnFrequencyConnectionStatusChanged;
        _openFreqService.FrequencyTransmissionStatusChanged += OnFrequencyTransmissionStatusChanged;

        _acmiClientService.ConnectionStatusChanged += OnAcmiConnectionStatusChanged;

        // Sync initial ACMI state — event may have already fired before this VM was created
        IsAcmiConnected = _acmiClientService.Status == AcmiConnectionStatus.Connected;

        // Subscribe to hotkey events
        _hotkeyService.HotkeyPressed += OnHotkeyPressed;
        _hotkeyService.HotkeyReleased += OnHotkeyReleased;

        // Only update position if valid coordinates provided
        if (latitude != 0 || longitude != 0)
        {
            UpdateRadioStationPosition();
        }
    }

    // Cards call these on their own location rather than broadcasting, so no other location can act on a card.
    // Each location joins with its own RadioStationData, so another location's join would put the card at that
    // location's position.
    public async Task JoinChannelAsync(ChannelCardViewModel channel)
    {
        if (!_openFreqService.IsAuthenticated) return;
        await _openFreqService.JoinFrequencyAsync(channel.FrequencyKhz, channel.Id, RadioStationData);
    }

    public async Task LeaveChannelAsync(ChannelCardViewModel channel)
    {
        if (!_openFreqService.IsAuthenticated) return;
        await _openFreqService.LeaveFrequencyAsync(channel.FrequencyKhz, channel.Id);
    }

    /// <summary>
    /// Puts one BMS radio slot into the state that BMS reports for it: tuned to
    /// <paramref name="frequencyKhz"/>, and joined or not joined.
    /// </summary>
    /// <remarks>
    /// The leave, the retune and the join all happen inside this one awaited call. The caller runs these
    /// one at a time per radio slot, so two of them can never interleave and leave the card and the server
    /// on different frequencies.
    /// </remarks>
    public async Task ApplySlotStateAsync(ChannelCardViewModel channel, int frequencyKhz, bool shouldJoin)
    {
        if (!_openFreqService.IsAuthenticated) return;

        // Leave by what the slot actually holds, not by what the card shows. A join left behind by an
        // earlier fault is cleared here, so every switch re-converges the slot.
        foreach (var joined in _openFreqService.GetJoinedFrequencies(channel.Id))
        {
            if (joined == frequencyKhz) continue;
            await _openFreqService.LeaveFrequencyAsync(joined, channel.Id);
        }

        channel.FrequencyKhz = frequencyKhz;

        if (shouldJoin)
        {
            if (!_openFreqService.IsFrequencyJoined(frequencyKhz, channel.Id))
                await _openFreqService.JoinFrequencyAsync(frequencyKhz, channel.Id, RadioStationData);

            _openFreqService.SetPan(frequencyKhz, channel.Id, channel.Pan);
            _openFreqService.SetSquelch(frequencyKhz, channel.Id, channel.IsSquelchEnabled);
        }
        else if (_openFreqService.IsFrequencyJoined(frequencyKhz, channel.Id))
        {
            await _openFreqService.LeaveFrequencyAsync(frequencyKhz, channel.Id);
        }

        channel.ConnectionStatus = _openFreqService.IsFrequencyJoined(frequencyKhz, channel.Id)
            ? Channel.ChannelConnectionStatus.Connected
            : Channel.ChannelConnectionStatus.Disconnected;
    }

    private void OnAcmiConnectionStatusChanged(object? sender, AcmiConnectionEventArgs e)
    {
        IsAcmiConnected = e.Status == AcmiConnectionStatus.Connected;
    }


    public ChannelCardViewModel CreateChannel(int frequencyKhz, string name, bool isInEditMode = true,
        RadioType? bmsRadioType = null)
    {
        var channel = new ChannelCardViewModel(_hotkeyService, name, frequencyKhz, isInEditMode,
            RadioStationData, this,
            Settings);
        channel.Name = name;
        channel.FrequencyKhz = frequencyKhz;
        channel.IsEditing = isInEditMode;
        channel.BmsRadioType = bmsRadioType;

        // A parking frequency means the radio is off, so the card must never show Connected.
        if (IFalconRadioSharedMemoryService.IsBmsParkingFrequency(frequencyKhz))
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

    // Moves a channel the user just edited from oldFrequencyKhz to the one it now shows.
    // BMS cards are not editable and take ApplySlotStateAsync instead.
    public async Task RetuneChannelAsync(ChannelCardViewModel channel, int oldFrequencyKhz)
    {
        if (!_openFreqService.IsAuthenticated) return;

        // Read the card before awaiting, since its frequency can change again in the meantime.
        var newFrequencyKhz = channel.FrequencyKhz;
        var pan = channel.Pan;

        await _openFreqService.LeaveFrequencyAsync(oldFrequencyKhz, channel.Id);
        await _openFreqService.JoinFrequencyAsync(newFrequencyKhz, channel.Id, RadioStationData);
        _openFreqService.SetPan(newFrequencyKhz, channel.Id, pan);
    }

    private void OnConnectionStateChanged(object? sender, ConnectionStateChangedEventArgs e)
    {
        var state = e.State;
        // Auto connect is triggered from the ChannelCardListViewModel
        if (state == ConnectionState.Disconnected)
        {
            // Fires from the WebSocket receive background thread — marshal to UI thread
            // so that ObservableCollection mutations and property changes are safe.
            Dispatcher.UIThread.Post(() =>
            {
                // Reset all channel status on disconnect
                foreach (var channel in Channels)
                {
                    channel.ConnectionStatus = Channel.ChannelConnectionStatus.Disconnected;
                    channel.ConnectionError = null;
                }

                AnyChannelTransmitting = false;
                AnyChannelReceiving = false;
            });
        }
        else if (Settings.ModeIsGci && state == ConnectionState.Connected && !IsAcmiConnected)
        {
            RadioStationData.Type = RadioStationData.RadioStationType.STATIONARY;
        }
    }

    private void OnFrequencyConnectionStatusChanged(object? sender, FrequencyConnectionStatusEventArgs e)
    {
        // Marshal to the UI thread so we don't modify the Channels collection while ImportBmsRadioChannels
        // is rebuilding it.
        Dispatcher.UIThread.Post(() =>
        {
            // Addressed per slot: only the card that tuned this frequency updates. Cards parked on the
            // same frequency without having joined it must stay Disconnected — otherwise PTT would
            // start a transmission for a slot that was never tuned.
            // The frequency must match too. A late event for a frequency the card has already left
            // would otherwise show the card as Connected while the slot holds a different frequency.
            var targets = Channels.Where(c => c.Id == e.SlotId && c.FrequencyKhz == e.FrequencyKhz).ToList();

            foreach (var channel in targets)
            {
                channel.ConnectionStatus = IFalconRadioSharedMemoryService.IsBmsParkingFrequency(e.FrequencyKhz)
                    ? Channel.ChannelConnectionStatus.Disconnected
                    : e.ConnectionStatus;
                channel.ConnectionError = e.Reason;
            }
        });
    }

    private void OnFrequencyTransmissionStatusChanged(object? sender, FrequencyTransmissionStatusEventArgs e)
    {
        Dispatcher.UIThread.Post(() =>
        {
            var modeMatches = e.Is3d == Settings.Is3dMode;

            foreach (var channel in Channels.Where(c => c.FrequencyKhz == e.FrequencyKhz).ToList())
            {
                if (e.TransmissionStatus == Channel.ChannelTransmissionStatus.Idle || modeMatches)
                    channel.TransmissionStatus = e.TransmissionStatus;
            }

            AnyChannelTransmitting =
                Channels.Any(c => c.TransmissionStatus == Channel.ChannelTransmissionStatus.Transmitting);
            AnyChannelReceiving = Channels.Any(c => c.TransmissionStatus == Channel.ChannelTransmissionStatus.Receiving);
        });
    }


    public void SetChannelSquelch(ChannelCardViewModel channel)
    {
        _openFreqService.SetSquelch(channel.FrequencyKhz, channel.Id, channel.IsSquelchEnabled);
    }

    public void RemoveChannel(ChannelCardViewModel channel)
    {
        Dispatcher.UIThread.Post(() =>
        {
            Channels.Remove(channel);
            channel.Dispose();
        });

        LeaveChannelAsync(channel).FireAndForget();
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
                        await _openFreqService.StartTransmissionAsync(channel.FrequencyKhz, channel.Id);
                    }
                }
                else if (e.Type == IHotkeyService.HotkeyType.SquelchToggle)
                {
                    if (channel != null && channel.ConnectionStatus != Channel.ChannelConnectionStatus.Disconnected &&
                        Settings.Is3dMode)
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
                    await _openFreqService.StopTransmissionAsync(channel.Id);
                }
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Error in hotkey release: {ex.Message}");
        }
    }

    public async Task JoinAllChannelsAsync()
    {
        foreach (var channel in Channels)
        {
            await _openFreqService.JoinFrequencyAsync(channel.FrequencyKhz, channel.Id, RadioStationData);
            _openFreqService.SetPan(channel.FrequencyKhz, channel.Id, channel.Pan);
        }
    }

    public async Task LeaveAllChannelsAsync()
    {
        // Leave every card, not just those showing Connected. That status lags the join, so a card that
        // joined a moment ago can still show Disconnected. The service ignores cards that never joined.
        foreach (var channel in Channels)
        {
            await _openFreqService.LeaveFrequencyAsync(channel.FrequencyKhz, channel.Id);
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
        _acmiClientService.ConnectionStatusChanged -= OnAcmiConnectionStatusChanged;

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
    public void ToggleEditing()
    {
        EditMode = !EditMode;
    }

    public record LocationSelectionRequestedMessage(Guid LocationId);

    [RelayCommand]
    public void EnterEditMode()
    {
        WeakReferenceMessenger.Default.Send(new LocationSelectionRequestedMessage(Id));
        EditMode = true;
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
        var window = new MapPickerWindow(Latitude, Longitude, Settings, _openFreqService);

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
            Settings,
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
                    // CalculateTAS returns m/s; convert to knots for display
                    const double MpsToKts = 1.94384;
                    var aircraftSpeedKts =
                        AcmiHeightmapConverter.CalculateTAS(aircraft.Mach, aircraft.Transform.Altitude) * MpsToKts;
                    _trackingWindow.UpdateTrackedPosition(
                        aircraft.Transform.Latitude,
                        aircraft.Transform.Longitude,
                        (aircraft.Transform.Heading + 360) % 360, // the ACMI streams sends headings as +/-180
                        aircraft.Transform.AltitudeFt,
                        aircraftSpeedKts, aircraft.Mach,
                        aircraft.CallSign);
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

    public class LocationDeleteRequestedMessage(Guid locationId)
    {
        public Guid LocationId { get; } = locationId;
    }

    [RelayCommand]
    public void DeleteLocation()
    {
        WeakReferenceMessenger.Default.Send(
            new LocationDeleteRequestedMessage(Id));
    }

    protected bool Equals(LocationViewModel other)
    {
        return Id.Equals(other.Id);
    }

    public override bool Equals(object? obj)
    {
        if (obj is null) return false;
        if (ReferenceEquals(this, obj)) return true;
        if (obj.GetType() != GetType()) return false;
        return Equals((LocationViewModel)obj);
    }

    public override int GetHashCode()
    {
        return Id.GetHashCode();
    }

    public void UpdateVhfHotkey(HotkeyBinding? capturedKey)
    {
        foreach (var channel in Channels)
        {
            if (channel.BmsRadioType == RadioType.Radio2)
            {
                channel.SquelchHotKey = capturedKey;
            }
        }
    }

    public void UpdateUhfHotkey(HotkeyBinding? capturedKey)
    {
        foreach (var channel in Channels)
        {
            if (channel.BmsRadioType is RadioType.Radio1 or RadioType.Guard)
            {
                channel.SquelchHotKey = capturedKey;
            }
        }
    }
}

internal static class FireAndForgetExtensions
{
    /// <summary>
    /// Lets <paramref name="task"/> run on without the caller waiting, for synchronous callers that can't await it.
    /// Unlike discarding it with <c>_ = task</c>, a failure isn't lost: it's rethrown on the caller's synchronization
    /// context (the UI dispatcher), or on the thread pool if there isn't one, and ends the app through the unhandled
    /// exception handler.
    /// </summary>
    public static async void FireAndForget(this Task task)
    {
        await task;
    }
}
