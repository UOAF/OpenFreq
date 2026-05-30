using OpenFreq.Common;

namespace OpenFreq.Common.Tests;

public class UtilTests
{
    [Fact]
    public void ResolveAddress_IpOnly_UsesDefaultPort()
    {
        var (ip, port) = Util.ResolveAddress("192.168.1.10", 9987);
        Assert.Equal("192.168.1.10", ip);
        Assert.Equal(9987, port);
    }

    [Fact]
    public void ResolveAddress_IpWithPort_ParsesBoth()
    {
        var (ip, port) = Util.ResolveAddress("10.0.0.5:1234", 9987);
        Assert.Equal("10.0.0.5", ip);
        Assert.Equal(1234, port);
    }

    [Fact]
    public void ResolveAddress_TooManyColons_Throws()
    {
        Assert.Throws<ArgumentException>(() => Util.ResolveAddress("1.2.3.4:5:6", 9987));
    }

    [Fact]
    public void ResolveAddress_PortNotANumber_Throws()
    {
        Assert.Throws<ArgumentException>(() => Util.ResolveAddress("1.2.3.4:abc", 9987));
    }

    [Theory]
    [InlineData("1.2.3.4:0")]
    [InlineData("1.2.3.4:65536")]
    [InlineData("1.2.3.4:-1")]
    public void ResolveAddress_PortOutOfRange_Throws(string address)
    {
        Assert.Throws<ArgumentException>(() => Util.ResolveAddress(address, 9987));
    }

    [Fact]
    public void ResolveAddress_Hostname_ResolvesToIp()
    {
        var (ip, port) = Util.ResolveAddress("localhost", 9987);
        Assert.False(string.IsNullOrEmpty(ip));
        Assert.Equal(9987, port);
    }

    [Fact]
    public void ResolveAddress_UnresolvableHostname_Throws()
    {
        Assert.Throws<ArgumentException>(
            () => Util.ResolveAddress("nonexistent.host.invalid.example", 9987));
    }

    [Fact]
    public void ResolveAddress_PortBoundaries_Accepted()
    {
        Assert.Equal(1, Util.ResolveAddress("1.2.3.4:1", 9987).port);
        Assert.Equal(65535, Util.ResolveAddress("1.2.3.4:65535", 9987).port);
    }
}
