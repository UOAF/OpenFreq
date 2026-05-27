using System;
using System.Collections.Generic;
using System.Globalization;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CommunityToolkit.Mvvm.Messaging;
using FalconBmsDataService.Models;
using OpenFreq.Client.Models;
using OpenFreqAudio;
using OpenFreqClient.Models;
using OpenFreqClient.Services;
using OpenFreqClient.Services.Interfaces;
using SharpHook.Data;

namespace OpenFreqClient.ViewModels;

public partial class ChannelCardViewModel : ViewModelBase, IDisposable
{
    private readonly IHotkeyService _hotkeyService;
    private readonly LocationViewModel _parentLocationViewModel;
    public SettingsViewModel Settings { get; }

    public Guid Id { get; } = Guid.NewGuid();

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(FrequencyMhzString))]
    [NotifyPropertyChangedFor(nameof(Type))]
    public partial int FrequencyKhz { get; set; }

    /// <summary>
    /// Frequency display string in MHz
    /// </summary>
    public string FrequencyMhzString
    {
        get => (FrequencyKhz / 1000d).ToString("F3", CultureInfo.InvariantCulture);
        set
        {
            if (double.TryParse(value, NumberStyles.Any, CultureInfo.InvariantCulture, out var mhz))
            {
                FrequencyKhz = (int)(mhz * 1000d);
            }
        }
    }

    [ObservableProperty] public partial string? Name { get; set; }

    [ObservableProperty] public partial float RxDb { get; set; }

    // this is just to display it in the UI
    public Channel.ChannelType Type
    {
        get
        {
            return (FrequencyKhz / 1000d) switch
            {
                < 200 and > 30 => Channel.ChannelType.VHF,
                > 200 => Channel.ChannelType.UHF,
                _ => Channel.ChannelType.Custom
            };
        }
    }

    // Direct mapping to BMS RadioType or null in GCI mode.
    // We cant use a sane frequency->type mapping because BMS likes to set lobby frequencies, e.g. 1.234 MHz
    public RadioType? BmsRadioType { get; set; }

    [ObservableProperty] public partial double SignalStrengthPercent { get; set; }
    [ObservableProperty] public partial double SignalStrengthDbm { get; set; }

    [ObservableProperty]
    public partial Channel.ChannelConnectionStatus ConnectionStatus { get; set; } =
        Channel.ChannelConnectionStatus.Disconnected;

    [ObservableProperty]
    public partial Channel.ChannelTransmissionStatus TransmissionStatus { get; set; } =
        Channel.ChannelTransmissionStatus.Idle;

    [ObservableProperty] public partial bool IsEditing { get; set; }

    [ObservableProperty] private bool _channelWasChanged;

    // Hotkey binding
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HotkeyDisplay), nameof(HasPttHotkey))]
    public partial HotkeyBinding? PttHotKey { get; set; } = null;

    public bool HasPttHotkey => PttHotKey != null;


    [ObservableProperty] public partial bool IsCapturingPttHotkey { get; set; }

    public string HotkeyDisplay => PttHotKey?.DisplayName ?? "None";

    [ObservableProperty] public partial HotkeyBinding? SquelchHotKey { get; set; }

    // Reference to the data of the RadioStationGroup
    [ObservableProperty] public partial RadioStationData RadioStationData { get; set; }

    [ObservableProperty] public partial bool IsEditable { get; set; }

    /// <summary>Pan: -100 = full left, 0 = center, +100 = full right.</summary>
    [ObservableProperty]
    public partial int Pan { get; set; } = 0;

    [ObservableProperty] public partial bool IsSquelchEnabled { get; set; } = true;

    // Store original values when entering edit mode
    private int _originalFrequencyKhz;


    [RelayCommand]
    public void BmsLobby1Clicked()
    {
        Name = "BMS Lobby 1";
        FrequencyKhz = 1234;
        PttHotKey = new KeyboardBinding(KeyCode.VcF1);
        ToggleEditing();
    }

    [RelayCommand]
    public void BmsLobby2Clicked()
    {
        Name = "BMS Lobby 2";
        FrequencyKhz = 339750;
        PttHotKey = new KeyboardBinding(KeyCode.VcF2);
        ToggleEditing();
    }


    public ChannelCardViewModel(IOpenFreqService openFreqService, IHotkeyService hotkeyService, string name,
        int frequencyKhz, bool isInEditMode,
        RadioStationData radioStationData,
        LocationViewModel parentLocationViewModel, SettingsViewModel settings, bool isEditable = true,
        RadioType? bmsRadioType = null)
    {
        Name = name;
        FrequencyKhz = frequencyKhz;
        IsEditing = isInEditMode;
        _hotkeyService = hotkeyService;
        RadioStationData = radioStationData;
        _parentLocationViewModel = parentLocationViewModel;
        Settings = settings;
        IsEditable = isEditable;
        BmsRadioType = bmsRadioType;

        WeakReferenceMessenger.Default.Register<SignalStrengthTracker.SignalStrengthUpdateMessage>(this,
            (_, m) =>
            {
                if (FrequencyKhz == m.FrequencyKhz)
                {
                    SignalStrengthPercent = m.StrengthPercent;
                    SignalStrengthDbm = m.SnrDb;
                }
            });
    }

    [RelayCommand]
    public void ToggleEditing()
    {
        if (!IsEditing)
        {
            // Entering edit mode - store current values
            _originalFrequencyKhz = FrequencyKhz;
        }
        else
        {
            // Exiting edit mode - send update if changed
            var message = new ChannelUpdatedMessage(
                Id,
                _originalFrequencyKhz,
                FrequencyKhz,
                ConnectionStatus,
                Pan,
                _parentLocationViewModel.IsBmsLocation
            );

            WeakReferenceMessenger.Default.Send(message);
        }

        IsEditing = !IsEditing;
    }

    [RelayCommand]
    private async Task BeginCaptureHotkeyAsync()
    {
        IsCapturingPttHotkey = true;
        try
        {
            var capturedKey = await _hotkeyService.CaptureNextHotkeyAsync();
            PttHotKey = capturedKey;
        }
        catch (OperationCanceledException)
        {
            // Capture was cancelled
        }
        finally
        {
            IsCapturingPttHotkey = false;
        }
    }

    partial void OnPttHotKeyChanging(HotkeyBinding? oldValue, HotkeyBinding? newValue)
    {
        // Unregister old binding
        if (oldValue != null)
        {
            _hotkeyService.UnregisterHotkey(IHotkeyService.HotkeyType.Ptt, oldValue, Id);
        }
    }

    partial void OnPttHotKeyChanged(HotkeyBinding? oldValue, HotkeyBinding? newValue)
    {
        // Register new binding
        if (newValue != null)
        {
            _hotkeyService.RegisterHotkey(IHotkeyService.HotkeyType.Ptt, newValue, Id);
        }
    }

    partial void OnSquelchHotKeyChanging(HotkeyBinding? oldValue, HotkeyBinding? newValue)
    {
        // Unregister old binding
        if (oldValue != null)
        {
            _hotkeyService.UnregisterHotkey(IHotkeyService.HotkeyType.SquelchToggle, oldValue, Id);
        }
    }

    partial void OnSquelchHotKeyChanged(HotkeyBinding? oldValue, HotkeyBinding? newValue)
    {
        // Register new binding
        if (newValue != null)
        {
            _hotkeyService.RegisterHotkey(IHotkeyService.HotkeyType.SquelchToggle, newValue, Id);
        }
    }

    [RelayCommand]
    private void ClearPttHotkey()
    {
        if (PttHotKey == null) return;
        _hotkeyService.UnregisterHotkey(IHotkeyService.HotkeyType.Ptt, PttHotKey, Id);
        PttHotKey = null;
    }

    partial void OnFrequencyKhzChanged(int value)
    {
        _channelWasChanged = true;
    }


    [RelayCommand]
    public void DeleteChannel()
    {
        WeakReferenceMessenger.Default.Send(new ChannelDeleteRequestedMessage(Id, FrequencyKhz));
    }

    public void StartTransmission()
    {
        if (ConnectionStatus == Channel.ChannelConnectionStatus.Disconnected)
            return;

        // Don't allow "click" transmissions in BMS 3d mode - rather use the comms switch
        if (Settings is { ModeIsGci: false, Is3dMode: true })
            return;

        // mute only the transmitting frequency
        var mutedFrequencies = new List<int> { FrequencyKhz };
        WeakReferenceMessenger.Default.Send(new StartTransmissionMessage(Id, FrequencyKhz, RadioStationData,
            mutedFrequencies));
    }

    public void StopTransmission()
    {
        if (ConnectionStatus == Channel.ChannelConnectionStatus.Disconnected)
            return;

        // Don't allow "click" transmissions in BMS 3d mode - rather use the comms switch
        if (Settings is { ModeIsGci: false, Is3dMode: true })
            return;

        WeakReferenceMessenger.Default.Send(new StopTransmissionMessage(Id, FrequencyKhz));
    }

    partial void OnPanChanged(int value)
    {
        WeakReferenceMessenger.Default.Send(new ChannelPanUpdateMessage(Id, FrequencyKhz, value));
    }

    [RelayCommand]
    public void ToggleJoinLeave()
    {
        WeakReferenceMessenger.Default.Send(new ChannelJoinLeaveRequestedMessage(Id, FrequencyKhz,
            ConnectionStatus != Channel.ChannelConnectionStatus.Connected, RadioStationData));
    }

    public void Join()
    {
        WeakReferenceMessenger.Default.Send(new ChannelJoinLeaveRequestedMessage(Id, FrequencyKhz,
            true, RadioStationData));
    }

    public void Leave()
    {
        WeakReferenceMessenger.Default.Send(new ChannelJoinLeaveRequestedMessage(Id, FrequencyKhz,
            false, RadioStationData));
    }

    [RelayCommand]
    public void ToggleSquelch()
    {
        IsSquelchEnabled = !IsSquelchEnabled;
        WeakReferenceMessenger.Default.Send(new SquelchEnabledDisabledMessage(channelId: Id,
            frequencyKhz: FrequencyKhz, squelchEnabled: IsSquelchEnabled));
    }

    public void Dispose()
    {
        if (PttHotKey != null)
        {
            _hotkeyService.UnregisterHotkey(IHotkeyService.HotkeyType.Ptt, PttHotKey, Id);
        }
        WeakReferenceMessenger.Default.Unregister<SignalStrengthTracker.SignalStrengthUpdateMessage>(this);
    }
}

