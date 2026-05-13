using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
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