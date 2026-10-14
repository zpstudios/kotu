using System.Collections.ObjectModel;
using System.Collections.Specialized;
using KOTU.Core.Routing;
using KOTU.Core.Threading;
using Xunit;

namespace KOTU.Core.Tests;

public sealed class ExplorerReuseTests
{
    private static ExplorerListing.Entry Entry(string name) =>
        new(@"C:\fixture\" + name, name, false, 123, new DateTime(2026, 1, 2), new DateTime(2026, 1, 1));

    private sealed class Item(ExplorerListing.Entry entry)
    {
        public ExplorerListing.Entry Entry { get; } = entry;
        public CancellableBatch Requests { get; } = new();
        public string? Result { get; set; }
        public bool InFlight { get; set; }
    }

    private static (int Reused, int Created, int Removed) Apply(ObservableCollection<Item> items,
        IReadOnlyList<ExplorerListing.Entry> entries) => ExplorerReuse.Items(items, entries,
        item => item.Entry, item => !item.Requests.Token.IsCancellationRequested,
        entry => new Item(entry), item => item.Requests.Dispose());

    [Fact]
    public void Unchanged_case_insensitive_path_reuses_entry_but_changed_name_is_a_rename()
    {
        var old = Entry("a.txt");
        var same = old with { Path = old.Path.ToUpperInvariant() };
        Assert.Same(old, Assert.Single(ExplorerReuse.Entries([old], [same], new WatchChanges())));
        var renamed = same with { Name = "A.txt" };
        Assert.Same(renamed, Assert.Single(ExplorerReuse.Entries([old], [renamed], new WatchChanges())));
    }

    [Fact]
    public void Every_file_state_component_invalidates_even_without_length_or_time_change()
    {
        var old = Entry("a.txt");
        ExplorerListing.Entry[] changes = [old with { Size = 124 }, old with { Modified = old.Modified.AddTicks(1) },
            old with { Created = old.Created.AddTicks(1) }, old with { IsPlaceholder = true },
            old with { Attributes = FileAttributes.Offline }, old with { Attributes = FileAttributes.ReadOnly },
            old with { IsFolder = true }];
        foreach (var changed in changes)
            Assert.Same(changed, Assert.Single(ExplorerReuse.Entries([old], [changed], new WatchChanges())));
    }

    [Fact]
    public void Cache_only_event_invalidates_only_its_path_and_rename_invalidates_both_ends()
    {
        var a = Entry("a.txt"); var b = Entry("b.txt");
        var changes = new WatchChanges(); changes.Add(a.Path.ToUpperInvariant());
        var result = ExplorerReuse.Entries([a, b], [a with { }, b with { }], changes);
        Assert.NotSame(a, result[0]); Assert.Same(b, result[1]);
        changes.Add(b.Path);
        result = ExplorerReuse.Entries([a, b], [a with { }, b with { }], changes);
        Assert.NotSame(a, result[0]); Assert.NotSame(b, result[1]);
    }

    [Fact]
    public void Dirty_paths_are_bounded_and_error_or_overflow_forces_all()
    {
        var changes = new WatchChanges();
        for (var i = 0; i < WatchChanges.PathLimit; i++) changes.Add($@"C:\fixture\{i}.txt");
        Assert.Equal(WatchChanges.PathLimit, changes.PathCount);
        changes.Add(@"c:\FIXTURE\0.txt");
        Assert.False(changes.ForceAll);
        changes.Add(@"C:\fixture\overflow.txt");
        Assert.True(changes.ForceAll); Assert.Equal(0, changes.PathCount);
        Assert.True(changes.Contains("unreported-path"));
        changes.Clear(); changes.MarkError();
        Assert.True(changes.Error); Assert.True(changes.ForceAll);
    }

    [Fact]
    public void Dirty_snapshot_can_restore_abandoned_scan_and_merge_events_during_edit()
    {
        var pending = new WatchChanges(); pending.Add("before-scan");
        var running = pending.Take();
        pending.Add("during-scan");
        Assert.False(running.Contains("during-scan"));
        pending.Merge(running); // 편집이 시작되어 결과 적용을 보류한 경우.
        var afterEdit = pending.Take();
        Assert.True(afterEdit.Contains("before-scan")); Assert.True(afterEdit.Contains("during-scan"));
        Assert.Equal(0, pending.PathCount);
        pending.MarkError(); pending.Merge(afterEdit);
        Assert.True(pending.ForceAll); // 오류로 감시자를 바꿔도 이전 경로의 무효화를 포함한다.
    }

    [Fact]
    public void Ten_thousand_unchanged_items_have_no_collection_notifications_and_one_change_is_one_replace()
    {
        var entries = Enumerable.Range(0, 10000).Select(i => Entry($"{i:D5}.txt")).ToArray();
        var items = new ObservableCollection<Item>(); Apply(items, entries);
        var original = items.ToArray();
        var notifications = new List<NotifyCollectionChangedAction>();
        items.CollectionChanged += (_, e) => notifications.Add(e.Action);
        var reused = Apply(items, ExplorerReuse.Entries(entries, entries.Select(e => e with { }).ToArray(), new WatchChanges()));
        Assert.Equal((10000, 0, 0), reused); Assert.Empty(notifications);
        var changed = entries.ToArray(); changed[501] = changed[501] with { Attributes = FileAttributes.Archive };
        Assert.Equal((9999, 1, 1), Apply(items, changed));
        Assert.Equal(new[] { NotifyCollectionChangedAction.Replace }, notifications);
        Assert.True(original[501].Requests.Token.IsCancellationRequested);
        Assert.Same(original[500], items[500]); Assert.Same(original[502], items[502]);
        foreach (var item in items) item.Requests.Dispose();
    }

    [Fact]
    public void Sort_filter_delete_and_insert_retain_only_live_entry_references_without_reset()
    {
        var a = Entry("a.txt"); var b = Entry("b.txt"); var c = Entry("c.txt");
        var items = new ObservableCollection<Item>(); Apply(items, [a, b, c]);
        var old = items.ToArray();
        items.CollectionChanged += (_, e) => Assert.NotEqual(NotifyCollectionChangedAction.Reset, e.Action);
        Apply(items, [c, a]);
        Assert.Same(old[2], items[0]); Assert.Same(old[0], items[1]);
        Assert.True(old[1].Requests.Token.IsCancellationRequested);
        Apply(items, [Entry("new.txt"), a, c]);
        Assert.Same(old[0], items[1]); Assert.Same(old[2], items[2]);
        foreach (var item in items) item.Requests.Dispose();
    }

    [Fact]
    public void Explicit_refresh_uses_fresh_entries_even_when_equal_and_reload_never_revives_old_generation()
    {
        var entry = Entry("a.txt");
        var items = new ObservableCollection<Item>(); Apply(items, [entry]);
        var old = items[0]; old.Result = "cached";
        Assert.Equal((0, 1, 1), Apply(items, [entry with { }]));
        Assert.True(old.Requests.Token.IsCancellationRequested); Assert.Null(items[0].Result);
        var unloaded = items[0]; unloaded.Requests.Dispose();
        Assert.Equal((0, 1, 1), Apply(items, [unloaded.Entry]));
        Assert.NotSame(unloaded, items[0]); Assert.False(items[0].Requests.Token.IsCancellationRequested);
        items[0].Requests.Dispose();
    }

    [Fact]
    public async Task Slow_unchanged_request_survives_hundred_dirty_generations_and_changed_work_stays_bounded()
    {
        var stable = Entry("stable.txt"); var changed = Entry("changed.txt");
        var entries = new[] { stable, changed };
        var items = new ObservableCollection<Item>(); Apply(items, entries);
        var stableItem = items[0]; var oldChanged = items[1];
        using var pool = new ModuleWorkerPool("A399 reuse slow provider", 2);
        using var slots = new SemaphoreSlim(2);
        using var started = new CountdownEvent(2);
        using var release = new ManualResetEventSlim();
        var calls = 0;
        async Task Preview(Item item)
        {
            var token = item.Requests.Token;
            if (item.InFlight || token.IsCancellationRequested) return;
            item.InFlight = true;
            try
            {
                using var lease = await CancellableBatch.EnterAsync(slots, token);
                var result = await pool.Run(_ =>
                {
                    if (Interlocked.Increment(ref calls) <= 2) started.Signal();
                    // 취소를 무시하는 관리 제공자. 게이트는 이 delegate의 반환까지 소유한다.
                    if (!release.Wait(TimeSpan.FromSeconds(10))) throw new TimeoutException();
                    return "preview";
                }, token);
                token.ThrowIfCancellationRequested();
                item.Result = result;
            }
            catch (OperationCanceledException) { }
            finally { item.InFlight = false; }
        }
        var requests = new List<Task> { Preview(items[0]), Preview(items[1]) };
        try
        {
            Assert.True(started.Wait(TimeSpan.FromSeconds(5)));
            for (var i = 0; i < 100; i++)
            {
                var dirty = new WatchChanges(); dirty.Add(changed.Path);
                entries = ExplorerReuse.Entries(entries, entries.Select(e => e with { }).ToArray(), dirty).ToArray();
                Assert.Equal((1, 1, 1), Apply(items, entries));
                requests.Add(Preview(items[0])); requests.Add(Preview(items[1]));
            }
            Assert.Same(stableItem, items[0]); Assert.True(stableItem.InFlight);
            Assert.Equal(2, Volatile.Read(ref calls)); Assert.Null(oldChanged.Result);
        }
        finally { release.Set(); }
        await Task.WhenAll(requests).WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(3, calls); // 초기 둘 + 변경 경로의 최신 하나만 실행.
        Assert.Equal("preview", stableItem.Result); Assert.Equal("preview", items[1].Result);
        Assert.Null(oldChanged.Result);
        foreach (var item in items) item.Requests.Dispose();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Late_provider_result_after_folder_switch_or_unload_cannot_fill_replacement(bool reload)
    {
        var entry = Entry("a.txt"); var items = new ObservableCollection<Item>(); Apply(items, [entry]);
        var old = items[0]; var token = old.Requests.Token;
        var provider = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        async Task Complete()
        {
            var result = await provider.Task;
            if (!token.IsCancellationRequested) old.Result = result;
        }
        var pending = Complete();
        if (reload) old.Requests.Dispose();
        Apply(items, [reload ? entry : Entry("other-folder.txt")]);
        provider.SetResult("late"); await pending;
        Assert.Null(old.Result); Assert.Null(items[0].Result);
        Assert.NotSame(old, items[0]); items[0].Requests.Dispose();
    }
}
