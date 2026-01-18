using System;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CommunityToolkit.Mvvm.Messaging;
using OpenFreqAudio;
using OpenFreqClient.Models;
using OpenFreqClient.Services.Interfaces;
using SharpHook.Data;

namespace OpenFreqClient.ViewModels;

public partial class ChannelCardViewModel : ViewModelBase, IDisposable
{
    private readonly IHotkeyService _hotkeyService;
    public RadioStationPreset Preset { get; set; }

    public Guid Id { get; } = Guid.NewGuid();

    [ObservableProperty] [NotifyPropertyChangedFor(nameof(FrequencyError), nameof(FrequencyMhzString))]
    private double _frequencyMhz;

    /// <summary>
    /// Frequency display string in MHz
    /// Internal storage is in Hz (SI unit)
    /// </summary>
    public string FrequencyMhzString
    {
        get => (FrequencyMhz).ToString("F3");
        set
        {
            if (double.TryParse(value, out var mhz))
            {
                FrequencyMhz = mhz;
            }
        }
    }

    [ObservableProperty] private string? _name;
    [ObservableProperty] private float _rxDb;
    [ObservableProperty] private Channel.ChannelType _type;
    [ObservableProperty] private bool _isEnabled = true;

    public Channel.ChannelType[] ChannelTypes =>
        Enum.GetValues(typeof(Channel.ChannelType)).Cast<Channel.ChannelType>().ToArray();

    [ObservableProperty] private Channel.ChannelStatus _status = Channel.ChannelStatus.Disconnected;
    [ObservableProperty] private bool _isEditing = true; // new channels are in edit mode by default

    [ObservableProperty] private bool _channelWasChanged = false;

    // Hotkey binding
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HotkeyDisplay), nameof(HasHotkey))]
    public partial KeyCode HotKey { get; set; } = KeyCode.VcUndefined;

    public bool HasHotkey => HotKey != KeyCode.VcUndefined;


    [ObservableProperty] private bool _isCapturingHotkey =
        false;

    public string HotkeyDisplay => GetKeyDisplayName(HotKey);

