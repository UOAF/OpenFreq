using System.Collections.Concurrent;
using System.Net.Mime;
using System.Reflection.Emit;
using Microsoft.Extensions.Logging;
using Serilog;
using Terminal.Gui;
using Label = Terminal.Gui.Label;

namespace OpenFreq.Server;

public class TerminalGuiServer : IDisposable
{
    private readonly ServerConfig _config;
    private readonly ServerStats _stats;
    private readonly ConcurrentQueue<TuiLogMessage> _logMessages;
    private readonly CancellationTokenSource _cts = new();
    private Task? _updateTask;

    private Label? _statusLabel;
    private TabView? _tabView;
    private ListView? _frequenciesListView;
    private ListView? _clientsListView;
    private ListView? _logsListView;

    private List<string> _frequencyLines = new();
    private List<string> _clientLines = new();
    private List<string> _logLines = new();

    private SignalingServer? _server;

    public TerminalGuiServer(ServerConfig config, ServerStats stats, ConcurrentQueue<TuiLogMessage> logMessages,
        SignalingServer server)
    {
        _config = config;
        _stats = stats;
        _logMessages = logMessages;
        _server = server;
    }

    public void Start()
    {
        Terminal.Gui.Application.Init();

        try
        {
            SetupUI();
            _updateTask = Task.Run(async () => await UpdateLoop());
            Terminal.Gui.Application.Run();
        }
        finally
        {
            Terminal.Gui.Application.Shutdown();
        }
    }

    public void Stop()
    {
        if (_cts.IsCancellationRequested) return; // Prevent double-stop
        _cts.Cancel();

        // Wait for update task to finish
        try
        {
            _updateTask?.Wait(TimeSpan.FromSeconds(2));
        }
        catch (AggregateException)
        {
        }

        Application.RequestStop();
    }

    private void SetupUI()
    {
        var top = Application.Top;

        var schemeDefault = new ColorScheme
        {
            Normal = Terminal.Gui.Attribute.Make(Color.Gray, Color.Black),
            Focus = Terminal.Gui.Attribute.Make(Color.BrightCyan, Color.Black),
            HotNormal = Terminal.Gui.Attribute.Make(Color.BrightYellow, Color.Black),
            HotFocus = Terminal.Gui.Attribute.Make(Color.BrightYellow, Color.Black)
        };

        var schemeHeader = new ColorScheme
        {
            Normal = Terminal.Gui.Attribute.Make(Color.BrightYellow, Color.Black)
        };

        var schemeFrame = new ColorScheme
        {
            Normal = Terminal.Gui.Attribute.Make(Color.BrightCyan, Color.Black)
        };

        var schemeStatus = new ColorScheme
        {
            Normal = Terminal.Gui.Attribute.Make(Color.Black, Color.BrightGreen)
        };

        // ──────────────────────────────────────────────
        // STATUS BAR (Top)
        // ──────────────────────────────────────────────
        _statusLabel = new Label("")
        {
            X = 0,
            Y = 0,
            Width = Dim.Fill(),
            Height = 1,
            ColorScheme = schemeStatus,
            TextAlignment = TextAlignment.Left
        };
        top.Add(_statusLabel);

        // ──────────────────────────────────────────────
        // TAB VIEW (Main Content)
        // ──────────────────────────────────────────────
        _tabView = new TabView()
        {
            X = 0,
            Y = Pos.Bottom(_statusLabel),
            Width = Dim.Fill(),
            Height = Dim.Fill(),
            ColorScheme = schemeDefault
        };

        _tabView.AddTab(new TabView.Tab("📡 Frequencies", CreateFrequenciesView(schemeFrame, schemeHeader)), false);
        _tabView.AddTab(new TabView.Tab("👥 Clients", CreateClientsView(schemeFrame, schemeHeader)), false);
        _tabView.AddTab(new TabView.Tab("📝 Logs", CreateLogsView(schemeFrame, schemeHeader)), false);

        top.Add(_tabView);

        // ──────────────────────────────────────────────
        // Global Key Shortcuts
        // ──────────────────────────────────────────────
        Application.RootKeyEvent += args =>
        {
            switch (args.Key)
            {
                case Key.Q | Key.CtrlMask:
                case Key.q | Key.CtrlMask:
                    Stop();
                    return true;
                case Key.F1:
                    _tabView.SelectedTab = _tabView.Tabs.ElementAt(0);
                    return true;
                case Key.F2:
                    _tabView.SelectedTab = _tabView.Tabs.ElementAt(1);
                    return true;
                case Key.F3:
                    _tabView.SelectedTab = _tabView.Tabs.ElementAt(2);
                    return true;
            }

            return false;
        };

        // ──────────────────────────────────────────────
        // Graceful Resize Handling
        // ──────────────────────────────────────────────
        Application.Resized += (_) =>
        {
            Application.MainLoop.Invoke(() =>
            {
                try
                {
                    Application.Top.LayoutSubviews();
                    if (_tabView != null)
                    {
                        foreach (var tab in _tabView.Tabs)
                        {
                            tab.View.LayoutSubviews();
                            tab.View.SetNeedsDisplay();
                        }

                        _tabView.LayoutSubviews();
                    }

                    Application.Driver.Clip = new Rect(0, 0, Application.Driver.Cols, Application.Driver.Rows);
                    _tabView?.SetFocus();
                    Application.Refresh();
                }
                catch
                {
                    /* ignore */
                }
            });
        };

        UpdateStatus();
        UpdateFrequencies();
        UpdateClients();
        UpdateLogs();
    }


