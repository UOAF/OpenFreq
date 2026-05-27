using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using OpenFreq.Client.Models;
using OpenFreq.Client.Services.Interfaces;

namespace OpenFreqClient.Services.Interfaces;

/// <summary>
/// Service for managing global hotkey bindings and events
/// Supports both keyboard (cross-platform) and joystick (Windows-only) bindings
/// </summary>
public interface IHotkeyService : IDisposable, ILifecycleService
{
    // Events for hotkey press/release
    event EventHandler<HotkeyPressedEventArgs>? HotkeyPressed;
    event EventHandler<HotkeyReleasedEventArgs>? HotkeyReleased;

    void PausePttKeys();
    void ResumePttKeys();

    bool PttKeysPaused { get; }

    void RegisterHotkey(HotkeyType type, HotkeyBinding binding, Guid channelId);
    void UnregisterHotkey(HotkeyType type, HotkeyBinding binding, Guid channelId);
    void UnregisterHotkeys(HotkeyType type);

    Task<HotkeyBinding?> CaptureNextHotkeyAsync(CancellationToken cancellationToken = default);


#if WINDOWS
    // Joystick-specific methods (Windows only)
    List<JoystickDeviceInfo> GetAvailableJoysticks();
    bool IsJoystickConnected(Guid deviceInstanceGuid);
#endif

    public enum HotkeyType
    {
        Ptt, // used for PTT
        SquelchToggle // toggle squelch on/off
    }
}
