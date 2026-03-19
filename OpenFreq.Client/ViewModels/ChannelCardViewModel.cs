using System;
using System.Collections.Generic;
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
    private readonly ChannelCardGroupViewModel _parentChannelCardGroupViewModel;
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
        get => (FrequencyKhz / 1000d).ToString("F3");
        set
        {
            if (double.TryParse(value, out var mhz))
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
    public partial KeyCode PttHotKey { get; set; } = KeyCode.VcUndefined;

    public bool HasPttHotkey => PttHotKey != KeyCode.VcUndefined;


    [ObservableProperty] public partial bool IsCapturingPttHotkey { get; set; }

    public string HotkeyDisplay => GetKeyDisplayName(PttHotKey);

    [ObservableProperty] public partial KeyCode SquelchHotKey { get; set; } = KeyCode.VcUndefined;

    // Reference to the data of the RadioStationGroup
    [ObservableProperty] public partial RadioStationData RadioStationData { get; set; }

    [ObservableProperty] public partial bool IsEditable { get; set; }

    [ObservableProperty]
    public partial RadioPlayback.AudioChannel AudioChannel { get; set; } = RadioPlayback.AudioChannel.Both;

    [ObservableProperty] public partial bool IsSquelchEnabled { get; set; } = true;

    // Store original values when entering edit mode
    private int _originalFrequencyKhz;
    private KeyCode _originalBinding;
    private readonly IOpenFreqService _openFreqService;


    [RelayCommand]
    public void BmsLobby1Clicked()
    {
        Name = "BMS Lobby 1";
        FrequencyKhz = 1234;
        PttHotKey = KeyCode.VcF1;
        ToggleEditing();
    }

    [RelayCommand]
    public void BmsLobby2Clicked()
    {
        Name = "BMS Lobby 2";
        FrequencyKhz = 339750;
        PttHotKey = KeyCode.VcF2;
        ToggleEditing();
    }


    public ChannelCardViewModel(IOpenFreqService openFreqService, IHotkeyService hotkeyService, string name,
        int frequencyKhz, bool isInEditMode,
        RadioStationData radioStationData,
        ChannelCardGroupViewModel parentChannelCardGroupViewModel, SettingsViewModel settings, bool isEditable = true,
        RadioType? bmsRadioType = null)
    {
        _openFreqService = openFreqService;
        Name = name;
        FrequencyKhz = frequencyKhz;
        IsEditing = isInEditMode;
        _hotkeyService = hotkeyService;
        RadioStationData = radioStationData;
        _parentChannelCardGroupViewModel = parentChannelCardGroupViewModel;
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
            _originalBinding = PttHotKey;
        }
        else
        {
            // Exiting edit mode - send update if changed
            var message = new ChannelUpdatedMessage(
                Id,
                _originalFrequencyKhz,
                FrequencyKhz,
                ConnectionStatus,
                AudioChannel,
                _parentChannelCardGroupViewModel.IsBmsGroup
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
            var capturedKey = await _hotkeyService.CaptureNextKeyAsync();
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

    partial void OnPttHotKeyChanging(KeyCode oldValue, KeyCode newValue)
    {
        // Unregister old binding
        if (oldValue != KeyCode.VcUndefined)
        {
            _hotkeyService.UnregisterHotkey(IHotkeyService.HotkeyType.Ptt, oldValue, Id);
        }
    }

    partial void OnPttHotKeyChanged(KeyCode oldValue, KeyCode newValue)
    {
        // Register new binding
        if (newValue != KeyCode.VcUndefined)
        {
            _hotkeyService.RegisterHotkey(IHotkeyService.HotkeyType.Ptt, newValue, Id);
        }
    }

    partial void OnSquelchHotKeyChanging(KeyCode oldValue, KeyCode newValue)
    {
        // Unregister old binding
        if (oldValue != KeyCode.VcUndefined)
        {
            _hotkeyService.UnregisterHotkey(IHotkeyService.HotkeyType.SquelchToggle, oldValue, Id);
        }
    }

    partial void OnSquelchHotKeyChanged(KeyCode oldValue, KeyCode newValue)
    {
        // Register new binding
        if (newValue != KeyCode.VcUndefined)
        {
            _hotkeyService.RegisterHotkey(IHotkeyService.HotkeyType.SquelchToggle, newValue, Id);
        }
    }

    [RelayCommand]
    private void ClearPttHotkey()
    {
        if (PttHotKey != KeyCode.VcUndefined)
        {
            _hotkeyService.UnregisterHotkey(IHotkeyService.HotkeyType.Ptt, PttHotKey, Id);
            PttHotKey = KeyCode.VcUndefined;
        }
    }

    private string GetKeyDisplayName(KeyCode key)
    {
        return key == KeyCode.VcUndefined ? "None" : key.ToString().Replace("Vc", "");
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

        var mutedFrequencies = _parentChannelCardGroupViewModel.GetAllFrequenciesOfChannelGroup(Type);
        mutedFrequencies.Remove(FrequencyKhz);
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

    partial void OnAudioChannelChanged(RadioPlayback.AudioChannel value)
    {
        WeakReferenceMessenger.Default.Send(new ChannelAudioChannelUpdateMessage(Id, FrequencyKhz, value));
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
        if (PttHotKey != KeyCode.VcUndefined)
        {
            _hotkeyService.UnregisterHotkey(IHotkeyService.HotkeyType.Ptt, PttHotKey, Id);
        }
    }
}

public class ChannelUpdatedMessage(
    Guid channelId,
    int oldFrequencyKhz,
    int newFrequencyKhz,
    Channel.ChannelConnectionStatus oldConnectionStatus,
    RadioPlayback.AudioChannel currentAudioChannel,
    bool isBmsChannel)
{
    public Guid ChannelId { get; } = channelId;
    public int OldFrequencyKhz { get; } = oldFrequencyKhz;
    public int NewFrequencyKhz { get; } = newFrequencyKhz;
    public Channel.ChannelConnectionStatus OldConnectionStatus { get; } = oldConnectionStatus;
    public bool IsBmsChannel { get; } = isBmsChannel;

    public RadioPlayback.AudioChannel CurrentAudioChannel { get; } = currentAudioChannel;

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

public class ChannelAudioChannelUpdateMessage(Guid channelId, int frequencyKhz, RadioPlayback.AudioChannel audioChannel)
{
    public Guid ChannelId { get; } = channelId;
    public int FrequencyKhz { get; } = frequencyKhz;
    public RadioPlayback.AudioChannel AudioChannel { get; } = audioChannel;
}