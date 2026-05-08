using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Runtime.Versioning;

namespace OpenFreqClient.Services;

public record BmsInstalledTheater(
    string Name,
    string HeightmapPath,
    string ProjString,
    double CenterLat,
    double CenterLon
);

public static class BmsDetectionService
{
    private const string RegistryKey = @"SOFTWARE\WOW6432Node\Benchmark Sims\Falcon BMS 4.38";

    public static string? GetBmsDirectory()
    {
        if (!OperatingSystem.IsWindows()) return null;
        return GetBmsDirectoryWindows();
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

    public static List<BmsInstalledTheater> GetInstalledTheaters(string bmsDirectory)
    {
        var result = new List<BmsInstalledTheater>();
        var lstPath = Path.Combine(bmsDirectory, "Data", "TerrData", "TheaterDefinition", "theater.lst");
        if (!File.Exists(lstPath)) return result;

        foreach (var rawLine in File.ReadAllLines(lstPath))
        {
            var line = rawLine.Trim();
            if (string.IsNullOrEmpty(line) || line.StartsWith('#')) continue;

            var tdfPath = Path.Combine(bmsDirectory, "Data", line);
            if (!File.Exists(tdfPath)) continue;

            var theater = ParseTdfFile(bmsDirectory, tdfPath);
            if (theater != null) result.Add(theater);
        }

        return result;
    }

    private static BmsInstalledTheater? ParseTdfFile(string bmsDirectory, string tdfPath)
    {
        string? name = null, newTerrainDir = null, terrainDir = null;

        foreach (var rawLine in File.ReadAllLines(tdfPath))
        {
            var line = rawLine.Trim();
            if (line.StartsWith('#') || !line.Contains(' ')) continue;

            var spaceIdx = line.IndexOf(' ');
            var key = line[..spaceIdx];
            var value = line[(spaceIdx + 1)..].Trim();

            switch (key)
            {
                case "name": name = value; break;
                case "newterraindir": newTerrainDir = value; break;
                case "terraindir":
                    if (terrainDir == null) terrainDir = value;
                    break;
            }
        }

        if (name == null || newTerrainDir == null || terrainDir == null) return null;

        var heightmapPath = Path.Combine(bmsDirectory, "Data", newTerrainDir, "HeightMap.raw");
        if (!File.Exists(heightmapPath)) return null;

        var theaterTxtPath = Path.Combine(bmsDirectory, "Data", terrainDir, "Theater.txt");
        return ParseTheaterTxt(name, heightmapPath, theaterTxtPath);
    }

    private static BmsInstalledTheater? ParseTheaterTxt(string name, string heightmapPath, string theaterTxtPath)
    {
        if (!File.Exists(theaterTxtPath)) return null;

        string? projString = null;
        double centerLat = 0, centerLon = 0;

        foreach (var line in File.ReadAllLines(theaterTxtPath))
        {
            if (line.StartsWith("Projection string="))
                projString = line["Projection string=".Length..].Trim();
            else if (line.StartsWith("Center latitude="))
                double.TryParse(line["Center latitude=".Length..].Trim(), CultureInfo.InvariantCulture, out centerLat);
            else if (line.StartsWith("Center longitude="))
                double.TryParse(line["Center longitude=".Length..].Trim(), CultureInfo.InvariantCulture, out centerLon);
        }

        return projString == null ? null : new BmsInstalledTheater(name, heightmapPath, projString, centerLat, centerLon);
    }
}
