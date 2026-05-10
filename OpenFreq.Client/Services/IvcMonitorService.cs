using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using OpenFreqClient.Services.Interfaces;

namespace OpenFreqClient.Services;

#if WINDOWS
public class IvcMonitorService : IIvcMonitorService
{
    private readonly ILogger<IvcMonitorService> _logger;
    private const string? IvcProcessName = "IVC Client";
    private CancellationTokenSource? _monitoringCts;
    private Task? _monitoringTask;

    public event EventHandler<IvcStatusChangedEventArgs>? IvcStatusChanged;
    public bool IsIvcRunning { get; private set; }

    public IvcMonitorService(ILogger<IvcMonitorService> logger)
    {
        _logger = logger;
    }

    public void Start()
    {
        _monitoringCts = new CancellationTokenSource();
        _monitoringTask = Task.Run(async () =>
        {
            _logger.LogInformation("Monitoring started");

            while (!_monitoringCts.Token.IsCancellationRequested)
            {
                try
                {
                    await Task.Delay(1000, _monitoringCts.Token);
                    CheckIvcStatus();
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Error monitoring IVC");
                }
            }

            _logger.LogInformation("Monitoring stopped");
        });
    }

    private void CheckIvcStatus()
    {
        var processes = Process.GetProcessesByName(IvcProcessName);
        var isRunning = processes.Length > 0;

        // Dispose process objects to avoid resource leak
        foreach (var process in processes)
        {
            process.Dispose();
        }

        // Only fire event if status changed
        if (isRunning == IsIvcRunning) return;
        IsIvcRunning = isRunning;
        _logger.LogInformation("IVC status changed: {Status}", isRunning ? "Running" : "Stopped");
        IvcStatusChanged?.Invoke(this, new IvcStatusChangedEventArgs { IsRunning = isRunning });
    }
    
    public void KillIvc()
    {
        var processes = Process.GetProcessesByName(IvcProcessName);
        // Dispose process objects to avoid resource leak
        foreach (var process in processes)
        {
            process.Kill();
            process.Dispose();
        }
    }
    
    public void Stop()
    {
        _monitoringCts?.Cancel();
    }

    public async ValueTask DisposeAsync()
    {
        _monitoringCts?.Cancel();

        if (_monitoringTask != null)
        {
            await _monitoringTask;
        }

        _monitoringCts?.Dispose();
    }
}
#else
public class IvcMonitorService : IIvcMonitorService
{
#pragma warning disable CS0067
    public event EventHandler<IvcStatusChangedEventArgs>? IvcStatusChanged;
#pragma warning restore CS0067
    public bool IsIvcRunning => false;

    public void Start() 
    { }

    public void Stop() { }

    public void KillIvc() { }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
#endif