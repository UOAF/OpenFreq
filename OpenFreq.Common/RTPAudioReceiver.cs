using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Threading.Channels;
using Microsoft.Extensions.Logging;
using OpenFreq.Common.Rtp;
using static OpenFreq.Common.RtpJitterBuffer;

// This is to avoid issues in Linux where the Concentus Factory methods does not work currently.
#pragma warning disable CS0618 // Type or member is obsolete

namespace OpenFreq.Common;

/// <summary>
/// RTP audio receiver with per-SSRC adaptive jitter buffering. Each concurrent talker (SSRC) gets an
/// independent jitter buffer and Opus decoder so their state never interferes.
/// Outputs clean, ordered PCM audio ready for RadioPlayback.
/// </summary>
public class RtpAudioReceiver : IDisposable
{
    public class AudioReceivedEventArgs : EventArgs
    {
        public Memory<short> AudioData { get; set; }
        public required AudioPacketMetadata Metadata { get; set; }
    }

    public event EventHandler<AudioReceivedEventArgs>? AudioReceived;
    public event EventHandler<string>? ErrorOccurred;

    private readonly ILogger<RtpAudioReceiver> _logger;
    private readonly UdpClient _udpClient;
    private readonly RtpJitterBufferPool _pool;
    private readonly bool _opusEnabled;

    private readonly CancellationTokenSource _cts = new();
    private readonly Task _rxTask;
    // NB: Draining the jitter buffer has no awaits, and blocks its thread
    // using Monitor.Wait(). (It wakes when packets are ready to leave
    // the jitter buffer, or when new packets were just fed in - see the
    // Monitor.Pulse() in the RX task.)
    // Give it a dedicated thread instead of betting on the goodwill of
    // the .NET runtime to notice when we're blocking one of the threads
    // in its async pool.
    private readonly Thread _jitterDrainThread;
    private readonly Task _playTask;

    public RtpAudioReceiver(ILoggerFactory loggerFactory, UdpClient udpClient, bool opusEnabled = true,
        int initialBufferMs = 150)
    {
        _logger = loggerFactory.CreateLogger<RtpAudioReceiver>();
        _opusEnabled = opusEnabled;

        var playChan = Channel.CreateBounded<AudioReceivedEventArgs>(new BoundedChannelOptions(128)
        {
            SingleReader = true,
            FullMode = BoundedChannelFullMode.DropOldest
        });

        _pool = new RtpJitterBufferPool(loggerFactory, _cts.Token, playChan.Writer, opusEnabled, initialBufferMs);
        _pool.SourceAdded += ssrc => _logger.LogInformation("Source joined:  SSRC={Ssrc:X8}", ssrc);
        _pool.SourceExpired += ssrc => _logger.LogInformation("Source expired: SSRC={Ssrc:X8}", ssrc);

        _udpClient = udpClient;
        var port = (_udpClient.Client.LocalEndPoint as IPEndPoint)!.Port;
        _logger.LogInformation("Started on port {Port}", port);
        _logger.LogInformation("  Opus: {OpusEnabled}", _opusEnabled);
        _logger.LogInformation("  Initial buffer: {BufferMs}ms (adaptive, per-SSRC)", initialBufferMs);

        _rxTask = Task.Run(() => ReceiveLoop(), _cts.Token);
        _jitterDrainThread = new Thread(DrainJitterBuffers);
        _jitterDrainThread.Start();
        _playTask = Task.Run(() => PlayTask(playChan.Reader));

        if (_logger.IsEnabled(LogLevel.Debug))
        {
            Task.Run(async () =>
                {
                    while (!_cts.IsCancellationRequested)
                    {
                        var stats = GetStatistics();
                        _logger.LogDebug("Network Stats (aggregated)");
                        _logger.LogDebug("  Packets: received={Received}, lost={Lost} ({LossPercent:F1}%)",
                            stats.received, stats.lost, stats.lossPercent);
                        _logger.LogDebug("  Jitter: {JitterMs:F1}ms (avg across sources)", stats.jitterMs);
                        _logger.LogDebug("  Buffer size: {BufferMs:F0}ms (avg, adaptive)", stats.bufferMs);
                        _logger.LogDebug("  Buffered packets: {Buffered}", stats.buffered);
                        await Task.Delay(5000);
                    }
                }
            );
        }
    }

