using OpenFreq.Common;
using OpenFreqClient.Services;
using OpenFreqClient.Services.Interfaces;

namespace OpenFreq.Client.Tests;

/// <summary>
/// Tests for the PTT start and end lines <see cref="OpenFreqService"/> logs for peers, and the game time it
/// logs and sends. Our own PTT lines are not covered: starting a transmission opens a BASS recording device.
/// </summary>
public class OpenFreqServicePttLogTests
{
    private const int Freq = 251_000;
    private const string Peer = "peer1";

    /// <summary>Connected in GCI mode (the harness default), with a radio tuned to <see cref="Freq"/>.</summary>
    private static async Task<(ServiceHarness Harness, CapturingLogger<OpenFreqService> Log)> JoinedHarnessAsync()
    {
        var log = new CapturingLogger<OpenFreqService>();
        var h = new ServiceHarness(log);
        await h.InitializeAuthenticatedAsync();
        await h.Service.JoinFrequencyAsync(Freq, Guid.NewGuid(), ServiceHarness.NewRadioStation());
        return (h, log);
    }

    private static void PeerTransmits(ServiceHarness h, bool transmitting, int frequencyKhz = Freq) =>
        h.Client.PeerTransmissionStateChanged += Raise.EventWith(
            new PeerTransmissionEventArgs(Peer, "Viper", frequencyKhz, transmitting, is3d: true));

    private static List<string> PttLines(CapturingLogger<OpenFreqService> log) =>
        log.Entries.Select(e => e.Message).Where(m => m.StartsWith("PTT ")).ToList();

    [Fact]
    public async Task Heartbeats_LogOneStartAndOneEnd()
    {
        var (h, log) = await JoinedHarnessAsync();
        h.Acmi.GameTimeSeconds.Returns(45296);

        PeerTransmits(h, true);
        PeerTransmits(h, true);
        PeerTransmits(h, true);
        PeerTransmits(h, false);

        Assert.Equal(
        [
            "PTT start: Viper (peer1) on 251.000 MHz, 3D, game time 12:34:56",
            "PTT end: Viper (peer1) on 251.000 MHz, released, game time 12:34:56"
        ], PttLines(log));
    }

    [Fact]
    public async Task PeerLeavesWhileTalking_LogsEnd()
    {
        var (h, log) = await JoinedHarnessAsync();

        PeerTransmits(h, true);
        h.Client.PeerLeft += Raise.EventWith(new PeerEventArgs(Peer, "Viper", Freq));

        Assert.Equal("PTT end: Viper (peer1) on 251.000 MHz, left the frequency",
            PttLines(log)[^1]);
    }

    [Fact]
    public async Task WeLeaveTheFrequencyWhileAPeerTalks_LogsEnd()
    {
        var (h, log) = await JoinedHarnessAsync();

        PeerTransmits(h, true);
        h.Client.FrequencyLeft += Raise.EventWith(new FrequencyLeftEventArgs(Freq));

        Assert.Equal("PTT end: Viper (peer1) on 251.000 MHz, we left the frequency",
            PttLines(log)[^1]);
    }

    [Fact]
    public async Task WeDisconnectWhileAPeerTalks_LogsEnd()
    {
        var (h, log) = await JoinedHarnessAsync();

        PeerTransmits(h, true);
        h.Client.ConnectionStateChanged +=
            Raise.EventWith(new ConnectionStateChangedEventArgs(ConnectionState.Disconnected));

        Assert.Equal("PTT end: Viper (peer1) on 251.000 MHz, we disconnected",
            PttLines(log)[^1]);
    }

    [Fact]
    public async Task PeerOnAFrequencyWeAreNotTunedTo_LogsNothing()
    {
        var (h, log) = await JoinedHarnessAsync();

        PeerTransmits(h, true, frequencyKhz: 135_100);
        PeerTransmits(h, false, frequencyKhz: 135_100);

        Assert.Empty(PttLines(log));
    }

    [Fact]
    public async Task InBmsMode_GameTimeComesFromSharedMemory()
    {
        var (h, log) = await JoinedHarnessAsync();
        h.Acmi.GameTimeSeconds.Returns(1);
        h.Falcon.GameTimeSeconds.Returns(45296);
        h.Service.SetOwnPositionMode(IOpenFreqService.Mode.BMS);

        PeerTransmits(h, true);

        Assert.Equal("PTT start: Viper (peer1) on 251.000 MHz, 3D, game time 12:34:56", Assert.Single(PttLines(log)));
    }

    [Fact]
    public async Task Client_GetsGameTimeFromTheCurrentSource()
    {
        var (h, _) = await JoinedHarnessAsync();
        h.Acmi.GameTimeSeconds.Returns(100);
        h.Falcon.GameTimeSeconds.Returns(200);

        var gameTime = h.Client.GameTimeSeconds;
        Assert.NotNull(gameTime);
        Assert.Equal(100, gameTime());

        h.Service.SetOwnPositionMode(IOpenFreqService.Mode.BMS);
        Assert.Equal(200, gameTime());
    }
}
