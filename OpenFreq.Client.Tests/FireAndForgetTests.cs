using OpenFreqClient.ViewModels;

namespace OpenFreq.Client.Tests;

/// <summary>
/// A task passed to <see cref="FireAndForgetExtensions.FireAndForget"/> that fails must have its exception rethrown
/// on the caller's synchronization context, where it reaches the unhandled exception handler, instead of staying
/// unobserved in the task.
/// </summary>
public class FireAndForgetTests
{
    [Fact]
    public void AlreadyFailedTask_IsRethrownOnTheCallersContext()
    {
        var failure = new InvalidOperationException("join failed");
        var context = new RecordingContext();

        WithContext(context, () => Task.FromException(failure).FireAndForget());

        var rethrow = Assert.Single(context.Posted);
        Assert.Same(failure, Assert.Throws<InvalidOperationException>(rethrow));
    }

    [Fact]
    public void TaskThatFailsLater_IsRethrownOnTheCallersContext()
    {
        var failure = new InvalidOperationException("join failed");
        var pending = new TaskCompletionSource();
        var context = new RecordingContext();

        WithContext(context, () => pending.Task.FireAndForget());
        Assert.Empty(context.Posted);

        pending.SetException(failure);
        var continuation = context.Posted.Dequeue();
        continuation();

        var rethrow = Assert.Single(context.Posted);
        Assert.Same(failure, Assert.Throws<InvalidOperationException>(rethrow));
    }

    [Fact]
    public void SuccessfulTask_PostsNothing()
    {
        var context = new RecordingContext();

        WithContext(context, () => Task.CompletedTask.FireAndForget());

        Assert.Empty(context.Posted);
    }

    private static void WithContext(SynchronizationContext context, Action action)
    {
        var previous = SynchronizationContext.Current;
        SynchronizationContext.SetSynchronizationContext(context);
        try
        {
            action();
        }
        finally
        {
            SynchronizationContext.SetSynchronizationContext(previous);
        }
    }

    /// <summary>Queues posted callbacks so the test decides when they run.</summary>
    private sealed class RecordingContext : SynchronizationContext
    {
        public Queue<Action> Posted { get; } = new();

        public override void Post(SendOrPostCallback callback, object? state) => Posted.Enqueue(() => callback(state));
    }
}
