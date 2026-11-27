using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Microsoft.Extensions.Logging;
using OpenFreqAudio.TerrainSampling;

namespace OpenFreq.Client.Terrain;

/// <summary>
/// What BMS exports to shared memory about the theater that it runs.
/// </summary>
/// <param name="TerrainDir">The theater's terrain folder (ThrTerraindir)</param>
/// <param name="Name">The theater's name (ThrName)</param>
/// <param name="ObjectDir">The theater's objects folder (ThrObjectdir)</param>
/// <param name="DataDir">The BMS Data folder (BmsDataDirectory)</param>
public sealed record BmsTheaterInfo(string TerrainDir, string? Name, string? ObjectDir, string? DataDir);

/// <summary>
/// Finds the folders of BMS theaters with the same rules as BMS.
/// </summary>
public static class BmsTheaterPaths
{
    /// <summary>
    /// The .tdf files that theater.lst lists, as full paths.
    /// </summary>
    public static IEnumerable<string> ListTdfFiles(string dataDir)
    {
        var lstPath = Path.Combine(dataDir, "TerrData", "TheaterDefinition", "theater.lst");
        string[] lines;
        try { lines = File.ReadAllLines(lstPath); }
        catch { return []; }

        return lines
            .Select(l => l.Trim())
            .Where(l => l.Length > 0 && l[0] is not ('#' or ';'))
            .Select(l => Path.Combine(dataDir, l));
    }

    /// <summary>
    /// Finds the theater in theater.lst with the given name, and parses its .tdf.
    /// Like BMS, the name test does not depend on case, and the first match wins.
    /// </summary>
    /// <returns>The .tdf keys and values, or null if no .tdf has the name</returns>
    public static Dictionary<string, string>? FindTdf(string dataDir, string theaterName)
    {
        foreach (var tdfPath in ListTdfFiles(dataDir))
        {
            Dictionary<string, string> tdf;
            try { tdf = BmsTdfParser.Parse(File.ReadAllText(tdfPath)); }
            catch { continue; }
            if (tdf.TryGetValue("name", out var name) && string.Equals(name, theaterName, StringComparison.OrdinalIgnoreCase))
            {
                return tdf;
            }
        }
        return null;
    }

    /// <summary>
    /// Joins a folder value from a .tdf (such as terraindir or objectdir) to the Data folder.
    /// Like BMS, a value that starts with a drive letter or "//" is a full path.
    /// </summary>
    public static string ResolveTdfDir(string dataDir, string value)
    {
        bool isFullPath = (value.Length >= 2 && char.IsAsciiLetter(value[0]) && value[1] == ':')
                          || value.StartsWith("//", StringComparison.Ordinal);
        return isFullPath ? value : Path.Join(dataDir, value);
    }

    /// <summary>
    /// The theater's NewTerrain folder.
    /// BMS uses newterraindir as it is written, and its working folder is the Data folder.
    /// If newterraindir is empty, BMS uses the NewTerrain folder in the theater's terrain folder.
    /// </summary>
    /// <param name="terrainDir">The theater's terrain folder, joined to the Data folder</param>
    public static string NewTerrainDir(string dataDir, string? newTerrainDirValue, string terrainDir)
        => string.IsNullOrEmpty(newTerrainDirValue)
            ? Path.Combine(terrainDir, "NewTerrain")
            : Path.Combine(dataDir, newTerrainDirValue);

    /// <summary>
    /// Finds the terrain files of the theater that BMS runs.
    /// </summary>
    /// <param name="theater">What BMS exports to shared memory</param>
    /// <param name="bmsBaseDir">
    /// The BMS folder from the registry. We use its Data folder if BMS does not export one.
    /// </param>
    public static TheaterTerrainFiles Resolve(BmsTheaterInfo theater, string? bmsBaseDir, ILogger logger)
    {
        string? dataDir = !string.IsNullOrEmpty(theater.DataDir) ? theater.DataDir
            : bmsBaseDir != null ? Path.Combine(bmsBaseDir, "Data")
            : null;

        string? newTerrainDirValue = null;
        if (dataDir == null || string.IsNullOrEmpty(theater.Name))
        {
            logger.LogWarning("BMS did not give its Data folder or theater name. Using {TerrainDir}\\NewTerrain.",
                theater.TerrainDir);
        }
        else if (FindTdf(dataDir, theater.Name) is { } tdf)
        {
            tdf.TryGetValue("newterraindir", out newTerrainDirValue);
        }
        else
        {
            logger.LogWarning("Could not find the .tdf of theater {Theater} in {DataDir}. Using {TerrainDir}\\NewTerrain.",
                theater.Name, dataDir, theater.TerrainDir);
        }

        string newTerrainDir = NewTerrainDir(dataDir ?? "", newTerrainDirValue, theater.TerrainDir);
        return TheaterTerrainFiles.FromNewTerrainDir(newTerrainDir,
            string.IsNullOrEmpty(theater.ObjectDir) ? null : theater.ObjectDir);
    }
}
