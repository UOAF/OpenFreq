using System;
using System.Globalization;
using Avalonia.Data.Converters;

namespace OpenFreqClient.Converters;

public class BoolToOpacityConverter : IValueConverter
{
    public static readonly BoolToOpacityConverter Instance = new();

    public double TrueValue { get; set; } = 0.75;
    public double FalseValue { get; set; } = 0.25;

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is true ? TrueValue : FalseValue;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotImplementedException();
}
