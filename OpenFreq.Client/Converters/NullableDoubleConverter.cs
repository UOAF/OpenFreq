using System;
using System.Globalization;
using Avalonia.Data.Converters;

namespace OpenFreqClient.Converters;

public class NullableDoubleConverter : IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is double d)
            return d.ToString(CultureInfo.InvariantCulture);
        
        return string.Empty;
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is not string str) return null;
        if (string.IsNullOrWhiteSpace(str))
            return null;
            
        if (double.TryParse(str, NumberStyles.Any, CultureInfo.InvariantCulture, out var result))
            return result;

        return null;
    }
}