using KOTU.Core.Threading;

namespace KOTU.Module.Image;

// One directory per viewer. SetFolder runs on the owner; only its serial worker touches the OS handle.
internal sealed class ImageFileWatch(ModuleWorker worker, Func<Action, bool> enqueue, Action<WatchChanges> changed)
{
    private FolderWatch? _watch;
    private string? _folder;
    private int _generation;

    public void SetFolder(string? folder)
    {
        if (string.Equals(_folder, folder, StringComparison.OrdinalIgnoreCase)) return;
        _folder = folder;
        var generation = ++_generation;
        worker.Post(() =>
        {
            _watch?.Dispose();
            _watch = null;
            if (folder is null) return;
            try
            {
                _watch = new FolderWatch(folder, enqueue, changes =>
                {
                    if (generation != _generation) return;
                    if (changes.Error)
                    {
                        // The handle may have stopped delivering events; resubscribe before reading.
                        _folder = null;
                        SetFolder(folder);
                    }
                    changed(changes);
                });
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException
                or PlatformNotSupportedException)
            {
                // Opening the image still works on filesystems without change notifications.
                enqueue(() => { if (generation == _generation) _folder = null; });
            }
        });
    }
}
