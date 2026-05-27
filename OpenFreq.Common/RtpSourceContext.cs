using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Threading.Channels;
using Concentus.Structs;
using Microsoft.Extensions.Logging;
using static OpenFreq.Common.RtpAudioReceiver;

namespace OpenFreq.Common;

/// <summary>
/// Encapsulates all state belonging to a single RTP synchronization source (SSRC).
/// Each concurrent talker gets an independent context so that jitter buffering,
/// Opus decoder state, and loss-concealment tracking never cross-contaminate
/// between streams.
/// </summary>
public sealed class RtpSourceContext : IDisposable
{
    public uint Ssrc { get; }

    // TODO: This is a crummy encapsulation boundary:
    // RTPJitterBufferPool fishes the jitter buffer out of this context,
    // adds packets to it, then stuffs them back in for decode.
    // (It was no better before, when RTPAudioReceiver just juggled
    // the context's guts directly.)
    //
    // Split these out better?
    public RtpJitterBuffer JitterBuffer { get; }
    /// <summary>
    /// Write to this channel to feed packets from the jitter buffer
    /// into the decoder task.
    /// </summary>
    public ChannelWriter<SequencedPacket> ToDecode { get; }
    /// <summary>
    /// Used by the decoder task to feed the AudioReceived delegate.
    /// </summary>
    /// <remarks>
    /// Under no circumstances should we close this;
    /// it's chared by all contexts.
    /// </remarks>
    private ChannelWriter<AudioReceivedEventArgs> ToPlay { get; }
    private OpusDecoder? OpusDecoder { get; }
    private Task DecoderTask { get; }

    // Last valid metadata from this source, used to reconstruct concealment packets
    private AudioPacketMetadata? LastValidMetadata { get; set; }

    // Used by the pool to prune sources that have gone silent
    public long LastActivityTicks { get; set; }

    // Help I'm trapped in an abstract object factory. If you get this, send help.
    private ILogger<RtpSourceContext> Logger { get; }

    public RtpSourceContext(
        uint ssrc,
        ILoggerFactory loggerFactory,
        CancellationToken ct,
        ChannelWriter<AudioReceivedEventArgs> player,
        bool opusEnabled,
        int initialBufferMs)
    {
        Logger = loggerFactory.CreateLogger<RtpSourceContext>();
        Ssrc = ssrc;

        LastActivityTicks = Stopwatch.GetTimestamp();

        JitterBuffer = new RtpJitterBuffer(loggerFactory.CreateLogger<RtpJitterBuffer>());
        JitterBuffer.SetTargetBufferSize(initialBufferMs);

        if (opusEnabled)
        {
#pragma warning disable CS0618 // Using the new factory method will not work on Linux
            OpusDecoder = new OpusDecoder(OpenFreqRtcClient.SAMPLE_RATE, 1);
#pragma warning restore CS0618
        }

        // Wire up our task and off we go.
        var decChan = Channel.CreateBounded<SequencedPacket>(new BoundedChannelOptions(128)
        {
            SingleReader = true,
            FullMode = BoundedChannelFullMode.DropOldest
        });
        ToPlay = player;
        ToDecode = decChan.Writer;
        DecoderTask = Task.Run(() => DecodeLoop(ct, decChan.Reader));
    }

    private async Task DecodeLoop(CancellationToken ct, ChannelReader<SequencedPacket> chan)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                var p = await chan.ReadAsync(ct);
#if DEBUG
                Logger.LogDebug(
                    "[RTPTRACE 4/6] decode-in  SSRC={Ssrc:X8} seq={Seq} concealment={Conceal}",
                    p.Ssrc, p.SequenceNumber, p.IsConcealment);
#endif

                // FEC/PLC — the drain thread detected a missing slot at  the correct clock position and injected this sentinel.
                // p.Payload is non-empty when N+1 was already buffered and carries FEC bits; in that case use FEC recovery.
                // p.Payload is empty for burst-loss or first-frame loss -> use PLC.
                if (p.IsConcealment)
                {
                    if (LastValidMetadata != null)
                    {
                        // Wrap payload in a minimal packet so GenerateConcealmentAudio  can reach it via nextPacket?.Payload; null signals pure PLC.
                        SequencedPacket? fecPacket = p.Payload.Length > 0 ? p : null;
                        Logger.LogDebug(
                            "SSRC={Ssrc:X8}: proactive {Mode}",
                            Ssrc, fecPacket != null ? "FEC" : "PLC");
                        await GenerateConcealmentAudio(ct, fecPacket);
                    }
                    continue;
                }

