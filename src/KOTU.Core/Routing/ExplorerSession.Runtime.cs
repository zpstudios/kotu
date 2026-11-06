using System.Collections.Concurrent;
using KOTU.Core.Diagnostics;
using KOTU.Core.Threading;

namespace KOTU.Core.Routing;

/// <summary>Ordered synchronous adapter boundaries; no presentation is required to run a session.</summary>
public enum ExplorerSessionUpdateKind
{
    PreparingNavigation, FolderRequested, ClearingEntries, NavigationStarted, Scanning, Scanned,
    PreparingDisplay, DisplayChanged, ScanFailed,
}

public sealed record ExplorerSessionUpdate(ExplorerSessionUpdateKind Kind, string Folder,
    bool FolderChanged = false, Exception? Error = null);

public sealed partial class ExplorerSession : IDisposable
{
    private IExplorerSessionServices? _services;
    private bool _started;
    private volatile bool _disposed;
    private readonly LatestRequest _scanRequest = new();
    private readonly LatestRequest _watchProbe = new();
    private readonly RefreshRequestGate _watchRefresh = new();
    private readonly WatchChanges _watchChanges = new();
    private IExplorerFolderWatch? _watcher;
    private volatile int _watchGeneration;
    // A created OS watch must also be owned while its adoption callback is still queued.
    private readonly object _watchLock = new();
    private readonly HashSet<IExplorerFolderWatch> _pendingWatches = [];
    private readonly TaskCompletionSource<bool> _closed = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int _publication;
    private int _displayGeneration = -1;
    private sealed record WatchCompletion(int Generation, WatchChanges Changes, bool Applied);
    private readonly ConcurrentQueue<WatchCompletion> _watchCompletions = new();

    public int Generation { get; private set; }
    public bool IsStarted => _started && !_disposed;
    public event Action<ExplorerSessionUpdate>? Updated;
    // Host notifications are independent of legacy presentation/Loaded lifetime.
    public event Action<string>? NavigationStarted;
    public event Action<string, IReadOnlyList<ExplorerListing.Entry>>? ViewChanged;
    public event Action<IReadOnlyList<ExplorerListing.Entry>>? FillCompleted;

    /// <summary>Adopted watch count for lifetime verification; pending creations are not active watches.</summary>
    public int ActiveWatcherCount => _watcher is null ? 0 : 1;
    public ExplorerWatchStatistics? WatchStatistics => (_services as ExplorerSessionServices)?.WatchStatistics;

