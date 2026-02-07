using System;
using System.Collections.ObjectModel;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Platform;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using FalconBmsDataService.Models;
using FalconBmsDataService.Services;
using FalconRadioService.Services;
using OpenFreq.Services.Acmi;
using OpenFreqAudio;
using OpenFreqClient.Models;
using OpenFreqClient.Services.Interfaces;

namespace OpenFreqClient.ViewModels;

public partial class SettingsViewModel : ViewModelBase
{
    private Window MainWindow => ((IClassicDesktopStyleApplicationLifetime)Application.Current!.ApplicationLifetime!)
        .MainWindow!;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsReadyToConnect))]
    public partial string OpenFreqServerAddress { get; set; } = string.Empty;

    [ObservableProperty] public partial string OpenFreqPassword { get; set; } = string.Empty;
    [ObservableProperty] public partial ObservableCollection<string> PlaybackDeviceNames { get; set; } = [];

    [ObservableProperty] public partial ObservableCollection<string> RecordingDeviceNames { get; set; } = [];

    [ObservableProperty] public partial int RecordingDeviceIndex { get; set; }
    [ObservableProperty] public partial int PlaybackDeviceIndex { get; set; }
    [ObservableProperty] public partial string SelectedTheater { get; set; } = "Korea KTO";


    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ModeIsGci))]
    [NotifyPropertyChangedFor(nameof(IsReadyToConnect))]
    private IOpenFreqService.Mode _connectionMode = IOpenFreqService.Mode.BMS;

    public bool ModeIsGci
    {
        get => ConnectionMode == IOpenFreqService.Mode.GCI;
        set => ConnectionMode = value ? IOpenFreqService.Mode.GCI : IOpenFreqService.Mode.BMS;
    }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsReadyToConnect))]
    public partial string TacviewServerAddress { get; set; } = string.Empty;

    [ObservableProperty] public partial string TacviewServerPassword { get; set; } = string.Empty;

    [ObservableProperty] public partial string HeightmapPath { get; set; } = string.Empty;

    [ObservableProperty] public partial string InputDeviceName { get; set; } = string.Empty;

    [ObservableProperty] public partial string OutputDeviceName { get; set; } = string.Empty;

    private readonly IAudioService _audioService;
    private readonly IFalconRadioSharedMemoryService _falconRadioSharedMemoryService;
    private readonly IFalconSharedMemoryService _falconSharedMemoryService;
    private readonly IAcmiClientService _acmiClientService;
    private readonly IOpenFreqService _openFreqService;

    // Window size & position
    private int _left, _top, _width, _height, _windowState;
    private int _maximizedScreenX, _maximizedScreenY, _maximizedScreenWidth, _maximizedScreenHeight;

    public bool IsReadyToConnect => OpenFreqServerAddress != string.Empty &&
                                    (
                                        (ModeIsGci &&
                                         HeightmapPath != string.Empty)
                                        || !ModeIsGci
                                    );

    [ObservableProperty]
    public partial RadioPlayback.AudioChannel BmsUhfAudioChannel { get; set; } = RadioPlayback.AudioChannel.Both;

    [ObservableProperty]
    public partial RadioPlayback.AudioChannel BmsVhfAudioChannel { get; set; } = RadioPlayback.AudioChannel.Both;

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

    public SettingsViewModel(IAudioService audioService, IFalconRadioSharedMemoryService falconRadioSharedMemoryService,
        IFalconSharedMemoryService falconSharedMemoryService, IAcmiClientService acmiClientService,
        IOpenFreqService openFreqService)
    {
        _audioService = audioService;
        _falconSharedMemoryService = falconSharedMemoryService;
        _falconRadioSharedMemoryService = falconRadioSharedMemoryService;
        _acmiClientService = acmiClientService;
        _openFreqService = openFreqService;
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

        _audioService.PlaybackDevicesChanged += OnPlaybackDevicesChanged;
        _audioService.RecordingDevicesChanged += OnRecordingDevicesChanged;
    }

    private void OnRecordingDevicesChanged(object? sender, DeviceChangedEventArgs e)
    {
        Dispatcher.UIThread.Post(() =>
        {
            Console.WriteLine(
                $"[ViewModel] Recording devices changed. Old={e.OldDeviceIndex}, New={e.NewDeviceIndex}, Removed={e.DeviceWasRemoved}");

            // Update the device list
            RecordingDeviceNames.Clear();
            foreach (var device in e.Devices)
            {
                RecordingDeviceNames.Add(device);
            }

            // Update selection
            RecordingDeviceIndex = e.NewDeviceIndex;

            // If device was removed and we're transmitting, switch the active device
            if (e.DeviceWasRemoved && e.NewDeviceIndex != e.OldDeviceIndex)
            {
                Console.WriteLine($"[ViewModel] Recording device was removed, switching to device {e.NewDeviceIndex}");
            }
        });
    }

    private void OnPlaybackDevicesChanged(object? sender, DeviceChangedEventArgs e)
    {
        Dispatcher.UIThread.Post(() =>
        {
            Console.WriteLine(
                $"[ViewModel] Playback devices changed. Old={e.OldDeviceIndex}, New={e.NewDeviceIndex}, Removed={e.DeviceWasRemoved}");

            // Update the device list
            PlaybackDeviceNames.Clear();
            foreach (var device in e.Devices)
            {
                PlaybackDeviceNames.Add(device);
            }

            // Update selection
            PlaybackDeviceIndex = e.NewDeviceIndex;

            // If device was removed and we're connected, switch the active device
            if (e.DeviceWasRemoved && e.NewDeviceIndex != e.OldDeviceIndex)
            {
                Console.WriteLine($"[ViewModel] Playback device was removed, switching to device {e.NewDeviceIndex}");
            }
        });
    }

    partial void OnRecordingDeviceIndexChanged(int value)
    {
        if (value >= 0 && value < RecordingDeviceNames.Count)
        {
            InputDeviceName = RecordingDeviceNames[value];
            _openFreqService.RecordingDeviceIndex = value;
        }
    }

    partial void OnPlaybackDeviceIndexChanged(int value)
    {
        if (value >= 0 && value < PlaybackDeviceNames.Count)
        {
            OutputDeviceName = PlaybackDeviceNames[value];
            _openFreqService.PlaybackDeviceIndex = value;
        }
    }

    partial void OnBmsUhfAudioChannelChanged(RadioPlayback.AudioChannel value)
    {
        var uhfChannel = _falconRadioSharedMemoryService.GetRadioChannel(RadioType.UHF);
        var guardChannel = _falconRadioSharedMemoryService.GetRadioChannel(RadioType.GUARD);
        if (uhfChannel != null)
        {
            _openFreqService.SetAudioChannel(uhfChannel.Frequency, value);
        }

        if (guardChannel != null)
        {
            _openFreqService.SetAudioChannel(guardChannel.Frequency, value);
        }
    }

    partial void OnBmsVhfAudioChannelChanged(RadioPlayback.AudioChannel value)
    {
        var vhfChannel = _falconRadioSharedMemoryService.GetRadioChannel(RadioType.UHF);
        if (vhfChannel != null)
        {
            _openFreqService.SetAudioChannel(vhfChannel.Frequency, value);
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

        RestoreWindowPosition(settings);
    }

    private void RestoreWindowPosition(OpenFreqSettings settings)
    {
        // Window settings
        if (settings.Left == null || settings.Top == null || settings.Height == null || settings.Width == null ||
            settings.WindowState == null)
        {
            // There are no settings to restore.
            // So leave the windows size and position at their defaults.
            return;
        }

        if (settings.WindowState == (int)WindowState.Maximized)
        {
            // Try to find the screen it was maximized on
            var screenToMaximeOn = FindScreenByBounds(
                settings.MaximizedScreenX,
                settings.MaximizedScreenY,
                settings.MaximizedScreenWidth,
                settings.MaximizedScreenHeight);

            if (screenToMaximeOn != null)
            {
                // Position window on that screen before maximizing
                MainWindow.Position = new PixelPoint(
                    screenToMaximeOn.WorkingArea.X + 100,
                    screenToMaximeOn.WorkingArea.Y + 100);
            }

            MainWindow.WindowState = WindowState.Maximized;
            return;
        }

        // Never restore to minimized
        if (settings.WindowState.Value == (int)WindowState.Minimized)
        {
            settings.WindowState = (int)WindowState.Normal;
        }

        var savedPosition = new PixelPoint(settings.Left.Value, settings.Top.Value);
        var screen = FindScreenContainingPositionInWorkingArea(savedPosition);
        if (screen == null)
        {
            // The saved window position (its top left corner) is not in the working area of an active screen.
            // So leave the windows size and position at their defaults.
            return;
        }

        const int min = 50;
        if (settings.Left.Value > screen.WorkingArea.X + screen.WorkingArea.Width - min
            || settings.Top.Value > screen.WorkingArea.Y + screen.WorkingArea.Height - min)
        {
            // The saved top left corner (position) is so close to the right or bottom edge of the screen's working area as to make the window difficult to access.
            // So leave the windows size and position at their defaults.
            return;
        }

        MainWindow.Position = savedPosition;

        MainWindow.Width = settings.Width.Value;
        MainWindow.Height = settings.Height.Value;
    }

    private Screen? FindScreenContainingPositionInWorkingArea(PixelPoint position)
    {
        return (
            // All active screens, not just any screens overlapping the window! 
            from screen in ((IClassicDesktopStyleApplicationLifetime)Application.Current!.ApplicationLifetime!)
                .MainWindow!.Screens.All
            where screen.WorkingArea.Contains(position)
            select screen).FirstOrDefault();
    }

    private Screen? FindScreenByBounds(int? x, int? y, int? width, int? height)
    {
        if (!x.HasValue || !y.HasValue || !width.HasValue || !height.HasValue)
            return null;

        return MainWindow.Screens.All.FirstOrDefault(s =>
            s.Bounds.X == x.Value &&
            s.Bounds.Y == y.Value &&
            s.Bounds.Width == width.Value &&
            s.Bounds.Height == height.Value);
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
            SelectedTheater = SelectedTheater,
            Left = _left,
            Top = _top,
            Width = _width,
            Height = _height,
            WindowState = _windowState,
            MaximizedScreenHeight = _maximizedScreenHeight,
            MaximizedScreenWidth = _maximizedScreenWidth,
            MaximizedScreenX = _maximizedScreenX,
            MaximizedScreenY = _maximizedScreenY
        };
    }

    public void UpdateWindowSettings()
    {
        // Always save the current state
        _windowState = (int)MainWindow.WindowState;

        switch (MainWindow.WindowState)
        {
            case WindowState.Minimized:
                return;

            case WindowState.Normal:
                _left = MainWindow.Position.X;
                _top = MainWindow.Position.Y;
                _width = (int)MainWindow.Width;
                _height = (int)MainWindow.Height;
                break;

            case WindowState.Maximized:
                var screen = MainWindow.Screens.ScreenFromWindow(MainWindow);
                if (screen != null)
                {
                    _maximizedScreenX = screen.Bounds.X;
                    _maximizedScreenY = screen.Bounds.Y;
                    _maximizedScreenWidth = screen.Bounds.Width;
                    _maximizedScreenHeight = screen.Bounds.Height;
                }

                // Don't update _left, _top, _width, _height - keep the last normal values
                break;
        }
    }
}