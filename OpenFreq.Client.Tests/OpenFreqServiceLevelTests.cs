using OpenFreqClient.Services;

namespace OpenFreq.Client.Tests;

/// <summary>Tests for the TX level telemetry helpers in <see cref="OpenFreqService"/>.</summary>
public class OpenFreqServiceLevelTests
{
    [Fact]
    public void CountFullScale_CountsBothRails()
    {
        short[] samples = [0, short.MaxValue, -short.MaxValue, short.MinValue, short.MaxValue - 1, 100];

        Assert.Equal(3, OpenFreqService.CountFullScale(samples));
    }
}
