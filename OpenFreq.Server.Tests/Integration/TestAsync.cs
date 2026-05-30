namespace OpenFreq.Server.Tests.Integration;

internal static class TestAsync
{
    /// <summary>Poll a condition until it holds or the timeout elapses; fails the test on timeout.</summary>
    public static async Task PollUntil(
        Func<bool> condition, TimeSpan? timeout = null, TimeSpan? interval = null)
    {
        var deadline = DateTime.UtcNow + (timeout ?? TimeSpan.FromSeconds(2));
        var step = interval ?? TimeSpan.FromMilliseconds(25);

        while (DateTime.UtcNow < deadline)
        {
            if (condition()) return;
            await Task.Delay(step);
        }

        if (!condition())
            throw new Xunit.Sdk.XunitException("Condition not met within timeout");
    }
}
