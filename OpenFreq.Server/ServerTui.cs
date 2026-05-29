using System.Collections.Concurrent;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.Globalization;
using System.Text;
using Microsoft.Extensions.Logging;
using Terminal.Gui;
using Terminal.Gui.App;
using Terminal.Gui.Drawing;
using Terminal.Gui.Drivers;
using Terminal.Gui.Input;
using Terminal.Gui.ViewBase;
using Terminal.Gui.Views;
using Attribute = Terminal.Gui.Drawing.Attribute;

namespace OpenFreqServer;

/// <summary>Row model for colored list views.</summary>
internal record ColoredRow(string Text, bool IsTransmitting);

/// <summary>
/// Reusable IListDataSource that renders transmitting rows in red.
/// </summary>
internal class ColoredListSource : IListDataSource
{
    private static readonly Attribute _normalAttr = new(ColorName16.BrightYellow, ColorName16.Black);
    private static readonly Attribute _transmitAttr = new(ColorName16.BrightRed, ColorName16.Black);
    private static readonly Attribute _selectedAttr = new(ColorName16.Black, ColorName16.Gray);
    private static readonly Attribute _selectedTxAttr = new(ColorName16.Black, ColorName16.BrightRed);

    public ObservableCollection<ColoredRow> Rows { get; } = new();

    public event NotifyCollectionChangedEventHandler? CollectionChanged;

    public ColoredListSource()
    {
        Rows.CollectionChanged += (s, e) => CollectionChanged?.Invoke(this, e);
    }

    public int Count => Rows.Count;
    public int MaxItemLength => Rows.Count > 0 ? Rows.Max(r => r.Text.Length) : 0;
    public bool SuspendCollectionChangedEvent { get; set; }
    public bool IsMarked(int item) => false;
    public void SetMark(int item, bool value) { }
    public void Dispose() { }
    public System.Collections.IList ToList() => Rows.Select(r => r.Text).ToList<string>();

    public void Render(ListView listView, bool selected, int item, int col, int row, int width, int viewportX)
    {
        if (item >= Rows.Count) return;
        var rowData = Rows[item];

        var text = rowData.Text;
        var maxLen = Math.Max(0, width - col);
        if (text.Length > maxLen) text = text[..maxLen];

        listView.Move(col, row);

        var attr = selected
            ? (rowData.IsTransmitting ? _selectedTxAttr : _selectedAttr)
            : (rowData.IsTransmitting ? _transmitAttr : _normalAttr);

        listView.SetAttribute(attr);
        listView.AddStr(text);

        // Pad remainder so selection highlight fills the row
        var pad = maxLen - text.Length;
        if (pad > 0) listView.AddStr(new string(' ', pad));
    }

}

public class TerminalGuiServer : IDisposable
{
    private readonly ServerConfig _config;
    private readonly ServerStats _stats;
    private readonly ConcurrentQueue<TuiLogMessage> _logMessages;
    private readonly CancellationTokenSource _cts = new();
    private Task? _updateTask;

    private Label? _statusLabel;
    private Tabs? _tabView;
    private ListView? _frequenciesListView;
    private ListView? _clientsListView;
    private ListView? _logsListView;

    private readonly ColoredListSource _frequencySource = new();
    private readonly ColoredListSource _clientSource = new();
    private readonly ObservableCollection<string> _logLines = new();
    private readonly string _version;

    private IApplication? _app;

    public TerminalGuiServer(ServerConfig config, ServerStats stats, ConcurrentQueue<TuiLogMessage> logMessages, string version = "unknown")
    {
        _config = config;
        _stats = stats;
        _logMessages = logMessages;
        _version = version;
    }

    public void Start()
    {
        Console.OutputEncoding = Encoding.UTF8;
        using var app = Application.Create().Init();
        _app = app;

        try
        {
            var top = new Window { Width = Dim.Fill(), Height = Dim.Fill() };
            SetupUi(top, app);
            _updateTask = Task.Run(async () => await UpdateLoop());
            app.Run(top);
        }
        finally
        {
            _app = null;
        }
    }

    public void Stop()
    {
        if (_cts.IsCancellationRequested) return;
        _cts.Cancel();

        try
        {
            _updateTask?.Wait(TimeSpan.FromSeconds(2));
        }
        catch (AggregateException)
        {
        }

        _app?.RequestStop();
    }

