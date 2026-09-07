using KOTU.Core.Threading;
using Xunit;

namespace KOTU.Core.Tests;

public class BoundedOperationTests
{
    private static readonly TimeSpan Limit = TimeSpan.FromSeconds(5);
    private static TaskCompletionSource<T> Source<T>() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    [Fact]
    public async Task Completed_result_keeps_ownership_even_with_canceled_caller()
    {
        using var cancel = new CancellationTokenSource();
        cancel.Cancel();
        var discarded = 0;
        var result = await BoundedOperation.WaitAsync(Task.FromResult(7), TimeSpan.Zero,
            cancel.Token, () => throw new Exception(), _ => discarded++);
        Assert.Equal(7, result);
        Assert.Equal(0, discarded);
    }

    [Fact]
    public async Task Already_canceled_caller_precedes_zero_timeout_for_pending_operation()
    {
        using var cancel = new CancellationTokenSource();
        cancel.Cancel();
        var source = Source<int>();
        var wait = BoundedOperation.WaitAsync(source.Task, TimeSpan.Zero, cancel.Token);
        var error = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => wait);
        Assert.Equal(cancel.Token, error.CancellationToken);
        source.SetResult(0);
    }

    [Fact]
    public async Task Original_timeout_exception_is_not_a_wait_timeout()
    {
        var source = Source<int>();
        var original = new TimeoutException("native failure");
        var cancelCalls = 0;
        var wait = BoundedOperation.WaitAsync(source.Task, Limit, cancel: () => cancelCalls++);
        source.SetException(original);
        Assert.Same(original, await Assert.ThrowsAsync<TimeoutException>(() => wait));
        Assert.Equal(0, cancelCalls);
    }

    [Fact]
    public async Task Original_fault_and_original_cancellation_are_preserved()
    {
        var fault = new InvalidDataException("invalid");
        Assert.Same(fault, await Assert.ThrowsAsync<InvalidDataException>(() =>
            BoundedOperation.WaitAsync(Task.FromException<int>(fault), Limit)));
        using var canceled = new CancellationTokenSource();
        canceled.Cancel();
        var error = await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            BoundedOperation.WaitAsync(Task.FromCanceled<int>(canceled.Token), Limit));
        Assert.Equal(canceled.Token, error.CancellationToken);
    }

    [Fact]
    public async Task Timeout_cancels_once_and_disposes_late_result_once()
    {
        var source = Source<object>();
        var canceled = Source<bool>();
        var cleaned = Source<object>();
        var cancels = 0;
        var disposals = 0;
        var value = new object();
        var wait = BoundedOperation.WaitAsync(source.Task, TimeSpan.Zero,
            cancel: () => { Interlocked.Increment(ref cancels); canceled.TrySetResult(true); },
            discard: result => { Interlocked.Increment(ref disposals); cleaned.TrySetResult(result); });
        await Assert.ThrowsAsync<TimeoutException>(() => wait.WaitAsync(Limit));
        await canceled.Task.WaitAsync(Limit);
        source.SetResult(value);
        Assert.Same(value, await cleaned.Task.WaitAsync(Limit));
        Assert.Equal(1, cancels);
        Assert.Equal(1, disposals);
    }

    [Fact]
    public async Task Caller_cancellation_is_distinct_and_cleanup_exceptions_do_not_replace_it()
    {
        using var cancel = new CancellationTokenSource();
        var source = Source<int>();
        var nativeCancel = Source<bool>();
        var cleanup = Source<bool>();
        var wait = BoundedOperation.WaitAsync(source.Task, Timeout.InfiniteTimeSpan, cancel.Token,
            () => { nativeCancel.SetResult(true); throw new InvalidOperationException(); },
            _ => { cleanup.SetResult(true); throw new InvalidOperationException(); });
        cancel.Cancel();
        var error = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => wait.WaitAsync(Limit));
        Assert.Equal(cancel.Token, error.CancellationToken);
        await nativeCancel.Task.WaitAsync(Limit);
        source.SetResult(1);
        await cleanup.Task.WaitAsync(Limit);
        Assert.True(wait.IsCanceled);
    }

    [Fact]
    public async Task Blocked_native_cancel_does_not_hold_the_waiter()
    {
        var source = Source<int>();
        using var release = new ManualResetEventSlim();
        var entered = Source<bool>();
        var finished = Source<bool>();
        try
        {
            var wait = BoundedOperation.WaitAsync(source.Task, TimeSpan.Zero, cancel: () =>
            {
                entered.SetResult(true);
                release.Wait();
                finished.SetResult(true);
            });
            await entered.Task.WaitAsync(Limit);
            await Assert.ThrowsAsync<TimeoutException>(() => wait.WaitAsync(Limit));
        }
        finally
        {
            release.Set();
            source.TrySetResult(0);
            await finished.Task.WaitAsync(Limit);
        }
    }

    [Fact]
    public async Task Delayed_success_is_owned_by_caller_and_never_discarded()
    {
        var source = Source<int>();
        var discarded = 0;
        var wait = BoundedOperation.WaitAsync(source.Task, Limit, discard: _ => discarded++);
        source.SetResult(42);
        Assert.Equal(42, await wait);
        Assert.Equal(0, discarded);
    }

    [Fact]
    public async Task Late_fault_does_not_change_timeout_outcome()
    {
        var source = Source<int>();
        var wait = BoundedOperation.WaitAsync(source.Task, TimeSpan.Zero);
        await Assert.ThrowsAsync<TimeoutException>(() => wait.WaitAsync(Limit));
        source.SetException(new IOException("late native error"));
        await Assert.ThrowsAsync<TimeoutException>(() => wait);
    }

    [Fact]
    public async Task Blocking_discard_runs_independently_of_the_callers_completion()
    {
        var source = Source<int>();
        var entered = Source<bool>();
        var finished = Source<bool>();
        using var release = new ManualResetEventSlim();
        var wait = BoundedOperation.WaitAsync(source.Task, TimeSpan.Zero, discard: _ =>
        {
            entered.SetResult(true);
            release.Wait();
            finished.SetResult(true);
        });
        try
        {
            await Assert.ThrowsAsync<TimeoutException>(() => wait.WaitAsync(Limit));
            source.SetResult(1);
            await entered.Task.WaitAsync(Limit);
            Assert.True(wait.IsCompleted);
            Assert.False(finished.Task.IsCompleted);
        }
        finally
        {
            release.Set();
            source.TrySetResult(1);
            await finished.Task.WaitAsync(Limit);
        }
    }
}
