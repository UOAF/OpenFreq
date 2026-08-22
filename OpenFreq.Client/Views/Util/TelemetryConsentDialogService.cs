using System.Threading.Tasks;
using DialogHostAvalonia;
using OpenFreqClient.Models;
using OpenFreqClient.ViewModels;

namespace OpenFreqClient.Views.Util;

public static class TelemetryConsentDialogService
{
    public static async Task<TelemetryConsentStatus> ShowAsync()
    {
        var dialog = new TelemetryConsentDialog
        {
            DataContext = new TelemetryConsentDialogViewModel()
        };
        var result = await DialogHost.Show(dialog, "MainDialogHost");
        return result is TelemetryConsentStatus status ? status : TelemetryConsentStatus.Declined;
    }
}
