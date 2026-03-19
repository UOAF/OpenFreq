using System.Collections.Specialized;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
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
        _viewModel?.ChannelList.AllChannelGroups.CollectionChanged += OnGroupsChanged;
       
    }
    
    private void OnGroupsChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.NewItems != null)
        {
            Dispatcher.UIThread.Post(() =>
            {
                ChannelGroupsScrollViewer.ScrollToEnd();
            });
        }
    }

    private void AddChannelGroupButton_OnClick(object? sender, RoutedEventArgs e)
    {
        _viewModel?.ChannelList.CreateChannelGroup(
            new ChannelGroupData { Name = $"Channel Group #{_viewModel.ChannelList.AllChannelGroups.Count + 1}" }, true);
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
            _viewModel?.Settings.HeightmapPath = file[0].Path.LocalPath;
        }
    }
    
    protected override void OnClosing(WindowClosingEventArgs e)
    {
        _viewModel?.Settings.UpdateWindowSettings();
    }
}