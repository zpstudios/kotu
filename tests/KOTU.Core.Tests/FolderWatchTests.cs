using KOTU.Core.Threading;
using KOTU.Core.Routing;
using Xunit;

namespace KOTU.Core.Tests;

public sealed class FolderWatchTests
{
    [Fact]
    public void Debounce_delivers_both_rename_paths_and_preserves_new_dirty_during_apply()
    {
        var clock = new Clock(); var queue = new Queue<Action>();
        var applied = new List<WatchChanges>();
        DebouncedWatchEvents? events = null;
        using (events = new DebouncedWatchEvents(TimeSpan.FromMilliseconds(400),
            action => { queue.Enqueue(action); return true; }, changes =>
            {
                applied.Add(changes);
                if (applied.Count == 1) events!.Signal(path: "later");
            }, clock))
        {
            events.Signal(path: "new", oldPath: "old");
            clock.Advance(400); queue.Dequeue()();
            Assert.True(applied[0].Contains("old")); Assert.True(applied[0].Contains("new"));
            Assert.False(applied[0].Contains("later"));
            clock.Advance(400); queue.Dequeue()();
            Assert.True(applied[1].Contains("later"));
            events.Signal(); // 보류 작업을 깨우는 통지는 경로를 무효화하지 않는다.
            clock.Advance(400); queue.Dequeue()();
            Assert.False(applied[2].ForceAll); Assert.Equal(0, applied[2].PathCount);
        }
    }

    private sealed class Clock : TimeProvider
    {
        private long _ticks;
        private FakeTimer? _timer;
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override long GetTimestamp() => _ticks;
        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
            => _timer = new FakeTimer(this, callback, state);
        public void Advance(int ms)
        {
            _ticks += TimeSpan.FromMilliseconds(ms).Ticks;
            _timer?.Fire();
        }
        public void ObsoleteTick() => _timer?.Callback();

        private sealed class FakeTimer(Clock clock, TimerCallback callback, object? state) : ITimer
        {
            private long _due = long.MaxValue;
            private bool _disposed;
            public bool Change(TimeSpan dueTime, TimeSpan period)
            {
                if (_disposed) return false;
                _due = dueTime == Timeout.InfiniteTimeSpan ? long.MaxValue : clock._ticks + dueTime.Ticks;
                return true;
            }
            public void Fire()
            {
                if (_disposed || clock._ticks < _due) return;
                _due = long.MaxValue;
                Callback();
            }
            public void Callback() => callback(state);
            public void Dispose() => _disposed = true;
            public ValueTask DisposeAsync() { Dispose(); return ValueTask.CompletedTask; }
        }
    }

    [Fact]
    public void Hundred_event_burst_posts_once_after_last_event_and_merges_error()
    {
        var clock = new Clock();
        var queue = new Queue<Action>();
        var applied = new List<bool>();
        using var events = new DebouncedWatchEvents(TimeSpan.FromMilliseconds(400),
            action => { queue.Enqueue(action); return true; }, changes => applied.Add(changes.Error), clock);
        for (var i = 0; i < 100; i++) { events.Signal(error: i == 5); clock.Advance(20); }
        Assert.Empty(queue);
        clock.Advance(379);
        Assert.Empty(queue);
        clock.Advance(1);
        Assert.Single(queue);
        queue.Dequeue()();
        Assert.Equal(new[] { true }, applied);
        clock.ObsoleteTick();
        Assert.Empty(queue);
    }

    [Fact]
    public void Blocked_UI_has_one_callback_and_rechecks_quiet_time_before_consuming()
    {
        var clock = new Clock();
        var queue = new Queue<Action>();
        var applied = 0;
        using var events = new DebouncedWatchEvents(TimeSpan.FromMilliseconds(400),
            action => { queue.Enqueue(action); return true; }, _ => applied++, clock);
        events.Signal();
        clock.Advance(400);
        for (var i = 0; i < 100; i++) { events.Signal(); clock.Advance(500); }
        Assert.Single(queue);
        events.Signal();
        queue.Dequeue()();
        Assert.Equal(0, applied);
        clock.Advance(399);
        Assert.Empty(queue);
        clock.Advance(1);
        queue.Dequeue()();
        Assert.Equal(1, applied);
    }

