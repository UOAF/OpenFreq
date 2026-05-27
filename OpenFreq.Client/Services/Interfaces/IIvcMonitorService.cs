using System;
using OpenFreq.Client.Services.Interfaces;

namespace OpenFreqClient.Services.Interfaces;

public interface IIvcMonitorService : ILifecycleService, IAsyncDisposable
{
    event EventHandler<IvcStatusChangedEventArgs>? IvcStatusChanged;
    bool IsIvcRunning { get; }
    void KillIvc();
}

public class IvcStatusChangedEventArgs : EventArgs
{
    public required bool IsRunning { get; init; }
}
