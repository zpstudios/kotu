using KOTU.Core.Contracts;

namespace KOTU.Core.Routing;

public enum BrowseSelectionKind { None, File, Folder }
public enum BrowseInfoTarget { Selection, OpenDocument, Checks, None }

/// <summary>Actual item selection, independent of open content and checked worksets. Parent is never an Entry.</summary>
public sealed record BrowseSelection
{
    private BrowseSelection(ExplorerListing.Entry? entry, int generation)
    {
        Entry = entry;
        Generation = generation;
    }

    public static BrowseSelection None { get; } = new(null, 0);
    public ExplorerListing.Entry? Entry { get; }
    public int Generation { get; }
    public BrowseSelectionKind Kind => Entry is null ? BrowseSelectionKind.None :
        Entry.IsFolder ? BrowseSelectionKind.Folder : BrowseSelectionKind.File;
    public bool IsNone => Kind == BrowseSelectionKind.None;
    public string? Path => Entry?.Path;
    public BrowseInfoKey? Key => Entry is { } entry ? BrowseInfoKey.From(entry, Generation) : null;
    public static BrowseSelection From(ExplorerListing.Entry? entry, int generation) =>
        entry is null ? None : new(entry, generation);

    public static BrowseInfoTarget Resolve(BrowseSelection selection, bool hasDocument, BrowseCheckSummary checks) =>
        !selection.IsNone ? BrowseInfoTarget.Selection : hasDocument ? BrowseInfoTarget.OpenDocument :
        checks.Total > 0 ? BrowseInfoTarget.Checks : BrowseInfoTarget.None;
}

/// <summary>Snapshot identity includes kind, attributes and generation even when timestamps are unchanged.</summary>
public sealed record BrowseInfoKey(string Path, BrowseSelectionKind Kind, long ModifiedTicks,
    FileAttributes Attributes, bool IsPlaceholder, int Generation, long CreatedTicks, long Size, string Name)
{
    public static BrowseInfoKey From(ExplorerListing.Entry entry, int generation) => new(
        entry.Path.ToUpperInvariant(), entry.IsFolder ? BrowseSelectionKind.Folder : BrowseSelectionKind.File,
        entry.Modified.Ticks, entry.Attributes, entry.IsPlaceholder, generation, entry.Created.Ticks, entry.Size, entry.Name);
}

public readonly record struct BrowseCheckSummary(int Total, int Visible)
{
    public int Hidden => Math.Max(0, Total - Visible);
    public IReadOnlyList<ContentInfoItem> BuildRows() =>
    [
        new("Selection", "No detail item selected"),
        new("Checked total", Total.ToString("N0")),
        new("In view", Visible.ToString("N0")),
        new("Hidden by filter", Hidden.ToString("N0")),
    ];
}
