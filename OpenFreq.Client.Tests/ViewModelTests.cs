using FalconBmsDataService.Models;
using FalconBmsDataService.Services;
using FalconRadioService.Models;
using FalconRadioService.Services;
using Microsoft.Extensions.Logging;
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

    [Fact]
    public void SavingAnEditedCard_JoinsWithItsOwnLocation()
    {
        var (openFreq, other, owner) = TwoLocations();
        var card = CardIn(owner, isInEditMode: true);

        card.FrequencyKhz = 251_000;
        card.ToggleEditing();

        openFreq.Received(1).JoinFrequencyAsync(251_000, card.Id, owner.RadioStationData);
        openFreq.DidNotReceive().JoinFrequencyAsync(Arg.Any<int>(), Arg.Any<Guid>(), other.RadioStationData);
    }

    [Fact]
    public void JoiningACard_JoinsOnce()
    {
        var (openFreq, _, owner) = TwoLocations();
        var card = CardIn(owner);

        card.Join();

        openFreq.Received(1).JoinFrequencyAsync(Arg.Any<int>(), Arg.Any<Guid>(), Arg.Any<RadioStationData>());
    }

    private static (IOpenFreqService OpenFreq, LocationViewModel Other, LocationViewModel Owner) TwoLocations()
    {
        var openFreq = VmFactory.OpenFreq();
        openFreq.IsAuthenticated.Returns(true);
        return (openFreq, VmFactory.Location(openFreq: openFreq), VmFactory.Location(openFreq: openFreq));
    }

    private static ChannelCardViewModel CardIn(LocationViewModel location, bool isInEditMode = false)
        => new(VmFactory.Hotkey(), "Ch1", 225_000, isInEditMode, location.RadioStationData, location,
            location.Settings);
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

    private static readonly TimeSpan WaitLimit = TimeSpan.FromSeconds(5);

    [Fact]
    public async Task BmsPttRelease_WaitsForTheStartToComplete_WithoutBlockingThePollingLoop()
    {
        var bms = new FlyingBmsRadio1();
        var startCalled = new TaskCompletionSource();
        var start = new TaskCompletionSource();
        bms.OpenFreq.StartTransmissionAsync(bms.Radio1.FrequencyKhz, bms.Radio1.Id)
            .Returns(_ =>
            {
                startCalled.TrySetResult();
                return start.Task;
            });
        var stopCalled = new TaskCompletionSource();
        bms.OpenFreq.StopTransmissionAsync(bms.Radio1.Id)
            .Returns(_ =>
            {
                stopCalled.TrySetResult();
                return Task.CompletedTask;
            });

        // Raised on another thread, so a handler that waits for the start fails the test instead of hanging it.
        await Task.Run(() =>
        {
            bms.Press();
            bms.Release();
        }).WaitAsync(WaitLimit);

        await startCalled.Task.WaitAsync(WaitLimit);
        await Task.Delay(100);
        Assert.False(stopCalled.Task.IsCompleted);

        start.SetResult();
        await stopCalled.Task.WaitAsync(WaitLimit);
    }

    [Fact]
    public async Task BmsPtt_FailedStart_IsLogged_AndTheReleaseStillRuns()
    {
        var bms = new FlyingBmsRadio1();
        bms.OpenFreq.StartTransmissionAsync(Arg.Any<int>(), Arg.Any<Guid>())
            .Returns(Task.FromException(new InvalidOperationException("Not authenticated")));
        var stopCalled = new TaskCompletionSource();
        bms.OpenFreq.StopTransmissionAsync(bms.Radio1.Id)
            .Returns(_ =>
            {
                stopCalled.TrySetResult();
                return Task.CompletedTask;
            });

        bms.Press();
        bms.Release();

        await stopCalled.Task.WaitAsync(WaitLimit);
        Assert.Contains(bms.Logger.Entries,
            entry => entry is { Level: LogLevel.Error, Message: "BMS PTT start on Radio1 failed" });
    }

    /// <summary>A view model in BMS flight, with a connected card for Radio 1.</summary>
    private sealed class FlyingBmsRadio1
    {
        public IOpenFreqService OpenFreq { get; } = VmFactory.OpenFreq();
        public IFalconRadioSharedMemoryService Rcc { get; } = Substitute.For<IFalconRadioSharedMemoryService>();
        public CapturingLogger<ChannelCardListViewModel> Logger { get; } = new();
        public ChannelCardViewModel Radio1 { get; }

        public FlyingBmsRadio1()
        {
            var hotkey = VmFactory.Hotkey();
            hotkey.PttKeysPaused.Returns(true);
            var falcon = Substitute.For<IFalconSharedMemoryService>();
            falcon.IsFlying.Returns(true);
            var vm = VmFactory.ChannelCardList(OpenFreq, hotkey, Rcc, falcon, Logger);

            // CreateChannel adds the card through the UI dispatcher, which doesn't run in tests.
            var location = VmFactory.Location(RadioStationData.RadioStationType.BMS, OpenFreq);
            Radio1 = new ChannelCardViewModel(hotkey, "Radio 1", 251_000, false, location.RadioStationData, location,
                location.Settings)
            {
                BmsRadioType = RadioType.Radio1,
                ConnectionStatus = Channel.ChannelConnectionStatus.Connected
            };
            location.Channels.Add(Radio1);
            vm.FalconLocation = location;
        }

        public void Press() =>
            Rcc.PttChanged += Raise.EventWith(new RadioPttChangedEventArgs(RadioType.Radio1, false, true));

        public void Release() =>
            Rcc.PttChanged += Raise.EventWith(new RadioPttChangedEventArgs(RadioType.Radio1, true, false));
    }
}
