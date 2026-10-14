using System.Collections.ObjectModel;
using KOTU.Core.Threading;

namespace KOTU.Core.Routing;

/// <summary>자동 재조회에서만 이전 Entry 참조를 승계한다. 새 참조는 새 요청 세대를 뜻한다.</summary>
public static class ExplorerReuse
{
    public static IReadOnlyList<ExplorerListing.Entry> Entries(
        IReadOnlyList<ExplorerListing.Entry> previous, IReadOnlyList<ExplorerListing.Entry> scanned,
        WatchChanges changes)
    {
        var byPath = previous.ToDictionary(e => e.Path, StringComparer.OrdinalIgnoreCase);
        return scanned.Select(e => !changes.Contains(e.Path) && byPath.TryGetValue(e.Path, out var old)
            && SameState(old, e) ? old : e).ToArray();
    }

    public static bool SameState(ExplorerListing.Entry a, ExplorerListing.Entry b) =>
        string.Equals(a.Path, b.Path, StringComparison.OrdinalIgnoreCase) && a.Name == b.Name &&
        a.IsFolder == b.IsFolder && a.Size == b.Size && a.Modified == b.Modified &&
        a.Created == b.Created && a.IsPlaceholder == b.IsPlaceholder && a.Attributes == b.Attributes;

    public static (int Reused, int Created, int Removed) Items<T>(ObservableCollection<T> target,
        IReadOnlyList<ExplorerListing.Entry> entries, Func<T, ExplorerListing.Entry> entryOf,
        Func<T, bool> isAlive, Func<ExplorerListing.Entry, T> create, Action<T> invalidate) where T : class
    {
        var previous = new Dictionary<ExplorerListing.Entry, T>(ReferenceEqualityComparer.Instance);
        foreach (var item in target)
            if (isAlive(item)) previous.Add(entryOf(item), item);
        var desired = new List<T>(entries.Count);
        var reused = 0;
        foreach (var entry in entries)
        {
            if (previous.TryGetValue(entry, out var item)) reused++;
            else item = create(entry);
            desired.Add(item);
        }
        var alive = new HashSet<T>(desired, ReferenceEqualityComparer.Instance);
        foreach (var item in target)
            if (!alive.Contains(item)) invalidate(item);
        var removed = target.Count - reused;
        Apply(target, desired);
        return (reused, desired.Count - reused, removed);
    }

    /// <summary>Reset 없이 삭제·삽입·이동한다. 동일 참조 항목은 통지도 컨테이너 교체도 필요 없다.</summary>
    public static void Apply<T>(ObservableCollection<T> target, IReadOnlyList<T> desired) where T : class
    {
        var alive = new HashSet<T>(desired, ReferenceEqualityComparer.Instance);
        var retained = new HashSet<T>(target, ReferenceEqualityComparer.Instance);
        // 일반적인 같은 위치 파일 변경은 Replace 한 번으로 끝낸다. 뒤 행을 밀지 않는다.
        if (target.Count == desired.Count && Enumerable.Range(0, target.Count).All(i =>
                ReferenceEquals(target[i], desired[i]) ||
                (!alive.Contains(target[i]) && !retained.Contains(desired[i]))))
        {
            for (var i = 0; i < desired.Count; i++)
                if (!ReferenceEquals(target[i], desired[i])) target[i] = desired[i];
            return;
        }
        for (var i = target.Count - 1; i >= 0; i--)
            if (!alive.Contains(target[i])) target.RemoveAt(i);
        for (var i = 0; i < desired.Count; i++)
        {
            var item = desired[i];
            if (i < target.Count && ReferenceEquals(target[i], item)) continue;
            if (retained.Contains(item)) target.Move(target.IndexOf(item), i);
            else target.Insert(i, item);
        }
    }
}
