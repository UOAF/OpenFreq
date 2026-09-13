using Microsoft.Extensions.Logging.Abstractions;
using OpenFreq.Services.Acmi;

namespace OpenFreq.Client.Tests;

/// <summary>
/// Tests for the game time <see cref="AcmiClientService"/> reads from a Tacview stream.
/// </summary>
public class AcmiClientServiceTests
{
    [Fact]
    public void FrameTime_BeforeReferenceTime_GivesNoGameTime()
    {
        using var acmi = new AcmiClientService(NullLogger<AcmiClientService>.Instance);

        acmi.ProcessLine("#12.5");

        Assert.Null(acmi.GameTimeSeconds);
    }

    [Fact]
    public void FrameTime_AfterReferenceTime_GivesTimeOfDay()
    {
        using var acmi = new AcmiClientService(NullLogger<AcmiClientService>.Instance);

        acmi.ProcessLine("0,ReferenceTime=2026-09-12T08:00:00Z");
        acmi.ProcessLine("#12.5");

        Assert.Equal(8 * 3600 + 12, acmi.GameTimeSeconds);
    }

    [Fact]
    public void FrameTime_PastMidnight_WrapsToTheNextDay()
    {
        using var acmi = new AcmiClientService(NullLogger<AcmiClientService>.Instance);

        acmi.ProcessLine("0,ReferenceTime=2026-09-12T23:59:50Z");
        acmi.ProcessLine("#15");

        Assert.Equal(5, acmi.GameTimeSeconds);
    }
}
