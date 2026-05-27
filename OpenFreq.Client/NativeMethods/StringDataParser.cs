using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;

namespace OpenFreq.Client.NativeMethods;

/// <summary>
/// Parser for Falcon BMS StringData shared memory area
/// </summary>
public static class StringDataParser
{
    // StringIdentifier enum values (BMS 4.38 FlightData.h)
    private const uint ThrTerraindir = 15;
    private const uint AcName = 29;
    private const uint AcNCTR = 30;

    /// <summary>
    /// Parses the StringData shared memory and extracts the theater terrain directory
    /// </summary>
    /// <param name="baseAddress">Base address of the mapped string data memory</param>
    /// <returns>Theater terrain directory string, or null if not found</returns>
    public static string? ParseTheaterTerrainDir(IntPtr baseAddress)
    {
        if (baseAddress == IntPtr.Zero)
            return null;

        try
        {
            int offset = 0;

            // Read VersionNum (uint32)
            uint versionNum = (uint)Marshal.ReadInt32(baseAddress, offset);
            offset += 4;

            // Read NoOfStrings (uint32)
            uint noOfStrings = (uint)Marshal.ReadInt32(baseAddress, offset);
            offset += 4;

            // Read dataSize (uint32)
            uint dataSize = (uint)Marshal.ReadInt32(baseAddress, offset);
            offset += 4;

            // Iterate through all strings to find ThrTerraindir
            for (int i = 0; i < noOfStrings; i++)
            {
                // Read strId (uint32)
                uint strId = (uint)Marshal.ReadInt32(baseAddress, offset);
                offset += 4;

                // Read strLength (uint32)
                uint strLength = (uint)Marshal.ReadInt32(baseAddress, offset);
                offset += 4;

                // If this is ThrTerraindir, read and return it
                if (strId == ThrTerraindir)
                {
                    // Read the null-terminated string
                    byte[] stringBytes = new byte[strLength];
                    Marshal.Copy(baseAddress + offset, stringBytes, 0, (int)strLength);

                    // Convert to string (ASCII/UTF-8)
                    return Encoding.UTF8.GetString(stringBytes).TrimEnd('\0');
                }

                // Skip this string's data (strLength + 1 for null terminator)
                offset += (int)strLength + 1;
            }

            // ThrTerraindir not found
            return null;
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>
    /// Parses AcName and AcNCTR from the StringData shared memory in a single pass.
    /// </summary>
    /// <param name="baseAddress">Base address of the mapped string data memory</param>
    /// <returns>Tuple of (AcName, AcNCTR), either may be null if not present</returns>
    public static (string? acName, string? acNctr) ParseAircraftInfo(IntPtr baseAddress)
    {
        if (baseAddress == IntPtr.Zero)
            return (null, null);

        try
        {
            int offset = 0;

            // Read VersionNum (uint32)
            offset += 4;

            // Read NoOfStrings (uint32)
            uint noOfStrings = (uint)Marshal.ReadInt32(baseAddress, offset);
            offset += 4;

            // Read dataSize (uint32)
            offset += 4;

            string? acName = null;
            string? acNctr = null;

            for (int i = 0; i < noOfStrings; i++)
            {
                uint strId = (uint)Marshal.ReadInt32(baseAddress, offset);
                offset += 4;

                uint strLength = (uint)Marshal.ReadInt32(baseAddress, offset);
                offset += 4;

                if (strId == AcName || strId == AcNCTR)
                {
                    byte[] stringBytes = new byte[strLength];
                    Marshal.Copy(baseAddress + offset, stringBytes, 0, (int)strLength);
                    var value = Encoding.UTF8.GetString(stringBytes).TrimEnd('\0');

                    if (strId == AcName) acName = value;
                    else acNctr = value;

                    if (acName != null && acNctr != null) break;
                }

                offset += (int)strLength + 1;
            }

            return (acName, acNctr);
        }
        catch
        {
            return (null, null);
        }
    }

    /// <summary>
    /// Gets all strings from the StringData shared memory (for debugging)
    /// </summary>
    public static Dictionary<uint, string> ParseAllStrings(IntPtr baseAddress)
    {
        var result = new Dictionary<uint, string>();

        if (baseAddress == IntPtr.Zero)
            return result;

        try
        {
            int offset = 0;

            uint versionNum = (uint)Marshal.ReadInt32(baseAddress, offset);
            offset += 4;

            uint noOfStrings = (uint)Marshal.ReadInt32(baseAddress, offset);
            offset += 4;

            uint dataSize = (uint)Marshal.ReadInt32(baseAddress, offset);
            offset += 4;

            for (int i = 0; i < noOfStrings; i++)
            {
                uint strId = (uint)Marshal.ReadInt32(baseAddress, offset);
                offset += 4;

                uint strLength = (uint)Marshal.ReadInt32(baseAddress, offset);
                offset += 4;

                byte[] stringBytes = new byte[strLength];
                Marshal.Copy(baseAddress + offset, stringBytes, 0, (int)strLength);

                string value = Encoding.UTF8.GetString(stringBytes).TrimEnd('\0');
                result[strId] = value;

                offset += (int)strLength + 1;
            }
        }

#pragma warning disable RCS1075
        catch (Exception)
        {
            // Return partial results
        }
#pragma warning restore RCS1075

        return result;
    }
}
