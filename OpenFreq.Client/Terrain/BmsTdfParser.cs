using System;
using System.Collections.Generic;

namespace OpenFreq.Client.Terrain;

/// <summary>
/// Reads BMS theater definition (.tdf) files with the same rules as BMS.
/// </summary>
/// <remarks>
/// - BMS cuts each line at its first CR.
/// - It skips a line that starts with '#', ';' or "//".
/// - The key is the first word. BMS matches keys without regard to case.
/// - The value is the rest of the line after the white space, without spaces or tabs at its ends.
///   A value can contain spaces.
/// - BMS ignores a line that has a key but no value after it.
/// - A later line overwrites an earlier line with the same key.
/// </remarks>
public static class BmsTdfParser
{
    public static Dictionary<string, string> Parse(string text)
    {
        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var rawLine in text.Split('\n'))
        {
            int cr = rawLine.IndexOf('\r');
            string line = cr < 0 ? rawLine : rawLine[..cr];
            if (line.Length == 0 || line[0] is '#' or ';' || line.StartsWith("//", StringComparison.Ordinal))
            {
                continue;
            }

            int keyStart = 0;
            while (keyStart < line.Length && IsSpace(line[keyStart])) keyStart++;
            int keyEnd = keyStart;
            while (keyEnd < line.Length && !IsSpace(line[keyEnd])) keyEnd++;
            if (keyEnd == keyStart || keyEnd == line.Length) continue;

            int valueStart = keyEnd;
            while (valueStart < line.Length && IsSpace(line[valueStart])) valueStart++;
            values[line[keyStart..keyEnd]] = line[valueStart..].Trim(' ', '\t');
        }
        return values;
    }

    // The C "isspace" set
    private static bool IsSpace(char c) => c is ' ' or '\t' or '\n' or '\v' or '\f' or '\r';
}
