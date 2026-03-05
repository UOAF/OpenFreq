/*
 * OpenFreq Server Test Client
 * Simple single-file client for testing OpenFreq Server with BASS audio
 *
 * Usage: dotnet run <server-ip> <password> <frequency-khz>
 * Example: dotnet run 127.0.0.1 changeMe123 334000 307300
 *
 * Controls:
 * - Press SPACE to transmit (Push-to-Talk)
 * - Press Q to quit
 */

using System.Runtime.InteropServices;
using ManagedBass;
using Microsoft.Extensions.Logging;
using OpenFreq.Common;
using ErrorEventArgs = OpenFreq.Common.ErrorEventArgs;

namespace OpenFreq.TestClient;

public class TestClient
{
    public static async Task Main(string[] args)
    {
        string serverIp = args.Length > 0 ? args[0] : "127.0.0.1";
        string password = args.Length > 1 ? args[1] : "changeMe123";
        
        // Parse frequencies from command line (in kHz) or use defaults
        List<int> frequencies = new();
        if (args.Length > 2)
        {
            for (int i = 2; i < args.Length; i++)
            {
                if (int.TryParse(args[i], out int freq))
                {
                    frequencies.Add(freq);
                }
            }
        }
        
        // Default frequencies if none provided
        if (frequencies.Count == 0)
        {
            frequencies = [334000, 307300]; // 334.0 MHz, 307.3 MHz
        }

        Console.WriteLine("╔════════════════════════════════════════╗");
        Console.WriteLine("║   OpenFreq Server Test Client (BASS)   ║");
        Console.WriteLine("╚════════════════════════════════════════╝");
        Console.WriteLine();
        Console.WriteLine($"Server: {serverIp}");
        Console.Write($"Frequencies: ");
        foreach (var freq in frequencies)
        {
            Console.Write($"{freq / 1000.0:F3} MHz ");
        }
        Console.WriteLine();
        Console.WriteLine();

        // Initialize BASS
        if (!Bass.Init())
        {
            Console.WriteLine($"Failed to initialize BASS: {Bass.LastError}");
            return;
        }

        Console.WriteLine("BASS initialized successfully");

        if (Bass.RecordingDeviceCount == 0)
        {
            Console.WriteLine("No recording device found");
            Bass.Free();
            return;
        }

        // Get recording device
        var recordDevice = 0;
        Console.WriteLine($"Using {Bass.RecordGetDeviceInfo(recordDevice).Name}");
        Bass.RecordInit(recordDevice);

        using ILoggerFactory factory = LoggerFactory.Create(builder =>
        {
            builder.AddConsole();
            builder.SetMinimumLevel(LogLevel.Debug);
        });

        // Create client
        var client = new OpenFreqRtcClient(factory, serverIp, password);
        var testClient = new TestClientWrapper(client, frequencies);

        try
        {
            await testClient.ConnectAsync();

            Console.WriteLine();
            Console.WriteLine("═══════════════════════════════════════");
            Console.WriteLine("  Controls:");
            Console.WriteLine("  [SPACE] - Push to Talk");
            Console.WriteLine("  [Q] - Quit");
            Console.WriteLine("═══════════════════════════════════════");
            Console.WriteLine();

            await testClient.RunAsync();
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Error: {ex.Message}");
        }
        finally
        {
            testClient.Dispose();
            Bass.Free();
        }
    }
}

// ============================================================================
// TestClientWrapper Class - Wraps OpenFreqRtcClient with BASS audio
// ============================================================================

public class TestClientWrapper : IDisposable
{
    const int CHANNELS = 1;
    
    private readonly Queue<byte> _audioBuffer = new();
    private readonly int OPUS_FRAME_BYTES = 1920;

    private readonly OpenFreqRtcClient _client;
    private readonly List<int> _frequencies;
    private readonly Dictionary<int, bool> _isTransmitting = new();
    private bool _isRunning;
    private CancellationTokenSource _cts = new();

    // BASS handles
    private int _recordHandle;
    private int _playbackStream;

    public TestClientWrapper(OpenFreqRtcClient client, List<int> frequencies)
    {
        _client = client;
        _frequencies = frequencies;
        
        foreach (var freq in frequencies)
        {
            _isTransmitting[freq] = false;
        }

        // Hook up events
        _client.ConnectionStateChanged += OnConnectionStateChanged;
        _client.Authenticated += OnAuthenticated;
        _client.FrequencyJoined += OnFrequencyJoined;
        _client.FrequencyLeft += OnFrequencyLeft;
        _client.PeerJoined += OnPeerJoined;
        _client.PeerLeft += OnPeerLeft;
        _client.PeerTransmissionStateChanged += OnPeerTransmissionStateChanged;
        _client.AudioDataReceived += OnAudioDataReceived;
        _client.ErrorOccurred += OnError;
        _client.TransmissionStateChanged += OnTransmissionStateChanged;
    }

