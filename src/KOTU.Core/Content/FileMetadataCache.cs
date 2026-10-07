namespace KOTU.Core.Content;

public sealed record FileMetadata(bool? Exists, long? Length)
{
    public static FileMetadata Unknown { get; } = new(null, null);

    /// <summary>워커 전용. 실패·소실은 표시 가능한 값으로 반환한다.</summary>
    public static FileMetadata Read(string path)
    {
        try
        {
            var info = new FileInfo(path);
            info.Refresh();
            return info.Exists ? new(true, info.Length) : new(false, null);
        }
        catch { return Unknown; }
    }
}

/// <summary>경로별 상한 캐시. 무효화/해제 전에 시작한 조회는 새 엔트리를 덮어쓰지 못한다.</summary>
public sealed class FileMetadataCache(Func<string, CancellationToken, Task<FileMetadata>> read, int capacity = 32)
    : IDisposable
{
    private sealed class Entry
    {
        public FileMetadata? Value;
        public Task<FileMetadata>? Pending;
    }

    private readonly object _gate = new();
    private readonly Dictionary<string, Entry> _entries = new(StringComparer.OrdinalIgnoreCase);
    private bool _disposed;

    public FileMetadata? Peek(string path)
    {
        lock (_gate) return _entries.TryGetValue(path, out var entry) ? entry.Value : null;
    }

    public void Invalidate(string path) { lock (_gate) _entries.Remove(path); }

    public async Task<FileMetadata?> GetAsync(string path, CancellationToken cancellation = default)
    {
        Entry entry;
        Task<FileMetadata> pending;
        lock (_gate)
        {
            if (_disposed || cancellation.IsCancellationRequested) return null;
            if (!_entries.TryGetValue(path, out entry!))
            {
                if (_entries.Count >= Math.Max(1, capacity)) _entries.Remove(_entries.Keys.First());
                _entries.Add(path, entry = new());
            }
            if (entry.Value is { } value) return value;
            pending = entry.Pending ??= ReadAsync(path, cancellation);
        }
        try
        {
            var value = await pending.ConfigureAwait(false);
            lock (_gate)
            {
                if (_disposed || cancellation.IsCancellationRequested ||
                    !_entries.TryGetValue(path, out var current) || !ReferenceEquals(current, entry)) return null;
                entry.Value = value;
                return value;
            }
        }
        catch (OperationCanceledException) { return null; }
        finally { lock (_gate) if (ReferenceEquals(entry.Pending, pending)) entry.Pending = null; }
    }

    private async Task<FileMetadata> ReadAsync(string path, CancellationToken cancellation)
    {
        try { return await read(path, cancellation).ConfigureAwait(false); }
        catch (OperationCanceledException) { throw; }
        catch { return FileMetadata.Unknown; }
    }

    public void Dispose() { lock (_gate) { _disposed = true; _entries.Clear(); } }
}
