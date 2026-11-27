using OpenFreqAudio.TerrainSampling;
using OpenFreqClient.Services.Audio;

namespace OpenFreq.Client.Tests;

/// <summary>
/// Tests for how <see cref="OpenFreqClient.Services.OpenFreqService"/> loads and swaps the terrain.
/// </summary>
public class OpenFreqServiceTerrainTests
{
    private static readonly TheaterTerrainFiles FilesA = new("a.raw", null, null, null);
    private static readonly TheaterTerrainFiles FilesB = new("b.raw", null, null, null);

    private static ISignalCalculator Calculator(double elevationMeters)
    {
        var calculator = Substitute.For<ISignalCalculator>();
        calculator.SampleElevation(default, default).ReturnsForAnyArgs(elevationMeters);
        return calculator;
    }

    [Fact]
    public void SameFiles_LoadOnce()
    {
        var h = new ServiceHarness();

        h.Service.LoadHeightmap(FilesA);
        h.Service.LoadHeightmap(FilesA with { });

        h.SignalCalculatorFactory.ReceivedWithAnyArgs(1).Create(default!, default!);
    }

    [Fact]
    public void NewFiles_BuildTheNewTerrain_BeforeDisposingTheOld()
    {
        var h = new ServiceHarness();
        var a = Calculator(1.0);
        var b = Calculator(2.0);
        h.SignalCalculatorFactory.Create(FilesA, default!).ReturnsForAnyArgs(a, b);

        h.Service.LoadHeightmap(FilesA);
        h.Service.LoadHeightmap(FilesB);

        Received.InOrder(() =>
        {
            h.SignalCalculatorFactory.Create(FilesB, Arg.Any<Microsoft.Extensions.Logging.ILoggerFactory>());
            a.Dispose();
        });
        b.DidNotReceive().Dispose();
        Assert.Equal(2.0, h.Service.SampleTerrainElevationMeters(0, 0));
    }

    [Fact]
    public void FailedLoad_KeepsTheOldTerrain_AndTriesAgainNextTime()
    {
        var log = new CapturingLogger<OpenFreqClient.Services.OpenFreqService>();
        var h = new ServiceHarness(log);
        var a = Calculator(1.0);
        h.SignalCalculatorFactory.Create(FilesA, Arg.Any<Microsoft.Extensions.Logging.ILoggerFactory>()).Returns(a);
        h.SignalCalculatorFactory.Create(FilesB, Arg.Any<Microsoft.Extensions.Logging.ILoggerFactory>())
            .Returns(_ => throw new InvalidDataException("b.raw is not a square map of int16 samples"));

        h.Service.LoadHeightmap(FilesA);
        h.Service.LoadHeightmap(FilesB);
        h.Service.LoadHeightmap(FilesB);

        a.DidNotReceive().Dispose();
        Assert.Equal(1.0, h.Service.SampleTerrainElevationMeters(0, 0));
        h.SignalCalculatorFactory.Received(2).Create(FilesB, Arg.Any<Microsoft.Extensions.Logging.ILoggerFactory>());
        Assert.Equal(2, log.Entries.Count(e => e.Level == Microsoft.Extensions.Logging.LogLevel.Error));
    }
}
