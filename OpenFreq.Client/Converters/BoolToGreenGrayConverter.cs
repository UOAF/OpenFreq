using System;
using System.Globalization;
using Avalonia.Data.Converters;
using Avalonia.Media;

namespace OpenFreqClient.Converters
{
    /// <summary>
    /// Converts a boolean value to Green (true) or Gray (false) color brush
    /// </summary>
    public class BoolToGreenGrayConverter : IValueConverter
    {
        public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        {
            if (value is bool boolValue && boolValue)
            {
                return new SolidColorBrush(Color.FromRgb(76, 175, 80)); // Material Green 500
            }

            return new SolidColorBrush(Color.FromRgb(158, 158, 158)); // Material Gray 500
        }

        public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        {
            throw new NotImplementedException("BoolToGreenGrayConverter does not support ConvertBack");
        }
    }
}
