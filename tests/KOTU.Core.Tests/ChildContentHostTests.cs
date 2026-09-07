using KOTU.Core.Content;
using KOTU.Core.Contracts;
using Xunit;

namespace KOTU.Core.Tests;

public class ChildContentHostTests
{
    private static readonly PrintPageSpec PageSpec = new(800, 1100, 0, 0, 800, 1100, 96, 96);
    private static ChildContentHost NewHost() => new(action => action());

    [Fact]
    public void RequestedAndSavedPathsDoNotReplaceOpenedPath()
    {
        var child = new FakeContent();
        var host = NewHost();
        var requested = new List<string>();
        var saved = new List<string>();
        host.CurrentPathChanged += requested.Add;
        host.ContentPathChanged += saved.Add;
        host.Attach(child, "initial");
        child.RaiseCurrentPathChanged("requested");
        child.RaiseContentPathChanged("saved");
        Assert.Equal("initial", host.OpenedPath);
        Assert.Equal(["requested"], requested);
        Assert.Equal(["saved"], saved);
        child.RaiseContentOpened("loaded");
        Assert.Equal("loaded", host.OpenedPath);
        child.RaiseUntitledOpened();
        Assert.Null(host.OpenedPath);
    }

    [Fact]
    public void DetachUnsubscribesBeforeVisualRemovalAndClearsEmptyState()
    {
        var child = new FakeContent { HasUnsavedChanges = true };
        var host = NewHost();
        host.Attach(child, "old");
        child.RaiseUnsavedChanged(true);
        var dirty = new List<bool>();
        var opened = new List<string>();
        var states = 0;
        host.UnsavedChanged += dirty.Add;
        host.ContentOpened += opened.Add;
        host.StateChanged += () => states++;
        host.Detach(() =>
        {
            Assert.Equal(0, child.SubscriptionCount);
            Assert.Null(host.Child);
            Assert.Null(host.OpenedPath);
            child.RaiseContentOpened("unloaded");
            child.RaiseUnsavedChanged(true);
        });
        host.Detach(() => { });
        Assert.Equal([false], dirty);
        Assert.Empty(opened);
        Assert.Equal(1, states);
        Assert.False(host.HasUnsavedChanges);
        Assert.False(host.HasMediaTransport);
        Assert.False(host.HasPlaybackSurface);
    }

    [Fact]
    public void ReplacementNeverProducesActionRequestsButCurrentChildDoes()
    {
        var host = NewHost();
        var old = new FakeContent();
        var current = new FakeContent();
        var actions = new List<string>();
        host.PrintRequested += () => actions.Add("print");
        host.ContentCloseRequested += () => actions.Add("close");
        host.UntitledWindowRequested += () => actions.Add("window");
        host.Attach(old, "old");
        host.Detach(() => { });
        host.Attach(current, "new");
        old.RaisePrintRequested();
        old.RaiseContentCloseRequested();
        old.RaiseUntitledWindowRequested();
        Assert.Empty(actions);
        current.RaisePrintRequested();
        current.RaiseContentCloseRequested();
        current.RaiseUntitledWindowRequested();
        Assert.Equal(["print", "close", "window"], actions);
    }

    [Fact]
    public void NestedRelayUsesOneQueueAndRejectsReplacedChild()
    {
        var queue = new Queue<Action>();
        var onOwnerThread = true;
        void Dispatch(Action action)
        {
            if (onOwnerThread) action();
            else queue.Enqueue(action);
        }
        var host = new ChildContentHost(Dispatch);
        var old = new FakeContent();
        host.Attach(old, "old");
        using var shell = new ContentContractSession(host, Dispatch, () => true);
        var opened = new List<string>();
        shell.ContentOpened += opened.Add;
        onOwnerThread = false;
        old.RaiseContentOpened("late old");
        onOwnerThread = true;
        host.Detach(() => { });
        var current = new FakeContent();
        host.Attach(current, "current");
        while (queue.TryDequeue(out var callback)) callback();
        Assert.Empty(opened);
        onOwnerThread = false;
        current.RaiseContentOpened("loaded");
        onOwnerThread = true;
        Assert.Single(queue);
        queue.Dequeue()();
        Assert.Empty(queue);
        Assert.Equal(["loaded"], opened);
    }

    [Fact]
    public void StateCallbackReplacementSuppressesRestOfOldNotification()
    {
        var host = NewHost();
        var old = new FakeContent();
        host.Attach(old, "old");
        var delivered = new List<string>();
        host.ContentOpened += delivered.Add;
        var replace = true;
        host.StateChanged += () =>
        {
            if (!replace) return;
            replace = false;
            host.Detach(() => { });
            host.Attach(new FakeContent(), "replacement");
        };
        old.RaiseContentOpened("late");
        Assert.Empty(delivered);
        Assert.Equal("replacement", host.OpenedPath);
    }

