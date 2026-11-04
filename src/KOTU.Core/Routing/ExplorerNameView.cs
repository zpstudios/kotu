namespace KOTU.Core.Routing;

/// <summary>The keyword is a projection of the full sorted listing, never the next sort's input.</summary>
public sealed class ExplorerNameView
{
    private IReadOnlyList<ExplorerListing.Entry> _arranged = [];
    public IReadOnlyList<ExplorerListing.Entry> Ordered => _arranged;
    public IReadOnlyList<ExplorerListing.Entry> Display { get; private set; } = [];
    public string Query { get; private set; } = string.Empty;

    public void Arrange(IReadOnlyList<ExplorerListing.Entry> entries, ExplorerListing.SortKey key,
        bool descending, IReadOnlySet<string> hiddenExtensions)
    {
        _arranged = ExplorerListing.Arrange(entries, key, descending, hiddenExtensions);
        ApplyQuery();
    }

    public bool SetQuery(string query)
    {
        if (Query == query) return false;
        Query = query;
        var before = Display;
        ApplyQuery();
        return !before.SequenceEqual(Display);
    }

    private void ApplyQuery() => Display = Query.Length == 0 ? _arranged :
        _arranged.Where(entry => entry.Name.Contains(Query, StringComparison.OrdinalIgnoreCase)).ToArray();

    public void ClearEntries() => _arranged = Display = [];

    public static (string Path, bool IsPlaceholder, long ModifiedTicks)? SelectionMetadata(
        IReadOnlyList<ExplorerListing.Entry> entries, string path)
    {
        var entry = entries.FirstOrDefault(item => !item.IsFolder &&
            string.Equals(item.Path, path, StringComparison.OrdinalIgnoreCase));
        return entry is null ? null : (entry.Path, entry.IsPlaceholder, entry.Modified.Ticks);
    }

    public void ClearFolder()
    {
        ClearEntries();
        Query = string.Empty;
    }
}
