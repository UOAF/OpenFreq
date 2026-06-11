using OpenFreq.Client.Models;
using OpenFreqClient.Models;
using OpenFreqClient.Services.Interfaces;
using OpenFreqClient.ViewModels;

namespace OpenFreq.Client.Tests;

/// <summary>
/// Logic-level tests for the coordinator ViewModels, constructed with mocked services
/// </summary>
public class MapPickerViewModelTests
{
    [Fact]
    public void PickConstructor_SetsCoordinates_NotTracking()
    {
        SettingsViewModel settings = VmFactory.Settings();
        settings.SelectedTheater = "Korea KTO";
        var vm = new MapPickerViewModel(initialLat: 37.5, initialLon: 127.0, settings);

        Assert.Equal(37.5, vm.Latitude);
        Assert.Equal(127.0, vm.Longitude);
        Assert.False(vm.IsTrackingMode);
    }

    [Fact]
    public void TrackingConstructor_SetsHeadingAndCallsign_Tracking()
    {
        SettingsViewModel settings = VmFactory.Settings();
        settings.SelectedTheater = "Korea KTO";

        var vm = new MapPickerViewModel(
            initialLat: 1, initialLon: 2, initialHeading: 90, settings, callsign: "Viper1");

        Assert.Equal(90, vm.Heading);
        Assert.Equal("Viper1", vm.TrackedCallsign);
        Assert.True(vm.IsTrackingMode);
    }
}

public class SettingsViewModelTests
{
    [Fact]
    public void Constructs_WithMockedServices()
    {
        var vm = VmFactory.Settings();
        Assert.NotNull(vm);
    }

    [Fact]
    public void IsReadyToConnect_FalseWhenServerAddressEmpty()
    {
        var vm = VmFactory.Settings();
        vm.OpenFreqServerAddress = string.Empty;

        Assert.False(vm.IsReadyToConnect);
    }

    [Fact]
    public void IsReadyToConnect_TrueInBmsModeWithAddress()
    {
        var vm = VmFactory.Settings();
        vm.ModeIsGci = false; // BMS mode: heightmap not required
        vm.OpenFreqServerAddress = "127.0.0.1";

        Assert.True(vm.IsReadyToConnect);
    }

    [Fact]
    public void IsReadyToConnect_GciRequiresHeightmap()
    {
        var vm = VmFactory.Settings();
        vm.ModeIsGci = true;
        vm.OpenFreqServerAddress = "127.0.0.1";
        vm.HeightmapPath = string.Empty;

        Assert.False(vm.IsReadyToConnect);
    }

    [Fact]
    public void AddOpenFreqServerAddressToHistory_AddsEntry()
    {
        var vm = VmFactory.Settings();
        vm.OpenFreqServerAddress = "10.0.0.1:9000";

        vm.AddOpenFreqServerAddressToHistory();

        Assert.Equal(["10.0.0.1:9000"], vm.OpenFreqServerAddressHistory);
    }

