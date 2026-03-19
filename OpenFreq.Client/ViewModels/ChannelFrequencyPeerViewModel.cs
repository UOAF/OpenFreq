using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;

namespace OpenFreqClient.ViewModels;

public partial class ChannelFrequencyPeerViewModel: ViewModelBase
{
    [ObservableProperty][NotifyPropertyChangedFor(nameof(FrequencyMhzString))] public partial int FrequencyKhz { get; set; }
    [ObservableProperty] public partial ObservableCollection<ChannelPeerViewModel> Peers { get; set; }
    
    public ChannelFrequencyPeerViewModel(int frequencyKhz, ObservableCollection<ChannelPeerViewModel> peers)
    {
        FrequencyKhz = frequencyKhz;
        Peers = peers;
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