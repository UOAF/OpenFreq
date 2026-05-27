using System;
using System.Globalization;
using Avalonia.Data.Converters;

namespace OpenFreqClient.Converters
{
    /// <summary>
    /// Converts a boolean indicating whether a hotkey is set to an appropriate tooltip string
    /// </summary>
    public class HotkeyTooltipConverter : IValueConverter
    {
        public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        {
            if (value is bool hasHotkey)
            {
                return hasHotkey
                    ? "Click to change hotkey, ESC clears it)"
                    : "Click to set a hotkey";
            }

            return "Set hotkey";
        }

        public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        {
            throw new NotImplementedException("HotkeyTooltipConverter does not support ConvertBack");
        }
    }
}
