using System;
using System.Threading;
using System.Threading.Tasks;
using OpenFreq.Client.Services.Interfaces;
using SharpHook.Data;

namespace OpenFreqClient.Services.Interfaces;

/// <summary>
/// Service for managing global hotkey bindings and events
/// </summary>
public interface IHotkeyService : IDisposable, ILifecycleService
{
    // Events for hotkey press/release
    event EventHandler<HotkeyPressedEventArgs>? HotkeyPressed;
    event EventHandler<HotkeyReleasedEventArgs>? HotkeyReleased;
    
    void PausePttKeys();
    void ResumePttKeys();
    
    bool PttKeysPaused { get; }
    
    // Binding management
    void RegisterHotkey(HotkeyType type, KeyCode key, Guid channelId);
    void UnregisterHotkey(HotkeyType type, KeyCode key, Guid channelId);
    void UnregisterHotkeys(HotkeyType type);
    
    // Capture
    Task<KeyCode> CaptureNextKeyAsync(CancellationToken cancellationToken = default);
    
    public enum HotkeyType
    {
        Ptt, // used for PTT
        SquelchToggle // toggle squelch on/off
    }
}