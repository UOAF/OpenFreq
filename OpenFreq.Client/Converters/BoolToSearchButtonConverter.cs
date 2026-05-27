using System;
using System.Globalization;
using Avalonia.Data.Converters;

namespace OpenFreqClient.Converters;

public class BoolToSearchButtonTextConverter : IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        return value is true ? "Searching..." : "Search";
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        throw new NotImplementedException();
    }
}
