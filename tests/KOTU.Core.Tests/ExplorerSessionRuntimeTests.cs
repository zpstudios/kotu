using System.Collections.Concurrent;
using KOTU.Core.Routing;
using KOTU.Core.Threading;
using Xunit;

namespace KOTU.Core.Tests;

public class ExplorerSessionRuntimeTests
{
    private const string Folder = "C:/fixture";
    private static ExplorerListing.Entry E(string name, string folder = Folder) =>
        new(folder + "/" + name, name, false, 1, DateTime.UnixEpoch, DateTime.UnixEpoch);

    [Theory]
    [InlineData(ExplorerListing.SortKey.Name)]
    [InlineData(ExplorerListing.SortKey.Type)]
    [InlineData(ExplorerListing.SortKey.Size)]
    [InlineData(ExplorerListing.SortKey.Created)]
    [InlineData(ExplorerListing.SortKey.Modified)]
    public async Task Sort_tools_publish_once_per_command_and_seed_new_surfaces_without_scanning(
        ExplorerListing.SortKey key)
    {
        using var f = new Fixture();
        var navigation = f.Session.NavigateAsync(Folder, ExplorerListing.AllFiles);
        var alpha = E("alpha.txt");
        var beta = E("beta.txt");
        f.Services.Scans[0].Complete(beta, alpha);
        Assert.True(await f.Finish(navigation));
        f.Session.SetItemChecked(Folder, alpha.Path, true);
        f.Session.SetItemChecked(Folder, beta.Path, true);
        f.Session.SetNameFilter("beta");
        // Choose from a different key, then use the same command for the direction button.
        f.Session.SelectSort(key == ExplorerListing.SortKey.Name
            ? ExplorerListing.SortKey.Type : ExplorerListing.SortKey.Name);
        var views = 0;
        var fills = 0;
        f.Session.ViewChanged += (_, _) => ++views;
        f.Session.FillCompleted += _ => ++fills;
        for (var command = 0; command < 2; command++)
        {
            f.Session.SelectSort(key);
            Assert.Equal(command + 1, views);
            Assert.Equal(command + 1, fills);
            Assert.Equal(key, f.Session.SortKey);
            Assert.Equal(command == 0 ? ExplorerSession.DefaultDescending(key)
                : !ExplorerSession.DefaultDescending(key), f.Session.Descending);
            Assert.Equal(new[] { beta.Path }, f.Session.CheckedPathsInView());
            Assert.True(f.Session.IsChecked(alpha.Path));
            Assert.True(f.Session.TrySeedDisplay(Folder, ExplorerListing.AllFiles, (_, display) =>
            {
                Assert.Same(f.Session.Display, display);
                Assert.Equal(beta.Path, Assert.Single(display).Path);
            }));
            Assert.Equal(command + 1, views); // seeding never republishes or toggles direction
            Assert.Single(f.Services.Scans);
        }
        f.Session.SetNameFilter("");
        Assert.Equal(2, f.Session.CheckedPathsInView().Count);
        Assert.Single(f.Services.Scans);
    }

    [Fact]
    public async Task No_view_navigation_publishes_in_order_and_uses_latest_query_without_another_scan()
    {
        using var f = new Fixture();
        var phases = new List<ExplorerSessionUpdateKind>();
        f.Session.Updated += update => phases.Add(update.Kind);
        var navigation = f.Session.NavigateAsync(Folder, new[] { ".txt" });
        Assert.Equal(Folder, f.Session.CurrentFolder);
        Assert.Equal("", f.Session.DisplayFolder);
        Assert.Empty(f.Session.Display);
        var generation = f.Session.Generation;
        f.Session.SetNameFilter("beta");
        Assert.Equal(generation, f.Session.Generation);
        Assert.False(f.Services.Scans[0].Cancellation.IsCancellationRequested);
        f.Services.Scans[0].Complete(E("alpha.txt"), E("beta.txt"));
        Assert.True(await f.Finish(navigation));
        Assert.Equal("beta.txt", Assert.Single(f.Session.Display).Name);
        Assert.Equal(Folder, f.Session.DisplayFolder);
        Assert.Single(f.Services.Scans);
        Assert.Equal(new[] { ExplorerSessionUpdateKind.PreparingNavigation,
            ExplorerSessionUpdateKind.FolderRequested, ExplorerSessionUpdateKind.ClearingEntries,
            ExplorerSessionUpdateKind.NavigationStarted, ExplorerSessionUpdateKind.Scanning,
            ExplorerSessionUpdateKind.Scanned, ExplorerSessionUpdateKind.PreparingDisplay,
            ExplorerSessionUpdateKind.DisplayChanged }, phases);
        var before = f.Services.Scans.Count;
        f.Session.SetNameFilter("");
        f.Session.SelectSort(ExplorerListing.SortKey.Size);
        f.Session.SetExtensionVisible(".txt", false);
        f.Session.RefreshView();
        Assert.Equal(before, f.Services.Scans.Count);
        Assert.Empty(f.Session.Display);
    }

