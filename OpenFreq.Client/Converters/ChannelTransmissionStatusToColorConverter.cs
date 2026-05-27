using System;
using System.Globalization;
using Avalonia;
using Avalonia.Data.Converters;
using Avalonia.Media;
using OpenFreqClient.Models;

namespace OpenFreqClient.Converters;

public class ChannelTransmissionStatusToColorConverter : IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is not Channel.ChannelTransmissionStatus status) return Brushes.Transparent;
        var app = Application.Current;
        if (app?.Resources == null)
            return GetFallbackBrush(status);

        return status switch
        {
            Channel.ChannelTransmissionStatus.Receiving => GetThemeColor(app, "ChannelStatusReceivingBrush"),
            Channel.ChannelTransmissionStatus.Transmitting => GetThemeColor(app, "ChannelStatusTransmittingBrush"),
            _ => Brushes.Transparent
        };

    }

    private static IBrush? GetThemeColor(Application app, string resourceKey)
    {
        if (app.Resources.TryGetResource(resourceKey, null, out var resource))
        {
            return resource as IBrush;
        }
        return null;
    }

    private static IBrush GetFallbackBrush(Channel.ChannelTransmissionStatus status)
    {
        return status switch
        {
            Channel.ChannelTransmissionStatus.Receiving => new SolidColorBrush(Color.FromRgb(76, 175, 80)),
            Channel.ChannelTransmissionStatus.Transmitting => new SolidColorBrush(Color.FromRgb(244, 67, 54)),
            _ => new SolidColorBrush(Color.FromRgb(60, 60, 60))
        };
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        throw new NotImplementedException();
    }
}
