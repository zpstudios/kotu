using Xunit;

namespace KOTU.DocumentModel.Tests;

public class EditorLifecycleTests
{
    [Fact]
    public async Task TransitionWaitsForLockThenCurrentSnapshot()
    {
        var locked = new TaskCompletionSource();
        var snapshot = new TaskCompletionSource();
        var calls = new List<string>();
        var task = EditorModeTransition.PrepareAsync(false, true,
            value => { calls.Add("lock:" + value); return locked.Task; },
            () => { calls.Add("snapshot"); return snapshot.Task; }, () => true, _ => Assert.Fail());
        Assert.Equal(["lock:True"], calls);
        Assert.False(task.IsCompleted);
        locked.SetResult();
        Assert.Equal(["lock:True", "snapshot"], calls);
        Assert.False(task.IsCompleted);
        snapshot.SetResult();
        await task;
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SnapshotFailureRestoresPreviousMode(bool viewing)
    {
        var modes = new List<bool>();
        await Assert.ThrowsAsync<TimeoutException>(() => EditorModeTransition.PrepareAsync(viewing, !viewing,
            value => { modes.Add(value); return Task.CompletedTask; },
            () => Task.FromException(new TimeoutException()), () => true, _ => Assert.Fail()));
        Assert.Equal([!viewing, viewing], modes);
    }

    [Fact]
    public async Task UncertainLockTimeoutStillOrdersRollback()
    {
        var attempts = 0;
        await Assert.ThrowsAsync<TimeoutException>(() => EditorModeTransition.PrepareAsync(false, true,
            _ => ++attempts == 1 ? Task.FromException(new TimeoutException()) : Task.CompletedTask,
            () => throw new InvalidOperationException("Snapshot must not run"), () => true, _ => Assert.Fail()));
        Assert.Equal(2, attempts);
    }

    [Fact]
    public async Task FailedRollbackRequiresActionableRecovery()
    {
        Exception? recovery = null;
        await Assert.ThrowsAsync<TimeoutException>(() => EditorModeTransition.PrepareAsync(false, true,
            _ => Task.FromException(new TimeoutException()), () => Task.CompletedTask,
            () => true, ex => recovery = ex));
        Assert.IsType<TimeoutException>(recovery);
    }

    [Fact]
    public async Task OldTransitionNeverRollsBackReplacement()
    {
        var current = true;
        var calls = 0;
        await Assert.ThrowsAsync<OperationCanceledException>(() => EditorModeTransition.PrepareAsync(false, true,
            _ => { calls++; current = false; return Task.CompletedTask; },
            () => throw new InvalidOperationException("Old snapshot must not run"), () => current, _ => Assert.Fail()));
        Assert.Equal(1, calls);
    }

    [Fact]
    public void TemporaryDetachKeepsSessionAndReleaseIsPermanent()
    {
        var lifetime = new EditorSurfaceLifetime();
        lifetime.Attach();
        lifetime.Detach();
        Assert.False(lifetime.IsAttached);
        Assert.False(lifetime.IsReleased);
        Assert.False(lifetime.IsFailed);
        lifetime.Attach();
        Assert.True(lifetime.IsAttached);
        Assert.True(lifetime.Release());
        lifetime.Attach();
        Assert.False(lifetime.IsAttached);
        Assert.False(lifetime.Release());
        Assert.False(lifetime.Fail());
    }

    [Fact]
    public void FailedSessionStaysFailedAcrossReattachAndRetainsReceivedBuffer()
    {
        var lifetime = new EditorSurfaceLifetime();
        var buffer = new VirtualDocumentBuffer("before");
        buffer.Apply(buffer.Epoch, 2, [new(6, 0, "\nreceived😀")]);
        var retained = buffer.Text;
        Assert.True(lifetime.Fail());
        lifetime.Detach();
        lifetime.Attach();
        Assert.True(lifetime.IsFailed);
        Assert.False(lifetime.Fail());
        Assert.Same(retained, buffer.Text);
        Assert.Equal(2, buffer.Version);
        Assert.True(lifetime.Release());
        Assert.Equal("before\nreceived😀", buffer.Text);
    }
}
