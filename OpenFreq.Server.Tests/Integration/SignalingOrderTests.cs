using OpenFreq.Common;

namespace OpenFreq.Server.Tests.Integration;

/// <summary>
/// The client sends signaling messages one at a time and in the order they were made, and a transmit
/// heartbeat can't outlive the stop that ended its transmission.
/// </summary>
public class SignalingOrderTests
{
    private const int Freq = 251_000; // 251.000 MHz
    private const int OtherFreq = 339_750; // 339.750 MHz

    [Fact]
    public async Task JoinLeaveBurst_ServerEndsInTheLastRequestedState()
    {
        await using var server = await SignalingServerHarness.StartAsync();
        await using var alice = RtcClientHarness.Create(server, "Alice");
        await alice.ConnectAsync();

        // Make every call before awaiting any of them. Each must still reach the server in call order.
        var calls = new List<Task>();
        for (var i = 0; i < 25; i++)
        {
            calls.Add(alice.Client.JoinFrequencyAsync(Freq));
            calls.Add(alice.Client.LeaveFrequencyAsync(Freq));
        }

        calls.Add(alice.Client.JoinFrequencyAsync(Freq));
        await Task.WhenAll(calls);

        // The server handles a connection's messages in order, so once a later join is acknowledged,
        // everything above has been handled too.
        await alice.Client.JoinFrequencyAsync(OtherFreq);
        await alice.FrequencyJoined.WaitForAsync(e => e.FrequencyKhz == OtherFreq);

        Assert.Equal([Freq, OtherFreq], server.Server.ChannelManager.GetClientChannels(alice.PeerId!).Order());
    }

    [Fact]
    public async Task RapidStartStop_EndsNotTransmitting()
    {
        await using var server = await SignalingServerHarness.StartAsync();
        await using var alice = RtcClientHarness.Create(server, "Alice");
        await alice.ConnectAsync();
        await alice.Client.JoinFrequencyAsync(Freq);
        await alice.FrequencyJoined.WaitForAsync(e => e.FrequencyKhz == Freq);

        // Every start launches a heartbeat. One that read "still transmitting" just before a stop used to
        // send after it, leaving the server showing us keyed until the next stop.
        for (var i = 0; i < 50; i++)
        {
            await alice.Client.StartTransmissionAsync(Freq, is3d: false);
            await alice.Client.StopTransmissionAsync(Freq, is3d: false);
        }

        // Several heartbeat periods (333 ms), so any straggler has had time to arrive.
        await Task.Delay(TimeSpan.FromSeconds(1));

        var status = server.Server.ChannelManager.GetAllChannelStates()[Freq].Single(p => p.Id == alice.PeerId).Status;
        Assert.Equal(PeerData.PeerStatus.Receiving, status);
    }
}
