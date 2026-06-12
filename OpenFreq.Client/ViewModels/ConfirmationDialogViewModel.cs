using System.Windows.Input;
using CommunityToolkit.Mvvm.Input;
using DialogHostAvalonia;

namespace OpenFreqClient.ViewModels;

public sealed class ConfirmationDialogViewModel(
    string title,
    string message,
    string cancelText = "Cancel",
    string confirmText = "OK",
    bool showCancel = true)
{
    public string Title { get; } = title;
    public string Message { get; } = message;
    public string CancelText { get; } = cancelText;
    public string ConfirmText { get; } = confirmText;
    public bool ShowCancel { get; } = showCancel;

    public ICommand ConfirmCommand { get; } = new RelayCommand(() =>
        DialogHost.Close("MainDialogHost", true));

    public ICommand CancelCommand { get; } = new RelayCommand(() =>
        DialogHost.Close("MainDialogHost", false));
}
