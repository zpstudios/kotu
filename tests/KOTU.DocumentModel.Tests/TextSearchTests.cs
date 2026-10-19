using System.Collections.Concurrent;
using Xunit;

namespace KOTU.DocumentModel.Tests;

public sealed class TextSearchTests
{
    private static TextSearchSnapshot Snapshot(string text, bool complete = true, bool replace = true) => new(text, 7, 11, complete, replace);

    [Theory]
    [InlineData("", "a", new int[0])]
    [InlineData("abc", "", new int[0])]
    [InlineData("ABC abc AbC", "abc", new[] { 0, 4, 8 })]
    [InlineData("aaaaa", "aa", new[] { 0, 2 })]
    [InlineData("a.b a?b", "a.b", new[] { 0 })]
    [InlineData("한글😀\r\n한글\n😀", "한글", new[] { 0, 6 })]
    [InlineData("한글😀\r\n한글\n😀", "😀", new[] { 2, 9 })]
    [InlineData("a\r\nb\nc\rd", "\r\n", new[] { 1 })]
    [InlineData("x\\n y\n", "\\n", new[] { 1 })]
    public void LiteralSearchUsesOriginalUtf16Offsets(string text, string query, int[] expected)
        => Assert.Equal(expected, TextSearch.Find(Snapshot(text), query).Starts);

    [Fact]
    public void NavigationWrapsAtBothEndsAndUsesCaretAnchor()
    {
        var result = TextSearch.Find(Snapshot("a-a-a"), "a");
        Assert.Equal(0, result.Navigate(2, false));
        Assert.Equal(2, result.Navigate(0, true));
        Assert.Equal(2, result.Navigate(-1, true));
        Assert.Equal(1, result.AtOrAfter(1));
        Assert.Equal(0, result.AtOrAfter(5));
        var empty = TextSearch.Find(Snapshot(""), "a");
        Assert.Equal(-1, empty.Navigate(-1, true));
        Assert.Equal(-1, empty.AtOrAfter(0));
    }

    [Fact]
    public void EveryStaleOrProtectedSnapshotBlocksReplacement()
    {
        var snapshot = Snapshot("abc");
        var result = TextSearch.Find(snapshot, "a");
        Assert.True(result.CanApplyReplacement(snapshot, "a"));
        Assert.False(result.CanApplyReplacement(snapshot with { DocumentGeneration = 8 }, "a"));
        Assert.False(result.CanApplyReplacement(snapshot with { TextRevision = 12 }, "a"));
        Assert.False(result.CanApplyReplacement(snapshot with { IsComplete = false }, "a"));
        Assert.False(result.CanApplyReplacement(snapshot with { CanReplace = false }, "a"));
        Assert.False(result.CanApplyReplacement(snapshot, "A"));
        var preview = TextSearch.Find(snapshot with { IsComplete = false }, "a");
        Assert.Single(preview.Starts);
        Assert.Null(TextSearch.Replace(preview, "x", 0, true));
        var readOnly = TextSearch.Find(snapshot with { CanReplace = false }, "a");
        Assert.False(readOnly.CanApplyReplacement(snapshot, "a"));
        Assert.Null(TextSearch.Replace(readOnly, "x", 0, true));
    }

    [Fact]
    public void ReplaceAllProducesOneRangeAndKeepsOutsideTextAndMixedNewlines()
    {
        const string text = "head😀A\r\nkeep\nA\rtail";
        var result = TextSearch.Find(Snapshot(text), "A");
        var edit = TextSearch.Replace(result, "한글", 0, true)!;
        Assert.Equal(4, edit.Count); // The 'a' in head and tail are literal ignore-case matches too.
        var actual = text.Remove(edit.Start, edit.Length).Insert(edit.Start, edit.Text);
        Assert.Equal("he한글d😀한글\r\nkeep\n한글\rt한글il", actual);
    }

    [Fact]
    public void SingleAndEmptyReplacementsAreNotRecursive()
    {
        var result = TextSearch.Find(Snapshot("cat cat"), "cat");
        Assert.Equal(new TextReplacement(4, 3, "catcat", 1), TextSearch.Replace(result, "catcat", 1, false));
        Assert.Equal(new TextReplacement(0, 7, " ", 2), TextSearch.Replace(result, "", 0, true));
        Assert.Null(TextSearch.Replace(result, "x", -1, false));
    }

