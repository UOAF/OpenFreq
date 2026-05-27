using System;
using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace OpenFreqClient.ViewModels;

public partial class ChannelFrequencyPeerViewModel : ViewModelBase
{
    [ObservableProperty][NotifyPropertyChangedFor(nameof(FrequencyMhzString))] public partial int FrequencyKhz { get; set; }
    [ObservableProperty] public partial ObservableCollection<ChannelPeerViewModel> Peers { get; set; }
    [ObservableProperty] public partial bool CanJoin { get; set; }

    public bool Is3dFrequency { get; }
    public IRelayCommand JoinCommand { get; }

    public ChannelFrequencyPeerViewModel(int frequencyKhz, ObservableCollection<ChannelPeerViewModel> peers, Action<int> joinFrequency, bool is3dFrequency, bool canJoin)
    {
        FrequencyKhz = frequencyKhz;
        Peers = peers;
        Is3dFrequency = is3dFrequency;
        CanJoin = canJoin;
        JoinCommand = new RelayCommand(() => joinFrequency(frequencyKhz));
    }

    public string FrequencyMhzString
    {
        get => (FrequencyKhz / 1000d).ToString("F3", CultureInfo.InvariantCulture);
        set
        {
            if (double.TryParse(value, NumberStyles.Any, CultureInfo.InvariantCulture, out var mhz))
            {
                FrequencyKhz = (int)(mhz * 1000d);
            }
        }
    }


}
