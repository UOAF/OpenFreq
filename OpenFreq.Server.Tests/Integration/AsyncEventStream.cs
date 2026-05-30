namespace OpenFreq.Server.Tests.Integration;

/// <summary>
/// Bridges a callback-style event into awaitable, consumable semantics so tests can write
/// <c>await stream.WaitForAsync(...)</c> instead of polling. Each published item is delivered
/// to at most one waiter (or buffered until one arrives), making "exactly one" assertions
/// natural and avoiding double-counting.
/// </summary>
public sealed class AsyncEventStream<T>
{
    private static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(2);

    private readonly object _gate = new();
    private readonly List<T> _buffered = new();
    private readonly List<(Func<T, bool> Predicate, TaskCompletionSource<T> Tcs)> _waiters = new();

    public void Publish(T item)
    {
        lock (_gate)
        {
            for (var i = 0; i < _waiters.Count; i++)
            {
                if (!_waiters[i].Predicate(item)) continue;
                var waiter = _waiters[i];
                _waiters.RemoveAt(i);
                waiter.Tcs.TrySetResult(item);
                return;
            }

            _buffered.Add(item);
        }
    }

    /// <summary>Wait for the next item matching <paramref name="predicate"/>, consuming it.</summary>
    public async Task<T> WaitForAsync(Func<T, bool>? predicate = null, TimeSpan? timeout = null)
    {
        predicate ??= _ => true;
        TaskCompletionSource<T> tcs;

        lock (_gate)
        {
            var idx = _buffered.FindIndex(x => predicate(x));
            if (idx >= 0)
            {
                var item = _buffered[idx];
                _buffered.RemoveAt(idx);
                return item;
            }

            tcs = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
            _waiters.Add((predicate, tcs));
        }

        using var cts = new CancellationTokenSource(timeout ?? DefaultTimeout);
        await using (cts.Token.Register(() =>
                     {
                         lock (_gate) _waiters.RemoveAll(w => ReferenceEquals(w.Tcs, tcs));
                         tcs.TrySetCanceled();
                     }))
        {
            try
            {
                return await tcs.Task;
            }
            catch (TaskCanceledException)
            {
                throw new TimeoutException(
                    $"No matching {typeof(T).Name} received within {(timeout ?? DefaultTimeout).TotalMilliseconds:F0} ms");
            }
        }
    }

    /// <summary>Assert that no matching item arrives within <paramref name="window"/>.</summary>
    public async Task AssertNoneAsync(Func<T, bool>? predicate = null, TimeSpan? window = null)
    {
        try
        {
            var item = await WaitForAsync(predicate, window ?? TimeSpan.FromMilliseconds(500));
            throw new Xunit.Sdk.XunitException($"Expected no matching {typeof(T).Name}, but received: {item}");
        }
        catch (TimeoutException)
        {
            // expected — nothing arrived
        }
    }
}
