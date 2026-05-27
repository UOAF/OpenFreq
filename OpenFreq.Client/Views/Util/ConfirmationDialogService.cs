using System.Threading.Tasks;
using DialogHostAvalonia;
using OpenFreqClient.ViewModels;

namespace OpenFreqClient.Views.Util;

public static class ConfirmationDialogService
{
    public static async Task<bool> ShowAsync(
        string title,
        string message,
        string cancelText = "Cancel",
        string confirmText = "OK")
    {
        var dialog = new ConfirmationDialog
        {
            DataContext = new ConfirmationDialogViewModel(
                title,
                message,
                cancelText,
                confirmText)
        };

        var result = await DialogHost.Show(dialog, "MainDialogHost");
        return result is true;
    }
}
