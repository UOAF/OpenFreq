using Microsoft.Extensions.Logging;
using OpenFreq.Client.NativeMethods;

namespace OpenFreq.Client.Tests;

/// <summary>
/// Calls the real Win32 functions, to check the P/Invoke declarations and the Windows 11 hidden-window opt-out.
/// </summary>
public class Win32TimerResolutionTests
{
    [Windows11Fact]
    public void Request_SetsTheResolution_AndKeepsItWhileTheWindowIsHidden()
    {
        var logger = new CapturingLogger<Win32TimerResolution>();

        using var request = Win32TimerResolution.Request(logger);

        Assert.NotNull(request);
        var entry = Assert.Single(logger.Entries);
        Assert.Equal((LogLevel.Information, "Timer resolution set to 1 ms"), entry);
    }
}

/// <summary>Skips the test before Windows 11, which has no hidden-window opt-out to check.</summary>
internal sealed class Windows11FactAttribute : FactAttribute
{
    public Windows11FactAttribute()
    {
        if (!OperatingSystem.IsWindowsVersionAtLeast(10, 0, 22000))
            Skip = "Needs Windows 11";
    }
}