    [Fact]
    public async Task Close_cancels_running_navigation_and_queued_work_without_needing_dispatch_to_drain()
    {
        var f = new Fixture();
        var navigation = f.Session.NavigateAsync(Folder, ExplorerListing.AllFiles);
        var scan = Assert.Single(f.Services.Scans);
        f.Session.Dispose();
        f.Session.Dispose();
        Assert.False(await navigation.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.True(scan.Cancellation.IsCancellationRequested);
        Assert.Equal(1, f.Services.Disposals);
        Assert.False(await f.Session.NavigateAsync("C:/other", ExplorerListing.AllFiles));
        scan.Complete(E("late.txt")); // a scanner may not be able to interrupt running OS I/O
        await f.Services.Drain();
        Assert.Empty(f.Session.Display);
        Assert.Single(f.Services.Scans);
        Assert.Throws<ObjectDisposedException>(() => f.Session.Start(f.Services));
        f.Dispose();
    }

    [Fact]
    public async Task Close_also_completes_a_navigation_whose_publication_is_queued_but_never_dispatched()
    {
        using var f = new Fixture();
        var navigation = f.Session.NavigateAsync(Folder, ExplorerListing.AllFiles);
        f.Services.Scans[0].Complete(E("late.txt"));
        await Until(() => f.Services.Queue.Count > 0); // deliberately do not pump the owner's queue
        f.Session.Dispose();
        Assert.False(await navigation.WaitAsync(TimeSpan.FromSeconds(5)));
        await f.Services.Drain();
        Assert.Empty(f.Session.Display);
    }

    [Fact]
    public async Task Overlapping_navigation_cancels_previous_request_and_only_latest_result_is_accepted()
    {
        using var f = new Fixture();
        var first = f.Session.NavigateAsync(Folder, ExplorerListing.AllFiles);
        var second = f.Session.NavigateAsync("C:/second", ExplorerListing.AllFiles);
        Assert.True(f.Services.Scans[0].Cancellation.IsCancellationRequested);
        Assert.False(await first);
        f.Services.Scans[1].Complete(E("new.txt", "C:/second"));
        Assert.True(await f.Finish(second));
        f.Services.Scans[0].Complete(E("old.txt"));
        await f.Services.Drain();
        Assert.Equal("C:/second", f.Session.DisplayFolder);
        Assert.Equal("new.txt", Assert.Single(f.Session.Display).Name);
    }

    [Fact]
    public async Task Late_watch_creation_and_queued_adoption_are_discarded_once_on_navigation_or_close()
    {
        using var f = new Fixture();
        var first = f.Session.NavigateAsync(Folder, ExplorerListing.AllFiles);
        var second = f.Session.NavigateAsync("C:/other", ExplorerListing.AllFiles);
        var old = f.Services.Watches[0].Complete();
        await Until(() => old.Disposals == 1);
        Assert.Equal(1, old.Stops);
        var current = f.Services.Watches[1].Complete();
        await Until(() => f.Services.Queue.Count > 0); // watch is created but adoption is queued
        f.Session.Dispose();
        await Until(() => current.Disposals == 1);
        await f.Services.Drain();
        Assert.Equal(1, current.Stops);
        Assert.Equal(1, current.Disposals);
        Assert.False(await first);
        Assert.False(await second);
        old.Fire(E("x").Path, evenIfStopped: true);
        current.Fire(E("y").Path, evenIfStopped: true);
        Assert.Empty(f.Services.Probes);
    }

    [Fact]
    public async Task Watch_created_after_close_or_failed_adoption_dispatch_is_disposed()
    {
        using var closed = new Fixture();
        _ = closed.Session.NavigateAsync(Folder, ExplorerListing.AllFiles);
        closed.Session.Dispose();
        var late = closed.Services.Watches[0].Complete();
        await Until(() => late.Disposals == 1);
        Assert.Empty(closed.Services.Queue);

        using var rejected = new Fixture();
        _ = rejected.Session.NavigateAsync(Folder, ExplorerListing.AllFiles);
        rejected.Services.RejectDispatch = true;
        var refused = rejected.Services.Watches[0].Complete();
        await Until(() => refused.Disposals == 1);
        Assert.Equal(1, refused.Stops);
    }

    [Fact]
    public async Task Hidden_or_absent_view_keeps_one_watch_and_coalesces_one_running_plus_one_pending_refresh()
    {
        using var f = new Fixture();
        var initial = E("a.txt");
        await f.Load(initial);
        var watcher = await f.AdoptWatch();
        watcher.Fire(initial.Path);
        for (var i = 0; i < 10; i++) watcher.Fire(initial.Path);
        Assert.Single(f.Services.Probes);
        f.Services.Probes[0].Complete(Folder);
        await f.Until(() => f.Services.Scans.Count == 2);
        f.Services.Scans[1].Complete(E("a.txt"), E("b.txt"));
        await f.Until(() => watcher.RefreshRequests > 0);
        Assert.Equal(2, f.Session.Display.Count);
        Assert.Single(f.Services.Watches); // auto-refresh doesn't replace the watch
        watcher.Fire(initial.Path); // next debounced notification releases exactly one follow-up
        Assert.Equal(2, f.Services.Probes.Count);
        f.Services.Probes[1].Complete(Folder);
        await f.Until(() => f.Services.Scans.Count == 3);
        f.Services.Scans[2].Complete(E("a.txt"));
        await f.Until(() => f.Session.Display.Count == 1);
        Assert.Single(f.Services.Watches);
    }

    [Fact]
    public async Task Rename_blocks_probe_and_rejects_mid_scan_result_then_replays_dirty_paths()
    {
        using var f = new Fixture();
        var a = E("a.txt");
        var b = E("b.txt");
        await f.Load(a, b);
        var watcher = await f.AdoptWatch();
        f.Services.IsEditing = true;
        watcher.Fire(a.Path);
        Assert.Empty(f.Services.Probes);
        f.Services.IsEditing = false;
        f.Session.NotifyEditEnded();
        Assert.Single(f.Services.Probes);
        f.Services.Probes[0].Complete(Folder);
        await f.Until(() => f.Services.Scans.Count == 2);
        f.Services.IsEditing = true;
        var dispatched = f.Services.Executed;
        f.Services.Scans[1].Complete(E("a.txt"), E("b.txt"));
        await f.Until(() => f.Services.Executed >= dispatched + 2);
        Assert.Same(a, f.Session.Entries[0]);
        Assert.Equal(0, watcher.RefreshRequests);
        f.Services.IsEditing = false;
        f.Session.NotifyEditEnded();
        await f.Until(() => f.Services.Probes.Count == 2);
        f.Services.Probes[1].Complete(Folder);
        await f.Until(() => f.Services.Scans.Count == 3);
        var replayed = E("a.txt");
        f.Services.Scans[2].Complete(replayed, E("b.txt"));
        await f.Until(() => ReferenceEquals(replayed, f.Session.Entries[0]));
        Assert.Same(b, f.Session.Entries[1]); // only the dirty path receives a new request generation
    }

    [Fact]
    public async Task Sort_during_watch_scan_cancels_result_but_replays_dirty_paths_without_duplicate_watch()
    {
        using var f = new Fixture();
        var a = E("a.txt");
        var b = E("b.txt");
        await f.Load(a, b);
        var watcher = await f.AdoptWatch();
        watcher.Fire(a.Path);
        f.Services.Probes[0].Complete(Folder);
        await f.Until(() => f.Services.Scans.Count == 2);
        f.Session.SelectSort(ExplorerListing.SortKey.Name);
        Assert.True(f.Services.Scans[1].Cancellation.IsCancellationRequested);
        await f.Until(() => watcher.RefreshRequests > 0);
        watcher.Fire(b.Path);
        f.Services.Probes[1].Complete(Folder);
        await f.Until(() => f.Services.Scans.Count == 3);
        var nextA = E("a.txt");
        var nextB = E("b.txt");
        f.Services.Scans[2].Complete(nextA, nextB);
        await f.Until(() => ReferenceEquals(nextA, f.Session.Entries[0]));
        Assert.Same(nextB, f.Session.Entries[1]); // dirt from both rejected and new batches survived
        Assert.Equal(new[] { "b.txt", "a.txt" }, f.Session.Display.Select(e => e.Name));
        f.Services.Scans[1].Complete(E("stale.txt"));
        await f.Services.Drain();
        Assert.Single(f.Services.Watches);
        Assert.Equal(2, f.Session.Display.Count);
    }

    [Fact]
    public async Task Error_replaces_watcher_forces_all_entries_new_and_ignores_old_callbacks()
    {
        using var f = new Fixture();
        var old = E("a.txt");
        await f.Load(old);
        var watcher = await f.AdoptWatch();
        watcher.Fire(error: true);
        Assert.Equal(2, f.Services.Watches.Count);
        Assert.Equal(1, watcher.Stops);
        watcher.Fire(old.Path, evenIfStopped: true);
        Assert.Single(f.Services.Probes);
        var replacement = await f.AdoptWatch(index: 1);
        f.Services.Probes[0].Complete(Folder);
        await f.Until(() => f.Services.Scans.Count == 2);
        var fresh = E("a.txt");
        f.Services.Scans[1].Complete(fresh);
        await f.Until(() => ReferenceEquals(fresh, f.Session.Entries[0]));
        Assert.NotSame(old, f.Session.Entries[0]);
        Assert.Equal(0, replacement.Stops);
    }

    [Fact]
    public async Task Explicit_refresh_replaces_watch_and_does_not_reuse_old_entries()
    {
        using var f = new Fixture();
        var old = E("a.txt");
        await f.Load(old);
        var watcher = await f.AdoptWatch();
        f.Session.SetItemChecked(Folder, old.Path, true);
        var navigation = f.Session.NavigateAsync(Folder, ExplorerListing.AllFiles);
        Assert.Equal(1, watcher.Stops);
        Assert.Equal(2, f.Services.Watches.Count);
        Assert.True(f.Session.IsChecked(old.Path));
        var fresh = E("a.txt");
        f.Services.Scans[1].Complete(fresh);
        Assert.True(await f.Finish(navigation));
        Assert.Same(fresh, f.Session.Entries[0]);
        Assert.True(f.Session.IsChecked(old.Path));
    }

    [Fact]
    public async Task Same_folder_failure_keeps_query_and_checks_and_successful_recovery_prunes_only_missing()
    {
        using var f = new Fixture();
        var a = E("alpha.txt");
        var b = E("beta.txt");
        await f.Load(a, b);
        f.Session.SetItemChecked(Folder, a.Path, true);
        f.Session.SetItemChecked(Folder, b.Path, true);
        f.Session.SetNameFilter("alpha");
        var failed = f.Session.NavigateAsync(Folder, ExplorerListing.AllFiles);
        f.Services.Scans[1].Fail(new IOException("synthetic"));
        Assert.False(await f.Finish(failed));
        Assert.Equal("alpha", f.Session.Query);
        Assert.Equal("", f.Session.DisplayFolder);
        Assert.True(f.Session.IsChecked(b.Path));
        var recovery = f.Session.NavigateAsync(Folder, ExplorerListing.AllFiles);
        f.Services.Scans[2].Complete(a);
        Assert.True(await f.Finish(recovery));
        Assert.Equal(new[] { a.Path }, f.Session.CheckedPathsInView());
        Assert.False(f.Session.IsChecked(b.Path));
    }

    [Theory]
    [InlineData(ExplorerSessionUpdateKind.NavigationStarted)]
    [InlineData(ExplorerSessionUpdateKind.PreparingDisplay)]
    [InlineData(ExplorerSessionUpdateKind.ClearingEntries)]
    public async Task Reentrant_navigation_at_adapter_boundary_cannot_publish_old_folder(ExplorerSessionUpdateKind boundary)
    {
        using var f = new Fixture();
        Task<bool>? replacement = null;
        f.Session.Updated += update =>
        {
            if (update.Kind == boundary && update.Folder == Folder)
                replacement = f.Session.NavigateAsync("C:/new", ExplorerListing.AllFiles);
        };
        var first = f.Session.NavigateAsync(Folder, ExplorerListing.AllFiles);
        if (boundary == ExplorerSessionUpdateKind.PreparingDisplay)
        {
            f.Services.Scans[0].Complete(E("old.txt"));
            await f.Until(() => replacement is not null);
        }
        Assert.NotNull(replacement);
        f.Services.Scans[^1].Complete(E("new.txt", "C:/new"));
        Assert.True(await f.Finish(replacement!));
        Assert.False(await f.Finish(first));
        Assert.Equal("C:/new", f.Session.DisplayFolder);
        Assert.Equal("new.txt", Assert.Single(f.Session.Display).Name);
    }

    [Fact]
    public async Task Reentrant_query_publication_does_not_publish_outer_projection_or_cancel_scan_generation()
    {
        using var f = new Fixture();
        await f.Load(E("alpha.txt"), E("beta.txt"));
        var publications = new List<string[]>();
        var reentered = false;
        f.Session.Updated += update =>
        {
            if (update.Kind == ExplorerSessionUpdateKind.PreparingDisplay && !reentered)
            {
                reentered = true;
                f.Session.SetNameFilter("beta");
            }
            if (update.Kind == ExplorerSessionUpdateKind.DisplayChanged)
                publications.Add(f.Session.Display.Select(e => e.Name).ToArray());
        };
        var generation = f.Session.Generation;
        f.Session.SetNameFilter("alpha");
        Assert.Equal(generation, f.Session.Generation);
        Assert.Equal(new[] { "beta.txt" }, Assert.Single(publications));
        Assert.Single(f.Services.Scans);
    }

    [Fact]
    public async Task Dispose_from_display_preparation_suppresses_publication()
    {
        using var f = new Fixture();
        var published = false;
        f.Session.Updated += update =>
        {
            if (update.Kind == ExplorerSessionUpdateKind.PreparingDisplay) f.Session.Dispose();
            if (update.Kind == ExplorerSessionUpdateKind.DisplayChanged) published = true;
        };
        var navigation = f.Session.NavigateAsync(Folder, ExplorerListing.AllFiles);
        f.Services.Scans[0].Complete(E("a.txt"));
        Assert.False(await f.Finish(navigation));
        Assert.False(published);
        Assert.Equal("", f.Session.DisplayFolder);
    }

    [Fact]
    public async Task Rejected_refresh_dispatch_does_not_lock_gate_and_dirty_paths_recover_on_next_owner_signal()
    {
        using var f = new Fixture();
        var old = E("a.txt");
        await f.Load(old);
        var watcher = await f.AdoptWatch();
        watcher.Fire(old.Path);
        f.Services.RejectDispatch = true;
        f.Services.Probes[0].Complete(Folder);
        await Until(() => f.Services.Rejected >= 2); // probe continuation and completion dispatch
        f.Services.RejectDispatch = false;
        watcher.Fire(E("b.txt").Path);
        Assert.Equal(2, f.Services.Probes.Count);
        f.Services.Probes[1].Complete(Folder);
        await f.Until(() => f.Services.Scans.Count == 2);
        var fresh = E("a.txt");
        f.Services.Scans[1].Complete(fresh);
        await f.Until(() => ReferenceEquals(fresh, f.Session.Entries[0]));
    }

    [Fact]
    public async Task Host_notifications_follow_adapters_and_work_without_a_legacy_surface()
    {
        using var f = new Fixture();
        var seen = new List<string>();
        f.Session.Updated += update =>
        {
            if (update.Kind == ExplorerSessionUpdateKind.DisplayChanged) seen.Add("adapter");
        };
        f.Session.NavigationStarted += folder => { Assert.Equal(Folder, folder); seen.Add("navigation"); };
        f.Session.ViewChanged += (folder, entries) =>
        {
            Assert.Equal(Folder, folder);
            Assert.Same(f.Session.Display, entries);
            Assert.True(f.Session.IsDisplayingFolder(folder));
            seen.Add("view");
        };
        f.Session.FillCompleted += entries => { Assert.Same(f.Session.Display, entries); seen.Add("fill"); };
        await f.Load(E("alpha.txt"), E("beta.txt"));
        Assert.Equal(new[] { "navigation", "adapter", "view", "fill" }, seen);
        seen.Clear();
        f.Session.SetNameFilter("beta");
        Assert.Equal(new[] { "adapter", "view", "fill" }, seen);
        Assert.Single(f.Services.Scans);
    }

    [Fact]
    public async Task Reentrant_query_does_not_send_old_view_to_next_host_or_old_fill()
    {
        using var f = new Fixture();
        var views = new List<IReadOnlyList<ExplorerListing.Entry>>();
        var fills = new List<IReadOnlyList<ExplorerListing.Entry>>();
        f.Session.ViewChanged += (_, entries) =>
        {
            if (entries.Count == 2) f.Session.SetNameFilter("beta");
        };
        f.Session.ViewChanged += (_, entries) => views.Add(entries);
        f.Session.FillCompleted += fills.Add;
        var navigation = f.Session.NavigateAsync(Folder, ExplorerListing.AllFiles);
        f.Services.Scans[0].Complete(E("alpha.txt"), E("beta.txt"));
        Assert.False(await f.Finish(navigation)); // nested publication superseded the outer one
        Assert.Equal("beta.txt", Assert.Single(Assert.Single(views)).Name);
        Assert.Same(Assert.Single(views), Assert.Single(fills));
    }

    [Fact]
    public async Task Reentrant_adapter_query_stops_old_update_before_next_adapter()
    {
        using var f = new Fixture();
        var seen = new List<string>();
        f.Session.Updated += update =>
        {
            if (update.Kind == ExplorerSessionUpdateKind.DisplayChanged && f.Session.Display.Count == 2)
                f.Session.SetNameFilter("beta");
        };
        f.Session.Updated += update =>
        {
            if (update.Kind == ExplorerSessionUpdateKind.DisplayChanged)
                seen.Add(string.Join(",", f.Session.Display.Select(entry => entry.Name)));
        };
        var navigation = f.Session.NavigateAsync(Folder, ExplorerListing.AllFiles);
        f.Services.Scans[0].Complete(E("alpha.txt"), E("beta.txt"));
        Assert.False(await f.Finish(navigation));
        Assert.Equal(new[] { "beta.txt" }, seen);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Reentrant_navigation_or_close_from_view_blocks_stale_fill(bool close)
    {
        using var f = new Fixture();
        var fills = 0;
        var lateViews = 0;
        f.Session.ViewChanged += (_, _) =>
        {
            if (close) f.Session.Dispose();
            else _ = f.Session.NavigateAsync("C:/next", ExplorerListing.AllFiles);
        };
        f.Session.ViewChanged += (_, _) => lateViews++;
        f.Session.FillCompleted += _ => fills++;
        var navigation = f.Session.NavigateAsync(Folder, ExplorerListing.AllFiles);
        f.Services.Scans[0].Complete(E("alpha.txt"));
        Assert.False(await f.Finish(navigation));
        Assert.Equal(0, fills);
        Assert.Equal(0, lateViews);
    }

    [Fact]
    public async Task Scan_failure_sends_empty_host_view_without_fill_and_retains_retry()
    {
        using var f = new Fixture();
        await f.Load(E("alpha.txt"));
        var views = new List<IReadOnlyList<ExplorerListing.Entry>>();
        var fills = 0;
        f.Session.ViewChanged += (_, entries) => views.Add(entries);
        f.Session.FillCompleted += _ => fills++;
        var failure = f.Session.NavigateAsync(Folder, ExplorerListing.AllFiles);
        f.Services.Scans[^1].Fail(new IOException("fixture failure"));
        Assert.False(await f.Finish(failure));
        Assert.Empty(Assert.Single(views));
        Assert.Equal(0, fills);
        await f.Load(E("recovered.txt"));
        Assert.Equal(1, fills);
    }

    [Fact]
    public async Task Host_check_notifications_reach_both_consumers_once_and_preserve_hidden_checks()
    {
        using var f = new Fixture();
        var alpha = E("alpha.txt");
        await f.Load(alpha, E("beta.txt"));
        var central = 0;
        var fallback = 0;
        f.Session.ChecksChanged += () => central++;
        f.Session.ChecksChanged += () => fallback++;
        Assert.True(f.Session.SetItemChecked(Folder, alpha.Path, true));
        Assert.False(f.Session.SetItemChecked(Folder, alpha.Path, true));
        Assert.Equal(1, central);
        Assert.Equal(central, fallback);
        f.Session.SetNameFilter("beta");
        Assert.Empty(f.Session.CheckedPathsForFolder(Folder));
        Assert.True(f.Session.IsChecked(alpha.Path));
        Assert.False(f.Session.SetItemChecked(Folder, alpha.Path, false));
        f.Session.SetNameFilter("");
        Assert.True(f.Session.SetItemChecked(Folder, alpha.Path, false));
        Assert.Equal(2, central);
        Assert.Equal(central, fallback);
        f.Session.Dispose();
        Assert.False(f.Session.SetItemChecked(Folder, alpha.Path, true));
    }

    [Fact]
    public async Task Real_watch_statistics_count_creation_stop_and_disposal_idempotently()
    {
        var folder = Path.Combine(Path.GetTempPath(), "KOTU-A417-watch-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            using var services = new ExplorerSessionServices(_ => true, () => false);
            var watch = await services.CreateWatchAsync(folder, _ => { });
            Assert.Equal(new ExplorerWatchStatistics(1, 0, 0, 1, 1, 1, 1), services.WatchStatistics);
            watch.StopNotifications();
            watch.StopNotifications();
            Assert.Equal(new ExplorerWatchStatistics(1, 1, 0, 1, 1, 0, 1), services.WatchStatistics);
            watch.Dispose();
            watch.Dispose();
            Assert.Equal(new ExplorerWatchStatistics(1, 1, 1, 0, 1, 0, 1), services.WatchStatistics);
        }
        finally { Directory.Delete(folder); }
    }

    [Fact]
    public async Task Adopted_watcher_count_tracks_single_owner_until_dispose()
    {
        using var f = new Fixture();
        await f.Load(E("alpha.txt"));
        Assert.Equal(0, f.Session.ActiveWatcherCount);
        var watch = await f.AdoptWatch();
        Assert.Equal(1, f.Session.ActiveWatcherCount);
        f.Session.SetNameFilter("alpha");
        f.Session.SelectSort(ExplorerListing.SortKey.Size);
        Assert.Equal(1, f.Session.ActiveWatcherCount);
        Assert.Single(f.Services.Watches);
        f.Session.Dispose();
        Assert.Equal(0, f.Session.ActiveWatcherCount);
        await Until(() => watch.Disposals == 1);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task New_surface_seeds_published_snapshot_including_empty_without_scan_or_host_events(bool empty)
    {
        using var f = new Fixture();
        var alpha = E("alpha.txt");
        await f.Load(empty ? [] : [alpha]);
        if (!empty) f.Session.SetItemChecked(Folder, alpha.Path, true);
        var events = 0;
        f.Session.ViewChanged += (_, _) => events++;
        f.Session.FillCompleted += _ => events++;
        string? surfaceFolder = null;
        IReadOnlyList<ExplorerListing.Entry>? surfaceEntries = null;
        Assert.True(f.Session.TrySeedDisplay(Folder, ExplorerListing.AllFiles, (folder, entries) =>
        {
            surfaceFolder = folder;
            surfaceEntries = entries;
        }));
        Assert.Equal(Folder, surfaceFolder);
        Assert.Same(f.Session.Display, surfaceEntries);
        Assert.Equal(empty ? 0 : 1, surfaceEntries!.Count);
        Assert.Equal(empty ? 0 : 1, f.Session.CheckedPathsForFolder(Folder).Count);
        Assert.Single(f.Services.Scans);
        Assert.Equal(0, events);
    }

    [Fact]
    public async Task Surface_seed_rejects_wrong_folder_or_module_and_closed_session()
    {
        using var f = new Fixture();
        await f.Load(E("alpha.txt"));
        var calls = 0;
        void Render(string folder, IReadOnlyList<ExplorerListing.Entry> entries) => calls++;
        Assert.False(f.Session.TrySeedDisplay("C:/other", ExplorerListing.AllFiles, Render));
        Assert.False(f.Session.TrySeedDisplay(Folder, [".png"], Render));
        f.Session.Dispose();
        Assert.False(f.Session.TrySeedDisplay(Folder, ExplorerListing.AllFiles, Render));
        Assert.Equal(0, calls);
    }

    [Theory]
    [InlineData(Folder)]
    [InlineData("C:/other")]
    public async Task Surface_seed_rejects_inflight_generation_even_when_display_folder_still_matches(string target)
    {
        using var f = new Fixture();
        await f.Load(E("old.txt"));
        var navigation = f.Session.NavigateAsync(target, ExplorerListing.AllFiles);
        var calls = 0;
        Assert.False(f.Session.TrySeedDisplay(target, ExplorerListing.AllFiles, (_, _) => calls++));
        Assert.Equal(0, calls);
        f.Services.Scans[^1].Complete(E("fresh.txt", target));
        Assert.True(await f.Finish(navigation));
        Assert.True(f.Session.TrySeedDisplay(target, ExplorerListing.AllFiles, (_, entries) =>
            Assert.Equal("fresh.txt", Assert.Single(entries).Name)));
    }

    [Fact]
    public async Task Surface_seed_reports_invalidation_when_render_reenters_query()
    {
        using var f = new Fixture();
        await f.Load(E("alpha.txt"), E("beta.txt"));
        Assert.False(f.Session.TrySeedDisplay(Folder, ExplorerListing.AllFiles, (_, _) => f.Session.SetNameFilter("beta")));
        Assert.True(f.Session.TrySeedDisplay(Folder, ExplorerListing.AllFiles, (_, entries) =>
            Assert.Equal("beta.txt", Assert.Single(entries).Name)));
        Assert.Single(f.Services.Scans);
    }

    [Fact]
    public async Task Folder_preferences_restore_before_navigation_notifications_and_stale_surfaces_cannot_cancel_scan()
    {
        using var f = new Fixture();
        await f.Load(E("alpha.txt"));
        Assert.True(f.Session.SelectViewMode(Folder, ExplorerPresentationState.ViewMode.List));
        Assert.True(f.Session.SelectSort(Folder, ExplorerListing.SortKey.Size));
        var secondFolder = "C:/second";
        var observed = false;
        f.Session.Updated += update =>
        {
            if (update.Kind != ExplorerSessionUpdateKind.FolderRequested || update.Folder != secondFolder) return;
            observed = true;
            Assert.Equal(ExplorerPresentationState.ViewMode.Preview, f.Session.Presentation.Mode);
            Assert.False(f.Session.SelectViewMode(Folder, ExplorerPresentationState.ViewMode.List));
            Assert.False(f.Session.SelectViewMode(secondFolder, ExplorerPresentationState.ViewMode.List));
            Assert.False(f.Session.SelectSort(Folder, ExplorerListing.SortKey.Name));
            Assert.False(f.Session.SelectSort(secondFolder, ExplorerListing.SortKey.Name));
        };
        var navigation = f.Session.NavigateAsync(secondFolder, ExplorerListing.AllFiles);
        Assert.True(observed);
        var generation = f.Session.Generation;
        Assert.False(f.Session.SelectSort(Folder, ExplorerListing.SortKey.Modified));
        Assert.Equal(generation, f.Session.Generation);
        Assert.False(f.Services.Scans[^1].Cancellation.IsCancellationRequested);
        f.Services.Scans[^1].Complete(E("beta.txt", secondFolder));
        Assert.True(await f.Finish(navigation));
        Assert.False(f.Session.SelectViewMode(Folder, ExplorerPresentationState.ViewMode.List));
        var returning = f.Session.NavigateAsync(Folder, ExplorerListing.AllFiles);
        Assert.Equal(ExplorerPresentationState.ViewMode.List, f.Session.Presentation.Mode);
        Assert.Equal(ExplorerListing.SortKey.Size, f.Session.SortKey);
        f.Services.Scans[^1].Complete(E("alpha.txt"));
        Assert.True(await f.Finish(returning));
    }

    [Fact]
    public async Task Watch_rescan_keeps_current_preferences_until_departure_even_when_another_window_saved_new_values()
    {
        var settings = new KOTU.Core.Settings.MemorySettingsService();
        using var f = new Fixture();
        f.Session.Settings = settings;
        await f.Load(E("alpha.txt"));
        f.Session.SelectViewMode(Folder, ExplorerPresentationState.ViewMode.List);
        f.Session.SelectSort(Folder, ExplorerListing.SortKey.Size);
        var watch = await f.AdoptWatch();
        using var other = new ExplorerSession { Settings = settings };
        other.BeginNavigation(Folder);
        other.AcceptScan(new[] { E("alpha.txt") });
        other.ArrangeFromScan();
        other.MarkDisplayed();
        other.SelectViewMode(Folder, ExplorerPresentationState.ViewMode.Preview);
        other.SelectSort(Folder, ExplorerListing.SortKey.Modified);
        watch.Fire(Folder + "/alpha.txt");
        await f.Until(() => f.Services.Probes.Count == 1);
        f.Services.Probes[0].Complete(Folder);
        await f.Until(() => f.Services.Scans.Count == 2);
        f.Services.Scans[1].Complete(E("alpha.txt"), E("beta.txt"));
        await f.Until(() => f.Session.Display.Count == 2);
        Assert.Equal(ExplorerPresentationState.ViewMode.List, f.Session.Presentation.Mode);
        Assert.Equal(ExplorerListing.SortKey.Size, f.Session.SortKey);
        var departing = f.Session.NavigateAsync("C:/elsewhere", ExplorerListing.AllFiles);
        var returning = f.Session.NavigateAsync(Folder, ExplorerListing.AllFiles);
        Assert.False(await departing);
        Assert.Equal(ExplorerPresentationState.ViewMode.Preview, f.Session.Presentation.Mode);
        Assert.Equal(ExplorerListing.SortKey.Modified, f.Session.SortKey);
        f.Services.Scans[^1].Complete(E("alpha.txt"));
        Assert.True(await f.Finish(returning));
    }

    private static async Task Until(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (!condition())
        {
            Assert.True(DateTime.UtcNow < deadline, "Timed out waiting for the controlled runtime.");
            await Task.Delay(1);
        }
    }

    private sealed class Fixture : IDisposable
    {
        public ExplorerSession Session { get; } = new();
        public Services Services { get; } = new();
        public Fixture() => Session.Start(Services);
        public async Task Until(Func<bool> condition) => await ExplorerSessionRuntimeTests.Until(() =>
        {
            Services.Pump();
            return condition();
        });
        public async Task<bool> Finish(Task<bool> task)
        {
            await Until(() => task.IsCompleted);
            return await task;
        }
        public async Task Load(params ExplorerListing.Entry[] entries)
        {
            var task = Session.NavigateAsync(Folder, ExplorerListing.AllFiles);
            Services.Scans[^1].Complete(entries);
            Assert.True(await Finish(task));
        }
        public async Task<Watch> AdoptWatch(int index = 0)
        {
            var watch = Services.Watches[index].Complete();
            await ExplorerSessionRuntimeTests.Until(() => Services.Queue.Count > 0);
            Services.Pump();
            return watch;
        }
        public void Dispose() => Session.Dispose();
    }

    private sealed class Services : IExplorerSessionServices
    {
        public bool IsEditing { get; set; }
        public volatile bool RejectDispatch;
        public int Rejected;
        public int Disposals;
        public int Executed;
        public ConcurrentQueue<Action> Queue { get; } = new();
        public List<Scan> Scans { get; } = [];
        public List<Probe> Probes { get; } = [];
        public List<WatchCreation> Watches { get; } = [];
        public bool TryEnqueue(Action action)
        {
            if (RejectDispatch) { Interlocked.Increment(ref Rejected); return false; }
            Queue.Enqueue(action);
            return true;
        }
        public Task<IReadOnlyList<ExplorerListing.Entry>> ScanAsync(string folder,
            IReadOnlyList<string> extensions, bool includeHidden, CancellationToken cancellation)
        {
            var scan = new Scan(cancellation);
            Scans.Add(scan);
            return scan.Completion.Task;
        }
        public Task<string> ExistingAncestorAsync(string folder, CancellationToken cancellation)
        {
            var probe = new Probe(cancellation);
            Probes.Add(probe);
            return probe.Completion.Task;
        }
        public Task<IExplorerFolderWatch> CreateWatchAsync(string folder, Action<WatchChanges> changed)
        {
            var creation = new WatchCreation(changed);
            Watches.Add(creation);
            return creation.Completion.Task;
        }
        public void Pump() { while (Queue.TryDequeue(out var action)) { action(); Executed++; } }
        public async Task Drain() { Pump(); await Task.Yield(); Pump(); }
        public void Dispose() => Disposals++;
    }

    private sealed class Scan(CancellationToken cancellation)
    {
        public CancellationToken Cancellation { get; } = cancellation;
        public TaskCompletionSource<IReadOnlyList<ExplorerListing.Entry>> Completion { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public void Complete(params ExplorerListing.Entry[] entries) => Completion.SetResult(entries);
        public void Fail(Exception error) => Completion.SetException(error);
    }

    private sealed class Probe(CancellationToken cancellation)
    {
        public CancellationToken Cancellation { get; } = cancellation;
        public TaskCompletionSource<string> Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public void Complete(string folder) => Completion.SetResult(folder);
    }

    private sealed class WatchCreation(Action<WatchChanges> changed)
    {
        public TaskCompletionSource<IExplorerFolderWatch> Completion { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Watch Complete()
        {
            var watch = new Watch(changed);
            Completion.SetResult(watch);
            return watch;
        }
    }

    private sealed class Watch(Action<WatchChanges> changed) : IExplorerFolderWatch
    {
        public int Stops;
        public int Disposals;
        public int RefreshRequests;
        public void Fire(string? path = null, bool error = false, bool evenIfStopped = false)
        {
            if (Stops > 0 && !evenIfStopped) return;
            var change = new WatchChanges();
            if (error) change.MarkError(); else change.Add(path);
            changed(change);
        }
        public void RequestRefresh() => RefreshRequests++;
        public void StopNotifications() => Interlocked.Increment(ref Stops);
        public void Dispose() => Interlocked.Increment(ref Disposals);
    }
}