    [Fact]
    public void TeardownCallbackCanAttachWithoutOldDirtyResetOverwritingIt()
    {
        var host = NewHost();
        host.Attach(new FakeContent(), "old");
        var current = new FakeContent { HasUnsavedChanges = true };
        var dirty = new List<bool>();
        host.UnsavedChanged += dirty.Add;
        host.Detach(() =>
        {
            host.Attach(current, "new");
            current.RaiseUnsavedChanged(true);
        });
        Assert.Equal([true], dirty);
        Assert.Equal("new", host.OpenedPath);
        Assert.True(host.HasUnsavedChanges);
    }

    [Fact]
    public async Task ChildReplacementInvalidatesPendingCloseUnderSameHost()
    {
        var completion = new TaskCompletionSource<bool>();
        var host = NewHost();
        host.Attach(new FakeContent { HasUnsavedChanges = true, Confirm = () => completion.Task }, "old");
        using var shell = new ContentContractSession(host, action => action(), () => true);
        var approval = shell.ConfirmCloseAsync();
        host.Detach(() => { });
        host.Attach(new FakeContent(), "new");
        completion.SetResult(true);
        Assert.False(await approval);
    }

    [Fact]
    public void BrowseOrderIsSeededIntoEveryReplacement()
    {
        var host = NewHost();
        string[] files = ["b", "a"];
        host.SetBrowseOrder("folder", files);
        var old = new FakeContent();
        host.Attach(old, "b");
        Assert.Equal("folder", old.BrowseFolder);
        Assert.Same(files, old.BrowseFiles);
        host.Detach(() => { });
        var current = new FakeContent();
        host.Attach(current, "a");
        Assert.Same(files, current.BrowseFiles);
    }

    [Fact]
    public async Task MissingContractsKeepEmptyDefaults()
    {
        var host = NewHost();
        host.Attach(new object(), null);
        Assert.False(host.CanPrintNow);
        Assert.Equal(string.Empty, host.PrintJobName);
        Assert.Equal(0, host.GetPrintPageCount(PageSpec));
        Assert.Null(await host.CreatePrintPageAsync(1, PageSpec));
        Assert.Null(await host.GetContentInfoAsync());
        Assert.True(await host.ConfirmCloseAsync());
        Assert.False(host.HasMediaTransport);
        Assert.False(host.CanPrevious);
        Assert.False(host.CanNext);
        host.Play(); host.Pause(); host.Previous(); host.Next(); host.OpenUntitled();
        Assert.Equal(TrayStatus.Idle("ALL"), host.GetTrayStatus());
    }

    [Fact]
    public async Task MediaPrintAndInformationDelegateWithoutConflatingAudioAndVideo()
    {
        var host = NewHost();
        var audio = new FakeContent();
        host.Attach(audio, "audio");
        Assert.True(host.HasMediaTransport);
        Assert.False(host.HasPlaybackSurface);
        Assert.True(host.CanPrevious);
        Assert.False(host.CanNext);
        host.Play(); host.Pause(); host.Previous(); host.Next(); host.OpenUntitled();
        Assert.Equal(["play", "pause", "previous", "next", "untitled"], audio.Commands);
        Assert.True(host.CanPrintNow);
        Assert.Equal("test job", host.PrintJobName);
        Assert.Equal(2, host.GetPrintPageCount(PageSpec));
        Assert.Same(audio.PrintPage, await host.CreatePrintPageAsync(1, PageSpec));
        Assert.Equal("test", Assert.Single((await host.GetContentInfoAsync())!).Value);
        Assert.Equal(audio.GetTrayStatus(), host.GetTrayStatus());
    }

    [Fact]
    public async Task ReplacedChildCannotReturnLateInformationOrPrintPage()
    {
        var info = new TaskCompletionSource<IReadOnlyList<ContentInfoItem>?>();
        var page = new TaskCompletionSource<object?>();
        var host = NewHost();
        host.Attach(new FakeContent { PendingInfo = info.Task, PendingPage = page.Task }, "old");
        var requestedInfo = host.GetContentInfoAsync();
        var requestedPage = host.CreatePrintPageAsync(1, PageSpec);
        host.Detach(() => { });
        host.Attach(new FakeContent(), "new");
        info.SetResult([new("Old", "value")]);
        page.SetResult(new object());
        Assert.Null(await requestedInfo);
        Assert.Null(await requestedPage);
    }
}

