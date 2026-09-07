using KOTU.Core.Content;
using Xunit;

namespace KOTU.Core.Tests;

public class ContentContractSessionTests
{
    [Fact]
    public void WorkerEventsNeverReadOwnerIdentityBeforeDispatch()
    {
        var queue = new System.Collections.Concurrent.ConcurrentQueue<Action>();
        var owner = Environment.CurrentManagedThreadId;
        var child = new FakeContent();
        var identityChecks = 0;
        using var session = new ContentContractSession(child, queue.Enqueue, () =>
        {
            Assert.Equal(owner, Environment.CurrentManagedThreadId);
            identityChecks++;
            return true;
        });
        var worker = new Thread(() => child.RaiseContentOpened("worker"));
        worker.Start();
        worker.Join();
        Assert.Equal(0, identityChecks);
        Assert.True(queue.TryDequeue(out var callback));
        callback();
        Assert.Equal(1, identityChecks);
    }

    [Fact]
    public void QueuedEventsCannotEscapeDisposedAttachmentOrSameObjectReattachment()
    {
        var queue = new Queue<Action>();
        var child = new FakeContent();
        var delivered = new List<string>();
        var session = new ContentContractSession(child, queue.Enqueue, () => true);
        session.ContentOpened += delivered.Add;
        child.RaiseContentOpened("old");
        session.Dispose();
        using var replacement = new ContentContractSession(child, queue.Enqueue, () => true);
        replacement.ContentOpened += delivered.Add;
        child.RaiseContentOpened("new");
        while (queue.TryDequeue(out var callback)) callback();
        Assert.Equal(["new"], delivered);
    }

    [Fact]
    public void IdentityIsCheckedOnDeliveryAndDisposedEventsAreNotQueued()
    {
        var queue = new Queue<Action>();
        var child = new FakeContent();
        var current = true;
        using var session = new ContentContractSession(child, queue.Enqueue, () => current);
        var delivered = false;
        session.ContentOpened += _ => delivered = true;
        child.RaiseContentOpened("queued");
        current = false;
        child.RaiseContentOpened("ignored");
        Assert.Equal(2, queue.Count);
        while (queue.TryDequeue(out var callback)) callback();
        Assert.False(delivered);
        session.Dispose();
        child.RaiseContentOpened("disposed");
        Assert.Empty(queue);
    }

    [Fact]
    public void EveryContractIsUnsubscribedAndDisposeIsIdempotent()
    {
        var child = new FakeContent();
        var session = new ContentContractSession(child, action => action(), () => true);
        Assert.Equal(13, child.SubscriptionCount);
        session.Dispose();
        session.Dispose();
        Assert.Equal(0, child.SubscriptionCount);
    }

    [Fact]
    public async Task DelayedCloseApprovalCannotAuthorizeReplacement()
    {
        var completion = new TaskCompletionSource<bool>();
        var child = new FakeContent { HasUnsavedChanges = true, Confirm = () => completion.Task };
        var current = true;
        using var session = new ContentContractSession(child, action => action(), () => current);
        var confirmation = session.ConfirmCloseAsync();
        current = false;
        completion.SetResult(true);
        Assert.False(await confirmation);
    }

    [Fact]
    public async Task DiscardApprovalMayLeaveDirtyState()
    {
        var child = new FakeContent { HasUnsavedChanges = true };
        using var session = new ContentContractSession(child, action => action(), () => true);
        Assert.True(await session.ConfirmCloseAsync());
        Assert.True(child.HasUnsavedChanges);
    }

    [Fact]
    public async Task RejectedOrThrowingGuardDoesNotInvalidateSession()
    {
        var child = new FakeContent { HasUnsavedChanges = true, Confirm = () => Task.FromResult(false) };
        using var session = new ContentContractSession(child, action => action(), () => true);
        Assert.False(await session.ConfirmCloseAsync());
        child.Confirm = () => throw new InvalidOperationException("test");
        await Assert.ThrowsAsync<InvalidOperationException>(() => session.ConfirmCloseAsync());
        child.Confirm = () => Task.FromResult(true);
        Assert.True(await session.ConfirmCloseAsync());
    }

    [Fact]
    public async Task DisposedPendingCloseIsRejected()
    {
        var completion = new TaskCompletionSource<bool>();
        var child = new FakeContent { HasUnsavedChanges = true, Confirm = () => completion.Task };
        var session = new ContentContractSession(child, action => action(), () => true);
        var confirmation = session.ConfirmCloseAsync();
        session.Dispose();
        completion.SetResult(true);
        Assert.False(await confirmation);
    }
}
