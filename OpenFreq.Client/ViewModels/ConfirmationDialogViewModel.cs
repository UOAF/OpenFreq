using System.Windows.Input;
using CommunityToolkit.Mvvm.Input;
using DialogHostAvalonia;

namespace OpenFreqClient.ViewModels;

public sealed class ConfirmationDialogViewModel(
    string title,
    string message,
    string cancelText = "Cancel",
    string confirmText = "OK",
    bool isDestructive = false)
{
    public string Title { get; } = title;
    public string Message { get; } = message;
    public string CancelText { get; } = cancelText;
    public string ConfirmText { get; } = confirmText;
    public string ConfirmButtonClasses { get; } = isDestructive ? "accent" : string.Empty;

    public ICommand ConfirmCommand { get; } = new RelayCommand(() =>
        DialogHost.Close("MainDialogHost", true));

    public ICommand CancelCommand { get; } = new RelayCommand(() =>
        DialogHost.Close("MainDialogHost", false));
}