    private View CreateFrequenciesView(ColorScheme frameScheme, ColorScheme headerScheme)
    {
        var frame = new FrameView(" Active Frequencies ")
        {
            X = 1,
            Y = 0,
            Width = Dim.Fill() - 2,
            Height = Dim.Fill() - 1,
            ColorScheme = frameScheme
        };

        _frequenciesListView = new ListView(_frequencyLines)
        {
            X = 1,
            Y = 1,
            Width = Dim.Fill() - 2,
            Height = Dim.Fill() - 2,
            AllowsMarking = false,
            CanFocus = true,
            ColorScheme = headerScheme
        };

        frame.Add(_frequenciesListView);
        return frame;
    }

    private View CreateClientsView(ColorScheme frameScheme, ColorScheme headerScheme)
    {
        var frame = new FrameView(" Connected Clients ")
        {
            X = 1,
            Y = 0,
            Width = Dim.Fill() - 2,
            Height = Dim.Fill() - 1,
            ColorScheme = frameScheme
        };

        _clientsListView = new ListView(_clientLines)
        {
            X = 1,
            Y = 1,
            Width = Dim.Fill() - 2,
            Height = Dim.Fill() - 2,
            AllowsMarking = false,
            CanFocus = true,
            ColorScheme = headerScheme
        };

        frame.Add(_clientsListView);
        return frame;
    }

    private View CreateLogsView(ColorScheme frameScheme, ColorScheme headerScheme)
    {
        var frame = new FrameView(" Recent Logs ")
        {
            X = 1,
            Y = 0,
            Width = Dim.Fill() - 2,
            Height = Dim.Fill() - 1,
            ColorScheme = frameScheme
        };

        _logsListView = new ListView(_logLines)
        {
            X = 1,
            Y = 1,
            Width = Dim.Fill() - 2,
            Height = Dim.Fill() - 2,
            AllowsMarking = false,
            CanFocus = true,
            ColorScheme = headerScheme
        };

        frame.Add(_logsListView);
        return frame;
    }