    private async Task ReceiveLoop()
    {
        _logger.LogInformation("Receive loop started");

        while (!_cts.IsCancellationRequested)
        {
            try
            {
                var result = await _udpClient.ReceiveAsync(_cts.Token);
                ProcessIncomingPacket(result.Buffer);
            }
            catch (OperationCanceledException)
            {
                // Normal cancellation via CTS
                break;
            }
            catch (ObjectDisposedException)
            {
                // Socket was disposed during shutdown - expected, don't care
                break;
            }
            catch (SocketException ex) when (
                ex.SocketErrorCode == SocketError.OperationAborted ||
                ex.SocketErrorCode == SocketError.Interrupted ||
                ex.SocketErrorCode == SocketError.Shutdown)
            {
                // Socket was closed/aborted during shutdown, don't care either
                break;
            }
            catch (Exception ex)
            {
                // Actual unexpected error
                if (!_cts.Token.IsCancellationRequested)
                {
                    ErrorOccurred?.Invoke(this, $"Receive error: {ex.Message}");
                }
            }
        }

        _logger.LogInformation("Receive loop stopped");
    }

    private void ProcessIncomingPacket(byte[] data)
    {
        try
        {
            var rtpPacket = RtpPacket.Parse(data);
            if (rtpPacket == null)
            {
                _logger.LogWarning("Invalid RTP packet");
                return;
            }
            lock (_pool)
            {
                _pool.AddPacket(rtpPacket);
                // Wake the playback loop, we have new packets.
                Monitor.Pulse(_pool);
            }
        }
        catch (Exception ex)
        {
            ErrorOccurred?.Invoke(this, $"Packet processing error: {ex.Message}");
        }
    }

    /// <summary>
    /// Playout timer callback. Polls every active SSRC for a ready packet,
    /// performing per-source FEC/PLC concealment independently.
    /// </summary>
    private void DrainJitterBuffers()
    {
        try
        {
            while (!_cts.IsCancellationRequested)
            {
                var now = Stopwatch.GetTimestamp();

                // All the state we need to playback is in the jitter pool.
                // Runs mutually exclusive with adding a new packet.
                lock (_pool)
                {
                    _pool.PruneStale(now);
                    long? ticksToSleep = null;
                    bool handledPackets = false;
                    foreach (var context in _pool.GetActiveSources())
                    {
                        PacketsReadyResult rp = context.JitterBuffer.GetReadyPackets(now);
                        switch (rp)
                        {
                            case PacketsReady pr:
                                foreach (var p in pr.Packets)
                                {
                                    // Always succeeds; channel drops oldest on full.
                                    context.ToDecode.TryWrite(p);
                                }
                                handledPackets = true;
                                break;

                            // The jitter buffer detected a missing slot at the correct clock position.
                            // Inject an empty packet so the decoder fills the hole now, not one frame late.
                            // The payload carries N+1's Opus bytes when available so the decoder can do FEC recovery instead of falling back to PLC.
                            case ConcealmentNeeded cn:
                                context.ToDecode.TryWrite(new SequencedPacket
                                {
                                    Ssrc = context.Ssrc,
                                    SequenceNumber = 0,
                                    Timestamp = 0,
                                    Payload = cn.FecPayload ?? [],
                                    Metadata = null,
                                    IsConcealment = true,
                                });
                                handledPackets = true;
                                break;

                            case WaitFor wf:
                                if (ticksToSleep.HasValue)
                                {
                                    ticksToSleep = Math.Min(ticksToSleep.Value, wf.Ticks);
                                }
                                else
                                {
                                    ticksToSleep = wf.Ticks;
                                }
                                break;

                            case NoPackets:
                                break;

                            default: throw new UnreachableException();
                        }
                    }

                    // If we handled some packets,
                    // go again immediately, we might have more.
                    if (handledPackets) continue;
                    // Otherwise wait until something happens - a new packet,
                    // some ready to play, etc.
                    if (ticksToSleep.HasValue)
                    {
                        var t = ticksToSleep.Value;
                        var ms = (int)((double)t / Stopwatch.Frequency * 1000.0);
                        if (ms > 0)
                        {
                            Monitor.Wait(_pool, ms);
                        }
                    }
                    else
                    {
                        Monitor.Wait(_pool);
                    }
                }
            }
        }
        catch (Exception ex)
        {
            ErrorOccurred?.Invoke(this, $"Playout error: {ex.Message}");
        }
    }

