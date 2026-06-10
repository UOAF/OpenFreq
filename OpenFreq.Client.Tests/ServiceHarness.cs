using FalconBmsDataService.Services;
using FalconRadioService.Services;
using Microsoft.Extensions.Logging.Abstractions;
using OpenFreq.Client.Models;
using OpenFreq.Common;
using OpenFreq.Services.Acmi;
using OpenFreqAudio;
using OpenFreqClient.Models;
using OpenFreqClient.Services;
using OpenFreqClient.Services.Audio;
using OpenFreqClient.Services.Interfaces;

namespace OpenFreq.Client.Tests;

/// <summary>
/// Builds a real <see cref="OpenFreqService"/> wired to substituted dependencies. The network client
/// (<see cref="IRtcClient"/>) and BASS playback (<see cref="IPlaybackService"/>) are NSubstitute fakes,
/// so the service runs fully in-process with no sockets or audio device.
/// </summary>
internal sealed class ServiceHarness
{
    public IRtcClient Client { get; } = Substitute.For<IRtcClient>();
    public IPlaybackService Playback { get; } = Substitute.For<IPlaybackService>();
    public ISignalCalculator SignalCalculator { get; } = Substitute.For<ISignalCalculator>();
    public IFalconSharedMemoryService Falcon { get; } = Substitute.For<IFalconSharedMemoryService>();
    public IFalconRadioSharedMemoryService FalconRadio { get; } = Substitute.For<IFalconRadioSharedMemoryService>();
    public IAcmiClientService Acmi { get; } = Substitute.For<IAcmiClientService>();

    public OpenFreqService Service { get; }

    public ServiceHarness()
    {
        var rtcFactory = Substitute.For<IRtcClientFactory>();
        rtcFactory.Create(default!, default!, default!, default).ReturnsForAnyArgs(Client);

        var playbackFactory = Substitute.For<IPlaybackServiceFactory>();
        playbackFactory.Create(default!, default).ReturnsForAnyArgs(Playback);

        var signalFactory = Substitute.For<ISignalCalculatorFactory>();
        signalFactory.Create(default!, default, default, default, default, default!).ReturnsForAnyArgs(SignalCalculator);

        Service = new OpenFreqService(
            Falcon, FalconRadio, NullLogger<OpenFreqService>.Instance, NullLoggerFactory.Instance, Acmi,
            rtcFactory, playbackFactory, signalFactory);
    }

    /// <summary>Run Initialize() with sane defaults (GCI mode, device 0).</summary>
    public Task InitializeAsync(IOpenFreqService.Mode mode = IOpenFreqService.Mode.GCI)
    {
        var settings = new OpenFreqSettings
        {
            OpenFreqServerAddress = "localhost",
            OpenFreqPassword = "",
            DisplayName = "Tester",
            OwnPositionMode = mode
        };
        return Service.Initialize(settings, recordingDeviceIndex: 0, playbackDeviceIndex: 0);
    }

    /// <summary>Initialize + mark the fake client authenticated so join/leave proceed.</summary>
    public async Task InitializeAuthenticatedAsync()
    {
        await InitializeAsync();
        Client.IsAuthenticated.Returns(true);
    }

    public static RadioStationData NewRadioStation() =>
        new() { Preset = RadioStationPresets.FighterF16, Ppm = 0 };
}
