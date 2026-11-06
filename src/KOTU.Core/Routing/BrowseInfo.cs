using KOTU.Core.Contracts;

namespace KOTU.Core.Routing;

/// <summary>Snapshot-only base information and the shared fail-closed gate for automatic rich information.</summary>
public static class BrowseInfo
{
    // Entry.Attributes defaults to zero when the producer has no confirmed metadata.
    public static bool AttributesKnown(FileAttributes attributes) => attributes != 0;
    public static bool CanReadContent(ExplorerListing.Entry entry) => !entry.IsFolder &&
        !entry.IsPlaceholder && AttributesKnown(entry.Attributes) &&
        (entry.Attributes & FileAttributes.Directory) == 0 && !ExplorerListing.IsCloudPlaceholder(entry.Attributes);

    public static IReadOnlyList<ContentInfoItem> BasicRows(ExplorerListing.Entry entry)
    {
        var known = AttributesKnown(entry.Attributes);
        var cloud = entry.IsPlaceholder || (known && ExplorerListing.IsCloudPlaceholder(entry.Attributes));
        var type = entry.IsFolder ? "Folder" : System.IO.Path.GetExtension(entry.Name) is { Length: > 0 } ext
            ? ext.ToUpperInvariant() + " file" : "File";
        return
        [
            new("Name", entry.Name),
            new("Full path", entry.Path),
            new("Type", type),
            new("Created", Date(entry.Created)),
            new("Modified", Date(entry.Modified)),
            new("Size", entry.IsFolder ? "Not calculated automatically" : entry.Size < 0 ? "Unavailable" :
                $"{entry.Size:N0} bytes ({ExplorerListing.FormatSize(entry.Size)})"),
            new("Attributes", known ? entry.Attributes.ToString() : "Unknown"),
            new("Availability", cloud ? "Online-only / recall required" : !known ? "Unknown" : "Local (attributes confirmed)"),
        ];
    }

    private static string Date(DateTime value) => value == default ? "Unavailable" : value.ToString("yyyy-MM-dd HH:mm:ss");

    /// <summary>Rechecks attributes on the worker. Unknown, cloud, directory or failed metadata never reach richReader.</summary>
    public static IReadOnlyList<ContentInfoItem> BuildVerified(ExplorerListing.Entry entry,
        Func<string, FileAttributes> readAttributes, Func<string, IReadOnlyList<ContentInfoItem>> richReader,
        Func<ExplorerListing.Entry, IReadOnlyList<ContentInfoItem>> safeRows, CancellationToken cancellation = default)
    {
        cancellation.ThrowIfCancellationRequested();
        if (!CanReadContent(entry)) return safeRows(entry);
        FileAttributes current;
        try { current = readAttributes(entry.Path); }
        catch (OperationCanceledException) { throw; }
        catch { cancellation.ThrowIfCancellationRequested(); return safeRows(entry with { Attributes = 0 }); }
        cancellation.ThrowIfCancellationRequested();
        var verified = entry with { Attributes = current };
        if (!CanReadContent(verified)) return safeRows(verified);
        cancellation.ThrowIfCancellationRequested();
        try { return MergeRich(verified, richReader(entry.Path)); }
        catch (OperationCanceledException) { throw; }
        catch { return safeRows(verified); }
    }

    /// <summary>Module builders retain their complete section; the shell replaces only their old basic prefix.</summary>
    public static IReadOnlyList<ContentInfoItem> MergeRich(ExplorerListing.Entry entry, IReadOnlyList<ContentInfoItem> rich)
    {
        var rows = new List<ContentInfoItem>(BasicRows(entry));
        var inModule = false;
        foreach (var row in rich)
        {
            if (row.IsSeparator) inModule = true;
            // Image Dimensions precedes its first separator. Remove only the known basic labels.
            if (!inModule && row.Label is "File" or "Size" or "Modified" or "Folder") continue;
            rows.Add(row);
        }
        return rows;
    }
}
