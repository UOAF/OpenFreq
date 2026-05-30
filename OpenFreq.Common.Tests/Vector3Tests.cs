using OpenFreq.Common;

namespace OpenFreq.Common.Tests;

public class Vector3Tests
{
    [Fact]
    public void DefaultConstructor_AllZero()
    {
        var v = new Vector3();
        Assert.Equal(0.0, v.X);
        Assert.Equal(0.0, v.Y);
        Assert.Equal(0.0, v.Z);
    }

    [Fact]
    public void ComponentConstructor_SetsComponents()
    {
        var v = new Vector3(1.5, -2.0, 3.25);
        Assert.Equal(1.5, v.X);
        Assert.Equal(-2.0, v.Y);
        Assert.Equal(3.25, v.Z);
    }

    [Fact]
    public void TupleConstructor_SetsComponents()
    {
        var v = new Vector3((4.0, 5.0, 6.0));
        Assert.Equal(4.0, v.X);
        Assert.Equal(5.0, v.Y);
        Assert.Equal(6.0, v.Z);
    }

    [Fact]
    public void ToString_FormatsCommaSeparated()
    {
        var v = new Vector3(1, 2, 3);
        Assert.Equal("1,2,3", v.ToString());
    }

    [Fact]
    public void ToTuple_ReturnsComponents()
    {
        var v = new Vector3(7.0, 8.0, 9.0);
        Assert.Equal((7.0, 8.0, 9.0), v.ToTuple());
    }

    [Fact]
    public void TupleConstructor_RoundTripsViaToTuple()
    {
        var tuple = (10.0, 20.0, 30.0);
        Assert.Equal(tuple, new Vector3(tuple).ToTuple());
    }
}
