using Concentus.Structs;
using Microsoft.Extensions.Logging;
using OpenFreq.Common.Rtp;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;

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
    private readonly Timer _playoutTimer;

    // Prune stale sources every N ticks (20ms * 50 = 1s)
    private int _pruneCountdown = 50;

    private const int OPUS_FRAME_SAMPLES = 960; // 20ms at 48kHz (matches sender)
    public static int PLAYOUT_INTERVAL_MS = 20;

    public RtpAudioReceiver(ILoggerFactory loggerFactory, UdpClient udpClient, bool opusEnabled = true,
        int initialBufferMs = 150)
    {
        _logger = loggerFactory.CreateLogger<RtpAudioReceiver>();
        _opusEnabled = opusEnabled;

        _pool = new RtpJitterBufferPool(loggerFactory, opusEnabled, initialBufferMs);
        _pool.SourceAdded += ssrc => _logger.LogInformation("Source joined:  SSRC={Ssrc:X8}", ssrc);
        _pool.SourceExpired += ssrc => _logger.LogInformation("Source expired: SSRC={Ssrc:X8}", ssrc);

        _udpClient = udpClient;
        var port = (_udpClient.Client.LocalEndPoint as IPEndPoint)!.Port;
        _logger.LogInformation("Started on port {Port}", port);
        _logger.LogInformation("  Opus: {OpusEnabled}", _opusEnabled);
        _logger.LogInformation("  Initial buffer: {BufferMs}ms (adaptive, per-SSRC)", initialBufferMs);

        Task.Run(() => ReceiveLoop(), _cts.Token);
        _playoutTimer = new Timer(PlayoutTimerCallback, null, 0, PLAYOUT_INTERVAL_MS);

        if (!_logger.IsEnabled(LogLevel.Debug))
        {
            Task.Run(async () =>
                {
                    while (true)
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

        while (!_cts.Token.IsCancellationRequested)
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

            // Pool demuxes by SSRC automatically
            _pool.AddPacket(rtpPacket);
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
    private void PlayoutTimerCallback(object? state)
    {
        try
        {
            // Prune sources that have gone silent
            if (--_pruneCountdown <= 0)
            {
                _pool.PruneStale();
                _pruneCountdown = 50;
            }

            foreach (var context in _pool.GetActiveSources())
            {
                // One packet per SSRC per 20ms tick — burst delivery is handled
                // by AdjustPlayoutClock advancing the playout clock incrementally.
                if (context.JitterBuffer.GetNextPacket() is { } packet)
                {

                    // Loss detection and concealment — per-source sequence
                    if (context.FirstPacketReceived)
                    {
                        ushort expectedSeq = (ushort)(context.LastSequenceReceived + 1);

                        if (packet.SequenceNumber != expectedSeq)
                        {
                            int gap = RtpPacket.SequenceDifference(packet.SequenceNumber, expectedSeq);

                            if (gap > 0 && gap < 100)
                            {
                                _logger.LogDebug(
                                    "SSRC={Ssrc:X8}: {Gap} lost packet(s) (seq {Start} to {End}), generating concealment",
                                    context.Ssrc, gap, expectedSeq, packet.SequenceNumber - 1);

                                for (int i = 0; i < gap; i++)
                                {
                                    ushort lostSeq = (ushort)(expectedSeq + i);
                                    // Only the last lost packet can use FEC (next packet carries FEC for it)
                                    GenerateConcealmentAudio(lostSeq, i == gap - 1 ? packet : null, context);
                                }
                            }
                        }
                    }
                    else
                    {
                        context.FirstPacketReceived = true;
                    }

                    context.LastSequenceReceived = packet.SequenceNumber;
                    ProcessReadyPacket(packet, context);
                }
            }
        }
        catch (Exception ex)
        {
            ErrorOccurred?.Invoke(this, $"Playout error: {ex.Message}");
        }
    }

    /// <summary>
    /// Process a packet that is ready for playout, using the source's own Opus decoder.
    /// </summary>
    private void ProcessReadyPacket(RtpPacket packet, RtpSourceContext context)
    {
        try
        {
            if (packet.ExtensionData is not { Length: > 0 })
            {
                _logger.LogWarning("SSRC={Ssrc:X8}: missing RTP header extension metadata", context.Ssrc);
                return;
            }

            var metadataJson = Encoding.UTF8.GetString(packet.ExtensionData).TrimEnd('\0');
            var metadata = JsonSerializer.Deserialize(metadataJson, OpenFreqJsonContext.Default.AudioPacketMetadata);

            if (metadata == null)
            {
                _logger.LogWarning("SSRC={Ssrc:X8}: failed to parse metadata", context.Ssrc);
                return;
            }

            context.LastValidMetadata = metadata;

            Memory<short> decodedAudio;
            if (_opusEnabled && context.OpusDecoder != null)
            {
                decodedAudio = DecodeOpus(packet.Payload, context.OpusDecoder, decodeFec: false);
                if (decodedAudio.Length == 0)
                    return;
            }
            else
            {
                var buf = new short[packet.Payload.Length / 2];
                decodedAudio = new Memory<short>(buf);
                packet.Payload.CopyTo(MemoryMarshal.AsBytes(decodedAudio.Span));
            }

            #if DEBUG
            _logger.LogDebug($"Playing packet from {packet.Ssrc}: {packet.SequenceNumber}");
            #endif
            
            AudioReceived?.Invoke(this, new AudioReceivedEventArgs
            {
                AudioData = decodedAudio,
                Metadata = metadata
            });
        }
        catch (Exception ex)
        {
            ErrorOccurred?.Invoke(this, $"Ready packet processing error: {ex.Message}");
        }
    }

    /// <summary>
    /// Generate FEC or PLC concealment audio for a lost packet within one source's context.
    /// </summary>
    private void GenerateConcealmentAudio(ushort lostSequence, RtpPacket? nextPacket, RtpSourceContext context)
    {
        try
        {
            Memory<short> concealmentAudio;

            byte[] nextOpusData = nextPacket?.Payload ?? [];

            _logger.LogDebug(
                "SSRC={Ssrc:X8}: loss recovery for seq {LostSeq}: {Bytes} bytes",
                context.Ssrc, lostSequence, nextOpusData.Length);

            concealmentAudio = DecodeOpus(nextOpusData, context.OpusDecoder, decodeFec: nextPacket != null);

            if (concealmentAudio.Length == 0)
                return;

            var frequencies = new List<FrequencyTransmission>();
            if (context.LastValidMetadata?.Frequencies != null)
            {
                foreach (var freq in context.LastValidMetadata.Frequencies)
                {
                    frequencies.Add(new FrequencyTransmission(
                        khz: freq.Khz,
                        txPowerWatts: freq.TxPowerWatts,
                        ppm: freq.Ppm,
                        position: freq.Position,
                        velocity: freq.Velocity,
                        in3d: freq.In3d,
                        ambientNoiseType: freq.AmbientNoiseType
                    ));
                }
            }

            var metadata = new AudioPacketMetadata
            {
                ClientId = context.LastValidMetadata?.ClientId ?? "Recovered",
                Frequencies = frequencies
            };

            AudioReceived?.Invoke(this, new AudioReceivedEventArgs
            {
                AudioData = concealmentAudio,
                Metadata = metadata
            });
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "SSRC={Ssrc:X8}: failed to generate concealment for seq {LostSeq}",
                context.Ssrc, lostSequence);
        }
    }

    /// <summary>
    /// Decode Opus audio using the provided decoder instance (one per SSRC).
    /// </summary>
    private Memory<short> DecodeOpus(ReadOnlySpan<byte> opusData, OpusDecoder decoder, bool decodeFec)
    {
        try
        {
            short[] pcmSamples = new short[OPUS_FRAME_SAMPLES];
            var outSpan = new Memory<short>(pcmSamples);

            int samplesDecoded;

            // We're actually looking for the _previous_ packet,
            // which can be FEC'd into the next in case it gets lost.
            samplesDecoded = decoder.Decode(
                opusData, outSpan.Span, OPUS_FRAME_SAMPLES, decodeFec);

            if (samplesDecoded <= 0)
            {
                _logger.LogWarning("Opus decode failed for {ByteCount} bytes (decodeFec={DecodeFec})",
                    opusData.Length, decodeFec);
                return Memory<short>.Empty;
            }
            return outSpan[..samplesDecoded];
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Opus decode exception (decodeFec={DecodeFec})", decodeFec);
            return Memory<short>.Empty;
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

    public void SetJitterBufferSize(int milliseconds)
        => _pool.SetAllBufferSizes(milliseconds);

    public void Dispose()
    {
        _cts.Cancel();
        _playoutTimer.Dispose();
        PrintStatistics();
        _pool.Dispose();
        _logger.LogInformation("Disposed");
    }
}