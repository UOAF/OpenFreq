using System.Collections.Concurrent;
using System.Reflection;
using Microsoft.Extensions.Logging;
using Serilog;
using Serilog.Events;

namespace OpenFreqServer;

static class Program
{
    static async Task Main(string[] args)
    {
        // Initialize Serilog for file logging
        var logsDirectory = Path.Combine(AppContext.BaseDirectory, "logs");
        Directory.CreateDirectory(logsDirectory);

        var logFile = Path.Combine(logsDirectory, $"openfreq-{DateTime.Now:yyyy-MM-dd}.log");

#if DEBUG
        Log.Logger = new LoggerConfiguration()
            .MinimumLevel.Debug()
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
                shared: true)
            .CreateLogger();
#else
        Log.Logger = new LoggerConfiguration()
            .MinimumLevel.Information()
            .MinimumLevel.Override("Microsoft", LogEventLevel.Warning)
            .MinimumLevel.Override("System", LogEventLevel.Warning)
            .Enrich.FromLogContext()
            .Enrich.WithProperty("Application", "OpenFreqServer")
            .WriteTo.File(
                logFile,
                outputTemplate: "{Timestamp:yyyy-MM-dd HH:mm:ss.fff zzz} [{Level:u3}] [{SourceContext}] {Message:lj}{NewLine}{Exception}",
                rollingInterval: RollingInterval.Day,
                retainedFileCountLimit: 30,
                shared: true)
            .CreateLogger();
#endif

        var version = Assembly.GetExecutingAssembly()
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()
            ?.InformationalVersion ?? "unknown";

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
                    .AddProvider(new TuiLoggerProvider(logMessages))
                    .AddSerilog(Log.Logger);
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
                Console.WriteLine("Press any key to exit...");
                Console.ReadKey(intercept: true);
                return;
            }

            // Create stats tracker and Terminal.Gui TUI
            var stats = new ServerStats(server.Clients, server.ChannelManager, server.AudioServer);
            using var tui = new TerminalGuiServer(config, stats, logMessages, version);

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
                Console.WriteLine("Press any key to exit...");
                Console.ReadKey(intercept: true);
                return;
            }

            // Start TUI (blocks until quit or shutdown requested)
            var tuiTask = Task.Run(() => tui.Start());

            // Wait for either TUI to quit or Ctrl+C
            await Task.WhenAny(tuiTask, Task.Delay(-1, shutdownCts.Token).ContinueWith(_ => { }));

            // Now properly shut down
            Log.Information("Shutting down server...");
            await server.StopAsync();
            tui.Stop();
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

    static ServerConfig? LoadConfiguration()
    {
        try
        {
            var configPath = Path.Combine(AppContext.BaseDirectory, "OpenFreq.Server.json");

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
