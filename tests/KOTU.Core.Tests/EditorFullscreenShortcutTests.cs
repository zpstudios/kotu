using KOTU.Core.Content;
using KOTU.Core.Contracts;
using Xunit;

namespace KOTU.Core.Tests;

public class EditorFullscreenShortcutTests
{
    private sealed class Editor : IEditorFullscreenShortcutSource, IContentLifetime
    {
        public bool OwnsNativeFullscreenShortcutFocus { get; set; } = true;
        public bool Current { get; set; } = true;
        public event Action<EditorFullscreenShortcut>? EditorFullscreenShortcutRequested;
        public int Subscribers => EditorFullscreenShortcutRequested?.GetInvocationList().Length ?? 0;
        public void Raise(bool repeat = false) => EditorFullscreenShortcutRequested?.Invoke(new(repeat, () => Current));
        public void ReleaseContent() => Raise();
    }

    [Fact]
    public void DirectDeliveryPreservesRepeatAndUnsubscribesExactlyOnce()
    {
        var editor = new Editor();
        using var session = new ContentContractSession(editor, action => action(), () => true);
        var received = new List<bool>();
        session.EditorFullscreenShortcutRequested += request => received.Add(request.IsRepeat);
        editor.Raise(); editor.Raise(true); editor.Raise(true);
        Assert.Equal([false, true, true], received);
        Assert.Equal(1, editor.Subscribers);
        session.Dispose(); session.Dispose(); editor.Raise();
        Assert.Equal(0, editor.Subscribers);
        Assert.Equal(3, received.Count);
    }

    [Fact]
    public void InvalidOriginAndReplacedOuterSessionRejectQueuedInput()
    {
        var editor = new Editor();
        var queue = new Queue<Action>();
        var current = true;
        using var session = new ContentContractSession(editor, queue.Enqueue, () => current);
        var received = 0;
        session.EditorFullscreenShortcutRequested += _ => received++;
        editor.Raise(); editor.Current = false; queue.Dequeue()();
        editor.Current = true; editor.Raise(); current = false; queue.Dequeue()();
        Assert.Equal(0, received);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ReplacementAfterInnerDeliveryCannotReviveRequestAtOuterDelivery(bool sameEditor)
    {
        var old = new Editor();
        var next = sameEditor ? old : new Editor();
        var innerQueue = new Queue<Action>();
        var outerQueue = new Queue<Action>();
        var host = new ChildContentHost(innerQueue.Enqueue);
        using var shell = new ContentContractSession(host, outerQueue.Enqueue, () => true);
        var received = new List<bool>();
        shell.EditorFullscreenShortcutRequested += request => received.Add(request.IsRepeat);
        host.Attach(old, "old.txt");
        old.Raise(); innerQueue.Dequeue()(); // already relayed, waiting in the outer dispatcher
        host.Detach(() => old.Raise());
        Assert.Equal(0, old.Subscribers);
        host.Attach(next, "new.txt"); next.Raise(true);
        while (innerQueue.TryDequeue(out var inner)) inner();
        while (outerQueue.TryDequeue(out var outer)) outer();
        Assert.Equal([true], received);
        host.Detach(() => { }); Assert.Equal(0, next.Subscribers);
    }

    [Fact]
    public void NestedHostForwardsNativeFocusOnlyFromCurrentChild()
    {
        var editor = new Editor();
        var host = new ChildContentHost(action => action());
        Assert.False(host.OwnsNativeFullscreenShortcutFocus);
        host.Attach(editor, null); Assert.True(host.OwnsNativeFullscreenShortcutFocus);
        editor.OwnsNativeFullscreenShortcutFocus = false; Assert.False(host.OwnsNativeFullscreenShortcutFocus);
        host.Detach(() => { }); Assert.False(host.OwnsNativeFullscreenShortcutFocus);
    }

    [Fact]
    public void RetainedRequestExpiresWhenEitherAttachmentOrOriginExpires()
    {
        var editor = new Editor();
        using var session = new ContentContractSession(editor, action => action(), () => true);
        EditorFullscreenShortcut? received = null;
        session.EditorFullscreenShortcutRequested += request => received = request;
        editor.Raise(); Assert.True(received!.IsCurrent);
        editor.Current = false; Assert.False(received.IsCurrent);
        editor.Current = true; session.Dispose(); Assert.False(received.IsCurrent);
    }
}
