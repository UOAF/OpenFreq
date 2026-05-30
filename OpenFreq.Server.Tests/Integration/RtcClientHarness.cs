using Microsoft.Extensions.Logging.Abstractions;
using OpenFreq.Common;

namespace OpenFreq.Server.Tests.Integration;

/// <summary>
/// Drives the real production client (<see cref="OpenFreqRtcClient"/>) against a test server
/// and exposes its callback events as awaitable streams. This is the primary driver for
/// behavioral integration tests — no signaling protocol is re-implemented here; only the
/// client's own events are adapted to <c>await</c>.
/// </summary>
public sealed class RtcClientHarness : IAsyncDisposable
{
    public OpenFreqRtcClient Client { get; }

    public AsyncEventStream<FrequencyJoinedEventArgs> FrequencyJoined { get; } = new();
    public AsyncEventStream<PeerEventArgs> PeerJoined { get; } = new();
    public AsyncEventStream<PeerEventArgs> PeerLeft { get; } = new();
    public AsyncEventStream<PeerTransmissionEventArgs> PeerTransmission { get; } = new();
    public AsyncEventStream<AllPeersStatusEventArgs> AllPeersStatus { get; } = new();
    public AsyncEventStream<OpenFreq.Common.ErrorEventArgs> Errors { get; } = new();

    private RtcClientHarness(OpenFreqRtcClient client)
    {
        Client = client;
        client.FrequencyJoined += (_, e) => FrequencyJoined.Publish(e);
        client.PeerJoined += (_, e) => PeerJoined.Publish(e);
        client.PeerLeft += (_, e) => PeerLeft.Publish(e);
        client.PeerTransmissionStateChanged += (_, e) => PeerTransmission.Publish(e);
        client.AllPeersStatusUpdateReceived += (_, e) => AllPeersStatus.Publish(e);
        client.ErrorOccurred += (_, e) => Errors.Publish(e);
    }

    public string? PeerId => Client.MyPeerId;

    public static RtcClientHarness Create(SignalingServerHarness server, string displayName, string password = "")
    {
        var client = new OpenFreqRtcClient(NullLoggerFactory.Instance, server.ServerAddress, password, displayName);
        return new RtcClientHarness(client);
    }

    /// <summary>Connect + authenticate. Throws on auth failure / timeout (the client's real behavior).</summary>
    public Task ConnectAsync() => Client.ConnectAsync(TimeSpan.FromSeconds(5));

    public async ValueTask DisposeAsync()
    {
        try
        {
            Client.Dispose();
        }
        catch
        {
            // best effort
        }

        await Task.CompletedTask;
    }
}
