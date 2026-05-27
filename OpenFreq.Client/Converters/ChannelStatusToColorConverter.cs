using System;
using System.Globalization;
using Avalonia;
using Avalonia.Data.Converters;
using Avalonia.Media;
using OpenFreqClient.Models;

namespace OpenFreqClient.Converters;

public class ChannelStatusToColorConverter : IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is not Channel.ChannelConnectionStatus status) return new SolidColorBrush(Color.FromRgb(60, 60, 60));
        var app = Application.Current;
        if (app?.Resources == null)
            return GetFallbackBrush(status);

        return status switch
        {
            Channel.ChannelConnectionStatus.Disconnected => GetThemeColor(app, "ChannelStatusDisconnectedBrush"),
            Channel.ChannelConnectionStatus.Connected => GetThemeColor(app, "ChannelStatusConnectedBrush"),
            _ => Brushes.Gray
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

    private static IBrush GetFallbackBrush(Channel.ChannelConnectionStatus connectionStatus)
    {
        return connectionStatus switch
        {
            Channel.ChannelConnectionStatus.Disconnected => Brushes.Gray,
            Channel.ChannelConnectionStatus.Connected => Brushes.DarkGray,
            _ => Brushes.Gray
        };
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        throw new NotImplementedException();
    }
}