                await ProcessReadyPacket(ct, p);
            }
            catch (OperationCanceledException) { break; }
            catch (ChannelClosedException) { break; }
            catch (Exception ex)
            {
                // Don't let a single decode failure permanently kill this SSRC's audio.
                Logger.LogError(ex, "SSRC={Ssrc:X8}: unhandled exception in decode loop", Ssrc);
            }
        }
    }

    /// <summary>
    /// Process a packet that is ready for playout, using the source's own Opus decoder.
    /// </summary>
    private async Task ProcessReadyPacket(CancellationToken ct, SequencedPacket packet)
    {
        if (packet.Metadata is not { Length: > 0 })
        {
            Logger.LogWarning("SSRC={Ssrc:X8}: missing RTP header extension metadata", Ssrc);
            return;
        }

        var metadataJson = System.Text.Encoding.UTF8.GetString(packet.Metadata).TrimEnd('\0');
        var metadata = JsonSerializer.Deserialize(metadataJson, OpenFreqJsonContext.Default.AudioPacketMetadata);

        if (metadata == null)
        {
            Logger.LogWarning("SSRC={Ssrc:X8}: failed to parse metadata", Ssrc);
            return;
        }

        LastValidMetadata = metadata;

        Memory<short> decodedAudio;
        if (OpusDecoder != null)
        {
            decodedAudio = DecodeOpus(packet.Payload, decodeFec: false);
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
        Logger.LogDebug(
            "[RTPTRACE 5/6] decoded    SSRC={Ssrc:X8} seq={Seq} samples={Samples} → play chan",
            packet.Ssrc, packet.SequenceNumber, decodedAudio.Length);
#endif

        await ToPlay.WriteAsync(new AudioReceivedEventArgs
        {
            AudioData = decodedAudio,
            Metadata = metadata
        }, ct);
    }

    /// <summary>
    /// Generate FEC or PLC concealment audio for a lost packet within one source's context.
    /// </summary>
    private async Task GenerateConcealmentAudio(CancellationToken ct, SequencedPacket? nextPacket)
    {
        Memory<short> concealmentAudio;

        byte[] nextOpusData = nextPacket?.Payload ?? [];

        Logger.LogDebug(
            "SSRC={Ssrc:X8}: loss recovery: {Bytes} bytes",
            Ssrc, nextOpusData.Length);

        concealmentAudio = DecodeOpus(nextOpusData, decodeFec: nextPacket != null);

        // LBRR FEC is not guaranteed in every packet (e.g. first frame of a talkspurt, or sender had FEC disabled for that window).  Fall back to PLC.
        if (concealmentAudio.Length == 0 && nextPacket != null)
        {
            Logger.LogDebug(
                "SSRC={Ssrc:X8}: FEC had no LBRR, falling back to PLC",
                Ssrc);
            concealmentAudio = DecodeOpus([], decodeFec: false);
        }

        if (concealmentAudio.Length == 0)
            return;

        var frequencies = new List<FrequencyTransmission>();
        if (LastValidMetadata?.Frequencies != null)
        {
            foreach (var freq in LastValidMetadata.Frequencies)
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
            ClientId = LastValidMetadata?.ClientId ?? "Recovered",
            Frequencies = frequencies
        };

        await ToPlay.WriteAsync(new AudioReceivedEventArgs
        {
            AudioData = concealmentAudio,
            Metadata = metadata
        }, ct);
    }

    /// <summary>
    /// Decode Opus audio using the provided decoder instance (one per SSRC).
    /// </summary>
    private Memory<short> DecodeOpus(ReadOnlySpan<byte> opusData, bool decodeFec)
    {
        if (OpusDecoder is null)
        {
            return Memory<short>.Empty;
        }

        const int OPUS_FRAME_SAMPLES = 960; // 20ms at 48kHz (matches sender)

        try
        {
            short[] pcmSamples = new short[OPUS_FRAME_SAMPLES];
            var outSpan = new Memory<short>(pcmSamples);

            int samplesDecoded;

            // We're actually looking for the _previous_ packet, which can be FEC'd into the next in case it gets lost.
            samplesDecoded = OpusDecoder.Decode(
                opusData, outSpan.Span, OPUS_FRAME_SAMPLES, decodeFec);

            if (samplesDecoded <= 0)
            {
                Logger.LogWarning("Opus decode failed for {ByteCount} bytes (decodeFec={DecodeFec})",
                    opusData.Length, decodeFec);
                return Memory<short>.Empty;
            }
            return outSpan[..samplesDecoded];
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Opus decode exception (decodeFec={DecodeFec})", decodeFec);
            return Memory<short>.Empty;
        }
    }

    public void Dispose()
    {
        ToDecode.Complete();
        DecoderTask.Wait();
        OpusDecoder?.Dispose();
    }
}