public class ChannelUpdatedMessage(
    Guid channelId,
    int oldFrequencyKhz,
    int newFrequencyKhz,
    Channel.ChannelConnectionStatus oldConnectionStatus,
    int currentPan,
    bool isBmsChannel)
{
    public Guid ChannelId { get; } = channelId;
    public int OldFrequencyKhz { get; } = oldFrequencyKhz;
    public int NewFrequencyKhz { get; } = newFrequencyKhz;
    public Channel.ChannelConnectionStatus OldConnectionStatus { get; } = oldConnectionStatus;
    public bool IsBmsChannel { get; } = isBmsChannel;
    public int CurrentPan { get; } = currentPan;

    public bool NeedsReconnect => !IsBmsChannel &&
                                  OldFrequencyKhz != NewFrequencyKhz &&
                                  OldConnectionStatus == Channel.ChannelConnectionStatus.Connected;
}

public class ChannelJoinLeaveRequestedMessage(
    Guid channelId,
    int frequencyKhz,
    bool join,
    RadioStationData radioStationData)
{
    public Guid ChannelId { get; } = channelId;
    public int FrequencyKhz { get; } = frequencyKhz;
    public bool Join { get; } = join;
    public RadioStationData RadioStationData { get; } = radioStationData;
}

public class SquelchEnabledDisabledMessage(
    Guid channelId,
    int frequencyKhz,
    bool squelchEnabled)
{
    public Guid ChannelId { get; } = channelId;
    public int FrequencyKhz { get; } = frequencyKhz;
    public bool SquelchEnabled { get; } = squelchEnabled;
}

public class StartTransmissionMessage(
    Guid channelId,
    int frequencyKhz,
    RadioStationData radioStationData,
    List<int> mutedRadioChannels)
{
    public Guid ChannelId { get; } = channelId;
    public int FrequencyKhz { get; } = frequencyKhz;
    public RadioStationData RadioStationData { get; } = radioStationData;
    public List<int> MutedRadioChannels { get; } = mutedRadioChannels;
}

public class StopTransmissionMessage(Guid channelId, int frequencyKhz)
{
    public Guid ChannelId { get; } = channelId;
    public int FrequencyKhz { get; } = frequencyKhz;
}

public class ChannelDeleteRequestedMessage(Guid channelId, int frequencyKhz)
{
    public Guid ChannelId { get; } = channelId;
    public int FrequencyKhz { get; } = frequencyKhz;
}

public class ChannelPanUpdateMessage(Guid channelId, int frequencyKhz, int pan)
{
    public Guid ChannelId { get; } = channelId;
    public int FrequencyKhz { get; } = frequencyKhz;
    public int Pan { get; } = pan;
}
