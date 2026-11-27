using Microsoft.Extensions.Logging.Abstractions;
using OpenFreq.Client.Terrain;

namespace OpenFreq.Client.Tests;

/// <summary>
/// Tests for reading BMS .tdf files and finding theater paths.
/// </summary>
public class TerrainFilesTests
{
    [Fact]
    public void Tdf_FollowsBmsRules()
    {
        const string text =
            "# comment\r\n" +
            "; also a comment\r\n" +
            "// and this\r\n" +
            "name Korea KTO\r\n" +
            "NEWTERRAINDIR\tTerrData\\Korea\\NewTerrain  \r\n" +
            "terraindir first\r\n" +
            "TerrainDir Terrdata\\korea\r\n" +
            "   objectdir   Add-On X\\Terrdata\\objects \t\r\n" +
            "moviedir\r\n" +
            "cut a\rnot this\n" +
            "empty \r\n";
        var tdf = BmsTdfParser.Parse(text);

        Assert.Equal("Korea KTO", tdf["name"]);
        Assert.Equal("TerrData\\Korea\\NewTerrain", tdf["newterraindir"]);
        Assert.Equal("Terrdata\\korea", tdf["terraindir"]);
        Assert.Equal("Add-On X\\Terrdata\\objects", tdf["OBJECTDIR"]);
        Assert.False(tdf.ContainsKey("moviedir"));
        Assert.Equal("a", tdf["cut"]);
        Assert.Equal("", tdf["empty"]);
        Assert.False(tdf.ContainsKey("#"));
        Assert.False(tdf.ContainsKey(";"));
    }

    [Fact]
    public void NewTerrainDir_UsesNewTerrainDirAsWritten_ElseTerrainDir()
    {
        Assert.Equal(Path.Combine(@"C:\BMS\Data", @"TerrData\Korea\NewTerrain"),
            BmsTheaterPaths.NewTerrainDir(@"C:\BMS\Data", @"TerrData\Korea\NewTerrain", @"C:\BMS\Data\Terrdata\other"));
        Assert.Equal(Path.Combine(@"C:\BMS\Data\Terrdata\korea", "NewTerrain"),
            BmsTheaterPaths.NewTerrainDir(@"C:\BMS\Data", "", @"C:\BMS\Data\Terrdata\korea"));
    }

    [Theory]
    [InlineData(@"D:\Theaters\X", @"D:\Theaters\X")]
    [InlineData("//server/share", "//server/share")]
    public void ResolveTdfDir_KeepsFullPaths(string value, string expected)
    {
        Assert.Equal(expected, BmsTheaterPaths.ResolveTdfDir(@"C:\BMS\Data", value));
    }

    [Fact]
    public void ResolveTdfDir_JoinsRelativePathsToData()
    {
        Assert.Equal(Path.Join(@"C:\BMS\Data", @"Add-On X\Terrdata"), BmsTheaterPaths.ResolveTdfDir(@"C:\BMS\Data", @"Add-On X\Terrdata"));
    }

    [Fact]
    public void Resolve_FindsTheTheaterByName_AndUsesItsNewTerrainDir()
    {
        using var dir = new TempDir();
        string data = dir.Path;
        string defs = Directory.CreateDirectory(Path.Combine(data, "TerrData", "TheaterDefinition")).FullName;
        File.WriteAllText(Path.Combine(defs, "theater.lst"), "TerrData\\TheaterDefinition\\a.tdf\r\n# skipped\r\nTerrData\\TheaterDefinition\\b.tdf\r\n");
        File.WriteAllText(Path.Combine(defs, "a.tdf"), "name Other\r\nnewterraindir Wrong\r\n");
        File.WriteAllText(Path.Combine(defs, "b.tdf"), "name My Theater\r\nnewterraindir Add-On\\NewTerrain\r\n");

        var info = new BmsTheaterInfo(Path.Combine(data, "terrain"), "my theater", Path.Combine(data, "objects"), data);
        var files = BmsTheaterPaths.Resolve(info, null, NullLogger.Instance);

        Assert.Equal(Path.Combine(data, "Add-On\\NewTerrain", "HeightMaps", "HeightMap.raw"), files.HeightmapPath);
        Assert.Equal(Path.Combine(data, "objects", "TerrainRelatedData"), files.LevelingDir);
    }

    [Fact]
    public void Resolve_WithoutTheTdf_UsesTheTerrainDir()
    {
        using var dir = new TempDir();
        var info = new BmsTheaterInfo(Path.Combine(dir.Path, "terrain"), "Nowhere", null, dir.Path);
        var files = BmsTheaterPaths.Resolve(info, null, NullLogger.Instance);

        Assert.Equal(Path.Combine(dir.Path, "terrain", "NewTerrain", "HeightMaps", "HeightMap.raw"), files.HeightmapPath);
        Assert.Null(files.LevelingDir);
    }

    internal sealed class TempDir : IDisposable
    {
        public string Path { get; } = Directory.CreateTempSubdirectory("ofc_terrain_").FullName;

        public void Dispose()
        {
            try { Directory.Delete(Path, recursive: true); } catch { /* temp folder best-effort */ }
        }
    }
}