    private async Task UpdateLoop()
    {
        while (!_cts.Token.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(1000, _cts.Token);

                Application.MainLoop.Invoke(() =>
                {
                    UpdateStatus();
                    UpdateFrequencies();
                    UpdateClients();
                    UpdateLogs();
                });
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception)
            {
                // Ignore update errors
            }
        }
    }

    private void UpdateStatus()
    {
        if (_statusLabel == null) return;

        var uptime = _stats.Uptime;
        var uptimeStr = $"{uptime.Days}d{uptime.Hours:D2}h{uptime.Minutes:D2}m{uptime.Seconds:D2}s";

        var color = _stats.TotalClients > 0
            ? Terminal.Gui.Attribute.Make(Color.Black, Color.BrightGreen)
            : Terminal.Gui.Attribute.Make(Color.Black, Color.BrightRed);

        _statusLabel.ColorScheme = new ColorScheme { Normal = color };

        _statusLabel.Text =
            $"● OpenFreq Server | Up: {uptimeStr} | " +
            $"WS:{_config.WebSocketPort} | Clients: {_stats.AuthenticatedClients}/{_stats.TotalClients} | " +
            $"TX: {_stats.ActiveTransmissions} | " +
            $"Auth:{(!string.IsNullOrEmpty(_config.ServerPassword) ? " Yes" : " No")} | " +
            $"Opus:{(_config.EnableOpusCompression ? " Yes" : " No")} | " +
            $"F1=Freq  F2=Clients  F3=Logs  CTRL+q=Quit";
    }

    private void UpdateFrequencies()
    {
        if (_frequenciesListView == null) return;

        // Save both selection and top visible item
        var currentSelection = _frequenciesListView.SelectedItem;
        var currentTopItem = _frequenciesListView.TopItem;

        var frequencies = _stats.GetFrequencyStats();

        _frequencyLines.Clear();

        if (frequencies.Count == 0)
        {
            _frequencyLines.Add("No active frequencies");
        }
        else
        {
            _frequencyLines.Add($"{"Frequency",-15} {"Clients",8} {"Status",10}");
            _frequencyLines.Add(new string('-', 40));

            foreach (var freq in frequencies)
            {
                var status = freq.ClientCount > 0 ? "● Active" : "○ Idle";
                _frequencyLines.Add($"{$"{freq.FrequencyKhz/1000d:F3} MHz",-15} {freq.ClientCount,8} {status,10}");
            }

            var totalActive = frequencies.Count(f => f.ClientCount > 0);
            _frequencyLines.Add(" ");
            _frequencyLines.Add($"Active: {totalActive}/{frequencies.Count}");
        }

        _frequenciesListView.SetSource(_frequencyLines);

        // Restore scroll position
        if (currentTopItem >= 0 && currentTopItem < _frequencyLines.Count)
        {
            _frequenciesListView.TopItem = currentTopItem;
        }

        if (currentSelection >= 0 && currentSelection < _frequencyLines.Count)
        {
            _frequenciesListView.SelectedItem = currentSelection;
            _frequenciesListView.EnsureSelectedItemVisible();
        }
    }

    private void UpdateClients()
    {
        if (_clientsListView == null) return;

        // Save both selection and top visible item
        var currentSelection = _clientsListView.SelectedItem;
        var currentTopItem = _clientsListView.TopItem;

        var clients = _stats.GetActiveClients().OrderByDescending(c => c.LastActivity).ToList();

        _clientLines.Clear();

        _clientLines.Add($"{"Client ID",-38} {"Frequency",-12} {"Port",6} {"Status",8} {"Activity",10}");
        _clientLines.Add(new string('─', 80));

        if (clients.Count == 0)
        {
            _clientLines.Add("No active clients");
        }
        else
        {
            foreach (var client in clients)
            {
                var shortId = client.Id.Length > 36 ? client.Id.Substring(0, 36) : client.Id;
                var audioPort = client.AudioPort > 0 ? client.AudioPort.ToString() : "-";
                var timeSinceActivity = (DateTime.UtcNow - client.LastActivity).TotalSeconds;
                var activityStr = timeSinceActivity < 60
                    ? $"{timeSinceActivity:F0}s ago"
                    : $"{timeSinceActivity / 60:F0}m ago";

                var frequencies = client.CurrentFrequencies.ToList();

                if (frequencies.Count == 0)
                {
                    // No frequencies
                    _clientLines.Add($"{shortId,-38} {"-",-12} {audioPort,6} {"● RX",8} {activityStr,10}");
                }
                else
                {
                    // First frequency with full client info
                    var firstFreq = frequencies[0];
                    var firstStatus = firstFreq.Value == ClientSession.FrequencyClientStatus.Transmitting
                        ? "● TX"
                        : "● RX";
                    _clientLines.Add(
                        $"{shortId,-38} {firstFreq.Key/1000d,-12:F3} {audioPort,6} {firstStatus,8} {activityStr,10}");

                    // Additional frequencies on subsequent lines
                    for (int i = 1; i < frequencies.Count; i++)
                    {
                        var freq = frequencies[i];
                        var status = freq.Value == ClientSession.FrequencyClientStatus.Transmitting ? "● TX" : "● RX";
                        _clientLines.Add($"{"",-38} {freq.Key/1000d,-12:F3} {"-",6} {status,8} {"",10}");
                    }
                }
            }

            _clientLines.Add(" ");
            _clientLines.Add($"Total: {clients.Count} clients");
        }

        _clientsListView.SetSource(_clientLines);

        // Restore scroll position
        if (currentTopItem >= 0 && currentTopItem < _clientLines.Count)
        {
            _clientsListView.TopItem = currentTopItem;
        }

        if (currentSelection >= 0 && currentSelection < _clientLines.Count)
        {
            _clientsListView.SelectedItem = currentSelection;
            _clientsListView.EnsureSelectedItemVisible();
        }
    }

    private void UpdateLogs()
    {
        if (_logsListView == null) return;

        // Save both selection and top visible item
        var currentSelection = _logsListView.SelectedItem;
        var currentTopItem = _logsListView.TopItem;
        var oldCount = _logsListView.Source?.Count ?? 0;
        var wasAtBottom = (currentSelection >= oldCount - 2) || oldCount == 0;

        var logs = _logMessages
            .OrderByDescending(m => m.Timestamp)
            .Take(100)
            .Reverse()
            .ToList();

        _logLines.Clear();
        _logLines.Add($"{"Time",-10} {"Level",-8} {"Message"}");
        _logLines.Add(new string('─', 80));

        if (logs.Count == 0)
        {
            _logLines.Add("No logs yet...");
        }
        else
        {
            foreach (var log in logs)
            {
                var timeStr = log.Timestamp.ToLocalTime().ToString("HH:mm:ss");
                var levelStr = log.Level switch
                {
                    LogLevel.Error => "ERROR",
                    LogLevel.Warning => "WARN",
                    LogLevel.Information => "INFO",
                    LogLevel.Debug => "DEBUG",
                    _ => log.Level.ToString()
                };

                _logLines.Add($"{timeStr,-10} {levelStr,-8} {log.Message}");
            }
        }

        _logsListView.SetSource(_logLines);

        // Restore scroll position
        if (wasAtBottom)
        {
            // Auto-scroll to bottom - let ListView position naturally
            _logsListView.SelectedItem = _logLines.Count - 1;
            _logsListView.EnsureSelectedItemVisible();
        }
        else
        {
            // Restore previous position
            if (currentTopItem >= 0 && currentTopItem < _logLines.Count)
            {
                _logsListView.TopItem = currentTopItem;
            }

            if (currentSelection >= 0 && currentSelection < _logLines.Count)
            {
                _logsListView.SelectedItem = currentSelection;
            }
        }
    }

    public void Dispose()
    {
        _cts.Cancel();
        _updateTask?.Wait(TimeSpan.FromSeconds(2));
        _cts.Dispose();
    }
}