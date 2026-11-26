using Xunit;

namespace KOTU.Module.Audio.Tests;

public class EqualizerApplyQueueTests
{
    [Fact]
    public async Task BurstKeepsOneRunningAndOnlyTheNewestPendingSnapshot()
    {
        var first = new TaskCompletionSource<EqualizerApplyResult>();
        var done = new TaskCompletionSource();
        var seen = new List<float>();
        var notifications = 0;
        var queue = new EqualizerApplyQueue<object>((_, values, _) =>
        {
            seen.Add(values![0]);
            return seen.Count == 1 ? first.Task : Task.FromResult(EqualizerApplyResult.Applied);
        }, _ => { notifications++; done.TrySetResult(); });
        var target = new object();
        queue.Enqueue(target, new EqualizerSnapshot(0, [0]));
        for (var i = 1; i <= 1000; i++) queue.Enqueue(target, new EqualizerSnapshot(0, [i / 100f]));
        Assert.Single(seen);
        first.SetResult(EqualizerApplyResult.Applied);
        await done.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(new float[] { 0, 10 }, seen);
        Assert.Equal(1, notifications);
    }

    [Fact]
    public async Task InvalidatingPlayerSuppressesOldApplyAndOldCompletion()
    {
        var first = new TaskCompletionSource<EqualizerApplyResult>();
        var done = new TaskCompletionSource();
        Func<bool>? oldCurrent = null;
        var oldPlayer = new object();
        var newPlayer = new object();
        var targets = new List<object>();
        var notifications = new List<EqualizerApplyResult>();
        var queue = new EqualizerApplyQueue<object>((target, _, current) =>
        {
            targets.Add(target);
            if (target == oldPlayer) { oldCurrent = current; return first.Task; }
            Assert.True(current());
            return Task.FromResult(EqualizerApplyResult.Applied);
        }, result => { notifications.Add(result); done.TrySetResult(); });
        queue.Enqueue(oldPlayer, new EqualizerSnapshot(0, [1]));
        queue.Enqueue(oldPlayer, new EqualizerSnapshot(0, [2]));
        queue.Invalidate();
        Assert.False(oldCurrent!());
        queue.Enqueue(newPlayer, new EqualizerSnapshot(0, [3]));
        first.SetResult(EqualizerApplyResult.DisabledAfterFailure);
        await done.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(new[] { oldPlayer, newPlayer }, targets);
        Assert.Equal(new[] { EqualizerApplyResult.Applied }, notifications);
    }

    [Fact]
    public async Task InvalidationClearsPendingRequestBeforeAnyMoreNativeWork()
    {
        var current = new TaskCompletionSource<EqualizerApplyResult>();
        Func<bool>? isCurrent = null;
        var count = 0;
        var queue = new EqualizerApplyQueue<object>((_, _, check) =>
        {
            count++;
            isCurrent = check;
            return current.Task;
        }, _ => Assert.Fail("Invalidated completion must be discarded."));
        var target = new object();
        queue.Enqueue(target, null);
        queue.Enqueue(target, new EqualizerSnapshot(0, [1]));
        queue.Invalidate();
        Assert.False(isCurrent!());
        current.SetResult(EqualizerApplyResult.Applied);
        await queue.Completion.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(1, count);
    }
}
