using Microsoft.Extensions.Logging;
using OpenFreq.Common;
using OpenFreqAudio;
using OpenFreqClient.Services;

namespace OpenFreq.Client.Tests;

/// <summary>
/// Tests for how <see cref="OpenFreqService"/> uses physics results for incoming 3D audio.
/// </summary>
public class OpenFreqServicePhysicsTests
{
    private const int Freq = 251_000;
    private const string Peer = "peer1";

    // Longer than OpenFreqService's 50 ms params cache, so the next packet re-runs physics.
    private static readonly TimeSpan PastParamsCache = TimeSpan.FromMilliseconds(80);

    private static AudioParams GoodParams(float snrDb) => new()
    {
        RadioFrequencyKHz = Freq,
        ReceivedDb = snrDb - 107f,
        ReceivedSnrDb = snrDb,
        FreeSpaceLossDb = 120f
    };

    private static AudioParams WithField(string field, float value)
    {
        var p = GoodParams(snrDb: 20f);
        typeof(AudioParams).GetField(field)!.SetValue(p, value);
        return p;
    }

    /// <summary>
    /// Connected, in 3D mode, with a terrain model loaded and a stationary radio tuned to <see cref="Freq"/>.
    /// Records every params update pushed to playback.
    /// </summary>
    private static async Task<(ServiceHarness Harness, CapturingLogger<OpenFreqService> Log, List<AudioParams> Pushed)>
        ReceivingHarnessAsync()
    {
        var log = new CapturingLogger<OpenFreqService>();
        var h = new ServiceHarness(log);
        await h.InitializeAuthenticatedAsync();
        h.Service.LoadHeightmap("unused.bin");
        h.Service.Apply3dAudioEffects = true;

        var station = ServiceHarness.NewRadioStation();
        station.Vector3 = new Vector3(100_000, 100_000, 10);
        await h.Service.JoinFrequencyAsync(Freq, Guid.NewGuid(), station);

        var pushed = new List<AudioParams>();
        h.Playback.IsStreamActive(Arg.Any<string>()).Returns(true);
        h.Playback.When(p => p.UpdateStreamParams(Arg.Any<string>(), Arg.Any<AudioParams>()))
            .Do(call => pushed.Add(call.ArgAt<AudioParams>(1)));

        return (h, log, pushed);
    }

    private static void ReceivePacket(ServiceHarness h) =>
        h.Client.AudioDataReceived += Raise.EventWith(new AudioDataEventArgs(Peer, new short[960],
            new AudioPacketMetadata
            {
                ClientId = Peer,
                DisplayName = "Viper",
                Frequencies =
                [
                    new FrequencyTransmission(Freq, txPowerWatts: 10.0, ppm: 0.0,
                        position: new Vector3(150_000, 100_000, 12_000), velocity: null, in3d: true)
                ]
            }));

    private static void PhysicsReturns(ServiceHarness h, AudioParams first, params AudioParams[] rest) =>
        h.SignalCalculator.CalculateAudioParams(default, default, default, default, default, default, default)
            .ReturnsForAnyArgs(first, rest);

    private static List<(LogLevel Level, string Message)> Errors(CapturingLogger<OpenFreqService> log) =>
        log.Entries.Where(e => e.Level == LogLevel.Error).ToList();

    [Fact]
    public async Task NonFinitePhysics_KeepsLastGoodParams_AndLogsOncePerRun()
    {
        var (h, log, pushed) = await ReceivingHarnessAsync();
        var good = GoodParams(snrDb: 20f);
        var later = GoodParams(snrDb: 10f);
        // The 1.1.0 field bug: SNR (and so received power) came out NaN.
        var bad = WithField(nameof(AudioParams.ReceivedSnrDb), float.NaN);
        PhysicsReturns(h, good, bad, bad, later, bad);

        for (int i = 0; i < 5; i++)
        {
            if (i > 0) await Task.Delay(PastParamsCache);
            ReceivePacket(h);
        }

        h.SignalCalculator.ReceivedWithAnyArgs(5)
            .CalculateAudioParams(default, default, default, default, default, default, default);
        Assert.Equal([good, good, good, later, later], pushed);

        // One error for each run of bad results: the two in a row, then the one after `later`.
        var errors = Errors(log);
        Assert.Equal(2, errors.Count);
        Assert.All(errors, e => Assert.Contains("ReceivedSnrDb NaN", e.Message));
    }

    [Theory]
    [InlineData(nameof(AudioParams.ReceivedDb))]
    [InlineData(nameof(AudioParams.ReceivedSnrDb))]
    [InlineData(nameof(AudioParams.FreeSpaceLossDb))]
    [InlineData(nameof(AudioParams.TerrainLossDb))]
    [InlineData(nameof(AudioParams.DropoutRate))]
    [InlineData(nameof(AudioParams.DeepFadeRate))]
    [InlineData(nameof(AudioParams.TuneOffsetPPM))]
    public async Task NaNInAnyField_BeforeAnyGoodParams_FallsBackToDefaults(string field)
    {
        var (h, log, pushed) = await ReceivingHarnessAsync();
        PhysicsReturns(h, WithField(field, float.NaN));

        ReceivePacket(h);

        var defaults = FastPathAudioSim.GetDefaultAudioParams(Freq);
        var p = Assert.Single(pushed);
        Assert.Equal(defaults.ReceivedDb, p.ReceivedDb);
        Assert.Equal(defaults.ReceivedSnrDb, p.ReceivedSnrDb);
        var error = Assert.Single(Errors(log));
        Assert.Contains($"{field} NaN", error.Message);
    }

    [Fact]
    public async Task InfinitePhysics_IsRefusedToo()
    {
        var (h, log, pushed) = await ReceivingHarnessAsync();
        var good = GoodParams(snrDb: 20f);
        PhysicsReturns(h, good, WithField(nameof(AudioParams.ReceivedDb), float.PositiveInfinity));

        ReceivePacket(h);
        await Task.Delay(PastParamsCache);
        ReceivePacket(h);

        Assert.Equal([good, good], pushed);
        Assert.Single(Errors(log));
    }

    [Fact]
    public async Task NewTalkspurt_RxLineHasNameAndGameTime()
    {
        var (h, log, _) = await ReceivingHarnessAsync();
        h.Acmi.GameTimeSeconds.Returns(45296);
        PhysicsReturns(h, GoodParams(snrDb: 20f));

        ReceivePacket(h);

        var rx = Assert.Single(log.Entries, e => e.Message.StartsWith("RX "));
        Assert.Contains($"from Viper ({Peer}), game time 12:34:56:", rx.Message);
    }
}
