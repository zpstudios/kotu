namespace KOTU.Core.Threading;

/// <summary>생성/Dispose는 워커 전용. 통지는 워커에서 병합하고 주입된 큐에 한 번만 올린다.</summary>
public sealed class FolderWatch : IDisposable
{
    public const NotifyFilters Filters = NotifyFilters.FileName | NotifyFilters.DirectoryName |
                                        NotifyFilters.LastWrite | NotifyFilters.Size | NotifyFilters.Attributes;
    private readonly FileSystemWatcher _watcher;
    private readonly DebouncedWatchEvents _events;

    public FolderWatch(string folder, Func<Action, bool> enqueue, Action<WatchChanges> apply)
    {
        _events = new DebouncedWatchEvents(TimeSpan.FromMilliseconds(400), enqueue, apply);
        try
        {
            _watcher = new FileSystemWatcher(folder)
            {
                NotifyFilter = Filters,
                IncludeSubdirectories = false,
            };
            _watcher.Created += OnChange;
            _watcher.Deleted += OnChange;
            _watcher.Renamed += OnChange;
            _watcher.Changed += OnChange;
            _watcher.Error += OnError;
            _watcher.EnableRaisingEvents = true;
        }
        catch
        {
            _events.Dispose();
            _watcher?.Dispose();
            throw;
        }
    }

    private void OnChange(object sender, FileSystemEventArgs args) =>
        _events.Signal(path: args.FullPath, oldPath: (args as RenamedEventArgs)?.OldFullPath);
    private void OnError(object sender, ErrorEventArgs args) => _events.Signal(error: true);
    public void RequestRefresh() => _events.Signal();

    // UI에서 즉시 세대를 닫고, OS 핸들 해제는 호출자가 워커로 넘긴다.
    public void StopNotifications() => _events.Dispose();

    public void Dispose()
    {
        StopNotifications();
        _watcher.Created -= OnChange;
        _watcher.Deleted -= OnChange;
        _watcher.Renamed -= OnChange;
        _watcher.Changed -= OnChange;
        _watcher.Error -= OnError;
        _watcher.Dispose();
    }
}
