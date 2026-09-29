using Microsoft.Extensions.Logging.Abstractions;

namespace OpenFreq.Common.Tests;

public class OpenFreqRtcClientTests
{
    /// <summary>
    /// A client has no RTP sender before it connects, or between a drop and the end of the reconnect.
    /// </summary>
    [Fact]
    public void MarkTransmitStartTime_WithoutAnRtpSender_DoesNothing()
    {
        using var client = new OpenFreqRtcClient(NullLoggerFactory.Instance, "127.0.0.1", "", "Tester");

        client.MarkTransmitStartTime();
    }
}
