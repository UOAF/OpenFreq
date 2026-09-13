using System;
using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;

namespace OpenFreq.Client.NativeMethods;

/// <summary>
/// Asks Windows to run this process's timers and sleeps at 1 ms resolution, until disposed.
/// </summary>
/// <remarks>
/// At the default resolution of about 15.6 ms, the time between 60 Hz <see cref="System.Threading.PeriodicTimer"/>
/// ticks can reach 30 ms, so the RCC poll can see a BMS PTT change late. BMS makes the same request for itself.
/// Since Windows 10 version 2004, a request applies only to the process that makes it.
/// </remarks>
internal sealed class Win32TimerResolution : IDisposable
{
    private const uint PeriodMs = 1;

    private const uint TIMERR_NOERROR = 0;
    private const int ProcessPowerThrottling = 4;
    private const uint PROCESS_POWER_THROTTLING_CURRENT_VERSION = 1;
    private const uint PROCESS_POWER_THROTTLING_IGNORE_TIMER_RESOLUTION = 0x4;

    private Win32TimerResolution()
    {
    }

    /// <returns>The request, or null when this isn't Windows or Windows refuses the resolution.</returns>
    public static Win32TimerResolution? Request(ILogger<Win32TimerResolution> logger)
    {
        if (!OperatingSystem.IsWindows()) return null;

        if (TimeBeginPeriod(PeriodMs) != TIMERR_NOERROR)
        {
            logger.LogWarning("Windows refused a {PeriodMs} ms timer resolution", PeriodMs);
            return null;
        }

        // By default, Windows 11 can ignore the request while this window is covered, minimized, or silent.
        // BMS in fullscreen covers the window, and that is when the PTT poll matters most.
        var throttling = new PROCESS_POWER_THROTTLING_STATE
        {
            Version = PROCESS_POWER_THROTTLING_CURRENT_VERSION,
            ControlMask = PROCESS_POWER_THROTTLING_IGNORE_TIMER_RESOLUTION,
            StateMask = 0
        };
        if (SetProcessInformation(GetCurrentProcess(), ProcessPowerThrottling, ref throttling,
                (uint)Marshal.SizeOf<PROCESS_POWER_THROTTLING_STATE>()))
        {
            logger.LogInformation("Timer resolution set to {PeriodMs} ms", PeriodMs);
        }
        else
        {
            // Windows versions before 11 never ignore the request, so on those versions this failure has no effect.
            logger.LogInformation(
                "Timer resolution set to {PeriodMs} ms, but Windows may ignore it while this window is hidden (Win32 error {Error})",
                PeriodMs, Marshal.GetLastPInvokeError());
        }

        return new Win32TimerResolution();
    }

    public void Dispose() => TimeEndPeriod(PeriodMs);

    [StructLayout(LayoutKind.Sequential)]
    private struct PROCESS_POWER_THROTTLING_STATE
    {
        public uint Version;
        public uint ControlMask;
        public uint StateMask;
    }

    [DllImport("winmm.dll", EntryPoint = "timeBeginPeriod")]
    private static extern uint TimeBeginPeriod(uint period);

    [DllImport("winmm.dll", EntryPoint = "timeEndPeriod")]
    private static extern uint TimeEndPeriod(uint period);

    [DllImport("kernel32.dll")]
    private static extern IntPtr GetCurrentProcess();

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool SetProcessInformation(IntPtr process, int processInformationClass,
        ref PROCESS_POWER_THROTTLING_STATE processInformation, uint processInformationSize);
}
