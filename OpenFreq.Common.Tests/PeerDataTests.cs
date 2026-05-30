using OpenFreq.Common;

namespace OpenFreq.Common.Tests;

public class PeerDataTests
{
    [Fact]
    public void Constructor_SetsAllFields()
    {
        var p = new PeerData("peer-1", "Viper", PeerData.PeerStatus.Transmitting, is3d: true);
        Assert.Equal("peer-1", p.Id);
        Assert.Equal("Viper", p.Name);
        Assert.Equal(PeerData.PeerStatus.Transmitting, p.Status);
        Assert.True(p.Is3d);
    }

    [Fact]
    public void Constructor_Is3dDefaultsFalse()
    {
        var p = new PeerData("peer-2", "Maverick", PeerData.PeerStatus.Receiving);
        Assert.False(p.Is3d);
    }

    [Fact]
    public void Constructor_AllowsNullName()
    {
        var p = new PeerData("peer-3", null, PeerData.PeerStatus.Receiving);
        Assert.Null(p.Name);
    }

    [Fact]
    public void Properties_AreMutable()
    {
        var p = new PeerData("peer-4", "Old", PeerData.PeerStatus.Receiving)
        {
            Name = "New",
            Status = PeerData.PeerStatus.Transmitting,
            Is3d = true
        };
        Assert.Equal("New", p.Name);
        Assert.Equal(PeerData.PeerStatus.Transmitting, p.Status);
        Assert.True(p.Is3d);
    }
}
