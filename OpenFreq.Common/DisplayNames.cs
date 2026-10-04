namespace OpenFreq.Common;

/// <summary>
/// The server and the client log display names with the same rule,
/// so that the two logs show the same name for the same PTT.
/// </summary>
public static class DisplayNames
{
    public static string ForLog(string? displayName) =>
        !string.IsNullOrWhiteSpace(displayName) ? displayName : "Unnamed";
}