    [Fact]
    public void Disposed_generation_ignores_queued_and_late_callbacks()
    {
        var clock = new Clock();
        var queue = new Queue<Action>();
        var applied = 0;
        var events = new DebouncedWatchEvents(TimeSpan.FromMilliseconds(400),
            action => { queue.Enqueue(action); return true; }, _ => applied++, clock);
        events.Signal(error: true);
        clock.Advance(400);
        events.Dispose();
        queue.Dequeue()();
        events.Signal();
        clock.ObsoleteTick();
        clock.Advance(500);
        Assert.Equal(0, applied);
        Assert.Empty(queue);
    }

    [Fact]
    public void Rejected_dispatch_stops_further_scheduling()
    {
        var clock = new Clock();
        var posts = 0;
        using var events = new DebouncedWatchEvents(TimeSpan.FromMilliseconds(400),
            _ => { posts++; return false; }, _ => Assert.Fail("Closed queue must not apply"), clock);
        events.Signal();
        clock.Advance(400);
        events.Signal();
        clock.Advance(500);
        Assert.Equal(1, posts);
    }

    [Fact]
    public void Event_during_consumption_is_not_lost()
    {
        var clock = new Clock();
        var queue = new Queue<Action>();
        var applied = 0;
        DebouncedWatchEvents? events = null;
        using (events = new DebouncedWatchEvents(TimeSpan.FromMilliseconds(400),
            action => { queue.Enqueue(action); return true; }, _ => { if (++applied == 1) events!.Signal(); }, clock))
        {
            events.Signal();
            clock.Advance(400);
            queue.Dequeue()();
            clock.Advance(400);
            queue.Dequeue()();
            Assert.Equal(2, applied);
        }
    }

    [Fact]
    public void Hundred_refreshes_during_slow_scan_and_edit_have_one_followup()
    {
        var gate = new RefreshRequestGate();
        var scans = 0;
        if (gate.Request(blocked: false)) scans++;
        for (var i = 0; i < 100; i++) Assert.False(gate.Request(blocked: false));
        gate.Complete();
        for (var i = 0; i < 100; i++) Assert.False(gate.Request(blocked: true));
        Assert.Equal(1, scans);
        Assert.True(gate.Pending);
        if (gate.Request(blocked: false)) scans++;
        gate.Complete();
        Assert.Equal(2, scans);
        Assert.False(gate.Pending);
        Assert.False(gate.Running);
    }

    [Fact]
    public void Reset_during_running_scan_keeps_the_single_run_until_completion()
    {
        var gate = new RefreshRequestGate();
        Assert.True(gate.Request(blocked: false));
        gate.Defer();
        gate.ResetPending();
        Assert.False(gate.Pending);
        Assert.True(gate.Running);
        Assert.False(gate.Request(blocked: false)); // 새 폴더 요청도 옛 실행과 겹치지 않는다.
        gate.Complete();
        Assert.True(gate.Request(blocked: false));
        gate.Defer(); // 재조회 도중 시작된 편집: 반영 보류 뒤 다시 요청한다.
        gate.Complete();
        Assert.True(gate.Pending);
    }