    private void SetupUi(View top, IApplication app)
    {
        const string freqTab = "≋ Frequencies";
        const string clientTab = "☺ Clients";
        const string logTab = "▤ Logs";

        var schemeDefault = new Scheme
        {
            Normal = new Attribute(ColorName16.Gray, ColorName16.Black),
            Focus = new Attribute(ColorName16.BrightCyan, ColorName16.Black),
            HotNormal = new Attribute(ColorName16.BrightYellow, ColorName16.Black),
            HotFocus = new Attribute(ColorName16.BrightYellow, ColorName16.Black)
        };

        var schemeHeader = new Scheme
        {
            Normal = new Attribute(ColorName16.BrightYellow, ColorName16.Black)
        };

        var schemeFrame = new Scheme
        {
            Normal = new Attribute(ColorName16.BrightCyan, ColorName16.Black)
        };

        var schemeStatus = new Scheme
        {
            Normal = new Attribute(ColorName16.Black, ColorName16.BrightGreen)
        };

        // ──────────────────────────────────────────────
        // STATUS BAR (Top)
        // ──────────────────────────────────────────────
        _statusLabel = new Label
        {
            X = 0,
            Y = 0,
            Width = Dim.Fill(),
            Height = 1,
        };
        _statusLabel.SetScheme(schemeStatus);
        top.Add(_statusLabel);

        // ──────────────────────────────────────────────
        // FOOTER (Bottom)
        // ──────────────────────────────────────────────
        var schemeFooter = new Scheme
        {
            Normal = new Attribute(ColorName16.Black, ColorName16.Gray)
        };
        var footer = new Label
        {
            X = 0,
            Y = Pos.AnchorEnd(1),
            Width = Dim.Fill(),
            Height = 1,
            Text = "  F1=Frequencies  F2=Clients  F3=Logs  │  CTRL+Q=Quit Server",
        };
        footer.SetScheme(schemeFooter);
        top.Add(footer);

        // ──────────────────────────────────────────────
        // TAB VIEW (Main Content)
        // ──────────────────────────────────────────────
        _tabView = new Tabs
        {
            X = 0,
            Y = Pos.Bottom(_statusLabel),
            Width = Dim.Fill(),
            Height = Dim.Fill() - 1,
        };
        _tabView.SetScheme(schemeDefault);

        var freqView = CreateFrequenciesView(schemeFrame, schemeHeader);
        freqView.Title = freqTab;
        _tabView.Add(freqView);

        var clientView = CreateClientsView(schemeFrame, schemeHeader);
        clientView.Title = clientTab;
        _tabView.Add(clientView);

        var logView = CreateLogsView(schemeFrame, schemeHeader);
        logView.Title = logTab;
        _tabView.Add(logView);

        top.Add(_tabView);

        // ──────────────────────────────────────────────
        // Global Key Shortcuts
        // ──────────────────────────────────────────────
        app.Keyboard.KeyDown += (_, key) =>
        {
            switch (key.KeyCode)
            {
                case KeyCode.Q | KeyCode.CtrlMask:
                    Stop();
                    break;
                case KeyCode.F1:
                    _tabView.Value = _tabView.TabCollection.ElementAt(0);
                    break;
                case KeyCode.F2:
                    _tabView.Value = _tabView.TabCollection.ElementAt(1);
                    break;
                case KeyCode.F3:
                    _tabView.Value = _tabView.TabCollection.ElementAt(2);
                    break;
            }
        };

        UpdateStatus();
        UpdateFrequencies();
        UpdateClients();
        UpdateLogs();
    }

    private View CreateFrequenciesView(Scheme frameScheme, Scheme headerScheme)
    {
        var frame = new FrameView
        {
            Title = " Active Frequencies ",
            X = 0,
            Y = 0,
            Width = Dim.Fill(),
            Height = Dim.Fill(),
        };
        frame.SetScheme(frameScheme);

        _frequenciesListView = new ListView
        {
            X = 1,
            Y = 1,
            Width = Dim.Fill() - 2,
            Height = Dim.Fill() - 2,
            CanFocus = true,
        };
        _frequenciesListView.SetScheme(headerScheme);
        _frequenciesListView.Source = _frequencySource;

        frame.Add(_frequenciesListView);
        return frame;
    }

