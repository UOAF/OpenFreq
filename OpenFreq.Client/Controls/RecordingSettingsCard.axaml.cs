using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using OpenFreqClient.ViewModels;

namespace OpenFreq.Client.Controls;

public partial class RecordingSettingsCard : UserControl
{
    public RecordingSettingsCard()
    {
        InitializeComponent();
    }

    private async void BrowseFolder_OnClick(object? sender, RoutedEventArgs e)
    {
        var storage = TopLevel.GetTopLevel(this)?.StorageProvider;
        if (storage == null || DataContext is not MainWindowViewModel vm) return;

        var folders = await storage.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = "Choose recording output folder",
            AllowMultiple = false
        });

        if (folders.Count > 0)
            vm.Settings.RecordingPath = folders[0].Path.LocalPath;
    }
}
