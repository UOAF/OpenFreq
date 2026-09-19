using OpenFreq.Client.Models;
using OpenFreqAudio;

namespace OpenFreq.Client.Tests;

/// <summary>
/// Tests for which location's ambient SFX <see cref="OpenFreqClient.Services.OpenFreqService"/> puts
/// on our own voice in the capture. Starting a transmission opens a BASS recording device, so the
/// switch to the location we transmit from isn't covered here.
/// </summary>
public class OpenFreqServiceOwnVoiceAmbientTests
{
    private static RadioStationData NewStation(RadioStationPreset preset) => new() { Preset = preset, Ppm = 0 };

    [Fact]
    public async Task FirstTunedLocation_SetsOwnVoiceAmbient()
    {
        var h = new ServiceHarness();
        await h.InitializeAuthenticatedAsync();

        await h.Service.JoinFrequencyAsync(251_000, Guid.NewGuid(), NewStation(RadioStationPresets.FighterF15));

        Assert.Equal(AmbientNoiseType.AirF15, h.Playback.OwnVoiceAmbient);
    }

    [Fact]
    public async Task LaterTunedLocation_KeepsFirstLocationsAmbient()
    {
        var h = new ServiceHarness();
        await h.InitializeAuthenticatedAsync();

        await h.Service.JoinFrequencyAsync(251_000, Guid.NewGuid(), NewStation(RadioStationPresets.FighterF15));
        await h.Service.JoinFrequencyAsync(252_000, Guid.NewGuid(), NewStation(RadioStationPresets.AWACS));

        Assert.Equal(AmbientNoiseType.AirF15, h.Playback.OwnVoiceAmbient);
    }

    [Fact]
    public async Task PresetChangeOnFollowedLocation_UpdatesOwnVoiceAmbient()
    {
        var h = new ServiceHarness();
        await h.InitializeAuthenticatedAsync();
        var station = NewStation(RadioStationPresets.FighterF15);
        await h.Service.JoinFrequencyAsync(251_000, Guid.NewGuid(), station);

        // As when BMS reports a new aircraft.
        station.Preset = RadioStationPresets.FighterF16;

        Assert.Equal(AmbientNoiseType.AirF16, h.Playback.OwnVoiceAmbient);
    }

    [Fact]
    public async Task PresetChangeOnOtherLocation_KeepsOwnVoiceAmbient()
    {
        var h = new ServiceHarness();
        await h.InitializeAuthenticatedAsync();
        var other = NewStation(RadioStationPresets.AWACS);
        await h.Service.JoinFrequencyAsync(251_000, Guid.NewGuid(), NewStation(RadioStationPresets.FighterF15));
        await h.Service.JoinFrequencyAsync(252_000, Guid.NewGuid(), other);

        other.Preset = RadioStationPresets.FighterF16;

        Assert.Equal(AmbientNoiseType.AirF15, h.Playback.OwnVoiceAmbient);
    }
}
