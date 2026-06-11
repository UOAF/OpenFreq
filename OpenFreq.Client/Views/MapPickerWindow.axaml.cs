using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Mapsui.Extensions;
using OpenFreqClient.Services.Interfaces;
using OpenFreqClient.ViewModels;

namespace OpenFreqClient.Views;

public partial class MapPickerWindow : Window
{
    private readonly string _selectedTheaterName = null!;
    private readonly MapPickerViewModel _viewModel;
    public (double lat, double lon)? SelectedPosition { get; private set; }

    public MapPickerWindow()
    {
        InitializeComponent();
        _viewModel = null!; // Will be set in other constructors
    }

    /// <summary>
    /// Constructor for position picking mode
    /// </summary>
    public MapPickerWindow(double initialLat, double initialLon, SettingsViewModel settings, IOpenFreqService? openFreqService = null) : this()
    {
        _selectedTheaterName = settings.SelectedTheater;
        _viewModel = new MapPickerViewModel(initialLat, initialLon, settings, openFreqService);
        _viewModel.PositionConfirmed += OnPositionConfirmed;
        DataContext = _viewModel;
        MapControl.PointerPressed += OnMapPointerPressed;
        Closed += (_, _) =>
        {
            _viewModel.PositionConfirmed -= OnPositionConfirmed;
            MapControl.PointerPressed -= OnMapPointerPressed;
        };
    }

    /// <summary>
    /// Constructor for aircraft tracking mode
    /// </summary>
    public MapPickerWindow(double initialLat, double initialLon, double initialHeading, SettingsViewModel settings, string? callsign = null) : this()
    {
        _selectedTheaterName = settings.SelectedTheater;
        _viewModel = new MapPickerViewModel(initialLat, initialLon, initialHeading, settings, callsign);
        DataContext = _viewModel;

        // Update window title for tracking mode
        Title = string.IsNullOrEmpty(callsign) ? "Aircraft Tracking" : $"Tracking: {callsign}";
    }

    /// <summary>
    /// Updates the tracked aircraft position (tracking mode only)
    /// </summary>
    public void UpdateTrackedPosition(double lat, double lon, double heading, double altitudeFt, double speedKts, double speedMach, string? callsign = null)
    {
        _viewModel.UpdateTrackedPosition(lat, lon, heading, altitudeFt, speedKts, speedMach, callsign);
        if (callsign != null)
            Title = $"Tracking: {callsign}";
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
