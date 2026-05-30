using OpenFreq.Common;
using OpenFreq.Common.Signaling;

namespace OpenFreq.Server.Tests.Integration;

/// <summary>
/// Server-robustness tests driven by <see cref="RawSignalingClient"/>. These deliberately send
/// frames the real client would never send, to verify the server defends itself against
/// misbehaving clients.
/// </summary>
public class SignalingRobustnessTests
{
    private const int Freq = 251_000;

    private static async Task<RawSignalingClient> ConnectRawAsync(SignalingServerHarness server)
    {
        var client = new RawSignalingClient();
        await client.ConnectAsync(server.WsUri);
        return client;
    }

    private static async Task AuthenticateAsync(RawSignalingClient client, string displayName = "Raw")
    {
        await client.SendAsync(SignalingMessageFactory.CreateAuthenticate("", displayName));
        await client.ReceiveUntilAsync<SuccessMessage>(SignalingMessageTypes.Success);
    }

    [Fact]
    public async Task Join_BeforeAuthentication_RaisesError()
    {
        await using var server = await SignalingServerHarness.StartAsync();
        await using var client = await ConnectRawAsync(server);

        await client.SendAsync(SignalingMessageFactory.CreateJoin(Freq));

        var error = await client.ReceiveUntilAsync<ErrorMessage>(SignalingMessageTypes.Error);
        Assert.Contains("authenticated", error.Error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Transmission_BeforeAuthentication_IsIgnored()
    {
        await using var server = await SignalingServerHarness.StartAsync();
        await using var client = await ConnectRawAsync(server);

        await client.SendAsync(SignalingMessageFactory.CreateTransmission(Freq, transmitting: true, is3d: false));

        // HandleTransmission returns silently for unauthenticated sessions — no reply at all.
        await client.AssertNoMessageAsync();
    }

    [Fact]
    public async Task DuplicateJoin_SameFrequency_RaisesError()
    {
        await using var server = await SignalingServerHarness.StartAsync();
        await using var client = await ConnectRawAsync(server);
        await AuthenticateAsync(client);

        await client.SendAsync(SignalingMessageFactory.CreateJoin(Freq));
        await client.ReceiveUntilAsync<ChannelStateMessage>(SignalingMessageTypes.ChannelState);

        await client.SendAsync(SignalingMessageFactory.CreateJoin(Freq));

        var error = await client.ReceiveUntilAsync<ErrorMessage>(SignalingMessageTypes.Error);
        Assert.Contains("already joined", error.Error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Transmission_OnUnjoinedFrequency_IsIgnored()
    {
        await using var server = await SignalingServerHarness.StartAsync(broadcastPeerUpdates: false);
        await using var sender = await ConnectRawAsync(server);
        await AuthenticateAsync(sender, "Sender");

        // Join one frequency, then try to transmit on a different, unjoined one.
        await sender.SendAsync(SignalingMessageFactory.CreateJoin(Freq));
        await sender.ReceiveUntilAsync<ChannelStateMessage>(SignalingMessageTypes.ChannelState);

        // A second client on the *other* frequency would be the only possible recipient.
        await using var listener = await ConnectRawAsync(server);
        await AuthenticateAsync(listener, "Listener");
        await listener.SendAsync(SignalingMessageFactory.CreateJoin(Freq + 1000));
        await listener.ReceiveUntilAsync<ChannelStateMessage>(SignalingMessageTypes.ChannelState);

        await sender.SendAsync(
            SignalingMessageFactory.CreateTransmission(Freq + 1000, transmitting: true, is3d: false));

        // Sender never joined Freq+1000, so nothing is broadcast to the listener.
        await listener.AssertNoMessageAsync();
    }

    [Fact]
    public async Task MalformedJson_RaisesErrorAndKeepsConnectionOpen()
    {
        await using var server = await SignalingServerHarness.StartAsync();
        await using var client = await ConnectRawAsync(server);

        await client.SendRawAsync("{ this is not valid json ]");

        var error = await client.ReceiveUntilAsync<ErrorMessage>(SignalingMessageTypes.Error);
        Assert.Contains("Invalid message format", error.Error);

        // Connection must survive — a follow-up authenticate still works.
        await AuthenticateAsync(client);
    }
}
