using System.Globalization;
using System.Text.RegularExpressions;
using OpenFreq.Common.Logging;
using Serilog;

namespace OpenFreq.Common.Tests;

public sealed class SessionLogTests : IDisposable
{
    private readonly string _directory = Directory.CreateTempSubdirectory("openfreq-session-log-").FullName;

    public void Dispose() => Directory.Delete(_directory, recursive: true);

    [Fact]
    public void CreatePath_HasAppStartTimeAndPid()
    {
        var before = DateTime.Now;
        var path = SessionLog.CreatePath(_directory, "client");
        var after = DateTime.Now;

        Assert.Equal(_directory, Path.GetDirectoryName(path));
        var match = Regex.Match(Path.GetFileName(path), @"^openfreq-client-(\d{8}-\d{6})-(\d+)\.log$");
        Assert.True(match.Success, path);

        var started = DateTime.ParseExact(match.Groups[1].Value, "yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);
        // The name has whole seconds.
        Assert.InRange(started, new DateTime(before.Ticks - before.Ticks % TimeSpan.TicksPerSecond), after);
        Assert.Equal(Environment.ProcessId, int.Parse(match.Groups[2].Value, CultureInfo.InvariantCulture));
    }

    [Fact]
    public void DeleteOld_DeletesOnlyOldLogFiles()
    {
        var oldLog = CreateFile("openfreq-client-2026-07-0720260707.log", DateTime.UtcNow.AddDays(-31));
        var newLog = CreateFile("openfreq-client-20260926-142120-12345.log", DateTime.UtcNow.AddDays(-29));
        var oldOther = CreateFile("notes.txt", DateTime.UtcNow.AddDays(-31));

        SessionLog.DeleteOld(_directory, TimeSpan.FromDays(30));

        Assert.False(File.Exists(oldLog));
        Assert.True(File.Exists(newLog));
        Assert.True(File.Exists(oldOther));
    }

    [Fact]
    public void SessionFile_WritesInvariantNumbersInAnyCulture()
    {
        var path = Path.Combine(_directory, "culture.log");
        var culture = CultureInfo.CurrentCulture;
        CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("de-DE");
        try
        {
            // Make sure that the culture applies. Without ICU, .NET can use the invariant culture.
            Assert.Equal("139,700", 139.7.ToString("F3"));

            using var logger = new LoggerConfiguration()
                .Enrich.With(new ElapsedEnricher())
                .WriteTo.SessionFile(path)
                .CreateLogger();
            logger.Information("PTT start: Bob ({PeerId}) on {FreqMhz:F3} MHz", "id", 139.7);
        }
        finally
        {
            CultureInfo.CurrentCulture = culture;
        }

        // Without the enricher, Serilog writes "{Elapsed:F3}" as text.
        Assert.Matches(@"^\d+\.\d{3} \[INF\] \[\] PTT start: Bob \(id\) on 139\.700 MHz\r?\n$", File.ReadAllText(path));
    }

    private string CreateFile(string name, DateTime lastWriteUtc)
    {
        var path = Path.Combine(_directory, name);
        File.WriteAllText(path, "");
        File.SetLastWriteTimeUtc(path, lastWriteUtc);
        return path;
    }
}
