using System;
using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace OpenFreqClient.ViewModels;

public partial class ChannelFrequencyPeerViewModel: ViewModelBase
{
    [ObservableProperty][NotifyPropertyChangedFor(nameof(FrequencyMhzString))] public partial int FrequencyKhz { get; set; }
    [ObservableProperty] public partial ObservableCollection<ChannelPeerViewModel> Peers { get; set; }

    public IRelayCommand JoinCommand { get; }

    public ChannelFrequencyPeerViewModel(int frequencyKhz, ObservableCollection<ChannelPeerViewModel> peers, Action<int> joinFrequency)
    {
        FrequencyKhz = frequencyKhz;
        Peers = peers;
        JoinCommand = new RelayCommand(() => joinFrequency(frequencyKhz));
    }
    
    public string FrequencyMhzString
    {
        get => (FrequencyKhz / 1000d).ToString("F3");
        set
        {
            if (double.TryParse(value, out var mhz))
            {
                FrequencyKhz = (int)(mhz * 1000d);
            }
        }
    }
    
    
}