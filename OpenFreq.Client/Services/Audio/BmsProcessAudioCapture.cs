using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;
using Microsoft.Extensions.Logging;

namespace OpenFreqClient.Services.Audio;

/// <summary>
/// Captures the audio of a single process tree (Falcon BMS) via WASAPI process loopback and
/// exposes it as a 48 kHz mono 16-bit PCM stream through <see cref="Read"/>.
///
/// Unlike a regular loopback (whole render endpoint) this isolates one application's audio using
/// the PROCESS_LOOPBACK activation path (Windows 10 2004+). The audio engine downmixes/resamples
/// to the requested format thanks to AUTOCONVERTPCM, so callers always get mono 48 kHz shorts —
/// no manual resampling needed.
///
/// Windows-only. On other platforms <see cref="Start"/> is a no-op that returns false.
/// </summary>
public sealed class BmsProcessAudioCapture(ILogger<BmsProcessAudioCapture> logger) : IDisposable
{
    // Matches OpenFreqRtcClient.SAMPLE_RATE — the TX pipeline is mono 48 kHz 16-bit.
    private const int SampleRate = 48000;
    private const int Channels = 1;
    private const int BitsPerSample = 16;
    private const string ProcessName = "Falcon BMS";

    private Thread? _captureThread;
    private volatile bool _running;
    private readonly ManualResetEventSlim _ready = new(false);
    private bool _startSucceeded;

    // Lock-guarded mono ring buffer. Writer = capture thread, reader = BASS record callback.
    // 0.5 s headroom absorbs the rate mismatch between WASAPI packets and the record callback.
    private readonly object _ringLock = new();
    private readonly short[] _ring = new short[SampleRate / 2];
    private int _ringHead; // next write
    private int _ringTail; // next read
    private int _ringCount;

    public bool IsRunning => _running;

    /// <summary>
    /// Start capturing the BMS process audio. Returns true if capture is live.
    /// Safe to call when already running (no-op) or off-Windows (returns false).
    /// </summary>
    public bool Start()
    {
        if (_running) return true;
        if (!OperatingSystem.IsWindows())
        {
            logger.LogDebug("BMS audio capture requested on non-Windows platform — ignored");
            return false;
        }

        var pid = FindBmsProcessId();
        if (pid == 0)
        {
            logger.LogWarning("BMS audio capture: '{Process}' process not found", ProcessName);
            return false;
        }

        _ready.Reset();
        _startSucceeded = false;
        _running = true;

        // All COM work (activation + capture loop) stays on one MTA thread so apartment state is consistent.
        _captureThread = new Thread(() => CaptureLoop(pid))
        {
            Name = "BmsProcessAudioCapture",
            IsBackground = true
        };
        _captureThread.SetApartmentState(ApartmentState.MTA);
        _captureThread.Start();

        // Wait for activation+initialize to report back (bounded).
        if (!_ready.Wait(TimeSpan.FromSeconds(3)) || !_startSucceeded)
        {
            logger.LogWarning("BMS audio capture failed to start within timeout");
            Stop();
            return false;
        }

        ClearRing();
        logger.LogInformation("BMS audio capture started (PID {Pid})", pid);
        return true;
    }

    public void Stop()
    {
        if (!_running && _captureThread == null) return;
        _running = false;
        _captureThread?.Join(TimeSpan.FromSeconds(2));
        _captureThread = null;
        ClearRing();
        logger.LogInformation("BMS audio capture stopped");
    }

    /// <summary>
    /// Copy up to <paramref name="count"/> mono samples into <paramref name="dst"/>.
    /// Returns the number actually available (may be less on underrun). Never blocks.
    /// </summary>
    public int Read(short[] dst, int count)
    {
        count = Math.Min(count, dst.Length);
        lock (_ringLock)
        {
            int n = Math.Min(count, _ringCount);
            for (int i = 0; i < n; i++)
            {
                dst[i] = _ring[_ringTail];
                _ringTail = (_ringTail + 1) % _ring.Length;
            }
            _ringCount -= n;
            return n;
        }
    }

    private void ClearRing()
    {
        lock (_ringLock)
        {
            _ringHead = _ringTail = _ringCount = 0;
        }
    }

    private void Write(short[] src, int count)
    {
        lock (_ringLock)
        {
            for (int i = 0; i < count; i++)
            {
                _ring[_ringHead] = src[i];
                _ringHead = (_ringHead + 1) % _ring.Length;
                if (_ringCount == _ring.Length)
                    _ringTail = (_ringTail + 1) % _ring.Length; // overflow: drop oldest
                else
                    _ringCount++;
            }
        }
    }

    private static uint FindBmsProcessId()
    {
        try
        {
            var processes = Process.GetProcessesByName(ProcessName);
            if (processes.Length == 0) return 0;
            uint pid = (uint)processes[0].Id;
            foreach (var p in processes) p.Dispose();
            return pid;
        }
        catch
        {
            return 0;
        }
    }

