using System.Windows.Input;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DialogHostAvalonia;
using OpenFreqClient.Models;

namespace OpenFreqClient.ViewModels;

public partial class TelemetryConsentDialogViewModel : ViewModelBase
{
    [ObservableProperty] public partial bool DetailsVisible { get; set; }

    public string Summary { get; } =
        "Help improve OpenFreq by sending diagnostic telemetry after sessions. This includes your " +
        "in-game callsign, simulated aircraft position, aircraft type, radio state, connection quality, " +
        "terrain and signal calculations, device health, and application errors.";

    public string Details { get; } =
        "OpenFreq never includes voice audio, recordings, passwords, your real name, IP addresses, or " +
        "computer account information. Diagnostic records are stored locally first, uploaded in the " +
        "background, and retained by UOAF for 30 days. You can disable telemetry, export it, or delete " +
        "the local queue from Settings at any time.";

    public ICommand AllowCommand { get; } = new RelayCommand(() =>
        DialogHost.Close("MainDialogHost", TelemetryConsentStatus.Granted));

    public ICommand DeclineCommand { get; } = new RelayCommand(() =>
        DialogHost.Close("MainDialogHost", TelemetryConsentStatus.Declined));

    [RelayCommand]
    private void ToggleDetails() => DetailsVisible = !DetailsVisible;
}
