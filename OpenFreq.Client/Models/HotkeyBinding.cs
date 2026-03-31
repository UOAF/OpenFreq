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
/// Keyboard key binding using SharpHook KeyCode
/// </summary>
public class KeyboardBinding : HotkeyBinding
{
    public KeyboardBinding()
    {
    }

    public KeyboardBinding(KeyCode keyCode)
    {
        KeyCode = keyCode;
    }

    [JsonConverter(typeof(JsonStringEnumConverter<KeyCode>))]
    public KeyCode KeyCode { get; set; }

    [JsonIgnore] 
    public override string DisplayName => new string($"Keyboard: {KeyCode}").Replace("Vc", string.Empty);

    [JsonIgnore]
    public override string SerializationKey => $"kb:{KeyCode}";

    public override bool Equals(HotkeyBinding? other)
    {
        return other is KeyboardBinding kb && kb.KeyCode == KeyCode;
    }

    [SuppressMessage("ReSharper", "NonReadonlyMemberInGetHashCode")]
    // We really need the setter for the KeyCode, otherwise it won't deserialize properly
    public override int GetHashCode()
    {
         
        return HashCode.Combine("keyboard", KeyCode);
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