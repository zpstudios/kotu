namespace KOTU.Core.Threading;

/// <summary>한 감시 세대의 유한 dirty 집합. 호출자가 동기화하며, 초과 시 전체 무효화로 접는다.</summary>
public sealed class WatchChanges
{
    public const int PathLimit = 1024;
    private readonly HashSet<string> _paths = new(StringComparer.OrdinalIgnoreCase);
    public bool Error { get; private set; }
    public bool ForceAll { get; private set; }
    public int PathCount => _paths.Count;
    public bool Contains(string path) => ForceAll || _paths.Contains(path);

    public void Add(string? path)
    {
        if (ForceAll) return;
        if (string.IsNullOrEmpty(path) || (!_paths.Contains(path) && _paths.Count >= PathLimit))
        {
            ForceAll = true;
            _paths.Clear();
        }
        else _paths.Add(path);
    }

    public void MarkError() { Error = true; Add(null); }

    public void Merge(WatchChanges other)
    {
        Error |= other.Error;
        if (other.ForceAll) Add(null);
        else foreach (var path in other._paths) Add(path);
    }

    public WatchChanges Take()
    {
        var result = new WatchChanges();
        result.Merge(this);
        Clear();
        return result;
    }

    public void Clear() { _paths.Clear(); Error = ForceAll = false; }
}
