using System.Collections.Concurrent;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using OpenFreq.Server;
using OpenFreqServer.Json;
using Serilog;
using Serilog.Events;

class Program
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
            .WriteTo.Console(outputTemplate: "[{Timestamp:HH:mm:ss} {Level:u3}] {Message:lj}{NewLine}{Exception}")
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

        try
        {
            Console.WriteLine("OpenFreq Server - Loading configuration...");

            // Load configuration
            var config = LoadConfiguration();
            if (config == null)
            {
                Console.WriteLine("Failed to load configuration. Exiting.");
                Log.Fatal("Failed to load configuration");
                return;
            }

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
                    .AddProvider(new TuiLoggerProvider(logMessages, 100))
                    .AddSerilog(Log.Logger);
            });

            var server = new SignalingServer(config, loggerFactory);

            // Create stats tracker and Terminal.Gui TUI
            var stats = new ServerStats(server.Clients, server.ChannelManager);
            using var tui = new TerminalGuiServer(config, stats, logMessages, server);

            // Setup graceful shutdown
            var shutdownCts = new CancellationTokenSource();
            Console.CancelKeyPress += (sender, e) =>
            {
                if (!shutdownCts.IsCancellationRequested)
                {
                    e.Cancel = true;
                    Log.Information("Shutdown requested by user");
                    shutdownCts.Cancel(); // Signal shutdown, don't block
                }
            };

            // Start server in background
            var serverTask = Task.Run(async () =>
            {
                try
                {
                    await server.StartAsync();
                }
                catch (Exception ex)
                {
                    Log.Fatal(ex, "Fatal server error");
                }
            });

            // Start TUI (blocks until quit or shutdown requested)
            var tuiTask = Task.Run(() => tui.Start());

            // Wait for either TUI to quit or Ctrl+C
            await Task.WhenAny(tuiTask, Task.Delay(-1, shutdownCts.Token).ContinueWith(_ => { }));

            // Now properly shut down
            Log.Information("Shutting down server...");
            await server.StopAsync(); // Async all the way
            tui.Stop();

            // Wait for server to finish
            await serverTask;
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
                    AudioBasePort = 10000,
                    MaxClientsPerChannel = 50,
                    MaxChannelsPerClient = 10,
                    EnableOpusCompression = true,
                    BroadcastPeerUpdates = true
                };

                var json = Json.Instance.Serialize(defaultConfig);

                File.WriteAllText(configPath, json);
                Log.Information("Default configuration created at: {ConfigPath}", configPath);
                return defaultConfig;
            }

            var configJson = File.ReadAllText(configPath);
            var config = Json.Instance.Deserialize<ServerConfig>(configJson);

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