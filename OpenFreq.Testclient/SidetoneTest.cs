using System;
using System.Threading;
using ManagedBass;
using Microsoft.Extensions.Logging;
using OpenFreqAudio;

namespace SidetoneTest;

/// <summary>
/// Simple console test for SidetonePlayback
/// Press and hold SPACE to hear sidetone (your microphone)
/// Press ESC to exit
/// </summary>
class Program
{
    private static SidetonePlayback? _sidetonePlayback;
    private static int _recordingHandle;
    private static bool _isRecording = false;
    private static readonly object _lock = new();

    // Mock frequency configuration
    private static readonly Dictionary<int, RadioPlayback.FrequencyConfig> _frequencyConfigs = new();

    private static ILoggerFactory loggerFactory = LoggerFactory.Create(builder => builder.AddConsole());

    static void Main(string[] args)
    {
        Console.WriteLine("╔═══════════════════════════════════════════════════════════╗");
        Console.WriteLine("║         SIDETONE PLAYBACK TEST PROGRAM                    ║");
        Console.WriteLine("╚═══════════════════════════════════════════════════════════╝");
        Console.WriteLine();

        try
        {
            // Initialize
            if (!Initialize())
            {
                Console.WriteLine("Initialization failed. Press any key to exit.");
                Console.ReadKey();
                return;
            }

            Console.WriteLine();
            Console.WriteLine("═══════════════════════════════════════════════════════════");
            Console.WriteLine("  CONTROLS:");
            Console.WriteLine("  - HOLD SPACE: Transmit (hear sidetone)");
            Console.WriteLine("  - PRESS ESC: Exit");
            Console.WriteLine("═══════════════════════════════════════════════════════════");
            Console.WriteLine();
            Console.WriteLine("Ready! Press and hold SPACE to test sidetone...");
            Console.WriteLine();

            // Main loop
            bool running = true;
            bool wasSpacePressed = false;

            while (running)
            {
                if (Console.KeyAvailable)
                {
                    var key = Console.ReadKey(true);

                    if (key.Key == ConsoleKey.Escape)
                    {
                        running = false;
                    }
                }

                // Check space bar state
                bool isSpacePressed = (GetAsyncKeyState(0x20) & 0x8000) != 0; // VK_SPACE = 0x20

                if (isSpacePressed && !wasSpacePressed)
                {
                    // Space just pressed
                    StartTransmit();
                    wasSpacePressed = true;
                }
                else if (!isSpacePressed && wasSpacePressed)
                {
                    // Space just released
                    StopTransmit();
                    wasSpacePressed = false;
                }

                Thread.Sleep(10); // Small delay to avoid busy-waiting
            }

            // Cleanup
            Console.WriteLine();
            Console.WriteLine("Shutting down...");
            if (_isRecording)
            {
                StopTransmit();
            }

            Cleanup();

            Console.WriteLine("Goodbye!");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"ERROR: {ex.Message}");
            Console.WriteLine(ex.StackTrace);
            Console.WriteLine();
            Console.WriteLine("Press any key to exit.");
            Console.ReadKey();
        }
    }

    static bool Initialize()
    {
        Console.WriteLine("[Init] Initializing BASS...");

        // Initialize BASS (for both recording and playback)
        if (!Bass.Init(-1, 48000, DeviceInitFlags.Default, IntPtr.Zero))
        {
            Console.WriteLine($"[Init] BASS initialization failed: {Bass.LastError}");
            return false;
        }

        Console.WriteLine($"[Init] BASS initialized successfully");
        Console.WriteLine($"[Init] Sample rate: 48000 Hz");
        Console.WriteLine($"[Init] Output device: {Bass.GetDeviceInfo(Bass.CurrentDevice).Name}");

        // Initialize recording
        if (!Bass.RecordInit(-1))
        {
            Console.WriteLine($"[Init] Recording initialization failed: {Bass.LastError}");
            return false;
        }

        var recordDevice = Bass.RecordGetDeviceInfo(Bass.CurrentRecordingDevice);
        Console.WriteLine($"[Init] Recording device: {recordDevice.Name}");

        // Setup mock frequency configuration
        _frequencyConfigs[251000] = new RadioPlayback.FrequencyConfig
        {
            Volume = 1f,
            AudioChannel = RadioPlayback.AudioChannel.Both,
            IsTuned = true
        };

        // Create SidetonePlayback
        Console.WriteLine("[Init] Creating SidetonePlayback...");
        _sidetonePlayback = new SidetonePlayback(loggerFactory.CreateLogger<SidetonePlayback>(),
            sampleRate: 48000,
            channels: 2,
            getFrequencyConfig: GetFrequencyConfig,
            deviceIndex: -1
        );

        Console.WriteLine("[Init] SidetonePlayback created successfully");

        return true;
    }

    static RadioPlayback.FrequencyConfig? GetFrequencyConfig(int frequencyKHz)
    {
        lock (_lock)
        {
            return _frequencyConfigs.TryGetValue(frequencyKHz, out var config) ? config : null;
        }
    }

    static void StartTransmit()
    {
        lock (_lock)
        {
            if (_isRecording) return;

            Console.WriteLine("[TX] ▶ TRANSMIT STARTED - You should hear yourself now");

            // Create sidetone stream
            _sidetonePlayback?.CreateStream(
                streamId: "test_sidetone",
                frequencyKHz: 251000,
                channels: 1, // Mono mic input
                volume: 0.5f, // 50% sidetone volume
                bufferMs: 10 // 10ms buffer for low latency
            );

            // Start recording
            _recordingHandle = Bass.RecordStart(
                48000, // Sample rate
                1, // Mono
                BassFlags.Float, // 32-bit float samples
                RecordingCallback,
                IntPtr.Zero
            );

            if (_recordingHandle == 0)
            {
                Console.WriteLine($"[TX] Recording start failed: {Bass.LastError}");
                return;
            }

            _isRecording = true;
        }
    }

    static void StopTransmit()
    {
        lock (_lock)
        {
            if (!_isRecording) return;

            Console.WriteLine("[TX] ■ TRANSMIT STOPPED");

            // Stop recording
            if (_recordingHandle != 0)
            {
                Bass.ChannelStop(_recordingHandle);
                _recordingHandle = 0;
            }

            // Stop sidetone
            _sidetonePlayback?.StopStream("test_sidetone");

            _isRecording = false;
        }
    }

    static bool RecordingCallback(int handle, IntPtr buffer, int length, IntPtr user)
    {
        if (_sidetonePlayback == null) return true;

        // Convert float samples to 16-bit PCM for SidetonePlayback
        int floatCount = length / sizeof(float);
        float[] floatSamples = new float[floatCount];
        System.Runtime.InteropServices.Marshal.Copy(buffer, floatSamples, 0, floatCount);

        // Convert to 16-bit PCM bytes
        byte[] pcmBytes = new byte[floatCount * 2];
        for (int i = 0; i < floatCount; i++)
        {
            short sample = (short)(Math.Clamp(floatSamples[i], -1f, 1f) * 32767f);
            pcmBytes[i * 2] = (byte)(sample & 0xFF);
            pcmBytes[i * 2 + 1] = (byte)((sample >> 8) & 0xFF);
        }

        // Push to sidetone
        _sidetonePlayback.PushAudioData("test_sidetone", pcmBytes);

        return true; // Continue recording
    }

    static void Cleanup()
    {
        Console.WriteLine("[Cleanup] Disposing SidetonePlayback...");
        _sidetonePlayback?.Dispose();

        Console.WriteLine("[Cleanup] Stopping BASS recording...");
        Bass.RecordFree();

        Console.WriteLine("[Cleanup] Stopping BASS...");
        Bass.Free();

        Console.WriteLine("[Cleanup] Complete");
    }

    // Windows API for checking key state
    [System.Runtime.InteropServices.DllImport("user32.dll")]
    static extern short GetAsyncKeyState(int vKey);
}