    [Fact]
    public async Task Hundred_preview_generations_keep_slow_worker_calls_bounded_and_discard_old_results()
    {
        using var batch = new CancellableBatch();
        using var workers = new ModuleWorkerPool("A399 synthetic preview", 3);
        using var slots = new SemaphoreSlim(3);
        using var firstStarted = new CountdownEvent(3);
        using var release = new ManualResetEventSlim();
        var generation = 0;
        var calls = 0;
        var applied = 0;
        var requests = new List<Task>();
        async Task Preview(int captured, CancellationToken token)
        {
            try
            {
                using var lease = await CancellableBatch.EnterAsync(slots, token);
                if (captured != Volatile.Read(ref generation)) return;
                await workers.Run(_ =>
                {
                    var call = Interlocked.Increment(ref calls);
                    if (call <= 3) firstStarted.Signal();
                    if (!release.Wait(TimeSpan.FromSeconds(10))) throw new TimeoutException();
                }, token);
                token.ThrowIfCancellationRequested();
                if (captured == Volatile.Read(ref generation)) Interlocked.Increment(ref applied);
            }
            catch (OperationCanceledException) { }
        }
        try
        {
            for (var i = 0; i < 3; i++) requests.Add(Preview(0, batch.Token));
            Assert.True(firstStarted.Wait(TimeSpan.FromSeconds(5)));
            for (var i = 0; i < 100; i++)
            {
                Interlocked.Increment(ref generation);
                batch.Restart();
                for (var item = 0; item < 3; item++) requests.Add(Preview(generation, batch.Token));
            }
            Assert.Equal(3, Volatile.Read(ref calls));
            Assert.Equal(0, Volatile.Read(ref applied));
        }
        finally { release.Set(); }
        await Task.WhenAll(requests).WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(6, calls); // 초기 느린 3개 + 최신 세대 3개, 중간 세대는 발사 0.
        Assert.Equal(3, applied); // 옛 결과는 적용하지 않는다.
    }

    [Fact]
    public async Task Local_synthetic_offline_bit_changes_are_reflected_in_new_entry_snapshots()
    {
        var root = Path.Combine(Path.GetTempPath(), "kotu-a399-state-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var path = Path.Combine(root, "state.txt");
        await File.WriteAllTextAsync(path, "Synthetic NTFS metadata; no cloud provider.");
        try
        {
            var original = Assert.Single(ExplorerListing.List(root, ExplorerListing.AllFiles));
            Assert.False(original.IsPlaceholder);
            foreach (var offline in new[] { true, false })
            {
                var seen = new TaskCompletionSource<ExplorerListing.Entry>(TaskCreationOptions.RunContinuationsAsynchronously);
                using var watcher = new FolderWatch(root, action => { action(); return true; }, error =>
                {
                    if (error.Error) seen.TrySetException(new IOException("Watcher error"));
                    else seen.TrySetResult(Assert.Single(ExplorerListing.List(root, ExplorerListing.AllFiles)));
                });
                File.SetAttributes(path, offline ? FileAttributes.Offline : FileAttributes.Normal);
                var snapshot = await seen.Task.WaitAsync(TimeSpan.FromSeconds(10));
                Assert.Equal(offline, snapshot.IsPlaceholder);
                Assert.Equal(original.Size, snapshot.Size);
                Assert.Equal(original.Modified, snapshot.Modified);
                Assert.NotSame(original, snapshot);
            }
        }
        finally
        {
            File.SetAttributes(path, FileAttributes.Normal);
            File.Delete(path);
            Directory.Delete(root);
        }
    }

    [Fact]
    public async Task Actual_local_attribute_only_change_is_delivered_without_content_change()
    {
        var root = Path.Combine(Path.GetTempPath(), "kotu-a399-watch-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var path = Path.Combine(root, "local.txt");
        await File.WriteAllTextAsync(path, "local fixture");
        try
        {
            var before = new FileInfo(path);
            var length = before.Length;
            var modified = before.LastWriteTimeUtc;
            var seen = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            using var watcher = new FolderWatch(root, action => { action(); return true; }, error => seen.TrySetResult(error.Error));
            File.SetAttributes(path, File.GetAttributes(path) ^ FileAttributes.Hidden);
            Assert.False(await seen.Task.WaitAsync(TimeSpan.FromSeconds(10)));
            var after = new FileInfo(path);
            Assert.Equal(length, after.Length);
            Assert.Equal(modified, after.LastWriteTimeUtc);
            Assert.Equal("local fixture", await File.ReadAllTextAsync(path));
        }
        finally
        {
            File.SetAttributes(path, FileAttributes.Normal);
            File.Delete(path);
            Directory.Delete(root);
        }
    }
}