    [Fact]
    public void LargeMatchSetHasExactCountWithoutUiGeometry()
    {
        var result = TextSearch.Find(Snapshot(new string('x', 1_048_576), complete: false), "x");
        Assert.Equal(1_048_576, result.Starts.Count);
        Assert.Equal(1_048_575, result.Starts[^1]);
        Assert.Null(TextSearch.Replace(result, "y", 0, true));
    }

    [Fact]
    public void CancellationAndReplacementExpansionAreBounded()
    {
        var cancelled = new CancellationToken(canceled: true);
        Assert.Throws<OperationCanceledException>(() => TextSearch.Find(Snapshot("abc"), "a", cancelled));
        var result = TextSearch.Find(Snapshot(new string('a', 10_000)), "a");
        Assert.Throws<OperationCanceledException>(() => TextSearch.Replace(result, "x", 0, true, cancelled));
        Assert.Throws<InvalidOperationException>(() => TextSearch.Replace(result, new string('b', 2000), 0, true));
    }

    [Fact]
    public async Task RapidRequestsAndBlockedDispatcherKeepOnlyLatestDelivery()
    {
        var queued = new ConcurrentQueue<Action>();
        var delivered = new List<TextSearchResult>();
        using var worker = new TextSearchWorker(action => { queued.Enqueue(action); return true; }, delivered.Add, 1);
        worker.Submit(Snapshot("first"), "first");
        await WaitUntil(() => queued.Count == 1);
        for (var i = 0; i < 1000; i++) worker.Submit(Snapshot("last") with { TextRevision = i }, "last");
        // Wait for computation without dispatching the first callback; there must still be only one callback.
        await Task.Delay(100);
        Assert.Single(queued);
        Assert.True(queued.TryDequeue(out var callback));
        callback();
        Assert.Single(delivered);
        Assert.Equal(999, delivered[0].Snapshot.TextRevision);
    }

    [Fact]
    public async Task CancelOrDisposeMakesAlreadyQueuedCallbackInert()
    {
        var queued = new ConcurrentQueue<Action>();
        var count = 0;
        var worker = new TextSearchWorker(action => { queued.Enqueue(action); return true; }, _ => count++, 1);
        worker.Submit(Snapshot("a"), "a");
        await WaitUntil(() => queued.Count == 1);
        worker.Cancel();
        Assert.True(queued.TryDequeue(out var first));
        first();
        Assert.Equal(0, count);
        worker.Submit(Snapshot("b"), "b");
        await WaitUntil(() => queued.Count == 1);
        worker.Dispose();
        Assert.True(queued.TryDequeue(out var second));
        second();
        worker.Submit(Snapshot("c"), "c");
        Assert.Equal(0, count);
        Assert.Empty(queued);
    }

    [Fact]
    public async Task RejectedDispatchAndIndependentWindowsRecover()
    {
        var queued = new ConcurrentQueue<Action>();
        var attempts = 0;
        var results = new List<string>();
        using var first = new TextSearchWorker(action =>
        {
            if (Interlocked.Increment(ref attempts) == 1) return false;
            queued.Enqueue(action);
            return true;
        }, result => results.Add(result.Query), 1);
        first.Submit(Snapshot("a"), "a");
        await WaitUntil(() => Volatile.Read(ref attempts) == 1);
        first.Submit(Snapshot("b"), "b");
        await WaitUntil(() => queued.Count == 1);
        using var second = new TextSearchWorker(action => { queued.Enqueue(action); return true; }, result => results.Add(result.Query), 1);
        second.Submit(Snapshot("c"), "c");
        await WaitUntil(() => queued.Count == 2);
        first.Dispose();
        while (queued.TryDequeue(out var action)) action();
        Assert.Equal(new[] { "c" }, results);
    }

    private static async Task WaitUntil(Func<bool> predicate)
    {
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (!predicate())
        {
            Assert.True(DateTime.UtcNow < deadline, "Worker did not finish within five seconds.");
            await Task.Delay(10);
        }
    }
}
