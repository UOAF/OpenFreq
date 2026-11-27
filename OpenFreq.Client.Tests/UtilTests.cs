using System.Globalization;
using OpenFreq.Client.Converters;
using OpenFreqClient.Converters;
using OpenFreq.Utilities;

namespace OpenFreq.Client.Tests;

/// <summary>Unit tests for self-contained helpers.</summary>
public class BmsHeightmapConverterTests
{
    [Fact]
    public void ToHeightmap_ThenFromHeightmap_RoundTrips()
    {
        const double x = 123_456.0, y = 654_321.0, z = 5_000.0;

        var (hx, hy, alt) = BmsHeightmapConverter.Default.ToHeightmap(x, y, z);
        var (bx, by, bz) = BmsHeightmapConverter.Default.FromHeightmap(hx, hy, alt);

        Assert.Equal(x, bx, precision: 6);
        Assert.Equal(y, by, precision: 6);
        Assert.Equal(z, bz, precision: 6);
    }

    [Fact]
    public void ToHeightmap_PutsNorthZeroOnLastRow()
    {
        var heightmap = BmsHeightmapConverter.Default;
        var (_, hy, _) = heightmap.ToHeightmap(0, 0, 0);

        Assert.Equal(heightmap.Samples - 1, hy / heightmap.CellSizeMeters, precision: 6);
    }

    [Fact]
    public void ToHeightmap_UsesTheMapSize()
    {
        // A 16384-sample map over 512 km has the same 31.25 m cells as the stock map, but half as many.
        var heightmap = new BmsHeightmapConverter(16_384, 512_000);
        var (_, hy, _) = heightmap.ToHeightmap(0, 0, 0);

        Assert.Equal(31.25, heightmap.CellSizeMeters, precision: 9);
        Assert.Equal(16_383 * 31.25, hy, precision: 6);
    }

    [Fact]
    public void ToHeightmap_AgreesWithLatLonConversion()
    {
        // BMS puts the theater center at 16384 cells east, 16383 cells north.
        var heightmap = BmsHeightmapConverter.Default;
        double cell = heightmap.CellSizeMeters;
        const double feetPerMeter = 1.0 / 0.3048;
        var (simX, simY, _) = heightmap.ToHeightmap(16384 * cell * feetPerMeter, 16383 * cell * feetPerMeter, 0);

        var (lx, ly) = TheaterCoordinateConverter.LatLonToXYMeters("Korea KTO", 38.5, 127.5,
            TheaterCoordinateConverter.CoordinateSystem.BMS_HEIGHTMAP_COORDINATE_SYTEM);

        Assert.Equal(lx, simX, 0.1);
        Assert.Equal(ly, simY, 0.1);
    }
}

public class NullableDoubleConverterTests
{
    private static readonly NullableDoubleConverter Converter = new();
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    [Fact]
    public void Convert_Double_FormatsInvariant()
    {
        var result = Converter.Convert(1.5, typeof(string), null, Inv);
        Assert.Equal("1.5", result);
    }

    [Fact]
    public void Convert_NonDouble_ReturnsEmpty()
    {
        Assert.Equal(string.Empty, Converter.Convert(null, typeof(string), null, Inv));
        Assert.Equal(string.Empty, Converter.Convert("nope", typeof(string), null, Inv));
    }

    [Fact]
    public void ConvertBack_ValidString_ReturnsDouble()
    {
        Assert.Equal(2.25, Converter.ConvertBack("2.25", typeof(double?), null, Inv));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("abc")]
    public void ConvertBack_BlankOrInvalid_ReturnsNull(string input)
    {
        Assert.Null(Converter.ConvertBack(input, typeof(double?), null, Inv));
    }
}

public class InvertedEnumToBoolConverterTests
{
    private enum Sample { A, B }

    private static readonly InvertedEnumToBoolConverter Converter = new();
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    [Fact]
    public void Convert_MatchingValue_ReturnsFalse()
    {
        Assert.Equal(false, Converter.Convert(Sample.A, typeof(bool), Sample.A, Inv));
    }

    [Fact]
    public void Convert_DifferentValue_ReturnsTrue()
    {
        Assert.Equal(true, Converter.Convert(Sample.A, typeof(bool), Sample.B, Inv));
    }
}
