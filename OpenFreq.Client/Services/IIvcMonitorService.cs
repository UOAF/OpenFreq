using System;
using System.Threading.Tasks;
using OpenFreqClient.Services.Interfaces;

namespace OpenFreqClient.Services;

#if WINDOWS
public class IvcMonitorService : IIvcMonitorService
{
    private CancellationTokenSource? _monitoringCts;
    private Task? _monitoringTask;
    private bool _isIvcRunning;

    public event EventHandler<IvcStatusChangedEventArgs>? IvcStatusChanged;
    public bool IsIvcRunning => _isIvcRunning;

    public void Start()
    {
        _monitoringCts = new CancellationTokenSource();
        _monitoringTask = Task.Run(async () =>
        {
            Console.WriteLine("[IvcMonitor] Monitoring started");

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
                    Console.WriteLine($"[IvcMonitor] Error monitoring IVC: {ex.Message}");
                }
            }

            Console.WriteLine("[IvcMonitor] Monitoring stopped");
        });
    }

    private void CheckIvcStatus()
    {
        var processes = Process.GetProcessesByName("IVC");
        var isRunning = processes.Length > 0;

        // Dispose process objects to avoid resource leak
        foreach (var process in processes)
        {
            process.Dispose();
        }

        // Only fire event if status changed
        if (isRunning != _isIvcRunning)
        {
            _isIvcRunning = isRunning;
            Console.WriteLine($"[IvcMonitor] IVC status changed: {(isRunning ? "Running" : "Stopped")}");
            IvcStatusChanged?.Invoke(this, new IvcStatusChangedEventArgs { IsRunning = isRunning });
        }
    }

    public void Stop()
    {
        _monitoringCts?.Cancel();
    }

    public async ValueTask DisposeAsync()
    {
        Console.WriteLine("[IvcMonitor] DisposeAsync starting");
        _monitoringCts?.Cancel();

        if (_monitoringTask != null)
        {
            await _monitoringTask;
        }

        _monitoringCts?.Dispose();
        Console.WriteLine("[IvcMonitor] DisposeAsync complete");
    }
}

#else
public class IvcMonitorServiceLinux : IIvcMonitorService
{
    public event EventHandler<IvcStatusChangedEventArgs>? IvcStatusChanged;
    public bool IsIvcRunning => false;

    public void Start() 
    { }

    public void Stop() { }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
#endif