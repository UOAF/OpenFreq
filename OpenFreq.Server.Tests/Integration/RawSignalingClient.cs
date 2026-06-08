using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using OpenFreq.Common;
using OpenFreq.Common.Signaling;

namespace OpenFreq.Server.Tests.Integration;

/// <summary>
/// Minimal raw WebSocket client used ONLY for server-robustness tests — it deliberately sends
/// frames the real <see cref="OpenFreqRtcClient"/> would refuse to send (messages before auth,
/// malformed JSON, duplicate joins). It is not a parallel implementation of the happy path;
/// it exists to attack the server's defensive handling.
/// </summary>
public sealed class RawSignalingClient : IAsyncDisposable
{
    private static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(2);

    private readonly ClientWebSocket _ws = new();

    public WebSocketState State => _ws.State;

    public Task ConnectAsync(Uri uri) => _ws.ConnectAsync(uri, CancellationToken.None);

    public Task SendAsync(SignalingMessage message)
    {
        var json = JsonSerializer.Serialize(message, OpenFreqJsonContext.Default.SignalingMessage);
        return SendRawAsync(json);
    }

    public async Task SendRawAsync(string text)
    {
        var bytes = Encoding.UTF8.GetBytes(text);
        await _ws.SendAsync(bytes, WebSocketMessageType.Text, true, CancellationToken.None);
    }

    /// <summary>Receive the next signaling message (any type), or throw on timeout / close.</summary>
    public async Task<SignalingMessage> ReceiveAsync(TimeSpan? timeout = null)
    {
        using var cts = new CancellationTokenSource(timeout ?? DefaultTimeout);
        var buffer = new byte[8192];
        var sb = new StringBuilder();

        while (true)
        {
            WebSocketReceiveResult result;
            try
            {
                result = await _ws.ReceiveAsync(buffer, cts.Token);
            }
            catch (OperationCanceledException)
            {
                throw new TimeoutException("No signaling message received within timeout");
            }

            if (result.MessageType == WebSocketMessageType.Close)
                throw new WebSocketException("Server closed the connection");

            sb.Append(Encoding.UTF8.GetString(buffer, 0, result.Count));
            if (!result.EndOfMessage) continue;

            var message = JsonSerializer.Deserialize(sb.ToString(), OpenFreqJsonContext.Default.SignalingMessage);
            if (message != null) return message;
            sb.Clear();
        }
    }

    /// <summary>Receive frames until one of the given <paramref name="type"/> arrives, returning its payload.</summary>
    public async Task<TPayload> ReceiveUntilAsync<TPayload>(
        string type, Func<TPayload, bool>? predicate = null, TimeSpan? timeout = null) where TPayload : class
    {
        predicate ??= _ => true;
        var deadline = DateTime.UtcNow + (timeout ?? DefaultTimeout);

        while (true)
        {
            var remaining = deadline - DateTime.UtcNow;
            if (remaining <= TimeSpan.Zero)
                throw new TimeoutException($"No '{type}' message received within timeout");

            var message = await ReceiveAsync(remaining);
            if (message.Type != type) continue;

            var payload = SignalingMessageFactory.DeserializePayload<TPayload>(message.Payload);
            if (payload != null && predicate(payload)) return payload;
        }
    }

    /// <summary>Collect every payload of <paramref name="type"/> that arrives within <paramref name="window"/>.</summary>
    public async Task<List<TPayload>> CollectAsync<TPayload>(string type, TimeSpan window) where TPayload : class
    {
        var result = new List<TPayload>();
        var deadline = DateTime.UtcNow + window;

        while (true)
        {
            var remaining = deadline - DateTime.UtcNow;
            if (remaining <= TimeSpan.Zero) break;

            SignalingMessage message;
            try { message = await ReceiveAsync(remaining); }
            catch (TimeoutException) { break; }

            if (message.Type != type) continue;

            var payload = SignalingMessageFactory.DeserializePayload<TPayload>(message.Payload);
            if (payload != null) result.Add(payload);
        }

        return result;
    }

    /// <summary>Assert no message arrives within <paramref name="window"/>.</summary>
    public async Task AssertNoMessageAsync(TimeSpan? window = null)
    {
        try
        {
            var message = await ReceiveAsync(window ?? TimeSpan.FromMilliseconds(500));
            throw new Xunit.Sdk.XunitException($"Expected no message, but received '{message.Type}'");
        }
        catch (TimeoutException)
        {
            // expected
        }
    }

    /// <summary>Wait for the server to close the socket (used after policy-violation cases).</summary>
    public async Task<bool> WaitForCloseAsync(TimeSpan? timeout = null)
    {
        try
        {
            await ReceiveAsync(timeout ?? DefaultTimeout);
            return false;
        }
        catch (WebSocketException)
        {
            return true;
        }
        catch (TimeoutException)
        {
            return false;
        }
    }

    public async ValueTask DisposeAsync()
    {
        try
        {
            if (_ws.State == WebSocketState.Open)
                await _ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "test done", CancellationToken.None);
        }
        catch
        {
            // best effort
        }

        _ws.Dispose();
    }
}
