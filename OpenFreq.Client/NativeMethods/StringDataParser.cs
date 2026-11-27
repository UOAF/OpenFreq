using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;
using OpenFreq.Client.Terrain;

namespace OpenFreq.Client.NativeMethods;

/// <summary>
/// Parser for Falcon BMS StringData shared memory area
/// </summary>
public static class StringDataParser
{
    // StringIdentifier enum values (BMS 4.38 FlightData.h)
    private const uint BmsDataDirectory = 4;
    private const uint ThrName = 13;
    private const uint ThrTerraindir = 15;
    private const uint ThrObjectdir = 19;
    private const uint AcName = 29;
    private const uint AcNCTR = 30;

    /// <summary>
    /// Parses the StringData shared memory and extracts what BMS tells us about the current theater
    /// </summary>
    /// <param name="baseAddress">Base address of the mapped string data memory</param>
    /// <returns>The theater info, or null if the theater terrain directory is not there (yet)</returns>
    public static BmsTheaterInfo? ParseTheaterInfo(IntPtr baseAddress)
    {
        var strings = ReadStrings(baseAddress, [BmsDataDirectory, ThrName, ThrTerraindir, ThrObjectdir]);
        if (!strings.TryGetValue(ThrTerraindir, out var terrainDir) || string.IsNullOrEmpty(terrainDir))
            return null;

        return new BmsTheaterInfo(terrainDir, strings.GetValueOrDefault(ThrName),
            strings.GetValueOrDefault(ThrObjectdir), strings.GetValueOrDefault(BmsDataDirectory));
    }

    /// <summary>
    /// Parses AcName and AcNCTR from the StringData shared memory in a single pass.
    /// </summary>
    /// <param name="baseAddress">Base address of the mapped string data memory</param>
    /// <returns>Tuple of (AcName, AcNCTR), either may be null if not present</returns>
    public static (string? acName, string? acNctr) ParseAircraftInfo(IntPtr baseAddress)
    {
        var strings = ReadStrings(baseAddress, [AcName, AcNCTR]);
        return (strings.GetValueOrDefault(AcName), strings.GetValueOrDefault(AcNCTR));
    }

    /// <summary>
    /// Reads the strings with the given IDs in a single pass.
    /// </summary>
    /// <returns>The strings that were found, by ID. Empty if the memory could not be read.</returns>
    private static Dictionary<uint, string> ReadStrings(IntPtr baseAddress, uint[] ids)
    {
        var result = new Dictionary<uint, string>();
        if (baseAddress == IntPtr.Zero)
            return result;

        try
        {
            // Skip VersionNum (uint32)
            int offset = 4;

            // Read NoOfStrings (uint32)
            uint noOfStrings = (uint)Marshal.ReadInt32(baseAddress, offset);
            offset += 4;

            // Skip dataSize (uint32)
            offset += 4;

            for (int i = 0; i < noOfStrings && result.Count < ids.Length; i++)
            {
                uint strId = (uint)Marshal.ReadInt32(baseAddress, offset);
                offset += 4;

                uint strLength = (uint)Marshal.ReadInt32(baseAddress, offset);
                offset += 4;

                if (Array.IndexOf(ids, strId) >= 0)
                {
                    byte[] stringBytes = new byte[strLength];
                    Marshal.Copy(baseAddress + offset, stringBytes, 0, (int)strLength);
                    result[strId] = Encoding.UTF8.GetString(stringBytes).TrimEnd('\0');
                }

                // Skip this string's data (strLength + 1 for null terminator)
                offset += (int)strLength + 1;
            }

            return result;
        }
        catch
        {
            return [];
        }
    }
}