// Store original values when entering edit mode
    private double _originalFrequencyMhz;
    private Channel.ChannelType _originalType;
    private KeyCode _originalBinding;

    public string? FrequencyError
    {
        get
        {
            if (Type == Channel.ChannelType.UHF && FrequencyMhz is < 225.000 or > 399.975)
                return "UHF frequency must be between 225.0 and 399.975 MHz";

            if (Type == Channel.ChannelType.VHF && FrequencyMhz is < 118.0 or > 137.0)
                return "VHF frequency must be between 118.0 and 137.0 MHz";
            
            // Custom: don't care
            
            // No error
            return null;
        }
    }
    
    [RelayCommand]
    public void BmsLobby1Clicked()
    {
        Name = "BMS Lobby 1";
        FrequencyMhz = 307.3;
        HotKey = KeyCode.VcF1;
        Type = Channel.ChannelType.Custom;
        ToggleEditing();
    }
    
    [RelayCommand]
    public void BmsLobby2Clicked()
    {
        Name = "BMS Lobby 2";
        FrequencyMhz = 1.234;
        HotKey = KeyCode.VcF2;
        Type = Channel.ChannelType.Custom;
        ToggleEditing();
    }


    public ChannelCardViewModel(IHotkeyService hotkeyService, RadioStationPreset preset)
    {
        _hotkeyService = hotkeyService;
        Preset = preset;
    }

    public ChannelCardViewModel(IHotkeyService hotkeyService, Channel channel, RadioStationPreset preset)
    {
        _hotkeyService = hotkeyService;
        Preset = preset;
        _frequencyMhz = channel.FrequencyMhz;
        _name = channel.Name;
        _rxDb = channel.RxDb;
        _type = channel.Type;
        _status = channel.Status;
    }

    [RelayCommand]
    public void ToggleEnabled()
    {
        IsEnabled = !IsEnabled;
        WeakReferenceMessenger.Default.Send(new ChannelEnabledDisabledMessage(channelId: Id, frequencyMhz: FrequencyMhz, enabled: IsEnabled));
    }

    [RelayCommand]
    public void ToggleEditing()
    {
        if (!IsEditing)
        {
            // Entering edit mode - store current values
            _originalFrequencyMhz = FrequencyMhz;
            _originalType = Type;
            _originalBinding = HotKey;
        }
        else
        {
            // Exiting edit mode - send update if valid and changed
            if (FrequencyError == null)
            {
                var message = new ChannelUpdatedMessage(
                    Id,
                    _originalFrequencyMhz,
                    FrequencyMhz,
                    _originalType,
                    Type,
                    Status,
                    _originalBinding,
                    HotKey
                );

                WeakReferenceMessenger.Default.Send(message);
            }
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

    partial void OnTypeChanged(Channel.ChannelType value)
    {
        // Revalidate frequency when type changes
        OnPropertyChanged(nameof(FrequencyError));
        _channelWasChanged = true;
    }

    partial void OnFrequencyMhzChanged(double value)
    {
        _channelWasChanged = true;
    }

    public Channel GetChannel()
    {
        return new Channel()
        {
            Name = this.Name, FrequencyMhz = this.FrequencyMhz, RxDb = this.RxDb, Type = this.Type
        };
    }

    [RelayCommand]
    public void DeleteChannel()
    {
        WeakReferenceMessenger.Default.Send(new ChannelDeleteRequestedMessage(Id, FrequencyMhz));
    }
    
    public void StartTransmission()
    {
        if (Status == Channel.ChannelStatus.Disconnected || FrequencyError != null)
            return;

        WeakReferenceMessenger.Default.Send(new StartTransmissionMessage(Id, FrequencyMhz, Preset));
    }

    public void StopTransmission()
    {
        if (Status == Channel.ChannelStatus.Disconnected)
            return;

        WeakReferenceMessenger.Default.Send(new StopTransmissionMessage(Id, FrequencyMhz));
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
    double oldFrequencyMhz,
    double newFrequencyMhz,
    Channel.ChannelType oldType,
    Channel.ChannelType newType,
    Channel.ChannelStatus oldStatus,
    KeyCode oldBinding,
    KeyCode newBinding)
{
    public Guid ChannelId { get; } = channelId;
    public double OldFrequencyMhz { get; } = oldFrequencyMhz;
    public double NewFrequencyMhz { get; } = newFrequencyMhz;
    public Channel.ChannelType OldType { get; } = oldType;
    public Channel.ChannelType NewType { get; } = newType;
    public Channel.ChannelStatus OldStatus { get; } = oldStatus;

    public KeyCode OldBinding { get; } = oldBinding;
    public KeyCode NewBinding { get; } = newBinding;


    public bool NeedsReconnect => OldFrequencyMhz != NewFrequencyMhz || OldType != NewType;
    public bool BindingChanged => OldBinding != NewBinding;
}

public class ChannelEnabledDisabledMessage(
    Guid channelId,
    double frequencyMhz,
    bool enabled)
{
    public Guid ChannelId { get; } = channelId;
    public double FrequencyMhz { get; } = frequencyMhz;
    public bool Enabled { get; } = enabled;
}

public class StartTransmissionMessage(Guid channelId, double frequencyMhz, RadioStationPreset stationPreset)
{
    public Guid ChannelId { get; } = channelId;
    public double FrequencyMhz { get; } = frequencyMhz;
    public RadioStationPreset stationPreset { get; } = stationPreset;
}

public class StopTransmissionMessage
{
    public Guid ChannelId { get; }
    public double FrequencyMhz { get; }

    public StopTransmissionMessage(Guid channelId, double frequencyMhz)
    {
        ChannelId = channelId;
        FrequencyMhz = frequencyMhz;
    }
}

public class ChannelDeleteRequestedMessage
{
    public Guid ChannelId { get; }
    public double FrequencyMhz { get; }
    
    public ChannelDeleteRequestedMessage(Guid channelId, double frequencyMhz)
    {
        ChannelId = channelId;
        FrequencyMhz = frequencyMhz;
    }
}