using System.Diagnostics;
using System.Globalization;
using Serilog;
using Serilog.Configuration;
using Serilog.Core;
using Serilog.Events;

namespace OpenFreq.Common.Logging;

/// <summary>
/// Adds <c>Elapsed</c>, the seconds since this enricher was made, to each log event.
/// Serilog runs enrichers on the thread that logs, when it makes the event,
/// so the stamp is as accurate as <c>{Timestamp}</c>.
/// </summary>
public sealed class ElapsedEnricher : ILogEventEnricher
{
    private readonly long _start = Stopwatch.GetTimestamp();

    public void Enrich(LogEvent logEvent, ILogEventPropertyFactory propertyFactory) =>
        logEvent.AddPropertyIfAbsent(new LogEventProperty("Elapsed",
            new ScalarValue(Stopwatch.GetElapsedTime(_start).TotalSeconds)));
}

/// <summary>
/// The log file setup that the client and the server share. tools/stepper reads the files of both,
/// so they must use the same line format.
/// </summary>
public static class SessionLog
{
    /// <summary>
    /// Each line starts with a monotonic stamp from <see cref="ElapsedEnricher"/>.
    /// The wall time appears once, in the start line.
    /// </summary>
    public const string FileTemplate =
        "{Elapsed:F3} [{Level:u3}] [{SourceContext}] {Message:lj}{NewLine}{Exception}";

    /// <summary>
    /// A new log file path for this process, for example <c>openfreq-client-20260926-142120-12345.log</c>.
    /// </summary>
    /// <remarks>
    /// The PID keeps two copies that start at the same time apart. The PID alone is not sufficient,
    /// because Windows reuses PIDs and Serilog appends to a file that already exists.
    /// The start time prevents that, and the names sort by time.
    /// </remarks>
    public static string CreatePath(string logsDirectory, string app) =>
        Path.Combine(logsDirectory, string.Create(CultureInfo.InvariantCulture,
            $"openfreq-{app}-{DateTime.Now:yyyyMMdd-HHmmss}-{Environment.ProcessId}.log"));

    /// <summary>
    /// Deletes each <c>*.log</c> file in <paramref name="logsDirectory"/> that was last written
    /// more than <paramref name="maxAge"/> ago.
    /// </summary>
    /// <remarks>
    /// Serilog's <c>retainedFileCountLimit</c> cannot do this. It deletes only the files with the same base name,
    /// and each process gives a new base name.
    /// </remarks>
    public static void DeleteOld(string logsDirectory, TimeSpan maxAge)
    {
        var cutoff = DateTime.UtcNow - maxAge;
        foreach (var file in Directory.EnumerateFiles(logsDirectory, "*.log"))
        {
            try
            {
                if (File.GetLastWriteTimeUtc(file) < cutoff)
                    File.Delete(file);
            }
            // A file that another running copy has open can fail to delete.
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
            }
        }
    }

    /// <summary>
    /// Writes a log file in the <see cref="FileTemplate"/> format. Add an <see cref="ElapsedEnricher"/> to the logger too.
    /// </summary>
    /// <remarks>
    /// Serilog 4 already uses the invariant culture when it has no format provider.
    /// The explicit provider makes that a stated rule, because tools/stepper cannot read <c>139,700 MHz</c>.
    /// </remarks>
    public static LoggerConfiguration SessionFile(this LoggerSinkConfiguration writeTo, string path) =>
        writeTo.File(path, outputTemplate: FileTemplate, formatProvider: CultureInfo.InvariantCulture);
}