    private async Task PlayTask(ChannelReader<AudioReceivedEventArgs> chan)
    {
        while (!_cts.IsCancellationRequested)
        {
            try
            {
                var e = await chan.ReadAsync(_cts.Token);
                AudioReceived?.Invoke(this, e);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                ErrorOccurred?.Invoke(this, $"Playout error: {ex.Message}");
            }
        }
    }

    public (int received, int lost, int late, int duplicate, int played,
        double lossPercent, double jitterMs, double bufferMs, int buffered) GetStatistics()
        => _pool.GetStatistics();

    public void PrintStatistics()
    {
        var stats = GetStatistics();
        _logger.LogInformation("=== RTP Receiver Statistics ===");
        _logger.LogInformation("  Packets received: {Received}", stats.received);
        _logger.LogInformation("  Packets lost: {Lost} ({LossPercent:F2}%)", stats.lost, stats.lossPercent);
        _logger.LogInformation("  Packets late: {Late}", stats.late);
        _logger.LogInformation("  Packets duplicate: {Duplicate}", stats.duplicate);
        _logger.LogInformation("  Packets played: {Played}", stats.played);
        _logger.LogInformation("  Loss Recovery:");

        _logger.LogInformation("  Measured jitter: {JitterMs:F1}ms (avg)", stats.jitterMs);
        _logger.LogInformation("  Buffer size: {BufferMs:F0}ms (avg, adaptive)", stats.bufferMs);
        _logger.LogInformation("  Currently buffered: {Buffered} packets", stats.buffered);

        foreach (var s in _pool.GetPerSourceStatistics())
        {
            _logger.LogInformation(
                "  SSRC={Ssrc:X8}: rx={Rx} lost={Lost} ({Loss:F1}%) jitter={Jitter:F1}ms buffer={Buffer:F0}ms buffered={Buffered}",
                s.ssrc, s.received, s.lost, s.lossPercent, s.jitterMs, s.bufferMs, s.buffered);
        }

        _logger.LogInformation("================================");
    }

    public void Dispose()
    {
        // 1. Signal everything to stop.
        _cts.Cancel();
        // 2. Wake the drain thread if it's in Monitor.Wait.
        lock (_pool)
        {
            Monitor.PulseAll(_pool);
        }
        // 3. No more packets entering the system.
        _rxTask.Wait();
        // 4. Drain thread done — no more packets dispatched to decode channels.
        _jitterDrainThread.Join();
        // 5. Play task already exited (cancellation token in ReadAsync).
        _playTask.Wait();
        // 6. All tasks quiesced — stats are stable. Must run before pool.Dispose
        //    clears _sources.
        PrintStatistics();
        // 7. Complete each source's decode channel, wait for its decoder task
        //    to finish, then dispose its Opus decoder.
        _pool.Dispose();
        _cts.Dispose();
        _logger.LogInformation("Disposed");
    }
}
