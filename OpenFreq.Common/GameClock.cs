using System.Globalization;

namespace OpenFreq.Common;

/// <summary>
/// Formats the in-game time of day for log lines, so testers can find the same moment in their in-game recordings.
/// </summary>
public static class GameClock
{
    /// <summary>
    /// ", game time HH:mm:ss" to append to a log line, or nothing when there is no game clock.
    /// </summary>
    public static string LogSuffix(int? secondsOfDay) =>
        secondsOfDay is { } seconds
            ? ", game time " + TimeSpan.FromSeconds(seconds).ToString(@"hh\:mm\:ss", CultureInfo.InvariantCulture)
            : "";
}
