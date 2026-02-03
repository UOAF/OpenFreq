using Avalonia;
using Avalonia.Controls;
using Avalonia.Data;
using OpenFreqAudio;

namespace OpenFreq.Client.Controls;

public partial class AudioChannelSelector : UserControl
{
    public static readonly StyledProperty<RadioPlayback.AudioChannel> SelectedChannelProperty =
        AvaloniaProperty.Register<AudioChannelSelector, RadioPlayback.AudioChannel>(
            nameof(SelectedChannel),
            defaultValue: RadioPlayback.AudioChannel.Both,
            defaultBindingMode: BindingMode.TwoWay);

    public RadioPlayback.AudioChannel SelectedChannel
    {
        get => GetValue(SelectedChannelProperty);
        set => SetValue(SelectedChannelProperty, value);
    }

    public AudioChannelSelector()
    {
        InitializeComponent();
    }
}