    /// <summary>The window/overlay host starts once; view Loaded/Unloaded never starts or stops data.</summary>
    public void Start(IExplorerSessionServices services)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_started)
        {
            if (!ReferenceEquals(services, _services)) throw new InvalidOperationException("Session already started.");
            return;
        }
        _services = services;
        _started = true;
        if (CurrentFolder.Length > 0) EnsureWatch(CurrentFolder);
    }

    public Task<bool> NavigateAsync(string folder, IReadOnlyList<string> extensions) =>
        NavigateCoreAsync(folder, extensions, fromWatch: false);

    // Invoked on the owner. The asynchronous continuation always returns through TryEnqueue,
    // so the runtime also works without a WinUI SynchronizationContext or an ExplorerPane.
    private Task<bool> NavigateCoreAsync(string folder, IReadOnlyList<string> extensions,
        bool fromWatch, WatchChanges? changes = null)
    {
        if (!IsStarted) return Task.FromResult(false);
        DrainWatchCompletions();
        var generation = ++Generation;
        var cancellation = _scanRequest.Begin();
        DiagTrace.Write("nav", $"NavigateToAsync {folder} exts={extensions.Count}");
        SetExtensions(extensions);
        Emit(ExplorerSessionUpdateKind.PreparingNavigation, folder);
        if (!IsCurrent(generation)) return Task.FromResult(false);
        var folderChanged = BeginNavigation(folder);
        Emit(ExplorerSessionUpdateKind.FolderRequested, folder, folderChanged);
        if (!IsCurrent(generation)) return Task.FromResult(false);
        if (!fromWatch || folderChanged) EnsureWatch(folder);
        var watch = _watchGeneration;
        if (folderChanged)
        {
            Emit(ExplorerSessionUpdateKind.ClearingEntries, folder);
            if (!IsCurrent(generation)) return Task.FromResult(false);
            ClearNavigationEntries();
            Emit(ExplorerSessionUpdateKind.NavigationStarted, folder, folderChanged: true);
            if (!IsCurrent(generation)) return Task.FromResult(false);
            Notify(NavigationStarted, handler => handler(folder));
            if (!IsCurrent(generation)) return Task.FromResult(false);
        }
        Emit(ExplorerSessionUpdateKind.Scanning, folder, folderChanged);
        if (!IsCurrent(generation)) return Task.FromResult(false);
        var services = _services!;
        // Both work submission and the settings snapshot happen on the owner thread.
        Task<IReadOnlyList<ExplorerListing.Entry>> scan;
        try { scan = services.ScanAsync(folder, Extensions, ShowHidden, cancellation); }
        catch (Exception error) { scan = Task.FromException<IReadOnlyList<ExplorerListing.Entry>>(error); }
        return CompleteScanAsync(scan, folder, folderChanged, fromWatch, watch, generation, changes, cancellation);
    }

    private async Task<bool> CompleteScanAsync(Task<IReadOnlyList<ExplorerListing.Entry>> scan,
        string folder, bool folderChanged, bool fromWatch, int watch, int generation,
        WatchChanges? changes, CancellationToken cancellation)
    {
        IReadOnlyList<ExplorerListing.Entry>? entries = null;
        Exception? failure = null;
        try { entries = await scan.WaitAsync(cancellation).ConfigureAwait(false); }
        catch (OperationCanceledException)
        {
            if (fromWatch) await DispatchAsync(() =>
            {
                if (IsStarted && watch == _watchGeneration && generation != Generation) _watchRefresh.Defer();
            }).ConfigureAwait(false);
            return false;
        }
        catch (Exception error) { failure = error; }
        var applied = false;
        await DispatchAsync(() =>
        {
            if (!IsCurrent(generation) || (fromWatch && watch != _watchGeneration))
            {
                if (IsStarted && fromWatch && watch == _watchGeneration) _watchRefresh.Defer();
                return;
            }
            if (fromWatch && _services!.IsEditing) { _watchRefresh.Defer(); return; }
            if (failure is not null)
            {
                Emit(ExplorerSessionUpdateKind.ClearingEntries, folder);
                if (!IsCurrent(generation)) return;
                ClearFailedScan();
                Emit(ExplorerSessionUpdateKind.ScanFailed, folder, error: failure);
                if (!IsCurrent(generation)) return;
                Notify(ViewChanged, handler => handler(folder, []));
                DiagTrace.Write("nav", $"scan failed {folder} {failure.GetType().Name}: {failure.Message}");
                return;
            }
            Emit(ExplorerSessionUpdateKind.Scanned, folder);
            if (!IsCurrent(generation)) return;
            DiagTrace.Write("nav", $"scan done {folder} count={entries!.Count}");
            if (fromWatch && !folderChanged && changes is not null)
                entries = ExplorerReuse.Entries(Entries, entries!, changes);
            AcceptScan(entries!);
            ArrangeFromScan();
            AdvanceViewGeneration();
            applied = PublishDisplay();
        }).ConfigureAwait(false);
        return applied;
    }

    /// <summary>Extension-filter projection, without an OS scan; retains legacy scan invalidation.</summary>
    public void RefreshView()
    {
        if (_disposed) return;
        ArrangeFromScan();
        AdvanceViewGeneration();
        if (IsStarted) PublishDisplay();
    }

    private void AdvanceViewGeneration()
    {
        ++Generation;
        _scanRequest.Cancel();
    }

    private bool PublishDisplay()
    {
        if (!IsStarted) return false;
        var generation = Generation;
        var publication = ++_publication;
        var folder = CurrentFolder;
        Emit(ExplorerSessionUpdateKind.PreparingDisplay, folder);
        if (!IsCurrent(generation) || publication != _publication) return false;
        MarkDisplayed();
        _displayGeneration = generation;
        Emit(ExplorerSessionUpdateKind.DisplayChanged, folder);
        if (!IsCurrent(generation) || publication != _publication) return false;
        var entries = Display;
        Notify(ViewChanged, handler => handler(folder, entries));
        // A consumer can navigate, change the query, or close the window synchronously.
        // Never prefetch the old publication after that nested publication has completed.
        if (!IsCurrent(generation) || publication != _publication ||
            !ReferenceEquals(entries, Display) || !IsDisplayingFolder(folder)) return false;
        Notify(FillCompleted, handler => handler(entries));
        return IsCurrent(generation) && publication == _publication;
    }

    private bool IsCurrent(int generation) => IsStarted && generation == Generation;

    /// <summary>
    /// Attach a newly visible surface to an already published snapshot without scanning or
    /// broadcasting View/Fill again. A pending scan or a different module filter cannot be seeded.
    /// Call synchronously on the owner; a reentrant render invalidates the returned success.
    /// </summary>
    public bool TrySeedDisplay(string? folder, IReadOnlyList<string> extensions,
        Action<string, IReadOnlyList<ExplorerListing.Entry>> render)
    {
        if (!IsStarted || _displayGeneration != Generation || !IsDisplayingFolder(folder) ||
            !Extensions.SequenceEqual(extensions, StringComparer.OrdinalIgnoreCase)) return false;
        var generation = Generation;
        var publication = _publication;
        var entries = Display;
        render(DisplayFolder, entries);
        return IsCurrent(generation) && publication == _publication &&
            ReferenceEquals(entries, Display) && IsDisplayingFolder(folder);
    }
    private void Emit(ExplorerSessionUpdateKind kind, string folder, bool folderChanged = false,
        Exception? error = null) => Notify(Updated, handler => handler(new(kind, folder, folderChanged, error)));

    private void Notify<T>(T? subscribers, Action<T> invoke) where T : Delegate
    {
        if (subscribers is null) return;
        var generation = Generation;
        var publication = _publication;
        foreach (T subscriber in subscribers.GetInvocationList())
        {
            if (!IsCurrent(generation) || publication != _publication) break;
            invoke(subscriber);
        }
    }

    private async Task<bool> DispatchAsync(Action action)
    {
        if (_disposed) return false;
        var completion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        if (_services is not { } services || !services.TryEnqueue(() =>
        {
            if (_disposed) { completion.TrySetResult(false); return; }
            try { action(); completion.TrySetResult(true); }
            catch (Exception error) { completion.TrySetException(error); }
        })) completion.TrySetResult(false);
        // An accepted callback can be stranded when its window dispatcher shuts down.
        // Closing the owner completes requests without depending on that queue draining.
        var finished = await Task.WhenAny(completion.Task, _closed.Task).ConfigureAwait(false);
        return await finished.ConfigureAwait(false);
    }

    private void EnsureWatch(string folder)
    {
        TearDownWatch();
        if (!IsStarted || folder.Length == 0) return;
        _ = CreateWatchAsync(folder, _watchGeneration, _services!);
    }

    private async Task CreateWatchAsync(string folder, int generation, IExplorerSessionServices services)
    {
        IExplorerFolderWatch made;
        try
        {
            made = await services.CreateWatchAsync(folder, changes =>
            {
                if (!IsStarted || generation != _watchGeneration) return;
                // Restart first: teardown clears pending dirt, then the error forces full invalidation.
                if (changes.Error) EnsureWatch(CurrentFolder);
                _watchChanges.Merge(changes);
                RequestWatchRefresh();
            }).ConfigureAwait(false);
        }
        catch { return; } // inaccessible or missing folder: explicit refresh remains available
        lock (_watchLock)
        {
            if (!_disposed && generation == _watchGeneration) _pendingWatches.Add(made);
            else { DiscardWatcher(made); return; }
        }
        if (!services.TryEnqueue(() => AdoptWatch(made, generation)))
        {
            lock (_watchLock)
                if (_pendingWatches.Remove(made)) DiscardWatcher(made);
        }
    }

    private void AdoptWatch(IExplorerFolderWatch watcher, int generation)
    {
        lock (_watchLock)
            if (!_pendingWatches.Remove(watcher)) return; // already discarded at teardown/close
        if (!IsStarted || generation != _watchGeneration) { DiscardWatcher(watcher); return; }
        _watcher = watcher;
        if (_watchRefresh.Pending && !_watchRefresh.Running && !_services!.IsEditing)
            watcher.RequestRefresh();
    }

    private static void DiscardWatcher(IExplorerFolderWatch watcher)
    {
        watcher.StopNotifications();
        _ = Task.Run(() => { try { watcher.Dispose(); } catch { /* obsolete OS handle */ } });
    }

    private void TearDownWatch()
    {
        _watchProbe.Cancel();
        ++_watchGeneration;
        if (_watcher is { } watcher) { _watcher = null; DiscardWatcher(watcher); }
        lock (_watchLock)
        {
            foreach (var pending in _pendingWatches) DiscardWatcher(pending);
            _pendingWatches.Clear();
        }
        // Do not reset Running: only the old request's finally may release its slot.
        _watchRefresh.ResetPending();
        _watchChanges.Clear();
    }

    private void RequestWatchRefresh()
    {
        DrainWatchCompletions();
        if (!IsStarted || CurrentFolder.Length == 0 || !_watchRefresh.Request(_services!.IsEditing)) return;
        var folder = CurrentFolder;
        var watch = _watchGeneration;
        var load = Generation;
        var cancellation = _watchProbe.Begin();
        var changes = _watchChanges.Take();
        Task<string> probe;
        try { probe = _services.ExistingAncestorAsync(folder, cancellation); }
        catch (Exception error) { probe = Task.FromException<string>(error); }
        _ = CompleteWatchRefreshAsync(probe, folder, watch, load, changes, cancellation);
    }

    private async Task CompleteWatchRefreshAsync(Task<string> probe, string folder, int watch,
        int load, WatchChanges changes, CancellationToken cancellation)
    {
        var applied = false;
        try
        {
            string target;
            try { target = await probe.WaitAsync(cancellation).ConfigureAwait(false); }
            catch (OperationCanceledException) { return; }
            catch { target = folder; }
            Task<bool>? navigation = null;
            await DispatchAsync(() =>
            {
                if (!IsStarted || watch != _watchGeneration || folder != CurrentFolder) return;
                if (load != Generation || _services!.IsEditing) { _watchRefresh.Defer(); return; }
                navigation = NavigateCoreAsync(target, Extensions, fromWatch: true, changes);
            }).ConfigureAwait(false);
            if (navigation is not null) applied = await navigation.ConfigureAwait(false);
        }
        catch (OperationCanceledException) { }
        catch (Exception error) { DiagTrace.Write("watch", $"refresh failed {error.GetType().Name}"); }
        finally
        {
            _watchCompletions.Enqueue(new(watch, changes, applied));
            await DispatchAsync(DrainWatchCompletions).ConfigureAwait(false);
        }
    }

    private void DrainWatchCompletions()
    {
        if (!IsStarted) return;
        while (_watchCompletions.TryDequeue(out var completion))
        {
            if (!completion.Applied && completion.Generation == _watchGeneration)
                _watchChanges.Merge(completion.Changes);
            _watchRefresh.Complete();
            if (_watchRefresh.Pending && !_services!.IsEditing) _watcher?.RequestRefresh();
        }
        // If dispatch was temporarily rejected, the next owner request drains this queue
        // before asking for the running slot. No worker thread mutates owner state.
    }

    public void NotifyEditEnded()
    {
        DrainWatchCompletions();
        if (IsStarted && _watchRefresh.Pending) RequestWatchRefresh();
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _closed.TrySetResult(false);
        ++Generation;
        _scanRequest.Dispose();
        TearDownWatch();
        _watchProbe.Dispose();
        _services?.Dispose();
        Updated = null;
        NavigationStarted = null;
        ViewChanged = null;
        FillCompleted = null;
        ChecksChanged = null;
    }
}
