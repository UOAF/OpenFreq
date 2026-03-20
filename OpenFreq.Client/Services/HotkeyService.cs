using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using OpenFreqClient.Services.Interfaces;
using SharpHook;
using SharpHook.Data;

namespace OpenFreqClient.Services;

public class HotkeyService : IHotkeyService
{
    private TaskPoolGlobalHook? _hook;
    private CancellationTokenSource? _cts;
    private Task? _hookTask;

    private readonly Dictionary<KeyCode, List<Guid>> _pttBindings = new();
    private readonly Dictionary<KeyCode, List<Guid>> _squelchToggleBindings = new();
    private readonly HashSet<KeyCode> _pressedKeys = [];

    public event EventHandler<HotkeyPressedEventArgs>? HotkeyPressed;
    public event EventHandler<HotkeyReleasedEventArgs>? HotkeyReleased;

    public void Start()
    {
        if (_hook != null) return;

        _cts = new CancellationTokenSource();
        _hook = new TaskPoolGlobalHook();
        _hook.KeyPressed += OnKeyPressed;
        _hook.KeyReleased += OnKeyReleased;

        // Store the task so we can properly stop it
        _hookTask = _hook.RunAsync();
    }

    public void Stop()
    {
        if (_hook == null) return;

        // Cancel the hook
        _cts?.Cancel();

        // Unsubscribe from events
        _hook.KeyPressed -= OnKeyPressed;
        _hook.KeyReleased -= OnKeyReleased;

        // Dispose the hook (this stops it)
        _hook.Dispose();
        _hook = null;

        // Wait for task to complete (with timeout)
        try
        {
            _hookTask?.Wait(TimeSpan.FromSeconds(1));
        }
        catch (AggregateException)
        {
            // Task was cancelled, this is expected
        }

        _hookTask = null;
        _cts?.Dispose();
        _cts = null;

        _pressedKeys.Clear();
    }

    public void PausePttKeys()
    { 
        PttKeysPaused = true;
    }

    public void ResumePttKeys()
    {
        PttKeysPaused = false;
    }

    public bool PttKeysPaused { get; private set; }

    public void RegisterHotkey(IHotkeyService.HotkeyType type, KeyCode key, Guid channelId)
    {
        var bindings = type switch
        {
            IHotkeyService.HotkeyType.Ptt => _pttBindings,
            IHotkeyService.HotkeyType.SquelchToggle => _squelchToggleBindings,
            _ => throw new ArgumentOutOfRangeException(nameof(type), type, null)
        };
        
        if (bindings.TryGetValue(key, out var bindingsList))
        {
            if (bindingsList.Contains(channelId))
            {
                return;
            }

            bindingsList.Add(channelId);
        }
        else
        {
            bindings[key] = [channelId];
        }
    }

    public void UnregisterHotkey(IHotkeyService.HotkeyType type, KeyCode key, Guid channelId)
    {
        var bindings = type switch
        {
            IHotkeyService.HotkeyType.Ptt => _pttBindings,
            IHotkeyService.HotkeyType.SquelchToggle => _squelchToggleBindings,
            _ => throw new ArgumentOutOfRangeException(nameof(type), type, null)
        };
        
        if (bindings.TryGetValue(key, out var bindingsList))
        {
            bindingsList.Remove(channelId);
        }
    }

    public void UnregisterHotkeys(IHotkeyService.HotkeyType type)
    {
        var bindings = type switch
        {
            IHotkeyService.HotkeyType.Ptt => _pttBindings,
            IHotkeyService.HotkeyType.SquelchToggle => _squelchToggleBindings,
            _ => throw new ArgumentOutOfRangeException(nameof(type), type, null)
        };
        bindings.Clear();
    }

    public async Task<KeyCode> CaptureNextKeyAsync(CancellationToken cancellationToken = default)
    {
        var tcs = new TaskCompletionSource<KeyCode>();

        void OnKeyCaptured(object? sender, KeyboardHookEventArgs e)
        {
            if (e.Data.KeyCode == KeyCode.VcEscape)
            {
                tcs.TrySetResult(KeyCode.VcUndefined);
            }
            tcs.TrySetResult(e.Data.KeyCode);
        }

        try
        {
            if (_hook == null)
            {
                throw new InvalidOperationException("Hotkey service is not started");
            }

            _hook.KeyPressed += OnKeyCaptured;

            // Wait for cancellation or key press
            using (cancellationToken.Register(() => tcs.TrySetCanceled()))
            {
                return await tcs.Task;
            }
        }
        finally
        {
            if (_hook != null)
            {
                _hook.KeyPressed -= OnKeyCaptured;
            }
        }
    }

    private void OnKeyPressed(object? sender, KeyboardHookEventArgs e)
    {
        if (_pressedKeys.Contains(e.Data.KeyCode))
            return; // Already pressed

        _pressedKeys.Add(e.Data.KeyCode);

        // We allow for arbitrary double binds, so just fire every valid event
        if (_pttBindings.TryGetValue(e.Data.KeyCode, out var pttBindingsList))
        {
            if (!PttKeysPaused)
                HotkeyPressed?.Invoke(this, new HotkeyPressedEventArgs(IHotkeyService.HotkeyType.Ptt, pttBindingsList));
        }
        
        if (_squelchToggleBindings.TryGetValue(e.Data.KeyCode, out var squelchBindingsList))
        {
            HotkeyPressed?.Invoke(this, new HotkeyPressedEventArgs(IHotkeyService.HotkeyType.SquelchToggle, squelchBindingsList));
        }
    }

    private void OnKeyReleased(object? sender, KeyboardHookEventArgs e)
    {
        _pressedKeys.Remove(e.Data.KeyCode);

        if (_pttBindings.TryGetValue(e.Data.KeyCode, out var pttBinding))
        {
            if (!PttKeysPaused)
             HotkeyReleased?.Invoke(this, new HotkeyReleasedEventArgs(IHotkeyService.HotkeyType.Ptt, pttBinding));
        }

        if (_squelchToggleBindings.TryGetValue(e.Data.KeyCode, out var squelchBinding))
        {
            HotkeyReleased?.Invoke(this, new HotkeyReleasedEventArgs(IHotkeyService.HotkeyType.SquelchToggle, squelchBinding));
        }
    }

    public void Dispose()
    {
        Stop();
    }
}

public class HotkeyPressedEventArgs(IHotkeyService.HotkeyType type, List<Guid> channelIds) : EventArgs
{
    public IHotkeyService.HotkeyType Type { get; } = type;
    public List<Guid> ChannelIds { get; } = channelIds;
}

public class HotkeyReleasedEventArgs(IHotkeyService.HotkeyType type, List<Guid> channelIds) : EventArgs
{
    public IHotkeyService.HotkeyType Type { get; } = type;
    public List<Guid> ChannelIds { get; } = channelIds;
}