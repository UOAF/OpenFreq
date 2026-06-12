using System.Collections.Concurrent;
using System.Reflection;
using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;
using Serilog;
using Serilog.Events;

namespace OpenFreqServer;

static class Program
{
    static async Task Main(string[] args)
    {
        var version = Assembly.GetExecutingAssembly()
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()
            ?.InformationalVersion ?? "unknown";

        if (args.Any(a => a is "-h" or "--help"))
        {
            PrintHelp(version);
            return;
        }

        // Headless mode: no TUI, logs to console + file
        var headlessMode = args.Any(a => a is "-a" or "--headless");

        // Initialize Serilog for file logging
        var baseDirectory = Path.GetDirectoryName(Environment.ProcessPath) ?? AppContext.BaseDirectory;
        var logsDirectory = Path.Combine(baseDirectory, "logs");
        Directory.CreateDirectory(logsDirectory);

        var logFile = Path.Combine(logsDirectory, $"openfreq-{DateTime.Now:yyyy-MM-dd}.log");

        var loggerConfiguration = new LoggerConfiguration()
#if DEBUG
            .MinimumLevel.Debug()
#else
            .MinimumLevel.Information()
#endif
            .MinimumLevel.Override("Microsoft", LogEventLevel.Warning)
            .MinimumLevel.Override("System", LogEventLevel.Warning)
            .Enrich.FromLogContext()
            .Enrich.WithProperty("Application", "OpenFreqServer")
            .WriteTo.File(
                logFile,
                outputTemplate:
                "{Timestamp:yyyy-MM-dd HH:mm:ss.fff zzz} [{Level:u3}] [{SourceContext}] {Message:lj}{NewLine}{Exception}",
                rollingInterval: RollingInterval.Day,
                retainedFileCountLimit: 30,
                shared: true);

        if (headlessMode)
        {
            // No TUI to display logs, so mirror them to stdout
            loggerConfiguration.WriteTo.Console(
                outputTemplate: "{Timestamp:HH:mm:ss.fff} [{Level:u3}] [{SourceContext}] {Message:lj}{NewLine}{Exception}");
        }

        Log.Logger = loggerConfiguration.CreateLogger();

        try
        {
            Console.WriteLine($"OpenFreq Server {version} - Loading configuration...");

            // Load configuration
            var config = LoadConfiguration();
            if (config == null)
            {
                Console.WriteLine("Failed to load configuration. Exiting.");
                Log.Fatal("Failed to load configuration");
                return;
            }

            Log.Information("OpenFreq Server {Version} starting", version);
            Log.Information("Server starting with configuration: Port={Port}, MaxClients={MaxClients}",
                config.WebSocketPort, config.MaxClientsPerChannel);

            // Setup logging infrastructure
            var logMessages = new ConcurrentQueue<TuiLogMessage>();

            using var loggerFactory = LoggerFactory.Create(builder =>
            {
                builder
                    .AddFilter("Microsoft", LogLevel.Warning)
                    .AddFilter("System", LogLevel.Warning)
                    .AddFilter("OpenFreq", LogLevel.Debug)
                    .AddSerilog(Log.Logger);

                if (!headlessMode)
                {
                    builder.AddProvider(new TuiLoggerProvider(logMessages));
                }
            });

            SignalingServer server;
            try
            {
                server = new SignalingServer(config, loggerFactory);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Can not start server, check your ports are not in use: {config.WebSocketPort}, {config.AudioPort}");
                Console.WriteLine($"Error: {ex.Message}");
                Log.Fatal(ex, "Failed to initialize server");
                WaitForKeyBeforeExit(headlessMode);
                return;
            }

            // Create stats tracker and Terminal.Gui TUI (skipped in headless mode)
            using TerminalGuiServer? tui = headlessMode
                ? null
                : new TerminalGuiServer(config,
                    new ServerStats(server.Clients, server.ChannelManager, server.AudioServer),
                    logMessages, version);

            // Setup graceful shutdown
            var shutdownCts = new CancellationTokenSource();
            Console.CancelKeyPress += (_, e) =>
            {
                if (!shutdownCts.IsCancellationRequested)
                {
                    e.Cancel = true;
                    Log.Information("Shutdown requested by user");
                    shutdownCts.Cancel(); // Signal shutdown, don't block
                }
            };

            // SIGTERM (systemd stop, Windows console close) — request graceful shutdown
            using var sigtermRegistration = PosixSignalRegistration.Create(PosixSignal.SIGTERM, context =>
            {
                context.Cancel = true;
                if (!shutdownCts.IsCancellationRequested)
                {
                    Log.Information("SIGTERM received");
                    shutdownCts.Cancel();
                }
            });

            // Start server (binds ports — throws on failure)
            try
            {
                await server.StartAsync();
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Can not start server, check your ports are not in use: {config.WebSocketPort}, {config.AudioPort}");
                Console.WriteLine($"Error: {ex.Message}");
                Log.Fatal(ex, "Failed to start server");
                WaitForKeyBeforeExit(headlessMode);
                return;
            }

            if (tui != null)
            {
                // Start TUI (blocks until quit or shutdown requested)
                var tuiTask = Task.Run(() => tui.Start());

                // Wait for either TUI to quit or Ctrl+C
                await Task.WhenAny(tuiTask, Task.Delay(-1, shutdownCts.Token).ContinueWith(_ => { }));
            }
            else
            {
                Log.Information("Running in headless mode, press Ctrl+C to stop");
                await Task.Delay(-1, shutdownCts.Token).ContinueWith(_ => { });
            }

            // Now properly shut down
            Log.Information("Shutting down server...");
            await server.StopAsync();
            tui?.Stop();
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Fatal error: {ex.Message}");
            Log.Fatal(ex, "Unhandled exception");
        }
        finally
        {
            Log.CloseAndFlush();
        }
    }

