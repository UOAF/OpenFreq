using System.Reflection;
using Semver;

namespace OpenFreq.Common;

/// <summary>
/// The application version shared between client and server for compatibility checks (set via Directory.Build.props / CI).
/// </summary>
public static class OpenFreqVersion
{
    public static string Current { get; } =
        (Assembly.GetEntryAssembly() ?? typeof(OpenFreqVersion).Assembly)
        .GetCustomAttribute<AssemblyInformationalVersionAttribute>()
        ?.InformationalVersion ?? "unknown";

    /// <summary>
    /// Two versions are compatible when they are equal, or when both parse as semver and share major.minor.
    /// </summary>
    public static bool AreCompatible(string? clientVersion, string? serverVersion)
    {
        if (clientVersion == null || serverVersion == null)
            return false;

        if (clientVersion == serverVersion)
            return true;

        return TryGetMajorMinor(clientVersion, out var clientMajor, out var clientMinor)
               && TryGetMajorMinor(serverVersion, out var serverMajor, out var serverMinor)
               && clientMajor == serverMajor
               && clientMinor == serverMinor;
    }

    /// <summary>
    /// Parses the major.minor components of a semver string ("1.2.3", "1.2.3-pre", "1.2.3+sha").
    /// </summary>
    public static bool TryGetMajorMinor(string version, out int major, out int minor)
    {
        if (SemVersion.TryParse(version, SemVersionStyles.Strict, out var parsed))
        {
            major = (int)parsed.Major;
            minor = (int)parsed.Minor;
            return true;
        }

        major = 0;
        minor = 0;
        return false;
    }
}

/// <summary>
/// Thrown when the server rejects authentication because the client/server versions dont match.
/// </summary>
#pragma warning disable RCS1194
public class VersionMismatchException(string clientVersion, string serverVersion)
    : Exception($"Version mismatch: client {clientVersion}, server {serverVersion}")
{
    public string ClientVersion { get; } = clientVersion;
    public string ServerVersion { get; } = serverVersion;
}
#pragma warning restore RCS1194
