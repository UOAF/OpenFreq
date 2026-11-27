using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.Versioning;
using OpenFreq.Client.Terrain;
using OpenFreq.Utilities;
using OpenFreqAudio.TerrainSampling;

namespace OpenFreqClient.Services;

public static class BmsDetectionService
{
    private const string RegistryKey = @"SOFTWARE\WOW6432Node\Benchmark Sims\Falcon BMS 4.38";

    public static string? GetBmsDirectory()
    {
        return !OperatingSystem.IsWindows() ? null : GetBmsDirectoryWindows();
    }

    [SupportedOSPlatform("windows")]
    private static string? GetBmsDirectoryWindows()
    {
        try
        {
            using var key = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(RegistryKey);
            return key?.GetValue("baseDir") as string;
        }
        catch
        {
            return null;
        }
    }

    public static List<TheaterDefinition> GetInstalledTheaters(string bmsDirectory)
    {
        var result = new List<TheaterDefinition>();
        var dataDir = Path.Combine(bmsDirectory, "Data");

        foreach (var tdfPath in BmsTheaterPaths.ListTdfFiles(dataDir))
        {
            if (!File.Exists(tdfPath)) continue;

            try
            {
                var theater = ParseTdfFile(dataDir, tdfPath);
                if (theater != null) result.Add(theater);
            }
            catch { /* skip this theater, continue with others */ }
        }

        return result;
    }

    private static TheaterDefinition? ParseTdfFile(string dataDir, string tdfPath)
    {
        var tdf = BmsTdfParser.Parse(File.ReadAllText(tdfPath));
        string? name = tdf.GetValueOrDefault("name");
        string? terrainDir = tdf.GetValueOrDefault("terraindir");
        string? newTerrainDir = tdf.GetValueOrDefault("newterraindir");
        string? objectDir = tdf.GetValueOrDefault("objectdir");

        // A theater that sets neither folder keeps the folders of the theater that BMS loaded before it,
        // which we cannot know.
        if (string.IsNullOrEmpty(name) || (string.IsNullOrEmpty(terrainDir) && string.IsNullOrEmpty(newTerrainDir)))
            return null;

        var newTerrain = BmsTheaterPaths.NewTerrainDir(dataDir, newTerrainDir,
            string.IsNullOrEmpty(terrainDir) ? "" : BmsTheaterPaths.ResolveTdfDir(dataDir, terrainDir));
        var absObjectDir = string.IsNullOrEmpty(objectDir) ? null : BmsTheaterPaths.ResolveTdfDir(dataDir, objectDir);
        var files = TheaterTerrainFiles.FromNewTerrainDir(newTerrain, absObjectDir);
        if (!File.Exists(files.HeightmapPath) || files.TheaterTxtPath == null) return null;

        var theaterTxt = TheaterTxt.Read(files.TheaterTxtPath);
        if (theaterTxt.Projection == null) return null;

        return new TheaterDefinition(name, theaterTxt.Projection, theaterTxt.CenterLat ?? 0, theaterTxt.CenterLon ?? 0,
            files.HeightmapPath, (theaterTxt.SizeKm ?? 1024) * 1000.0, absObjectDir);
    }
}
