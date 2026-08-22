using System;
using System.IO;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using OpenFreqClient.Models;
using OpenFreqClient.Services.Interfaces;
using OpenFreqClient.ViewModels;
using OpenFreqClient.Views.Util;

namespace OpenFreqClient.Views;

public partial class MainWindow : Window
{
    private MainWindowViewModel? _viewModel;

    // For confirmation dialog if BMS mode set and connected
    private bool _forceClose;

    public MainWindow()
    {
        InitializeComponent();
    }

    protected override async void OnLoaded(RoutedEventArgs e)
    {
        base.OnLoaded(e);
        _viewModel = DataContext as MainWindowViewModel;
        if (_viewModel == null) return;

        await _viewModel.InitializationTask;
        if (_viewModel.Settings.TelemetryConsentState == TelemetryConsentStatus.Unknown)
        {
            var consent = await TelemetryConsentDialogService.ShowAsync();
            await _viewModel.ApplyTelemetryConsentAsync(consent);
        }
    }

    private async void ExportTelemetryButton_OnClick(object? sender, RoutedEventArgs e)
    {
        if (_viewModel == null) return;
        var file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = "Export OpenFreq Diagnostics",
            SuggestedFileName = $"openfreq-diagnostics-{DateTime.Now:yyyy-MM-dd-HHmm}.zip",
            DefaultExtension = "zip",
            FileTypeChoices =
            [
                new FilePickerFileType("Zip archive") { Patterns = ["*.zip"] }
            ]
        });
        if (file == null) return;
        await _viewModel.ExportTelemetryAsync(file.Path.LocalPath);
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

    private void AddressHistoryBox_GotFocus(object? sender, FocusChangedEventArgs e)
    {
        // AutoCompleteBox only opens its dropdown while typing; open it on focus so
        // the address history is reachable by click alone.
        if (sender is AutoCompleteBox { ItemsSource: not null } box)
            box.IsDropDownOpen = true;
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
