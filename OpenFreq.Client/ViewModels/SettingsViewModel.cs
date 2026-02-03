using System;
using System.Collections.ObjectModel;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using FalconBmsDataService.Services;
using FalconRadioService.Services;
using OpenFreq.Services.Acmi;
using OpenFreqAudio;
using OpenFreqClient.Models;
using OpenFreqClient.Services.Interfaces;

namespace OpenFreqClient.ViewModels;

public partial class SettingsViewModel : ViewModelBase
{
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(IsReadyToConnect))]
    private string _openFreqServerAddress = string.Empty;

    [ObservableProperty] private string _openFreqPassword = string.Empty;

    [ObservableProperty] private ObservableCollection<string> _playbackDeviceNames = new();
    [ObservableProperty] private ObservableCollection<string> _recordingDeviceNames = new();
    [ObservableProperty] private int _recordingDeviceIndex;
    [ObservableProperty] private int _playbackDeviceIndex;
    [ObservableProperty] public partial string SelectedTheater { get; set; } = "Korea KTO";
    


    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ModeIsGci))]
    [NotifyPropertyChangedFor(nameof(IsReadyToConnect))]
    private IOpenFreqService.Mode _connectionMode = IOpenFreqService.Mode.BMS;

    public bool ModeIsGci
    {
        get => ConnectionMode == IOpenFreqService.Mode.GCI;
        set { ConnectionMode = value ? IOpenFreqService.Mode.GCI : IOpenFreqService.Mode.BMS; }
    }

    [ObservableProperty] [NotifyPropertyChangedFor(nameof(IsReadyToConnect))]
    private string _tacviewServerAddress = string.Empty;

    [ObservableProperty] private string _tacviewServerPassword = string.Empty;
    [ObservableProperty] private string _heightmapPath = string.Empty;

    [ObservableProperty] private string _inputDeviceName = string.Empty;
    [ObservableProperty] private string _outputDeviceName = string.Empty;
    private readonly IAudioService _audioService;
    private readonly IFalconRadioSharedMemoryService _falconRadioSharedMemoryService;
    private readonly IFalconSharedMemoryService _falconSharedMemoryService;
    private readonly IAcmiClientService _acmiClientService;

    public bool IsReadyToConnect => OpenFreqServerAddress != string.Empty &&
                                    (
                                        (ModeIsGci && TacviewServerAddress != string.Empty &&
                                         HeightmapPath != string.Empty)
                                        || !ModeIsGci
                                    );

    [ObservableProperty] public partial RadioPlayback.AudioChannel BmsUhfAudioChannel { get; set; } = RadioPlayback.AudioChannel.Both;
    [ObservableProperty] public partial RadioPlayback.AudioChannel BmsVhfAudioChannel { get; set; } = RadioPlayback.AudioChannel.Both;

    partial void OnConnectionModeChanged(IOpenFreqService.Mode value)
    { 
        switch (value)
        {
            case IOpenFreqService.Mode.BMS:
                _falconSharedMemoryService.Start();
                _falconRadioSharedMemoryService.Start();
                _acmiClientService.DisconnectAsync().Wait(50);
                _acmiClientService.Stop();
                break;
            case IOpenFreqService.Mode.GCI:
                _falconSharedMemoryService.Stop();
                _falconRadioSharedMemoryService.Stop();
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(value), value, null);
        }
    }

    public SettingsViewModel(IAudioService audioService, IFalconRadioSharedMemoryService falconRadioSharedMemoryService, IFalconSharedMemoryService falconSharedMemoryService, IAcmiClientService acmiClientService)
    {
        _audioService = audioService;
        _falconSharedMemoryService = falconSharedMemoryService;
        _falconRadioSharedMemoryService = falconRadioSharedMemoryService;
        _acmiClientService = acmiClientService;
        InitializeAudioDevices();
    }

    private void InitializeAudioDevices()
    {
        _audioService.Init();

        var playbackDevices = _audioService.GetPlaybackDevices();
        PlaybackDeviceNames = new ObservableCollection<string>(playbackDevices);
        PlaybackDeviceIndex = _audioService.DefaultPlaybackDevice;

        var recordingDevices = _audioService.GetRecordingDevices();
        RecordingDeviceNames = new ObservableCollection<string>(recordingDevices);
        RecordingDeviceIndex = _audioService.DefaultRecordingDevice;
    }

    partial void OnRecordingDeviceIndexChanged(int value)
    {
        if (value >= 0 && value < RecordingDeviceNames.Count)
        {
            _inputDeviceName = RecordingDeviceNames[value];
        }
    }

    partial void OnPlaybackDeviceIndexChanged(int value)
    {
        if (value >= 0 && value < PlaybackDeviceNames.Count)
        {
            _outputDeviceName = PlaybackDeviceNames[value];
        }
    }

    public void LoadFromSettings(OpenFreqSettings settings)
    {
        OpenFreqServerAddress = settings.OpenFreqServerAddress;
        OpenFreqPassword = settings.OpenFreqPassword;
        ConnectionMode = settings.OwnPositionMode;
        TacviewServerAddress = settings.TacviewServerAddress;
        TacviewServerPassword = settings.TacviewServerPassword;
        SelectedTheater = settings.SelectedTheater;

        // Restore audio device selection
        InputDeviceName = settings.InputDeviceName;
        OutputDeviceName = settings.OutputDeviceName;

        if (!string.IsNullOrEmpty(InputDeviceName))
        {
            RecordingDeviceIndex = RecordingDeviceNames.ToList().IndexOf(InputDeviceName);
            if (RecordingDeviceIndex < 0)
                RecordingDeviceIndex = _audioService.DefaultRecordingDevice;
        }

        if (!string.IsNullOrEmpty(OutputDeviceName))
        {
            PlaybackDeviceIndex = PlaybackDeviceNames.ToList().IndexOf(OutputDeviceName);
            if (PlaybackDeviceIndex < 0)
                PlaybackDeviceIndex = _audioService.DefaultPlaybackDevice;
        }
    }

    public OpenFreqSettings GetSettings()
    {
        return new OpenFreqSettings
        {
            OpenFreqServerAddress = OpenFreqServerAddress,
            OpenFreqPassword = OpenFreqPassword,
            OwnPositionMode = ConnectionMode,
            TacviewServerAddress = TacviewServerAddress,
            TacviewServerPassword = TacviewServerPassword,
            InputDeviceName = InputDeviceName,
            OutputDeviceName = OutputDeviceName,
            HeightmapPath = HeightmapPath,
            SelectedTheater =  SelectedTheater
        };
    }
}