    static void PrintHelp(string version)
    {
        Console.WriteLine($"""
            OpenFreq Server {version}
            Signaling and audio relay server for OpenFreq voice communication.

            Usage: OpenFreq.Server [options]

            Options:
              -a, --headless    Run headless (no TUI), logs to console and file
              -h, --help      Show this help and exit

            Configuration:
              Read from OpenFreq.Server.json next to the executable; a default
              config is created on first run.

            Logs are written to the logs/ directory (daily rolling files).
            """);
    }

    static void WaitForKeyBeforeExit(bool headlessMode)
    {
        // No interactive console in headless mode (and ReadKey throws when stdin is redirected)
        if (headlessMode || Console.IsInputRedirected)
            return;

        Console.WriteLine("Press any key to exit...");
        Console.ReadKey(intercept: true);
    }

    static ServerConfig? LoadConfiguration()
    {
        try
        {
            var configPath = Path.Combine(
                Path.GetDirectoryName(Environment.ProcessPath) ?? AppContext.BaseDirectory,
                "OpenFreq.Server.json");

            if (!File.Exists(configPath))
            {
                Log.Information("Configuration file not found at: {ConfigPath}, creating default", configPath);

                var defaultConfig = new ServerConfig
                {
                    ServerPassword = "",
                    WebSocketPort = 9987,
                    AudioPort = 9988,
                    MaxClientsPerChannel = 50,
                    MaxChannelsPerClient = 10,
                    EnableOpusCompression = true,
                    BroadcastPeerUpdates = true
                };

                var json = Json.Json.Instance.Serialize(defaultConfig);

                File.WriteAllText(configPath, json);
                Log.Information("Default configuration created at: {ConfigPath}", configPath);
                return defaultConfig;
            }

            var configJson = File.ReadAllText(configPath);
            var config = Json.Json.Instance.Deserialize<ServerConfig>(configJson);

            if (config == null)
            {
                Log.Error("Failed to parse configuration file");
                return null;
            }

            return config;
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Error loading configuration");
            return null;
        }
    }
}
