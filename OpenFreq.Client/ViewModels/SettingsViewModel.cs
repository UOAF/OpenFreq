using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Platform;
using Avalonia.Styling;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using FalconBmsDataService.Services;
using FalconRadioService.Services;
using Microsoft.Extensions.Logging;
using OpenFreq.Client.Models;
using OpenFreq.Services.Acmi;
using OpenFreq.Utilities;
using OpenFreqClient.Models;
using OpenFreqClient.Services;
using OpenFreqClient.Services.Interfaces;

namespace OpenFreqClient.ViewModels;

public partial class SettingsViewModel : ViewModelBase, IDisposable
{
    private Window MainWindow => ((IClassicDesktopStyleApplicationLifetime)Application.Current!.ApplicationLifetime!)
        .MainWindow!;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsReadyToConnect))]
    public partial string OpenFreqServerAddress { get; set; } = string.Empty;
    [ObservableProperty] public partial ObservableCollection<string> OpenFreqServerAddressHistory { get; set; } = [];
    [ObservableProperty] public partial string DisplayName { get; set; } = "Joe Pilot";
    [ObservableProperty] public partial string OpenFreqPassword { get; set; } = string.Empty;
    [ObservableProperty] public partial ObservableCollection<string> PlaybackDeviceNames { get; set; } = [];

    [ObservableProperty] public partial ObservableCollection<string> RecordingDeviceNames { get; set; } = [];

    [ObservableProperty] public partial bool HasPlaybackDevices { get; set; }
    [ObservableProperty] public partial bool HasRecordingDevices { get; set; }

    [ObservableProperty] public partial int RecordingDeviceIndex { get; set; }
    [ObservableProperty] public partial int PlaybackDeviceIndex { get; set; }
    [ObservableProperty] public partial string SelectedTheater { get; set; } = "Korea KTO";
    [ObservableProperty] public partial string MapLayer { get; set; } = "Carto";

    // BMS auto-detection
    private static readonly string[] DefaultTheaterNames = ["Korea KTO", "Balkans", "Ikaros", "ITO"];

    [ObservableProperty] public partial bool BmsInstallFound { get; set; }

    [ObservableProperty] public partial ObservableCollection<TheaterDefinition> TheaterDefinitions { get; set; } = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsReadyToConnect))]
    public partial TheaterDefinition? SelectedBmsTheater { get; set; }

    [ObservableProperty] public partial ObservableCollection<string> AvailableTheaterNames { get; set; } = new(DefaultTheaterNames);


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
    [ObservableProperty] public partial ObservableCollection<string> TacviewServerAddressHistory { get; set; } = [];

    [ObservableProperty] public partial string TacviewServerPassword { get; set; } = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsReadyToConnect))]
    public partial string HeightmapPath { get; set; } = string.Empty;

    [ObservableProperty] public partial string InputDeviceName { get; set; } = string.Empty;

    [ObservableProperty] public partial string OutputDeviceName { get; set; } = string.Empty;

    [ObservableProperty] public partial double MasterVolume { get; set; } = 1.0;
    [ObservableProperty] public partial bool SidetoneEnabled { get; set; } = false;
    [ObservableProperty] public partial bool MicNormalizationEnabled { get; set; } = true;
    [ObservableProperty] public partial double SidetoneVolume { get; set; } = 0.4;
    [ObservableProperty] public partial double AmbientNoiseVolume { get; set; } = 1.0;
    [ObservableProperty] public partial bool AutoRecordInGameMode { get; set; } = false;
    [ObservableProperty] public partial string RecordingPath { get; set; } = Path.Combine(Path.GetDirectoryName(Environment.ProcessPath) ?? AppContext.BaseDirectory, "recordings");
    /// <summary>False = capture to file, True = stream the capture mix to a playback device.</summary>
    [ObservableProperty] public partial bool StreamToDevice { get; set; } = false;
    /// <summary>Inverse of <see cref="StreamToDevice"/>, for the "Record to file" radio button.</summary>
    public bool RecordToFile
    {
        get => !StreamToDevice;
        set { if (value) StreamToDevice = false; }
    }
    /// <summary>When true, own voice in the capture gets the full radio FX; when false it stays clean.</summary>
    [ObservableProperty] public partial bool ApplyOwnVoiceSfx { get; set; } = true;
    /// <summary>List index into <see cref="PlaybackDeviceNames"/> for the monitor/stream output device.</summary>
    [ObservableProperty] public partial int MonitorDeviceIndex { get; set; }
    [ObservableProperty] public partial string MonitorDeviceName { get; set; } = string.Empty;
    [ObservableProperty] public partial bool IsDarkMode { get; set; }
    [ObservableProperty] public partial bool MinimizeOnConnect { get; set; } = true;

    public bool IsWindowsPlatform { get; } = OperatingSystem.IsWindows();

    private readonly ILogger<SettingsViewModel> _logger;
    private readonly IAudioService _audioService;
    private readonly IFalconRadioSharedMemoryService _falconRadioSharedMemoryService;
    private readonly IFalconSharedMemoryService _falconSharedMemoryService;
    private readonly IAcmiClientService _acmiClientService;
    private readonly IOpenFreqService _openFreqService;
    private readonly IHotkeyService _hotkeyService;

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
    public partial int BmsRadio1Pan { get; set; } = 0;

    [ObservableProperty]
    public partial int BmsRadio2Pan { get; set; } = 0;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(BmsUhfSquelchHotkeyDisplay))]
    public partial HotkeyBinding? BmsUhfSquelchHotkey { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(BmsVhfSquelchHotkeyDisplay))]
    public partial HotkeyBinding? BmsVhfSquelchHotkey { get; set; }

    public string BmsUhfSquelchHotkeyDisplay =>
        BmsUhfSquelchHotkey?.DisplayName ?? "None";

    public string BmsVhfSquelchHotkeyDisplay =>
        BmsVhfSquelchHotkey?.DisplayName ?? "None";


    // This is displayed in the Top Bar but shared throughout the app
    [ObservableProperty] public partial bool Is3dMode { get; set; }

    partial void OnIs3dModeChanged(bool value)
    {
        _logger.LogInformation("Game mode changed to {Mode}", value ? "In-game" : "Lobby");
        _openFreqService.Apply3dAudioEffects = value;
        _ = _openFreqService.NotifyModeAsync(value);
    }

    partial void OnConnectionModeChanged(IOpenFreqService.Mode value)
    {
        _logger.LogInformation("Connection mode changed to {Mode}", value);
        switch (value)
        {
            case IOpenFreqService.Mode.BMS:
                _falconSharedMemoryService.Start();
                _falconRadioSharedMemoryService.Start();
                _acmiClientService.DisconnectAsync().Wait(50);
                _acmiClientService.Stop();
                break;
            case IOpenFreqService.Mode.GCI:
                Is3dMode = false;
                _falconSharedMemoryService.Stop();
                _falconRadioSharedMemoryService.Stop();
                _hotkeyService.UnregisterHotkeys(IHotkeyService.HotkeyType.SquelchToggle);
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(value), value, null);
        }
    }

    partial void OnSelectedBmsTheaterChanged(TheaterDefinition? value)
    {
        if (value == null) return;
        HeightmapPath = value.HeightmapPath ?? string.Empty;
        SelectedTheater = value.Name;
    }

    private void InitializeBmsDetection()
    {
        var bmsDir = BmsDetectionService.GetBmsDirectory();
        if (bmsDir == null) return;

        List<TheaterDefinition> theaters;
        try { theaters = BmsDetectionService.GetInstalledTheaters(bmsDir); }
        catch { return; }

        if (theaters.Count == 0) return;

        BmsInstallFound = true;
        TheaterDefinitions = new ObservableCollection<TheaterDefinition>(theaters);

        foreach (var t in theaters)
        {
            TheaterCoordinateConverter.RegisterTheater(t);
            if (!AvailableTheaterNames.Contains(t.Name))
                AvailableTheaterNames.Add(t.Name);
        }

        SelectedBmsTheater ??= TheaterDefinitions[0];
    }

    public SettingsViewModel(ILogger<SettingsViewModel> logger, IAudioService audioService, IFalconRadioSharedMemoryService falconRadioSharedMemoryService,
        IFalconSharedMemoryService falconSharedMemoryService, IAcmiClientService acmiClientService,
        IOpenFreqService openFreqService, IHotkeyService hotkeyService)
    {
        _logger = logger;
        _audioService = audioService;
        _falconSharedMemoryService = falconSharedMemoryService;
        _falconRadioSharedMemoryService = falconRadioSharedMemoryService;
        _acmiClientService = acmiClientService;
        _openFreqService = openFreqService;
        _hotkeyService = hotkeyService;
        InitializeAudioDevices();
        InitializeBmsDetection();
    }

    private void InitializeAudioDevices()
    {
        _audioService.Init();

        var playbackDevices = _audioService.GetPlaybackDevices();
        PlaybackDeviceNames = new ObservableCollection<string>(playbackDevices);
        PlaybackDeviceIndex = _audioService.DefaultPlaybackDevice;
        HasPlaybackDevices = playbackDevices.Count > 0;

        var recordingDevices = _audioService.GetRecordingDevices();
        RecordingDeviceNames = new ObservableCollection<string>(recordingDevices);
        RecordingDeviceIndex = _audioService.DefaultRecordingDevice;
        HasRecordingDevices = recordingDevices.Count > 0;

        _audioService.PlaybackDevicesChanged += OnPlaybackDevicesChanged;
        _audioService.RecordingDevicesChanged += OnRecordingDevicesChanged;
    }

    partial void OnDisplayNameChanged(string value)
    {
        if (ConnectionMode == IOpenFreqService.Mode.GCI && _openFreqService.IsAuthenticated)
        {
            _ = Task.Run(async () =>
            {
                try
                {
                    await _openFreqService.UpdateDisplayNameAsync(value);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Failed to update display name to {DisplayName}", value);
                }
            });
        }
    }

    private void OnRecordingDevicesChanged(object? sender, DeviceChangedEventArgs e)
    {
        Dispatcher.UIThread.Post(() =>
        {
            _logger.LogInformation(
                "Recording devices changed: Old={Old}, New={New}, DeviceWasRemoved={Removed}",
                e.OldDeviceIndex, e.NewDeviceIndex, e.DeviceWasRemoved);

            RecordingDeviceNames.Clear();
            foreach (var device in e.Devices)
                RecordingDeviceNames.Add(device);
            HasRecordingDevices = e.Devices.Count > 0;

            // Update UI selection (may be same value — observable equality guard won't re-fire)
            RecordingDeviceIndex = e.NewDeviceIndex;

            // Always propagate the resolved BASS index directly to the service, bypassing the
            // [ObservableProperty] equality check. Without this, if the physical device changed
            // but the list index stayed the same, OnRecordingDeviceIndexChanged won't fire and
            // the service keeps a stale BASS device index.
            if (e.NewDeviceIndex >= 0)
            {
                var bassIndex = _audioService.GetRecordingBassIndex(e.NewDeviceIndex);
                if (bassIndex >= 0)
                {
                    _logger.LogInformation(
                        "Forcing recording BASS device switch to index {BassIndex} (DeviceWasRemoved={Removed})",
                        bassIndex, e.DeviceWasRemoved);
                    _openFreqService.RecordingDeviceIndex = bassIndex;
                }
                else
                {
                    _logger.LogWarning("No valid BASS recording device for list index {ListIndex}", e.NewDeviceIndex);
                }
            }
            else
            {
                _logger.LogError("Recording device change yielded no valid device (NewIndex={New})", e.NewDeviceIndex);
            }
        });
    }

    private void OnPlaybackDevicesChanged(object? sender, DeviceChangedEventArgs e)
    {
        Dispatcher.UIThread.Post(() =>
        {
            _logger.LogInformation(
                "Playback devices changed: Old={Old}, New={New}, DeviceWasRemoved={Removed}",
                e.OldDeviceIndex, e.NewDeviceIndex, e.DeviceWasRemoved);

            PlaybackDeviceNames.Clear();
            foreach (var device in e.Devices)
                PlaybackDeviceNames.Add(device);
            HasPlaybackDevices = e.Devices.Count > 0;

            // Update UI selection (may be same value — observable equality guard won't re-fire)
            PlaybackDeviceIndex = e.NewDeviceIndex;

            // Always propagate the resolved BASS index directly to the service, bypassing the
            // [ObservableProperty] equality check. Critical case: selected device is removed and
            // the fallback lands at the same list index — the observable setter is a no-op,
            // ChangeOutputDevice is never called, RadioPlayback silently plays to a dead device.
            if (e.NewDeviceIndex >= 0)
            {
                var bassIndex = _audioService.GetPlaybackBassIndex(e.NewDeviceIndex);
                if (bassIndex >= 0)
                {
                    _logger.LogInformation(
                        "Forcing playback BASS device switch to index {BassIndex} (DeviceWasRemoved={Removed})",
                        bassIndex, e.DeviceWasRemoved);
                    _openFreqService.PlaybackDeviceIndex = bassIndex;
                }
                else
                {
                    _logger.LogWarning("No valid BASS playback device for list index {ListIndex}", e.NewDeviceIndex);
                }
            }
            else
            {
                _logger.LogError("Playback device change yielded no valid device (NewIndex={New})", e.NewDeviceIndex);
            }
        });
    }

    partial void OnMasterVolumeChanged(double value) => _openFreqService.MasterVolume = value;

    partial void OnSidetoneEnabledChanged(bool value) => _openFreqService.SidetoneEnabled = value;

    partial void OnMicNormalizationEnabledChanged(bool value) => _openFreqService.MicNormalizationEnabled = value;

    partial void OnSidetoneVolumeChanged(double value) => _openFreqService.SidetoneVolume = value;

    partial void OnAmbientNoiseVolumeChanged(double value) => _openFreqService.AmbientNoiseVolume = value;

    partial void OnAutoRecordInGameModeChanged(bool value) => _openFreqService.AutoRecordInGameMode = value;

    partial void OnRecordingPathChanged(string value) => _openFreqService.RecordingPath = value;

    partial void OnStreamToDeviceChanged(bool value)
    {
        _openFreqService.Sink = value
            ? IOpenFreqService.CaptureSink.Device
            : IOpenFreqService.CaptureSink.File;
        OnPropertyChanged(nameof(RecordToFile));
    }

    partial void OnApplyOwnVoiceSfxChanged(bool value) => _openFreqService.ApplyOwnVoiceSfx = value;

    partial void OnMonitorDeviceIndexChanged(int value)
    {
        if (value < 0 || value >= PlaybackDeviceNames.Count) return;
        MonitorDeviceName = PlaybackDeviceNames[value];
        _openFreqService.MonitorDeviceIndex = _audioService.GetPlaybackBassIndex(value);
    }

    partial void OnIsDarkModeChanged(bool value)
    {
        Application.Current!.RequestedThemeVariant = value ? ThemeVariant.Dark : ThemeVariant.Light;
    }

    partial void OnRecordingDeviceIndexChanged(int value)
    {
        if (value >= 0 && value < RecordingDeviceNames.Count)
        {
            InputDeviceName = RecordingDeviceNames[value];
            _openFreqService.RecordingDeviceIndex = _audioService.GetRecordingBassIndex(value);
        }
    }

    partial void OnPlaybackDeviceIndexChanged(int value)
    {
        if (value >= 0 && value < PlaybackDeviceNames.Count)
        {
            OutputDeviceName = PlaybackDeviceNames[value];
            _openFreqService.PlaybackDeviceIndex = _audioService.GetPlaybackBassIndex(value);
        }
    }

    partial void OnBmsRadio1PanChanged(int value) { }

    partial void OnBmsRadio2PanChanged(int value) { }

    public void LoadFromSettings(OpenFreqSettings settings)
    {
        OpenFreqServerAddress = settings.OpenFreqServerAddress;
        OpenFreqPassword = settings.OpenFreqPassword;
        OpenFreqServerAddressHistory = new ObservableCollection<string>(settings.OpenFreqServerAddressHistory);
        ConnectionMode = IsWindowsPlatform ? settings.OwnPositionMode : IOpenFreqService.Mode.GCI;
        TacviewServerAddress = settings.TacviewServerAddress;
        TacviewServerPassword = settings.TacviewServerPassword;
        TacviewServerAddressHistory = new ObservableCollection<string>(settings.TacviewServerAddressHistory);
        SelectedTheater = settings.SelectedTheater;
        MapLayer = settings.MapLayer;
        BmsRadio1Pan = settings.BmsRadio1Pan;
        BmsRadio2Pan = settings.BmsRadio2Pan;
        MasterVolume = settings.MasterVolume;
        SidetoneEnabled = settings.SidetoneEnabled;
        MicNormalizationEnabled = settings.MicNormalizationEnabled;
        SidetoneVolume = settings.SidetoneVolume;
        MinimizeOnConnect = settings.MinimizeOnConnect;
        AmbientNoiseVolume = settings.AmbientNoiseVolume;
        AutoRecordInGameMode = settings.AutoRecordInGameMode;
        // Keep the computed default ("recordings" next to the exe) when no path was saved.
        if (!string.IsNullOrWhiteSpace(settings.RecordingPath))
            RecordingPath = settings.RecordingPath;
        StreamToDevice = settings.CaptureSink == IOpenFreqService.CaptureSink.Device;
        ApplyOwnVoiceSfx = settings.ApplyOwnVoiceSfx;
        if (settings.DarkMode.HasValue)
        {
            IsDarkMode = settings.DarkMode.Value;
            Application.Current!.RequestedThemeVariant = IsDarkMode ? ThemeVariant.Dark : ThemeVariant.Light;
        }
        else
        {
            // No saved preference — RequestedThemeVariant stays "Default" (follows OS).
            Dispatcher.UIThread.Post(() =>
            {
                IsDarkMode = Application.Current?.ActualThemeVariant == ThemeVariant.Dark;
            }, DispatcherPriority.Background);
        }

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

        // Monitor/stream output device (reuses the playback device list).
        MonitorDeviceName = settings.MonitorDeviceName;
        MonitorDeviceIndex = !string.IsNullOrEmpty(MonitorDeviceName)
            ? PlaybackDeviceNames.ToList().IndexOf(MonitorDeviceName)
            : _audioService.DefaultPlaybackDevice;
        if (MonitorDeviceIndex < 0)
            MonitorDeviceIndex = _audioService.DefaultPlaybackDevice;

        RestoreWindowPosition(settings);

        // Re-select BMS theater from saved heightmap path
        if (BmsInstallFound && !string.IsNullOrEmpty(HeightmapPath))
        {
            var match = TheaterDefinitions.FirstOrDefault(t =>
                string.Equals(t.HeightmapPath, HeightmapPath, StringComparison.OrdinalIgnoreCase));
            if (match != null)
            {
                SelectedBmsTheater = match;
            }
        }

        // Sync HeightmapPath from already-selected theater if path was empty (e.g. clean state)
        if (string.IsNullOrEmpty(HeightmapPath) && SelectedBmsTheater?.HeightmapPath != null)
            HeightmapPath = SelectedBmsTheater.HeightmapPath;
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
            OpenFreqServerAddressHistory = [.. OpenFreqServerAddressHistory],
            OwnPositionMode = ConnectionMode,
            TacviewServerAddress = TacviewServerAddress,
            TacviewServerPassword = TacviewServerPassword,
            TacviewServerAddressHistory = [.. TacviewServerAddressHistory],
            DisplayName = DisplayName,
            InputDeviceName = InputDeviceName,
            OutputDeviceName = OutputDeviceName,
            HeightmapPath = HeightmapPath,
            SelectedTheater = SelectedTheater,
            MapLayer = MapLayer,
            BmsRadio1Pan = BmsRadio1Pan,
            BmsRadio2Pan = BmsRadio2Pan,
            BmsSquelchUhfHotkey = BmsUhfSquelchHotkey,
            BmsSquelchVhfHotkey = BmsVhfSquelchHotkey,
            MasterVolume = MasterVolume,
            SidetoneEnabled = SidetoneEnabled,
            MicNormalizationEnabled = MicNormalizationEnabled,
            SidetoneVolume = SidetoneVolume,
            MinimizeOnConnect = MinimizeOnConnect,
            AmbientNoiseVolume = AmbientNoiseVolume,
            AutoRecordInGameMode = AutoRecordInGameMode,
            RecordingPath = RecordingPath,
            CaptureSink = StreamToDevice
                ? IOpenFreqService.CaptureSink.Device
                : IOpenFreqService.CaptureSink.File,
            MonitorDeviceName = MonitorDeviceName,
            ApplyOwnVoiceSfx = ApplyOwnVoiceSfx,
            DarkMode = IsDarkMode,
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

    private const int MaxAddressHistory = 10;

    public void AddOpenFreqServerAddressToHistory() => AddToHistory(OpenFreqServerAddressHistory, OpenFreqServerAddress);

    public void AddTacviewServerAddressToHistory() => AddToHistory(TacviewServerAddressHistory, TacviewServerAddress);

    private static void AddToHistory(ObservableCollection<string> history, string value)
    {
        value = value.Trim();
        if (value.Length == 0)
            return;

        var existing = history.FirstOrDefault(h => string.Equals(h, value, StringComparison.OrdinalIgnoreCase));
        if (existing != null)
            history.Remove(existing);

        history.Insert(0, value);
        while (history.Count > MaxAddressHistory)
            history.RemoveAt(history.Count - 1);
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

    public void Dispose()
    {
        _audioService.PlaybackDevicesChanged -= OnPlaybackDevicesChanged;
        _audioService.RecordingDevicesChanged -= OnRecordingDevicesChanged;
    }
}