    public async Task ConnectAsync()
    {
        // Connect to server
        await _client.ConnectAsync();
        Console.WriteLine("✓ WebSocket connected");
        Console.WriteLine($"✓ Authenticated (Peer ID: {_client.MyPeerId})");
        Console.WriteLine($"✓ Audio port assigned: {_client.AudioPort}");

        // Join frequencies
        foreach (var freq in _frequencies)
        {
            await _client.JoinFrequencyAsync(freq);
            await Task.Delay(300);
            Console.WriteLine($"✓ Joined frequency {freq / 1000.0:F3} MHz");
        }

        // Initialize playback stream
        _playbackStream = Bass.CreateStream(OpenFreqRtcClient.SAMPLE_RATE, CHANNELS, BassFlags.Default, StreamProcedureType.Push);
        if (_playbackStream == 0)
        {
            throw new Exception($"Failed to create playback stream: {Bass.LastError}");
        }

        // Pre-fill with silence to prevent initial stuttering
        for (int i = 0; i < 4; i++)
        {
            byte[] silence = new byte[1920];
            Bass.StreamPutData(_playbackStream, silence, silence.Length);
        }
        Bass.ChannelPlay(_playbackStream, false);
        Console.WriteLine("✓ Audio playback ready");
    }

    public async Task RunAsync()
    {
        _isRunning = true;
        DateTime? lastSpaceTime = null;
        const int SpaceReleaseDelayMs = 150;

        // Input loop
        while (_isRunning)
        {
            // Consume all available keys in the buffer
            while (Console.KeyAvailable)
            {
                var key = Console.ReadKey(true);

                if (key.Key == ConsoleKey.Spacebar)
                {
                    lastSpaceTime = DateTime.UtcNow;
                }
                else if (key.Key == ConsoleKey.Q)
                {
                    await StopTransmissionAsync();
                    _isRunning = false;
                    break;
                }
            }

            if (!_isRunning) break;

            // Determine if space is "currently held" based on recent activity
            bool spaceIsHeld = lastSpaceTime.HasValue && 
                               (DateTime.UtcNow - lastSpaceTime.Value).TotalMilliseconds < SpaceReleaseDelayMs;

            // Handle transmission
            bool anyTransmitting = _isTransmitting.Values.Any(x => x);
            
            if (spaceIsHeld && !anyTransmitting)
            {
                await StartTransmissionAsync();
            }
            else if (!spaceIsHeld && anyTransmitting)
            {
                await StopTransmissionAsync();
                lastSpaceTime = null;
            }

            await Task.Delay(50);
        }
    }

    private async Task StartTransmissionAsync()
    {
        Bass.CurrentRecordingDevice = 0;

        // Start recording with callback that sends via client
        _recordHandle = Bass.RecordStart(OpenFreqRtcClient.SAMPLE_RATE, CHANNELS, BassFlags.RecordPause, RecordProcedure);
        if (_recordHandle == 0)
        {
            Console.WriteLine($"Failed to start recording: {Bass.LastError}");
            return;
        }

        Bass.ChannelPlay(_recordHandle);

        // Tell client to start transmission on all frequencies
        foreach (var frequency in _frequencies)
        {
            await _client.StartTransmissionAsync(frequency);
            _isTransmitting[frequency] = true;
        }
    }

    private async Task StopTransmissionAsync()
    {
        bool anyTransmitting = _isTransmitting.Values.Any(x => x);
        if (!anyTransmitting) return;

        // Stop recording
        if (_recordHandle != 0)
        {
            Bass.ChannelStop(_recordHandle);
            Bass.StreamFree(_recordHandle);
            _recordHandle = 0;
        }

        // Tell client to stop transmission on all frequencies
        foreach (var frequency in _frequencies)
        {
            if (_isTransmitting[frequency])
            {
                await _client.StopTransmissionAsync(frequency);
                _isTransmitting[frequency] = false;
            }
        }
    }

