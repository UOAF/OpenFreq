using System.Linq;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Material.Styles.Controls;
using OpenFreqAudio;
using OpenFreqClient.Models;
using OpenFreqClient.ViewModels;

namespace OpenFreqClient.Views;

public partial class MainWindow : Window
{
    private MainWindowViewModel? _viewModel;

    public MainWindow()
    {
        InitializeComponent();
    }

    protected override void OnLoaded(RoutedEventArgs e)
    {
        base.OnLoaded(e);
        _viewModel = DataContext as MainWindowViewModel;
    }

    private void Button_OnClick(object? sender, RoutedEventArgs e)
    {
        var channelGroup = _viewModel?.ChannelList.ChannelGroups.FirstOrDefault();
        if (channelGroup == null)
        {
            channelGroup =
                _viewModel?.ChannelList.CreateChannelGroup(new ChannelGroupData { Name = "Default Group" }, true);
        }

        channelGroup?.CreateChannel(225000, $"Channel #{channelGroup.Channels.Count + 1}", Channel.ChannelType.UHF);
    }

    private void MenuButton_OnClick(object? sender, RoutedEventArgs e)
    {
        var drawer = this.FindControl<NavigationDrawer>("LeftDrawer");
        if (drawer != null)
        {
            drawer.LeftDrawerOpened = !drawer.LeftDrawerOpened;
        }
    }

    private async void HeightmapButton_OnClick(object? sender, RoutedEventArgs e)
    {
        var storage = StorageProvider;
        var filepickerOptions = new FilePickerOpenOptions
        {
            Title = "Open Heightmap File",
            AllowMultiple = false,
            FileTypeFilter = new[]
            {
                new FilePickerFileType("BMS NT HeightMap") { Patterns = new[] { "HeightMap.raw" } }
            }
        };

        var file = await storage.OpenFilePickerAsync(filepickerOptions);
        if (file.Count > 0)
        {
            _viewModel.Settings.HeightmapPath = file[0].Path.LocalPath;
        }
    }
}