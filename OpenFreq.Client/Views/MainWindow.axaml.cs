using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using OpenFreqClient.Services.Interfaces;
using OpenFreqClient.ViewModels;
using OpenFreqClient.Views.Util;

namespace OpenFreqClient.Views;

public partial class MainWindow : Window
{
    private MainWindowViewModel? _viewModel;
    
    // For confirmation dialog if BMS mode set and connected
    private bool _forceClose ;

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
            FileTypeFilter =
            [
                new FilePickerFileType("BMS NT HeightMap") { Patterns = new[] { "HeightMap.raw" } }
            ]
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

        if (!_forceClose &&
            _viewModel?.Settings.ConnectionMode == IOpenFreqService.Mode.BMS &&
            _viewModel.OpenFreqConnected)
        {
            e.Cancel = true;
            _ = ConfirmExitAsync();
        }
    }

    private async Task ConfirmExitAsync()
    {
        var confirmed = await ConfirmationDialogService.ShowAsync(
            "Exit OpenFreq",
            "Connected in BMS mode. Exit anyway?",
            "Cancel",
            "Exit");

        if (confirmed)
        {
            _forceClose = true;
            Close();
        }
    }
}