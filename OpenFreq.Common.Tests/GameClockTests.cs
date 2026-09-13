namespace OpenFreq.Common.Tests;

public class GameClockTests
{
    [Theory]
    [InlineData(0, ", game time 00:00:00")]
    [InlineData(45296, ", game time 12:34:56")]
    [InlineData(86399, ", game time 23:59:59")]
    public void LogSuffix_SecondsOfDay_IsHoursMinutesSeconds(int secondsOfDay, string expected)
    {
        Assert.Equal(expected, GameClock.LogSuffix(secondsOfDay));
    }

    [Fact]
    public void LogSuffix_NoClock_IsEmpty()
    {
        Assert.Equal("", GameClock.LogSuffix(null));
    }
}
