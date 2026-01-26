using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Mapsui.Extensions;
using OpenFreqClient.ViewModels;

namespace OpenFreqClient.Views;

public partial class MapPickerWindow : Window
{
    private readonly string _selectedTheaterName;
    public (double lat, double lon)? SelectedPosition { get; private set; }
    
    public MapPickerWindow()
    {
        InitializeComponent();
    }
    
    public MapPickerWindow(double initialLat, double initialLon, string selectedTheaterName) : this()
    {
        _selectedTheaterName  = selectedTheaterName;
        var viewModel = new MapPickerViewModel(initialLat, initialLon, selectedTheaterName);
        viewModel.PositionConfirmed += OnPositionConfirmed;
        DataContext = viewModel;
        MapControl.PointerPressed += OnMapPointerPressed;
    }
    
    private void OnMapPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (!e.Properties.IsRightButtonPressed) return;
        if (DataContext is not MapPickerViewModel vm) return;
        if (MapControl.Map?.Navigator?.Viewport == null) return;
        
        // Get pointer position relative to map control
        var screenPosition = e.GetPosition(MapControl);
        
        // Convert screen position to world position
        var worldPosition = MapControl.Map.Navigator.Viewport.ScreenToWorld(
            screenPosition.X, 
            screenPosition.Y);
        
        vm.OnMapClicked(worldPosition);
    }
    
    private void OnPositionConfirmed(object? sender, (double lat, double lon) position)
    {
        SelectedPosition = position;
        Close(position);
    }
    
    private void OnCancel(object? sender, RoutedEventArgs e)
    {
        Close(null);
    }
}