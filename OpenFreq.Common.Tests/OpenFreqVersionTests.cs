namespace OpenFreq.Common.Tests;

public class OpenFreqVersionTests
{
    [Theory]
    [InlineData("1.2.3", "1.2.3")] // identical
    [InlineData("1.2.3", "1.2.4")] // patch difference is non-breaking
    [InlineData("1.2.0", "1.2.99")]
    [InlineData("1.2.3-beta", "1.2.4")] // pre-release stripped before comparing
    [InlineData("1.2.3+abc123", "1.2.4+def456")] // build metadata stripped
    [InlineData("0.0.0-local", "0.0.0-local")] // dev builds match by equality
    public void AreCompatible_True(string client, string server)
    {
        Assert.True(OpenFreqVersion.AreCompatible(client, server));
    }

    [Theory]
    [InlineData("1.2.3", "1.3.3")] // minor mismatch
    [InlineData("1.2.3", "2.2.3")] // major mismatch
    [InlineData("0.0.0-local", "1.2.3")] // dev build vs release
    [InlineData("unknown", "1.2.3")] // non-semver vs semver
    [InlineData("unknown", "also-unknown")] // non-semver falls back to strict equality
    [InlineData("1.2", "1.2.3")] // incomplete semver core
    [InlineData(null, "1.2.3")] // missing client version (pre-version clients)
    [InlineData("1.2.3", null)]
    [InlineData(null, null)]
    public void AreCompatible_False(string? client, string? server)
    {
        Assert.False(OpenFreqVersion.AreCompatible(client, server));
    }

    [Theory]
    [InlineData("1.2.3", 1, 2)]
    [InlineData("10.20.30", 10, 20)]
    [InlineData("1.2.3-rc.1", 1, 2)]
    [InlineData("1.2.3+gitsha", 1, 2)]
    [InlineData("1.2.3-rc.1+gitsha", 1, 2)]
    public void TryGetMajorMinor_Parses(string version, int expectedMajor, int expectedMinor)
    {
        Assert.True(OpenFreqVersion.TryGetMajorMinor(version, out var major, out var minor));
        Assert.Equal(expectedMajor, major);
        Assert.Equal(expectedMinor, minor);
    }

    [Theory]
    [InlineData("unknown")]
    [InlineData("1.2")]
    [InlineData("1.2.3.4")]
    [InlineData("1.x.3")]
    [InlineData("")]
    public void TryGetMajorMinor_RejectsNonSemver(string version)
    {
        Assert.False(OpenFreqVersion.TryGetMajorMinor(version, out _, out _));
    }
}
