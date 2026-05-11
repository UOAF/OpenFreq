using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Text.Json.Serialization;
using SharpHook.Data;

namespace OpenFreq.Client.Models;

/// <summary>
/// Base class for all hotkey bindings (keyboard or joystick)
/// </summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "$type")]
[JsonDerivedType(typeof(KeyboardBinding), typeDiscriminator: "keyboard")]
#if WINDOWS
[JsonDerivedType(typeof(JoystickButtonBinding), typeDiscriminator: "joystick")]
#endif
public abstract class HotkeyBinding : IEquatable<HotkeyBinding>
{
    /// <summary>
    /// Human-readable display name for the binding
    /// </summary>
    [JsonIgnore] 
    public abstract string DisplayName { get; }

    /// <summary>
    /// Unique identifier for serialization and comparison
    /// </summary>
    [JsonIgnore]
    public abstract string SerializationKey { get; }

    public abstract bool Equals(HotkeyBinding? other);
    public abstract override int GetHashCode();
    public abstract override bool Equals(object? obj);

    public override string ToString() => DisplayName;
}

/// <summary>
/// Keyboard key binding using SharpHook KeyCode with optional modifier keys
/// </summary>
public class KeyboardBinding : HotkeyBinding
{
    public KeyboardBinding()
    {
    }

    public KeyboardBinding(KeyCode keyCode, bool shift = false, bool ctrl = false, bool alt = false)
    {
        KeyCode = keyCode;
        ShiftModifier = shift;
        CtrlModifier = ctrl;
        AltModifier = alt;
    }

    [JsonConverter(typeof(JsonStringEnumConverter<KeyCode>))]
    public KeyCode KeyCode { get; set; }

    public bool ShiftModifier { get; set; }
    public bool CtrlModifier { get; set; }
    public bool AltModifier { get; set; }

    [JsonIgnore]
    public override string DisplayName
    {
        get
        {
            var key = KeyCode.ToString().Replace("Vc", string.Empty);
            var parts = new List<string>();
            if (CtrlModifier) parts.Add("Ctrl");
            if (AltModifier) parts.Add("Alt");
            if (ShiftModifier) parts.Add("Shift");
            parts.Add(key);
            return $"Keyboard: {string.Join("+", parts)}";
        }
    }

    [JsonIgnore]
    public override string SerializationKey =>
        $"kb:{(CtrlModifier ? "C" : "")}{(AltModifier ? "A" : "")}{(ShiftModifier ? "S" : "")}{KeyCode}";

    public override bool Equals(HotkeyBinding? other)
    {
        return other is KeyboardBinding kb
               && kb.KeyCode == KeyCode
               && kb.ShiftModifier == ShiftModifier
               && kb.CtrlModifier == CtrlModifier
               && kb.AltModifier == AltModifier;
    }

    [SuppressMessage("ReSharper", "NonReadonlyMemberInGetHashCode")]
    // We really need the setter for the KeyCode, otherwise it won't deserialize properly
    public override int GetHashCode()
    {
        return HashCode.Combine("keyboard", KeyCode, ShiftModifier, CtrlModifier, AltModifier);
    }

    public override bool Equals(object? obj)
    {
        return Equals(obj as HotkeyBinding);
    }

    // Implicit conversion from KeyCode for backward compatibility
    public static implicit operator KeyboardBinding(KeyCode keyCode) => new(keyCode);
}

#if WINDOWS
/// <summary>
/// Joystick button binding using DirectInput device GUID and button index
/// </summary>
public class JoystickButtonBinding : HotkeyBinding
{
    public JoystickButtonBinding()
    {
    }

    public Guid DeviceInstanceGuid { get; set; }
    public string? DeviceName { get; set; }
    public int ButtonIndex { get; set; }

    public JoystickButtonBinding(Guid deviceInstanceGuid, string deviceName, int buttonIndex)
    {
        DeviceInstanceGuid = deviceInstanceGuid;
        DeviceName = deviceName;
        ButtonIndex = buttonIndex;
    }

    [JsonIgnore]
    public override string DisplayName => $"{DeviceName} - Button {ButtonIndex + 1}";

    [JsonIgnore]
    public override string SerializationKey => $"js:{DeviceInstanceGuid}:{ButtonIndex}";

    public override bool Equals(HotkeyBinding? other)
    {
        return other is JoystickButtonBinding jb
               && jb.DeviceInstanceGuid == DeviceInstanceGuid
               && jb.ButtonIndex == ButtonIndex;
    }

    [SuppressMessage("ReSharper", "NonReadonlyMemberInGetHashCode")]
    public override int GetHashCode()
    {
        return HashCode.Combine("joystick", DeviceInstanceGuid, ButtonIndex);
    }

    public override bool Equals(object? obj)
    {
        return Equals(obj as HotkeyBinding);
    }
}

/// <summary>
/// Information about an available joystick device
/// </summary>
public class JoystickDeviceInfo
{
    public Guid InstanceGuid { get; set; }
    public string DeviceName { get; set; } = string.Empty;
    public int ButtonCount { get; set; }
    public string ProductName { get; set; } = string.Empty;

    public override string ToString() => $"{DeviceName} ({ButtonCount} buttons)";
}
#endif

/// <summary>
/// Custom equality comparer for HotkeyBinding to use in dictionaries
/// </summary>
public class HotkeyBindingComparer : IEqualityComparer<HotkeyBinding>
{
    public bool Equals(HotkeyBinding? x, HotkeyBinding? y)
    {
        if (ReferenceEquals(x, y)) return true;
        if (x is null || y is null) return false;
        return x.Equals(y);
    }

    public int GetHashCode(HotkeyBinding obj)
    {
        return obj.GetHashCode();
    }
}