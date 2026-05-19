using System;
using System.IO;
using Avalonia;
using Microsoft.Extensions.DependencyInjection;
using OpenFreqClient.Services;
using Serilog;
using Serilog.Events;

namespace OpenFreqClient;

sealed class Program
{
    public static IServiceProvider? ServiceProvider { get; private set; }
    // Initialization code. Don't use any Avalonia, third-party APIs or any
    // SynchronizationContext-reliant code before AppMain is called: things aren't initialized
    // yet and stuff might break.
    [STAThread]
    public static void Main(string[] args)
    {
        // Initialize Serilog for file logging
        var exeDir = Path.GetDirectoryName(Environment.ProcessPath) ?? AppContext.BaseDirectory;
        var logsDirectory = Path.Combine(exeDir, "logs");
        Directory.CreateDirectory(logsDirectory);

        var logFile = Path.Combine(logsDirectory, $"openfreq-client-{DateTime.Now:yyyy-MM-dd}.log");

#if DEBUG
        Log.Logger = new LoggerConfiguration()
            .MinimumLevel.Debug()
            .MinimumLevel.Override("Microsoft", LogEventLevel.Warning)
            .MinimumLevel.Override("System", LogEventLevel.Warning)


            // OVERRIDES - Disables Debug Logs
            // --------------------------------
            // Outputs the Playback Buffer State
            .MinimumLevel.Override("OpenFreqAudio.RadioPlayback", LogEventLevel.Warning)

            // Outputs the Physics Calculations
            .MinimumLevel.Override("OpenFreqAudio.FastPathAudioSim", LogEventLevel.Warning)

            // Outputs the packet timings (playback queue)
            .MinimumLevel.Override("OpenFreq.Common.RtpAudioReceiver", LogEventLevel.Warning)
            
            // Outputs the RTP receiver
            .MinimumLevel.Override("OpenFreq.Common.RtpSourceContext", LogEventLevel.Warning)


            .Enrich.FromLogContext()
            .Enrich.WithProperty("Application", "OpenFreqClient")
            .WriteTo.File(
                logFile,
                outputTemplate: "{Timestamp:yyyy-MM-dd HH:mm:ss.fff zzz} [{Level:u3}] [{SourceContext}] {Message:lj}{NewLine}{Exception}",
                rollingInterval: RollingInterval.Day,
                retainedFileCountLimit: 30,
                shared: true)
            .WriteTo.Console(outputTemplate: "[{Timestamp:HH:mm:ss.fff} {Level:u3}] [{SourceContext}] {Message:lj}{NewLine}{Exception}")
            .CreateLogger();
#else
        Log.Logger = new LoggerConfiguration()
            .MinimumLevel.Information()
            .MinimumLevel.Override("Microsoft", LogEventLevel.Warning)
            .MinimumLevel.Override("System", LogEventLevel.Warning)
            .Enrich.FromLogContext()
            .Enrich.WithProperty("Application", "OpenFreqClient")
            .WriteTo.File(
                logFile,
                outputTemplate: "{Timestamp:yyyy-MM-dd HH:mm:ss.fff zzz} [{Level:u3}] [{SourceContext}] {Message:lj}{NewLine}{Exception}",
                rollingInterval: RollingInterval.Day,
                retainedFileCountLimit: 30,
                shared: true)
            .CreateLogger();
#endif

        // Set up dependency injection
        var services = new ServiceCollection();
        services.AddOpenFreqServices();
        ServiceProvider = services.BuildServiceProvider();

        BuildAvaloniaApp()
            .StartWithClassicDesktopLifetime(args);
    }

    // Avalonia configuration, don't remove; also used by visual designer.
    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .WithInterFont()
            .LogToTrace();
}
