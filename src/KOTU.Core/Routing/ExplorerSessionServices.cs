using KOTU.Core.Threading;

namespace KOTU.Core.Routing;

/// <summary>OS work and owner dispatch for one explorer session. Watch callbacks run on the owner.</summary>
public interface IExplorerSessionServices : IDisposable
{
    bool IsEditing { get; }
    bool TryEnqueue(Action action);
    Task<IReadOnlyList<ExplorerListing.Entry>> ScanAsync(string folder, IReadOnlyList<string> extensions,
        bool includeHidden, CancellationToken cancellation);
    Task<string> ExistingAncestorAsync(string folder, CancellationToken cancellation);
    Task<IExplorerFolderWatch> CreateWatchAsync(string folder, Action<WatchChanges> changed);
}

public interface IExplorerFolderWatch : IDisposable
{
    void RequestRefresh();
    void StopNotifications();
}

public sealed record ExplorerWatchStatistics(int Created, int Stopped, int Disposed, int Live,
    int MaximumLive, int Notifying, int MaximumNotifying);

/// <summary>One serial scan/probe worker; the existing FolderWatch retains its 400ms debounce.</summary>
public sealed class ExplorerSessionServices(Func<Action, bool> enqueue, Func<bool> isEditing)
    : IExplorerSessionServices
{
    private ModuleWorker? _worker;
    private readonly object _watchStatisticsLock = new();
    private int _created, _stopped, _disposed, _maximumLive, _maximumNotifying;
    public ExplorerWatchStatistics WatchStatistics
    {
        get
        {
            lock (_watchStatisticsLock)
                return new(_created, _stopped, _disposed, _created - _disposed,
                    _maximumLive, _created - _stopped, _maximumNotifying);
        }
    }
    private ModuleWorker Worker => _worker ??= new ModuleWorker("KOTU explorer worker");
    public bool IsEditing => isEditing();
    public bool TryEnqueue(Action action) => enqueue(action);

    public Task<IReadOnlyList<ExplorerListing.Entry>> ScanAsync(string folder,
        IReadOnlyList<string> extensions, bool includeHidden, CancellationToken cancellation) =>
        Worker.Run(_ => ExplorerListing.List(folder, extensions, includeHidden: includeHidden), cancellation);

    public Task<string> ExistingAncestorAsync(string folder, CancellationToken cancellation) =>
        Worker.Run(ctx => PathProbe.ExistingAncestor(folder, ctx.Cancellation), cancellation);

    public Task<IExplorerFolderWatch> CreateWatchAsync(string folder, Action<WatchChanges> changed) =>
        Task.Run<IExplorerFolderWatch>(() =>
        {
            var inner = new FolderWatch(folder, enqueue, changed);
            lock (_watchStatisticsLock)
            {
                ++_created;
                _maximumLive = Math.Max(_maximumLive, _created - _disposed);
                _maximumNotifying = Math.Max(_maximumNotifying, _created - _stopped);
            }
            return new Watch(inner, this);
        });

    public void Dispose() { _worker?.Dispose(); }

    private sealed class Watch(FolderWatch inner, ExplorerSessionServices owner) : IExplorerFolderWatch
    {
        private int _stopped, _disposed;
        public void RequestRefresh() => inner.RequestRefresh();
        public void StopNotifications()
        {
            if (Interlocked.Exchange(ref _stopped, 1) != 0) return;
            inner.StopNotifications();
            lock (owner._watchStatisticsLock) ++owner._stopped;
        }
        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
            StopNotifications();
            inner.Dispose();
            lock (owner._watchStatisticsLock) ++owner._disposed;
        }
    }
}