    private bool RecordProcedure(int handle, IntPtr buffer, int length, IntPtr user)
    {
        bool anyTransmitting = _isTransmitting.Values.Any(x => x);
        if (!anyTransmitting)
            return true;

        try
        {
            // Copy audio data to managed array
            byte[] audioData = new byte[length];
            Marshal.Copy(buffer, audioData, 0, length);

            // Add to buffer
            foreach (byte b in audioData)
            {
                _audioBuffer.Enqueue(b);
            }

            // Process complete frames
            while (_audioBuffer.Count >= OPUS_FRAME_BYTES)
            {
                // Extract exactly one frame
                byte[] frameData = new byte[OPUS_FRAME_BYTES];
                for (int i = 0; i < OPUS_FRAME_BYTES; i++)
                {
                    frameData[i] = _audioBuffer.Dequeue();
                }

                // Send complete frame to all transmitting frequencies
                var transmitData = new List<(int frequencyKhz, double txPowerWatts, double ppm, Vector3? position, Vector3? velocity)>();
                foreach (var frequency in _isTransmitting.Keys)
                {
                    transmitData.Add((frequency, 50, 0, new Vector3(), null));
                }
                _client.SendAudio(frameData, transmitData, false);
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Error sending audio: {ex.Message}");
        }

        return true;
    }

    // Event handlers
    private void OnConnectionStateChanged(object? sender, ConnectionStateChangedEventArgs e)
    {
        // Connection state changes are logged by the client
    }

    private void OnAuthenticated(object? sender, AuthenticationEventArgs e)
    {
        // Already logged in ConnectAsync
    }

    private void OnFrequencyJoined(object? sender, FrequencyJoinedEventArgs e)
    {
        // Already logged in ConnectAsync
    }

    private void OnFrequencyLeft(object? sender, FrequencyLeftEventArgs e)
    {
        Console.WriteLine($"[LEFT FREQUENCY] {e.FrequencyKhz / 1000.0:F3} MHz");
    }

    private void OnPeerJoined(object? sender, PeerEventArgs e)
    {
        var shortId = e.PeerId.Length > 8 ? e.PeerId[..8] : e.PeerId;
        Console.WriteLine($"[PEER JOINED] {shortId} on {e.FrequencyKhz / 1000.0:F3} MHz");
    }

    private void OnPeerLeft(object? sender, PeerEventArgs e)
    {
        var shortId = e.PeerId.Length > 8 ? e.PeerId[..8] : e.PeerId;
        Console.WriteLine($"[PEER LEFT] {shortId} from {e.FrequencyKhz / 1000.0:F3} MHz");
    }

    private void OnPeerTransmissionStateChanged(object? sender, PeerTransmissionEventArgs e)
    {
        var state = e.IsTransmitting ? "TRANSMITTING" : "STOPPED";
        var shortId = e.PeerId.Length > 8 ? e.PeerId[..8] : e.PeerId;
        Console.WriteLine($"[PEER {shortId}] {state} on {e.FrequencyKhz / 1000.0:F3} MHz");
    }

    private void OnTransmissionStateChanged(object? sender, TransmissionStateEventArgs e)
    {
        var state = e.IsTransmitting ? "TRANSMITTING" : "STOPPED";
        Console.WriteLine($"[{state}] on {e.FrequencyKhz / 1000.0:F3} MHz");
    }

    private void OnAudioDataReceived(object? sender, AudioDataEventArgs e)
    {
        if (_playbackStream == 0) return;

        long bufferLevel = Bass.ChannelGetData(_playbackStream, IntPtr.Zero, (int)DataFlags.Available);
        double bufferMs = (bufferLevel / 96000.0) * 1000.0;

        const double MAX_BUFFER_MS = 500;

        if (bufferMs > MAX_BUFFER_MS)
        {
            // Flush and start fresh to prevent excessive latency
            Bass.ChannelSetPosition(_playbackStream, 0);
            Console.WriteLine($"[BUFFER RESET] Was {bufferMs:F1}ms");
        }
    
        Bass.StreamPutData(_playbackStream, e.AudioData, e.AudioData.Length);
    }

    private void OnError(object? sender, ErrorEventArgs errorEventArgs)
    {
        Console.WriteLine($"[ERROR] {errorEventArgs.ErrorMessage}");
    }

    public void Dispose()
    {
        _isRunning = false;
        _cts.Cancel();
        _cts.Dispose();

        if (_recordHandle != 0)
        {
            Bass.ChannelStop(_recordHandle);
            Bass.StreamFree(_recordHandle);
        }

        if (_playbackStream != 0)
        {
            Bass.ChannelStop(_playbackStream);
            Bass.StreamFree(_playbackStream);
        }

        _client.Dispose();

        Console.WriteLine("Disconnected");
    }
}