    [Fact]
    public void AddTacviewServerAddressToHistory_AddsEntry()
    {
        var vm = VmFactory.Settings();
        vm.TacviewServerAddress = "10.0.0.2:42674";

        vm.AddTacviewServerAddressToHistory();

        Assert.Equal(["10.0.0.2:42674"], vm.TacviewServerAddressHistory);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void AddToHistory_SkipsEmptyAndWhitespace(string address)
    {
        var vm = VmFactory.Settings();
        vm.OpenFreqServerAddress = address;

        vm.AddOpenFreqServerAddressToHistory();

        Assert.Empty(vm.OpenFreqServerAddressHistory);
    }

    [Fact]
    public void AddToHistory_TrimsValue()
    {
        var vm = VmFactory.Settings();
        vm.OpenFreqServerAddress = "  10.0.0.1:9000  ";

        vm.AddOpenFreqServerAddressToHistory();

        Assert.Equal(["10.0.0.1:9000"], vm.OpenFreqServerAddressHistory);
    }

    [Fact]
    public void AddToHistory_MovesDuplicateToTop()
    {
        var vm = VmFactory.Settings();
        vm.OpenFreqServerAddress = "server-a:9000";
        vm.AddOpenFreqServerAddressToHistory();
        vm.OpenFreqServerAddress = "server-b:9000";
        vm.AddOpenFreqServerAddressToHistory();

        // Case-insensitive duplicate moves to the top instead of being added twice.
        vm.OpenFreqServerAddress = "SERVER-A:9000";
        vm.AddOpenFreqServerAddressToHistory();

        Assert.Equal(["SERVER-A:9000", "server-b:9000"], vm.OpenFreqServerAddressHistory);
    }

    [Fact]
    public void AddToHistory_CapsAtMax()
    {
        var vm = VmFactory.Settings();
        for (var i = 0; i < 11; i++)
        {
            vm.OpenFreqServerAddress = $"server-{i}:9000";
            vm.AddOpenFreqServerAddressToHistory();
        }

        Assert.Equal(10, vm.OpenFreqServerAddressHistory.Count);
        Assert.Equal("server-10:9000", vm.OpenFreqServerAddressHistory[0]);
        Assert.DoesNotContain("server-0:9000", vm.OpenFreqServerAddressHistory);
    }

    [Fact]
    public void History_RoundTripsThroughSettings()
    {
        var vm = VmFactory.Settings();
        vm.OpenFreqServerAddressHistory = ["server-b:9000", "server-a:9000"];
        vm.TacviewServerAddressHistory = ["tacview-host:42674"];

        var settings = vm.GetSettings();
        // A saved DarkMode or window placement makes LoadFromSettings touch Application.Current /
        // the main window, which don't exist here.
        settings.DarkMode = null;
        settings.WindowState = null;

        var fresh = VmFactory.Settings();
        fresh.LoadFromSettings(settings);

        Assert.Equal(["server-b:9000", "server-a:9000"], fresh.OpenFreqServerAddressHistory);
        Assert.Equal(["tacview-host:42674"], fresh.TacviewServerAddressHistory);
    }

    [Fact]
    public void WindowPlacement_SurvivesSaveWithoutUpdateWindowSettings()
    {
        // Closing while minimized skips the size/position capture in UpdateWindowSettings;
        // the values loaded at startup must survive the save instead of becoming nulls/zeros.
        var vm = VmFactory.Settings();
        vm.LoadFromSettings(new OpenFreqSettings
        {
            Left = 100,
            Top = 200,
            Width = 800,
            Height = 500
        });

        var saved = vm.GetSettings();

        Assert.Equal(100, saved.Left);
        Assert.Equal(200, saved.Top);
        Assert.Equal(800, saved.Width);
        Assert.Equal(500, saved.Height);
    }
}

public class LocationViewModelTests
{
    [Fact]
    public void IsBmsLocation_ReflectsRadioStationType()
    {
        Assert.True(VmFactory.Location(RadioStationData.RadioStationType.BMS).IsBmsLocation);
        Assert.False(VmFactory.Location(RadioStationData.RadioStationType.STATIONARY).IsBmsLocation);
    }

    [Fact]
    public void ToggleEditing_FlipsEditMode()
    {
        var vm = VmFactory.Location();
        var before = vm.EditMode;

        vm.ToggleEditing();

        Assert.Equal(!before, vm.EditMode);
    }
}

public class ChannelCardListViewModelTests
{
    [Fact]
    public void Constructs_AndExposesSettings()
    {
        var vm = VmFactory.ChannelCardList();

        Assert.NotNull(vm.Settings);
        Assert.True(vm.IsLocationPanelExpanded); // default
    }

    [Fact]
    public void ToggleLocationPanelCommand_FlipsExpandedState()
    {
        var vm = VmFactory.ChannelCardList();
        var before = vm.IsLocationPanelExpanded;

        vm.ToggleLocationPanelCommand.Execute(null);

        Assert.Equal(!before, vm.IsLocationPanelExpanded);
    }
}
