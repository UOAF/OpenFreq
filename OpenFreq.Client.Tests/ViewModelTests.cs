using OpenFreq.Client.Models;
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
