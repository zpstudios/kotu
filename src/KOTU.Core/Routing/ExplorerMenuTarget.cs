namespace KOTU.Core.Routing;

/// <summary>Immutable menu intent. Only real entries from the current view can be targets.</summary>
public sealed record ExplorerMenuTarget(
    string Folder, long Generation, ExplorerListing.Entry Clicked,
    IReadOnlyList<ExplorerListing.Entry> Working, ExplorerMenuScope Scope)
{
    public string ScopeLabel => Scope switch
    {
        ExplorerMenuScope.Checked => $"checked {Working.Count}",
        ExplorerMenuScope.Selected => $"selected {Working.Count}",
        _ => Clicked.IsFolder ? "this folder" : "this file",
    };

    public static ExplorerMenuTarget? Capture(string? folder, long generation, string clicked,
        IEnumerable<ExplorerListing.Entry> view, IEnumerable<string> checks, IEnumerable<string> selection)
    {
        if (string.IsNullOrEmpty(folder)) return null;
        var entries = view.ToArray();
        var hit = entries.FirstOrDefault(e => SamePath(e.Path, clicked));
        if (hit is null) return null; // the parent tile is never an Entry
        var checkedSet = checks.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var selectedSet = selection.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var working = entries.Where(e => checkedSet.Contains(e.Path)).ToArray();
        var scope = ExplorerMenuScope.Checked;
        if (working.Length == 0)
        {
            working = entries.Where(e => selectedSet.Contains(e.Path)).ToArray();
            scope = ExplorerMenuScope.Selected;
        }
        if (!working.Any(e => SamePath(e.Path, clicked)))
        {
            working = [hit];
            scope = ExplorerMenuScope.Clicked;
        }
        return new(folder, generation, hit, Array.AsReadOnly(working), scope);
    }

    public bool IsCurrent(string? folder, long generation, IEnumerable<ExplorerListing.Entry> view,
        IEnumerable<string> checks, IEnumerable<string> selection, bool useWorkingSet)
    {
        if (generation != Generation || !SamePath(folder, Folder)) return false;
        var now = Capture(folder, generation, Clicked.Path, view, checks, selection);
        return now is not null && now.Clicked == Clicked &&
            (!useWorkingSet || (now.Scope == Scope && now.Working.SequenceEqual(Working)));
    }

    public static bool SamePath(string? a, string? b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
}

public enum ExplorerMenuScope { Clicked, Checked, Selected }

/// <summary>Metadata-only validation. Call on a worker, never enumerate or read file contents.</summary>
public static class ExplorerMenuValidation
{
    public sealed record Identity(FileAttributes Attributes, DateTime Created);

    public static Identity ReadIdentity(string path) => new(File.GetAttributes(path), File.GetCreationTime(path));

    public static string? Validate(IEnumerable<ExplorerListing.Entry> entries, Func<string, Identity> read)
    {
        foreach (var entry in entries)
        {
            // Reject command-like, relative, device and malformed paths before entering any shell API.
            if (!IsSafeAbsolutePath(entry.Path)) return "The item path cannot be verified";
            try
            {
                var live = read(entry.Path);
                if (live.Attributes == 0 || live.Attributes == (FileAttributes)(-1))
                    return "The item could not be verified";
                if (live.Attributes.HasFlag(FileAttributes.Directory) != entry.IsFolder || live.Created != entry.Created)
                    return "The item changed. Open the menu again";
            }
            catch { return "The item is unavailable or could not be verified"; }
        }
        return null;
    }

    public static bool IsSafeAbsolutePath(string path)
    {
        // Preserve ordinary extended drive/UNC paths used by long-path file operations.
        // Validate their ordinary spelling, but pass the original string unchanged to the typed API.
        if (path.StartsWith(@"\\?\UNC\", StringComparison.OrdinalIgnoreCase)) path = @"\\" + path[8..];
        else if (path.StartsWith(@"\\?\", StringComparison.Ordinal))
        {
            path = path[4..];
            if (path.Length < 3 || !char.IsAsciiLetter(path[0]) || path[1] != ':' || path[2] != '\\') return false;
        }
        if (string.IsNullOrWhiteSpace(path) || path.IndexOfAny(['\0', '"', '\r', '\n', '|', '<', '>', '*', '?', '/']) >= 0)
            return false;
        if (path.StartsWith(@"\\.\", StringComparison.Ordinal))
            return false;
        // Windows paths are deliberately recognized without depending on the test host's OS.
        var drive = path.Length >= 3 && char.IsAsciiLetter(path[0]) && path[1] == ':' && path[2] == '\\';
        var unc = path.StartsWith(@"\\", StringComparison.Ordinal) && path[2..].Split('\\') is { Length: >= 2 } parts &&
            parts[0].Length > 0 && parts[1].Length > 0;
        if (!drive && !unc) return false;
        return !path[(drive ? 2 : 0)..].Contains(':') &&
            !path.Split('\\', '/').Any(part => part is "." or "..");
    }
}