    private View CreateClientsView(Scheme frameScheme, Scheme headerScheme)
    {
        var frame = new FrameView
        {
            Title = " Connected Clients ",
            X = 0,
            Y = 0,
            Width = Dim.Fill(),
            Height = Dim.Fill(),
        };
        frame.SetScheme(frameScheme);

        _clientsListView = new ListView
        {
            X = 1,
            Y = 1,
            Width = Dim.Fill() - 2,
            Height = Dim.Fill() - 2,
            CanFocus = true,
        };
        _clientsListView.SetScheme(headerScheme);
        _clientsListView.Source = _clientSource;

        frame.Add(_clientsListView);
        return frame;
    }

    private View CreateLogsView(Scheme frameScheme, Scheme headerScheme)
    {
        var frame = new FrameView
        {
            Title = " Recent Logs ",
            X = 0,
            Y = 0,
            Width = Dim.Fill(),
            Height = Dim.Fill(),
        };
        frame.SetScheme(frameScheme);

        _logsListView = new ListView
        {
            X = 1,
            Y = 1,
            Width = Dim.Fill() - 2,
            Height = Dim.Fill() - 2,
            CanFocus = true,
        };
        _logsListView.SetScheme(headerScheme);
        _logsListView.SetSource(_logLines);

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

                _app?.Invoke(() =>
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
        }
    }

    private void UpdateStatus()
    {
        if (_statusLabel == null) return;

        var uptime = _stats.Uptime;
        var uptimeStr = $"{uptime.Days}d{uptime.Hours:D2}h{uptime.Minutes:D2}m{uptime.Seconds:D2}s";

        var color = _stats.TotalClients > 0
            ? new Attribute(ColorName16.Black, ColorName16.BrightGreen)
            : new Attribute(ColorName16.Black, ColorName16.BrightRed);

        _statusLabel.SetScheme(new Scheme { Normal = color });

        var info =
            $"● OpenFreq Server {_version} | Up: {uptimeStr} | " +
            $"Ports: {_config.WebSocketPort} (ws://) {_config.AudioPort} (Audio) | Clients: {_stats.AuthenticatedClients} | " +
            $"TX: {_stats.ActiveTransmissions} | " +
            $"Auth:{(!string.IsNullOrEmpty(_config.ServerPassword) ? " Yes" : " No")} | " +
            $"Opus:{(_config.EnableOpusCompression ? " Yes" : " No")}";

        const string hotkeys = " | F1=Freq  F2=Clients  F3=Logs  CTRL+q=Quit";
        var termWidth = _app?.Screen.Width ?? Console.WindowWidth;
        var full = info + hotkeys;
        _statusLabel.Text = full.Length <= termWidth ? full : info.Length <= termWidth ? info : info[..termWidth];
    }

    private void UpdateFrequencies()
    {
        if (_frequenciesListView == null) return;

        var currentSelection = _frequenciesListView.SelectedItem;
        var frequencies = _stats.GetFrequencyStats();
        var rows = _frequencySource.Rows;

        rows.Clear();

        if (frequencies.Count == 0)
        {
            rows.Add(new ColoredRow("No active frequencies", false));
        }
        else
        {
            rows.Add(new ColoredRow($"{"Frequency",-15} {"Clients",8} {"Status",10}", false));
            rows.Add(new ColoredRow(new string('-', 40), false));

            foreach (var freq in frequencies)
            {
                var indicator = freq.IsTransmitting ? "● TX" : freq.ClientCount > 0 ? "● RX" : "○ Idle";
                var freqStr = (freq.FrequencyKhz / 1000d).ToString("F3", CultureInfo.InvariantCulture) + " MHz";
                var text = $"{freqStr,-15} {freq.ClientCount,8} {indicator,10}";
                rows.Add(new ColoredRow(text, freq.IsTransmitting));
            }

            var totalActive = frequencies.Count(f => f.ClientCount > 0);
            rows.Add(new ColoredRow(" ", false));
            rows.Add(new ColoredRow($"Active: {totalActive}/{frequencies.Count}", false));
        }

        if (currentSelection >= 0 && currentSelection < rows.Count)
        {
            _frequenciesListView.SelectedItem = currentSelection;
            _frequenciesListView.EnsureSelectedItemVisible();
        }
    }

