namespace KOTU.Core.Routing;

/// <summary>Window-scoped presentation shared by S1/S4; it owns no scan, checks or metadata.</summary>
public sealed class ExplorerPresentationState
{
    public enum ViewMode { Preview, List }
    public ViewMode Mode { get; set; }
    public sealed record Snapshot(string? Active, string[] Selected, string? Focused,
        bool ParentFocused, string? Anchor, bool ParentAnchor);
    private string? _folder;
    private Snapshot _snapshot = new(null, [], null, false, null, true);

    public void Save(string folder, Snapshot snapshot)
    {
        _folder = folder;
        _snapshot = snapshot with { Selected = snapshot.Selected.ToArray() };
    }

    public Snapshot Read(string folder, IEnumerable<string> displayed)
    {
        if (!string.Equals(_folder, folder, StringComparison.OrdinalIgnoreCase))
            return new(null, [], null, false, null, true);
        var paths = new HashSet<string>(displayed, StringComparer.OrdinalIgnoreCase);
        var selected = _snapshot.Selected.Where(paths.Contains).ToArray();
        return _snapshot with
        {
            Active = _snapshot.Active is { } active && selected.Contains(active, StringComparer.OrdinalIgnoreCase) ? active : selected.FirstOrDefault(),
            Selected = selected,
            Focused = _snapshot.Focused is { } focused && paths.Contains(focused) ? focused : null,
            Anchor = _snapshot.Anchor is { } anchor && paths.Contains(anchor) ? anchor : null,
        };
    }
}
