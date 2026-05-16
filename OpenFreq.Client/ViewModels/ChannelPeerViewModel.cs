using System.Collections.Generic;
using Avalonia;
using Avalonia.Media;
using CommunityToolkit.Mvvm.ComponentModel;

namespace OpenFreqClient.ViewModels;

public partial class ChannelPeerViewModel : ViewModelBase
{
    public ChannelPeerViewModel(string id, string name, bool isTransmitting, bool isOwnUser)
    {
        Id = id;
        Name = name;
        IsTransmitting = isTransmitting;
        IsOwnUser = isOwnUser;
    }

    [ObservableProperty] public partial string Id { get; set; }
    [ObservableProperty] public partial string Name { get; set; }
    
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(TransmitBorderColor), nameof(TransmitBorderThickness),
        nameof(TransmitHighlightColor))]
    public partial bool IsTransmitting { get; set; } = false;
    
    public bool IsOwnUser { get; set; }
    
    public Color TransmitHighlightColor => IsTransmitting
        ? Color.FromArgb(20, 255, 193, 7)
        : Colors.Transparent;
    
    public Color TransmitBorderColor => Color.FromRgb(255, 193, 7);
    
    public Thickness TransmitBorderThickness => IsTransmitting
        ? new Thickness(2, 0, 0, 0) // Left border
        : new Thickness(0);
    
    public FontWeight PeerFontWeight => IsOwnUser ? FontWeight.Bold : FontWeight.Normal;

    private sealed class IdEqualityComparer : IEqualityComparer<ChannelPeerViewModel>
    {
        public bool Equals(ChannelPeerViewModel? x, ChannelPeerViewModel? y)
        {
            if (ReferenceEquals(x, y)) return true;
            if (x is null) return false;
            if (y is null) return false;
            if (x.GetType() != y.GetType()) return false;
            return x.Id == y.Id;
        }

        public int GetHashCode(ChannelPeerViewModel obj)
        {
            return obj.Id.GetHashCode();
        }
    }
    
    protected bool Equals(ChannelPeerViewModel other)
    {
        return Id == other.Id;
    }

    public override bool Equals(object? obj)
    {
        if (obj is null) return false;
        if (ReferenceEquals(this, obj)) return true;
        if (obj.GetType() != GetType()) return false;
        return Equals((ChannelPeerViewModel)obj);
    }

    public override int GetHashCode()
    {
        return Id.GetHashCode();
    }
}