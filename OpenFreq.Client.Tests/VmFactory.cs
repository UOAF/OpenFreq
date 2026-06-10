using System.Collections.ObjectModel;
using FalconBmsDataService.Services;
using FalconRadioService.Services;
using Microsoft.Extensions.Logging.Abstractions;
using OpenFreq.Client.Models;
using OpenFreq.Services.Acmi;
using OpenFreqAudio;
using OpenFreqClient.Services.Interfaces;
using OpenFreqClient.ViewModels;

namespace OpenFreq.Client.Tests;

/// <summary>
/// Builds ViewModels wired to NSubstitute service mocks. ViewModels coordinate the UI but their
/// logic (commands, computed properties, state) runs without a window — only methods that hop onto
/// <c>Dispatcher.UIThread</c> are avoided by the tests.
/// </summary>
internal static class VmFactory
{
    public static IOpenFreqService OpenFreq() => Substitute.For<IOpenFreqService>();
    public static IHotkeyService Hotkey() => Substitute.For<IHotkeyService>();

    public static IAudioService Audio()
    {
        var audio = Substitute.For<IAudioService>();
        // Non-null device lists are required: the SettingsViewModel ctor wraps them in ObservableCollection.
        audio.GetPlaybackDevices().Returns(new List<string>());
        audio.GetRecordingDevices().Returns(new List<string>());
        return audio;
    }

    public static SettingsViewModel Settings(
        IOpenFreqService? openFreq = null,
        IHotkeyService? hotkey = null,
        IAudioService? audio = null)
        => new(
            NullLogger<SettingsViewModel>.Instance,
            audio ?? Audio(),
            Substitute.For<IFalconRadioSharedMemoryService>(),
            Substitute.For<IFalconSharedMemoryService>(),
            Substitute.For<IAcmiClientService>(),
            openFreq ?? OpenFreq(),
            hotkey ?? Hotkey());

    public static ChannelCardListViewModel ChannelCardList(IOpenFreqService? openFreq = null)
    {
        var of = openFreq ?? OpenFreq();
        return new ChannelCardListViewModel(
            of,
            Hotkey(),
            Substitute.For<IAcmiClientService>(),
            NullLogger<ChannelCardListViewModel>.Instance,
            Substitute.For<IFalconRadioSharedMemoryService>(),
            Substitute.For<IFalconSharedMemoryService>(),
            Settings(of));
    }

    public static LocationViewModel Location(
        RadioStationData.RadioStationType type = RadioStationData.RadioStationType.STATIONARY,
        IOpenFreqService? openFreq = null)
    {
        var of = openFreq ?? OpenFreq();
        return new LocationViewModel(
            of,
            Hotkey(),
            Substitute.For<IAcmiClientService>(),
            Settings(of),
            name: "Loc1",
            preset: RadioStationPresets.FighterF16,
            radioStationType: type,
            globalTacviewCallsigns: new ObservableCollection<LocationViewModel.TacviewAircraftItem>());
    }
}