    // Reached only from Start() after an OperatingSystem.IsWindows() guard.
    private void CaptureLoop(uint pid)
    {
        IAudioClient? audioClient = null;
        IAudioCaptureClient? captureClient = null;
        try
        {
            audioClient = ActivateProcessLoopbackClient(pid);

            var format = new WAVEFORMATEX
            {
                wFormatTag = WAVE_FORMAT_PCM,
                nChannels = Channels,
                nSamplesPerSec = SampleRate,
                wBitsPerSample = BitsPerSample,
                nBlockAlign = (ushort)(Channels * BitsPerSample / 8),
                nAvgBytesPerSec = (uint)(SampleRate * Channels * BitsPerSample / 8),
                cbSize = 0
            };

            int hr = audioClient.Initialize(
                AUDCLNT_SHAREMODE_SHARED,
                AUDCLNT_STREAMFLAGS_LOOPBACK | AUDCLNT_STREAMFLAGS_AUTOCONVERTPCM,
                0, 0, ref format, IntPtr.Zero);
            Marshal.ThrowExceptionForHR(hr);

            var captureIid = IID_IAudioCaptureClient;
            hr = audioClient.GetService(ref captureIid, out object svc);
            Marshal.ThrowExceptionForHR(hr);
            captureClient = (IAudioCaptureClient)svc;

            Marshal.ThrowExceptionForHR(audioClient.Start());

            _startSucceeded = true;
            _ready.Set();

            var scratch = new short[SampleRate / 10]; // 100 ms, grows if a packet is larger
            while (_running)
            {
                Marshal.ThrowExceptionForHR(captureClient.GetNextPacketSize(out uint packetFrames));
                if (packetFrames == 0)
                {
                    Thread.Sleep(8);
                    continue;
                }

                while (packetFrames != 0)
                {
                    hr = captureClient.GetBuffer(out IntPtr data, out uint frames, out uint flags, out _, out _);
                    Marshal.ThrowExceptionForHR(hr);

                    int count = (int)frames; // mono => 1 sample per frame
                    if (count > scratch.Length) scratch = new short[count];

                    if ((flags & AUDCLNT_BUFFERFLAGS_SILENT) != 0)
                        Array.Clear(scratch, 0, count);
                    else
                        Marshal.Copy(data, scratch, 0, count);

                    Write(scratch, count);
                    Marshal.ThrowExceptionForHR(captureClient.ReleaseBuffer(frames));
                    Marshal.ThrowExceptionForHR(captureClient.GetNextPacketSize(out packetFrames));
                }
            }

            audioClient.Stop();
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "BMS audio capture loop terminated");
            _running = false;
            _ready.Set(); // unblock Start() on early failure
        }
        finally
        {
            if (OperatingSystem.IsWindows())
            {
                if (captureClient != null) Marshal.ReleaseComObject(captureClient);
                if (audioClient != null) Marshal.ReleaseComObject(audioClient);
            }
        }
    }

    private static IAudioClient ActivateProcessLoopbackClient(uint pid)
    {
        var activationParams = new AUDIOCLIENT_ACTIVATION_PARAMS
        {
            ActivationType = AUDIOCLIENT_ACTIVATION_TYPE_PROCESS_LOOPBACK,
            TargetProcessId = pid,
            ProcessLoopbackMode = PROCESS_LOOPBACK_MODE_INCLUDE_TARGET_PROCESS_TREE
        };

        IntPtr blob = Marshal.AllocHGlobal(Marshal.SizeOf<AUDIOCLIENT_ACTIVATION_PARAMS>());
        try
        {
            Marshal.StructureToPtr(activationParams, blob, false);

            var prop = new PROPVARIANT
            {
                vt = VT_BLOB,
                cbSize = (uint)Marshal.SizeOf<AUDIOCLIENT_ACTIVATION_PARAMS>(),
                pBlobData = blob
            };

            var handler = new ActivateCompletionHandler();
            var iid = IID_IAudioClient;
            int hr = ActivateAudioInterfaceAsync(
                VIRTUAL_AUDIO_DEVICE_PROCESS_LOOPBACK, ref iid, ref prop, handler, out _);
            Marshal.ThrowExceptionForHR(hr);

            if (!handler.Completed.Wait(TimeSpan.FromSeconds(2)))
                throw new TimeoutException("ActivateAudioInterfaceAsync did not complete");

            Marshal.ThrowExceptionForHR(handler.ActivateResult);
            return (IAudioClient)handler.ActivatedInterface!;
        }
        finally
        {
            Marshal.FreeHGlobal(blob);
        }
    }

    public void Dispose() => Stop();

    // ---- WASAPI process-loopback COM interop ------------------------------------------------

    private const string VIRTUAL_AUDIO_DEVICE_PROCESS_LOOPBACK = "VAD\\Process_Loopback";

    private const int AUDIOCLIENT_ACTIVATION_TYPE_PROCESS_LOOPBACK = 1;
    private const int PROCESS_LOOPBACK_MODE_INCLUDE_TARGET_PROCESS_TREE = 0;

    private const int AUDCLNT_SHAREMODE_SHARED = 0;
    private const uint AUDCLNT_STREAMFLAGS_LOOPBACK = 0x00020000;
    private const uint AUDCLNT_STREAMFLAGS_AUTOCONVERTPCM = 0x80000000;
    private const uint AUDCLNT_BUFFERFLAGS_SILENT = 0x2;

    private const ushort WAVE_FORMAT_PCM = 1;
    private const ushort VT_BLOB = 65;

    private static Guid IID_IAudioClient = new("1CB9AD4C-DBFA-4c32-B178-C2F568A703B2");
    private static Guid IID_IAudioCaptureClient = new("C8ADBD64-E71E-48a0-A4DE-185C395CD317");

    [DllImport("Mmdevapi.dll", ExactSpelling = true, PreserveSig = true)]
    private static extern int ActivateAudioInterfaceAsync(
        [MarshalAs(UnmanagedType.LPWStr)] string deviceInterfacePath,
        ref Guid riid,
        ref PROPVARIANT activationParams,
        IActivateAudioInterfaceCompletionHandler completionHandler,
        out IActivateAudioInterfaceAsyncOperation operation);

    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    private struct WAVEFORMATEX
    {
        public ushort wFormatTag;
        public ushort nChannels;
        public uint nSamplesPerSec;
        public uint nAvgBytesPerSec;
        public ushort nBlockAlign;
        public ushort wBitsPerSample;
        public ushort cbSize;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct AUDIOCLIENT_ACTIVATION_PARAMS
    {
        public int ActivationType;
        public uint TargetProcessId;
        public int ProcessLoopbackMode;
    }

    // Matches PROPVARIANT byte layout for the VT_BLOB case on x86/x64.
    [StructLayout(LayoutKind.Sequential)]
    private struct PROPVARIANT
    {
        public ushort vt;
        public ushort wReserved1;
        public ushort wReserved2;
        public ushort wReserved3;
        public uint cbSize;       // blob.cbSize
        public IntPtr pBlobData;  // blob.pBlobData
    }

    [ComImport, Guid("41D949AB-9862-444A-80F6-C261334DA5EB"),
     InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IActivateAudioInterfaceCompletionHandler
    {
        void ActivateCompleted(IActivateAudioInterfaceAsyncOperation activateOperation);
    }

    [ComImport, Guid("72A22D78-CDE4-431D-B8CC-843A71199B6D"),
     InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IActivateAudioInterfaceAsyncOperation
    {
        void GetActivateResult(out int activateResult,
            [MarshalAs(UnmanagedType.IUnknown)] out object activatedInterface);
    }

    [ComImport, Guid("1CB9AD4C-DBFA-4c32-B178-C2F568A703B2"),
     InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IAudioClient
    {
        [PreserveSig] int Initialize(int shareMode, uint streamFlags, long bufferDuration,
            long periodicity, ref WAVEFORMATEX format, IntPtr audioSessionGuid);
        [PreserveSig] int GetBufferSize(out uint numBufferFrames);
        [PreserveSig] int GetStreamLatency(out long latency);
        [PreserveSig] int GetCurrentPadding(out uint numPaddingFrames);
        [PreserveSig] int IsFormatSupported(int shareMode, ref WAVEFORMATEX format, IntPtr closestMatch);
        [PreserveSig] int GetMixFormat(out IntPtr deviceFormat);
        [PreserveSig] int GetDevicePeriod(out long defaultPeriod, out long minimumPeriod);
        [PreserveSig] int Start();
        [PreserveSig] int Stop();
        [PreserveSig] int Reset();
        [PreserveSig] int SetEventHandle(IntPtr eventHandle);
        [PreserveSig] int GetService(ref Guid riid,
            [MarshalAs(UnmanagedType.IUnknown)] out object ppv);
    }

    [ComImport, Guid("C8ADBD64-E71E-48a0-A4DE-185C395CD317"),
     InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IAudioCaptureClient
    {
        [PreserveSig] int GetBuffer(out IntPtr data, out uint numFramesToRead,
            out uint flags, out ulong devicePosition, out ulong qpcPosition);
        [PreserveSig] int ReleaseBuffer(uint numFramesRead);
        [PreserveSig] int GetNextPacketSize(out uint numFramesInPacket);
    }

    private sealed class ActivateCompletionHandler : IActivateAudioInterfaceCompletionHandler
    {
        public readonly ManualResetEventSlim Completed = new(false);
        public int ActivateResult { get; private set; }
        public object? ActivatedInterface { get; private set; }

        public void ActivateCompleted(IActivateAudioInterfaceAsyncOperation activateOperation)
        {
            try
            {
                activateOperation.GetActivateResult(out int hr, out object iface);
                ActivateResult = hr;
                ActivatedInterface = iface;
            }
            catch (Exception ex)
            {
                ActivateResult = ex.HResult;
            }
            finally
            {
                Completed.Set();
            }
        }
    }
}
