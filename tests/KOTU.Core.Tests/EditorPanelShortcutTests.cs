using KOTU.Core.Content;
using KOTU.Core.Contracts;
using Xunit;

namespace KOTU.Core.Tests;

public class EditorPanelShortcutTests
{
    private sealed class Editor : IEditorPanelShortcutSource, IContentLifetime
    {
        public event Action<EditorPanelShortcut>? EditorPanelShortcutRequested;
        public int Subscribers => EditorPanelShortcutRequested?.GetInvocationList().Length ?? 0;
        public void Raise(EditorPanelShortcut request) => EditorPanelShortcutRequested?.Invoke(request);
        public void ReleaseContent() => Raise(Request);
    }

    private static readonly EditorPanelShortcut Request = new(EditorPanelSide.Right, true, true, true, true, true);

    [Fact]
    public void DirectSessionPreservesSideRepeatAndModifiers()
    {
        var editor = new Editor();
        using var session = new ContentContractSession(editor, action => action(), () => true);
        var received = new List<EditorPanelShortcut>();
        session.EditorPanelShortcutRequested += received.Add;
        editor.Raise(Request);
        Assert.Equal([Request], received);
        Assert.Equal(1, editor.Subscribers);
        session.Dispose();
        session.Dispose();
        editor.Raise(Request);
        Assert.Single(received);
        Assert.Equal(0, editor.Subscribers);
    }

    [Fact]
    public void CurrentAttachmentIsCheckedAtDelivery()
    {
        var editor = new Editor();
        var queue = new Queue<Action>();
        var current = true;
        using var session = new ContentContractSession(editor, queue.Enqueue, () => current);
        var received = 0;
        session.EditorPanelShortcutRequested += _ => received++;
        editor.Raise(Request);
        current = false;
        queue.Dequeue()();
        Assert.Equal(0, received);
    }

    [Fact]
    public void SameEditorReattachmentCannotReviveQueuedPanelRequest()
    {
        var editor = new Editor();
        var queue = new Queue<Action>();
        var host = new ChildContentHost(queue.Enqueue);
        using var shell = new ContentContractSession(host, action => action(), () => true);
        var received = new List<EditorPanelShortcut>();
        shell.EditorPanelShortcutRequested += received.Add;
        host.Attach(editor, "document.txt");
        editor.Raise(Request);
        host.Detach(() => editor.Raise(Request));
        Assert.Equal(0, editor.Subscribers);
        host.Attach(editor, "document.txt");
        var fresh = new EditorPanelShortcut(EditorPanelSide.Left, false, false, false, false, false);
        editor.Raise(fresh);
        while (queue.TryDequeue(out var callback)) callback();
        Assert.Equal([fresh], received);
    }

    [Fact]
    public void NestedRelayCannotDeliverAfterOuterSessionDisposal()
    {
        var editor = new Editor();
        var host = new ChildContentHost(action => action());
        host.Attach(editor, null);
        var queue = new Queue<Action>();
        var shell = new ContentContractSession(host, queue.Enqueue, () => true);
        var received = 0;
        shell.EditorPanelShortcutRequested += _ => received++;
        editor.Raise(Request);
        shell.Dispose();
        while (queue.TryDequeue(out var callback)) callback();
        Assert.Equal(0, received);
        host.Detach(() => { });
        Assert.Empty(queue);
    }

    [Fact]
    public void ReplacementChildOnlyRelaysItsOwnRequests()
    {
        var old = new Editor();
        var next = new Editor();
        var queue = new Queue<Action>();
        var host = new ChildContentHost(queue.Enqueue);
        var received = new List<EditorPanelShortcut>();
        host.EditorPanelShortcutRequested += received.Add;
        host.Attach(old, null);
        old.Raise(Request);
        host.Detach(() => { });
        host.Attach(next, null);
        old.Raise(Request);
        var fresh = Request with { IsRepeat = false };
        next.Raise(fresh);
        while (queue.TryDequeue(out var callback)) callback();
        Assert.Equal([fresh], received);
    }
}
