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

    [ObservableProperty] public partial float SignalStrength { get; set; }

    [ObservableProperty] public partial Channel.ChannelStatus Status { get; set; } = Channel.ChannelStatus.Disconnected;
    [ObservableProperty] public partial bool IsEditing { get; set; } = true;

    [ObservableProperty] private bool _channelWasChanged;

    // Hotkey binding
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HotkeyDisplay), nameof(HasHotkey))]
    public partial KeyCode HotKey { get; set; } = KeyCode.VcUndefined;

    public bool HasHotkey => HotKey != KeyCode.VcUndefined;


    [ObservableProperty] private bool _isCapturingHotkey;

    public string HotkeyDisplay => GetKeyDisplayName(HotKey);

    // Reference to the data of the RadioStationGroup
    [ObservableProperty] public partial RadioStationData RadioStationData { get; set; }

    [ObservableProperty] public partial bool IsEnabled { get; set; }

    [ObservableProperty]
    public partial RadioPlayback.AudioChannel AudioChannel { get; set; } = RadioPlayback.AudioChannel.Both;

// Store original values when entering edit mode
    private int _originalFrequencyKhz;
    private KeyCode _originalBinding;

    partial void OnIsEnabledChanged(bool oldValue, bool newValue)
    {
        WeakReferenceMessenger.Default.Send(new ChannelEnabledDisabledMessage(channelId: Id,
            frequencyKhz: FrequencyKhz, enabled: newValue));
    }

    [RelayCommand]
    public void BmsLobby1Clicked()
    {
        Name = "BMS Lobby 1";
        FrequencyKhz = 1234;
        HotKey = KeyCode.VcF1;
        ToggleEditing();
    }

    [RelayCommand]
    public void BmsLobby2Clicked()
    {
        Name = "BMS Lobby 2";
        FrequencyKhz = 339750;
        HotKey = KeyCode.VcF2;
        ToggleEditing();
    }


    public ChannelCardViewModel(IHotkeyService hotkeyService, RadioStationData radioStationData,
        ChannelCardGroupViewModel parentChannelCardGroupViewModel, bool isEnabled = true, RadioType? bmsRadioType = null)
    {
        _hotkeyService = hotkeyService;
        RadioStationData = radioStationData;
        _parentChannelCardGroupViewModel = parentChannelCardGroupViewModel;
        IsEnabled = isEnabled;
        BmsRadioType = bmsRadioType;

        WeakReferenceMessenger.Default.Register<SignalStrengthTracker.SignalStrengthUpdateMessage>(this,
            (r, m) =>
            {
                if (FrequencyKhz == m.FrequencyKhz)
                {
                    SignalStrength = m.Strength;
                }
            });
    }

    public ChannelCardViewModel(IHotkeyService hotkeyService, Channel channel, RadioStationData radioStationData,
        ChannelCardGroupViewModel parentChannelCardGroupViewModel, RadioType? bmsRadioType = null)
    {
        _hotkeyService = hotkeyService;
        IsEnabled = channel.Enabled;
        BmsRadioType = bmsRadioType;
        RadioStationData = radioStationData;
        _parentChannelCardGroupViewModel = parentChannelCardGroupViewModel;
        FrequencyKhz = channel.FrequencyKhz;
        Name = channel.Name;
        RxDb = channel.RxDb;
        Status = channel.Status;
    }

    [RelayCommand]
    public void ToggleEditing()
    {
        if (!IsEditing)
        {
            // Entering edit mode - store current values
            _originalFrequencyKhz = FrequencyKhz;
            _originalBinding = HotKey;
        }
        else
        {
            // Exiting edit mode - send update if changed
            var message = new ChannelUpdatedMessage(
                Id,
                _originalFrequencyKhz,
                FrequencyKhz,
                Status,
                _originalBinding,
                HotKey,
                IsEnabled,
                AudioChannel
            );

            WeakReferenceMessenger.Default.Send(message);
        }

        IsEditing = !IsEditing;
    }

    [RelayCommand]
    private async Task BeginCaptureHotkeyAsync()
    {
        IsCapturingHotkey = true;
        try
        {
            var capturedKey = await _hotkeyService.CaptureNextKeyAsync();
            HotKey = capturedKey;
        }
        catch (OperationCanceledException)
        {
            // Capture was cancelled
        }
        finally
        {
            IsCapturingHotkey = false;
        }
    }

    partial void OnHotKeyChanging(KeyCode oldValue, KeyCode newValue)
    {
        // Unregister old binding
        if (oldValue != KeyCode.VcUndefined)
        {
            _hotkeyService.UnregisterHotkey(oldValue, Id);
        }
    }

    partial void OnHotKeyChanged(KeyCode oldValue, KeyCode newValue)
    {
        // Register new binding
        if (newValue != KeyCode.VcUndefined)
        {
            _hotkeyService.RegisterHotkey(newValue, Id);
        }
    }

    [RelayCommand]
    private void ClearHotkey()
    {
        if (HotKey != KeyCode.VcUndefined)
        {
            _hotkeyService.UnregisterHotkey(HotKey, Id);
            HotKey = KeyCode.VcUndefined;
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
        if (Status == Channel.ChannelStatus.Disconnected)
            return;

        var mutedFrequencies = _parentChannelCardGroupViewModel.GetAllFrequenciesOfChannelGroup(Type);
        mutedFrequencies.Remove(FrequencyKhz);
        WeakReferenceMessenger.Default.Send(new StartTransmissionMessage(Id, FrequencyKhz, RadioStationData, mutedFrequencies));
    }

    public void StopTransmission()
    {
        if (Status == Channel.ChannelStatus.Disconnected)
            return;

        WeakReferenceMessenger.Default.Send(new StopTransmissionMessage(Id, FrequencyKhz));
    }

    partial void OnAudioChannelChanged(RadioPlayback.AudioChannel value)
    {
        WeakReferenceMessenger.Default.Send(new ChannelAudioChannelUpdateMessage(Id, FrequencyKhz, value));
    }

    public void Dispose()
    {
        if (HotKey != KeyCode.VcUndefined)
        {
            _hotkeyService.UnregisterHotkey(HotKey, Id);
        }
    }
}

public class ChannelUpdatedMessage(
    Guid channelId,
    int oldFrequencyKhz,
    int newFrequencyKhz,
    Channel.ChannelStatus oldStatus,
    KeyCode oldBinding,
    KeyCode newBinding,
    bool isEnabled,
    RadioPlayback.AudioChannel currentAudioChannel)
{
    public Guid ChannelId { get; } = channelId;
    public int OldFrequencyKhz { get; } = oldFrequencyKhz;
    public int NewFrequencyKhz { get; } = newFrequencyKhz;
    public Channel.ChannelStatus OldStatus { get; } = oldStatus;

    public KeyCode OldBinding { get; } = oldBinding;
    public KeyCode NewBinding { get; } = newBinding;

    public bool IsEnabled { get; } = isEnabled;

    public RadioPlayback.AudioChannel CurrentAudioChannel { get; } = currentAudioChannel;

    public bool NeedsReconnect => OldFrequencyKhz != NewFrequencyKhz;
    public bool BindingChanged => OldBinding != NewBinding;
}

public class ChannelEnabledDisabledMessage(
    Guid channelId,
    int frequencyKhz,
    bool enabled)
{
    public Guid ChannelId { get; } = channelId;
    public int FrequencyKhz { get; } = frequencyKhz;
    public bool Enabled { get; } = enabled;
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