using Xunit;

namespace KOTU.DocumentModel.Tests;

public sealed class EditorSelectionRequestTrackerTests
{
    [Fact]
    public void RapidNextKeepsNewestTargetThroughEarlierResponses()
    {
        var requests = new EditorSelectionRequestTracker();
        var result = TextSearch.Find(new("a a a a", 1, 1, true, true), "a");
        var current = 0;
        current = result.Navigate(current, false);
        var first = requests.Begin(1, "pane-a");
        current = result.Navigate(current, false);
        var second = requests.Begin(1, "pane-a");
        // 첫 응답의 state는 1번 매치를 보고하지만 최신 목표(2)를 되돌리지 않는다.
        if (!requests.IsPending) current = 1;
        Assert.False(requests.Complete(first));
        current = result.Navigate(current, false);
        var third = requests.Begin(1, "pane-a");
        Assert.Equal(3, current);
        Assert.False(requests.Complete(second));
        Assert.True(requests.IsPending);
        Assert.True(requests.Complete(third));
        Assert.False(requests.IsPending);
        Assert.False(requests.Complete(first));
    }

    [Fact]
    public void ManualSelectionOrQueryCancellationMakesPendingResponsesInert()
    {
        var requests = new EditorSelectionRequestTracker();
        var first = requests.Begin(1, "pane-a");
        var second = requests.Begin(1, "pane-a");
        requests.Cancel();
        Assert.False(requests.IsPending);
        Assert.False(requests.Complete(first));
        Assert.False(requests.Complete(second));
        Assert.False(requests.IsCurrent(second));
        var afterManualSelection = requests.Begin(1, "pane-a");
        Assert.False(requests.Complete(second));
        Assert.True(requests.Complete(afterManualSelection));
    }

    [Fact]
    public void OldDocumentAndOldPaneCannotReleaseNewSelectionOwner()
    {
        var requests = new EditorSelectionRequestTracker();
        var old = requests.Begin(1, "pane-a");
        requests.Cancel();
        var current = requests.Begin(2, "pane-b");
        Assert.False(requests.Complete(old));
        Assert.False(requests.Complete(current with { Generation = 1 }));
        Assert.False(requests.Complete(current with { Epoch = "pane-a" }));
        Assert.True(requests.IsPending);
        Assert.True(requests.Complete(current));
    }
}
