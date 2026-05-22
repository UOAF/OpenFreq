using System;
using System.ComponentModel;
using Avalonia.Controls;
using Avalonia.Threading;
using OpenFreqClient.ViewModels;

namespace OpenFreqClient.Views;

public partial class LocationDetailView : UserControl
{
    public LocationDetailView()
    {
        InitializeComponent();
    }

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        if (DataContext is LocationViewModel vm)
            vm.PropertyChanged += OnVmPropertyChanged;
    }

    private void OnVmPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(LocationViewModel.EditMode)
            && DataContext is LocationViewModel { EditMode: true })
        {
            Dispatcher.UIThread.Post(() =>
            {
                NameInput.Focus();
                NameInput.SelectAll();
            }, DispatcherPriority.Loaded);
        }
    }
}