    private void UpdateClients()
    {
        if (_clientsListView == null) return;

        var currentSelection = _clientsListView.SelectedItem;
        var clients = _stats.GetActiveClients().OrderByDescending(c => c.LastActivity).ToList();
        var rows = _clientSource.Rows;

        rows.Clear();

        rows.Add(new ColoredRow($"{"Display Name",-24} {"Client ID",-20} {"Frequency",-12} {"Status",8} {"Last WS",9} {"Last RTP",9}", false));
        rows.Add(new ColoredRow(new string('─', 90), false));

        if (clients.Count == 0)
        {
            rows.Add(new ColoredRow("No active clients", false));
        }
        else
        {
            foreach (var client in clients)
            {
                var displayName = string.IsNullOrWhiteSpace(client.DisplayName)
                    ? "Unnamed"
                    : client.DisplayName;
                var shortDisplayName = displayName.Length > 24
                    ? displayName.Substring(0, 21) + "..."
                    : displayName;

                var shortId = client.Id.Length > 20 ? client.Id.Substring(0, 20) : client.Id;
                var timeSinceWs = (DateTime.UtcNow - client.LastActivity).TotalSeconds;
                var wsStr = timeSinceWs < 60
                    ? $"{timeSinceWs:F0}s ago"
                    : $"{timeSinceWs / 60:F0}m ago";

                var lastRtp = _stats.GetLastRtpReceived(client.Id);
                var rtpStr = lastRtp.HasValue
                    ? ((DateTime.UtcNow - lastRtp.Value).TotalSeconds is var rtpAge && rtpAge < 60
                        ? $"{rtpAge:F0}s ago"
                        : $"{rtpAge / 60:F0}m ago")
                    : "no RTP";

                var frequencies = client.CurrentFrequencies.ToList();
                var anyTransmitting = frequencies.Any(f =>
                    f.Value == ClientSession.FrequencyClientStatus.Transmitting);

                if (frequencies.Count == 0)
                {
                    rows.Add(new ColoredRow(
                        $"{shortDisplayName,-24} {shortId,-20} {"-",-12} {"● RX",8} {wsStr,9} {rtpStr,9}", false));
                }
                else
                {
                    var firstFreq = frequencies[0];
                    var firstTx = firstFreq.Value == ClientSession.FrequencyClientStatus.Transmitting;
                    var firstStatus = firstTx ? "● TX" : "● RX";
                    rows.Add(new ColoredRow(
                        $"{shortDisplayName,-24} {shortId,-20} {(firstFreq.Key / 1000d).ToString("F3", CultureInfo.InvariantCulture),-12} {firstStatus,8} {wsStr,9} {rtpStr,9}",
                        anyTransmitting));

                    for (int i = 1; i < frequencies.Count; i++)
                    {
                        var freq = frequencies[i];
                        var tx = freq.Value == ClientSession.FrequencyClientStatus.Transmitting;
                        var status = tx ? "● TX" : "● RX";
                        rows.Add(new ColoredRow(
                            $"{"",-24} {"",-20} {(freq.Key / 1000d).ToString("F3", CultureInfo.InvariantCulture),-12} {status,8} {"",9} {"",9}", anyTransmitting));
                    }
                }
            }

            rows.Add(new ColoredRow(" ", false));
            rows.Add(new ColoredRow($"Total: {clients.Count} clients", false));
        }

        if (currentSelection >= 0 && currentSelection < rows.Count)
        {
            _clientsListView.SelectedItem = currentSelection;
            _clientsListView.EnsureSelectedItemVisible();
        }
    }

    private void UpdateLogs()
    {
        if (_logsListView == null) return;

        var currentSelection = _logsListView.SelectedItem;
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

        if (wasAtBottom)
        {
            _logsListView.SelectedItem = _logLines.Count - 1;
            _logsListView.EnsureSelectedItemVisible();
        }
        else if (currentSelection >= 0 && currentSelection < _logLines.Count)
        {
            _logsListView.SelectedItem = currentSelection;
        }
    }

    public void Dispose()
    {
        _cts.Cancel();
        _updateTask?.Wait(TimeSpan.FromSeconds(2));
        _cts.Dispose();
    }
}
