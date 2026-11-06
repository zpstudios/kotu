using KOTU.Core.Settings;

namespace KOTU.Core.Routing;

/// <summary>
/// Nonvisual explorer state shared by its display surfaces. Call on the owning UI thread.
/// The window host starts/disposes the runtime; views only consume its ordered adapter boundaries.
/// Selection, active item and keyboard focus belong to the surfaces, never to the checked workset.
/// </summary>
public sealed partial class ExplorerSession
{
    private const string SortSettingKey = "explorer.sort";
    private const string SortDescSettingKey = "explorer.sortDesc";
    public const string ShowHiddenSettingKey = "explorer.showHidden";

    private readonly ExplorerNameView _nameView = new();
    private readonly HashSet<string> _hiddenExtensions = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _checkedPaths = new(StringComparer.OrdinalIgnoreCase);
    private ISettingsService? _settings;

    public event Action? ChecksChanged;

    public int MetadataGeneration { get; private set; }
    public BrowseCheckSummary CheckSummary => new(_checkedPaths.Count, CheckedPathsInView().Count);

    public string CurrentFolder { get; private set; } = string.Empty;
    public string DisplayFolder { get; private set; } = string.Empty;
    public IReadOnlyList<string> Extensions { get; private set; } = [];
    public IReadOnlyList<ExplorerListing.Entry> Entries { get; private set; } = [];
    public IReadOnlyList<ExplorerListing.Entry> Ordered => _nameView.Ordered;
    public IReadOnlyList<ExplorerListing.Entry> Display => _nameView.Display;
    public string Query => _nameView.Query;
    public ExplorerListing.SortKey SortKey { get; private set; } = ExplorerListing.SortKey.Name;
    public bool Descending { get; private set; }
    public bool ShowHidden { get; private set; }
    public int HiddenExtensionCount => _hiddenExtensions.Count;

    // Settings injection only loads state, as before; the host decides when to refresh the view.
    public ISettingsService? Settings
    {
        get => _settings;
        set
        {
            _settings = value;
            SortKey = value?.Get(SortSettingKey, "name") switch
            {
                "size" => ExplorerListing.SortKey.Size,
                "modified" => ExplorerListing.SortKey.Modified,
                "created" => ExplorerListing.SortKey.Created,
                "type" => ExplorerListing.SortKey.Type,
                _ => ExplorerListing.SortKey.Name,
            };
            Descending = value?.Get(SortDescSettingKey, DefaultDescending(SortKey))
                ?? DefaultDescending(SortKey);
            ShowHidden = value?.Get(ShowHiddenSettingKey, false) ?? false;
        }
    }

    public static bool DefaultDescending(ExplorerListing.SortKey key) =>
        key is ExplorerListing.SortKey.Size or ExplorerListing.SortKey.Modified
            or ExplorerListing.SortKey.Created;

    public void SelectSort(ExplorerListing.SortKey key)
    {
        if (_disposed) return;
        if (key == SortKey) Descending = !Descending;
        else
        {
            SortKey = key;
            Descending = DefaultDescending(key);
            _settings?.Set(SortSettingKey, key.ToString().ToLowerInvariant());
        }
        _settings?.Set(SortDescSettingKey, Descending);
        _settings?.RequestSave();
        // Keyword filtering must never become the next stable sort's input.
        _nameView.Arrange(Ordered, SortKey, Descending, _hiddenExtensions);
        AdvanceViewGeneration();
        if (IsStarted) PublishDisplay();
    }

    public void SetExtensions(IReadOnlyList<string> extensions)
    {
        if (!Extensions.SequenceEqual(extensions)) _hiddenExtensions.Clear();
        Extensions = extensions;
    }

    public void SetExtensionVisible(string extension, bool visible)
    {
        if (visible) _hiddenExtensions.Remove(extension);
        else _hiddenExtensions.Add(extension);
    }

    public bool IsExtensionVisible(string extension) => !_hiddenExtensions.Contains(extension);

    public void ClearExtensionFilter() => _hiddenExtensions.Clear();

    public void SetShowHidden(bool value)
    {
        ShowHidden = value;
        _settings?.Set(ShowHiddenSettingKey, value);
        _settings?.RequestSave();
    }

    /// <summary>Begin navigation at the original early check-clear boundary, before UI teardown.</summary>
    public bool BeginNavigation(string folder)
    {
        var changed = !string.Equals(folder, CurrentFolder, StringComparison.OrdinalIgnoreCase);
        if (changed) _checkedPaths.Clear();
        CurrentFolder = folder;
        return changed;
    }

    /// <summary>Called after the old view models have been detached on a real folder change.</summary>
    public void ClearNavigationEntries()
    {
        ++MetadataGeneration;
        Entries = [];
        _nameView.ClearFolder();
        DisplayFolder = string.Empty;
    }

    /// <summary>A failed same-folder scan keeps the query and checks for a later recovery.</summary>
    public void ClearFailedScan()
    {
        ++MetadataGeneration;
        Entries = [];
        _nameView.ClearEntries();
        DisplayFolder = string.Empty;
    }

    public void AcceptScan(IReadOnlyList<ExplorerListing.Entry> entries)
    {
        ++MetadataGeneration;
        Entries = entries;
        if (_checkedPaths.Count == 0) return;
        var alive = new HashSet<string>(entries.Select(e => e.Path), StringComparer.OrdinalIgnoreCase);
        _checkedPaths.RemoveWhere(path => !alive.Contains(path));
    }

    /// <summary>Scans and extension changes restart stable ties from the raw scan order.</summary>
    public void ArrangeFromScan() =>
        _nameView.Arrange(Entries, SortKey, Descending, _hiddenExtensions);

    // A query change never advances the host's scan generation.
    public bool SetNameFilter(string query)
    {
        if (_disposed) return false;
        var changed = _nameView.SetQuery(query);
        if (changed && IsStarted && DisplayFolder == CurrentFolder) PublishDisplay();
        return changed;
    }

    /// <summary>Keep the original publication boundary: after VM reconciliation, before Fill.</summary>
    public void MarkDisplayed() => DisplayFolder = CurrentFolder;

    public bool IsChecked(string path) => _checkedPaths.Contains(path);

    public bool IsDisplayingFolder(string? folder) =>
        string.Equals(folder, CurrentFolder, StringComparison.OrdinalIgnoreCase) &&
        string.Equals(folder, DisplayFolder, StringComparison.OrdinalIgnoreCase);

    /// <summary>Only real displayed entries may be checked; the separate parent tile is not one.</summary>
    public bool SetItemChecked(string? folder, string path, bool value)
    {
        if (_disposed || !IsDisplayingFolder(folder)) return false;
        var entry = Display.FirstOrDefault(e => string.Equals(e.Path, path, StringComparison.OrdinalIgnoreCase));
        if (entry is null) return false;
        var changed = value ? _checkedPaths.Add(entry.Path) : _checkedPaths.Remove(entry.Path);
        if (changed) ChecksChanged?.Invoke();
        return changed;
    }

    /// <summary>Hidden checks survive, but operations consume only the display-ordered intersection.</summary>
    public IReadOnlyList<string> CheckedPathsInView(bool filesOnly = false) => Display
        .Where(entry => (!filesOnly || !entry.IsFolder) && _checkedPaths.Contains(entry.Path))
        .Select(entry => entry.Path)
        .ToArray();

    public IReadOnlyList<string> CheckedPathsForFolder(string? folder) =>
        IsDisplayingFolder(folder) ? CheckedPathsInView() : [];
}
