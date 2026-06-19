using System;
using System.Globalization;
using Avalonia.Data.Converters;

namespace OpenFreqClient.Converters;

/// <summary>
/// Negates a boolean. Two-way, so it can drive a TwoWay control binding
/// (e.g. a RadioButton.IsChecked) directly off the inverse of a backing flag.
/// </summary>
public class InverseBoolConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is bool b ? !b : false;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is bool b ? !b : false;
}
