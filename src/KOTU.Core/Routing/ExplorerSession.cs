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
    private readonly MemorySettingsService _memoryPreferences = new();
    private ISettingsService PreferenceStore => _settings ?? _memoryPreferences;
    public ExplorerPresentationState Presentation { get; } = new();

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
            RestoreFolderPreferences();
            ReadColumns();
            ShowHidden = value?.Get(ShowHiddenSettingKey, false) ?? false;
        }
    }

    public static bool DefaultDescending(ExplorerListing.SortKey key) =>
        key is ExplorerListing.SortKey.Size or ExplorerListing.SortKey.Modified
            or ExplorerListing.SortKey.Created;

    public void SelectSort(ExplorerListing.SortKey key)
    {
        if (_disposed || !Enum.IsDefined(key)) return;
        if (key == SortKey) Descending = !Descending;
        else
        {
            SortKey = key;
            Descending = DefaultDescending(key);
        }
        var store = PreferenceStore;
        store.Set(SortSettingKey, key.ToString().ToLowerInvariant());
        store.Set(SortDescSettingKey, Descending);
        if (ExplorerFolderPreferences.KeyFor(CurrentFolder) is { } folderKey)
        {
            InitializeMissingFolderPreferences(folderKey);
            store.Set(folderKey + ".sort", key.ToString().ToLowerInvariant());
            store.Set(folderKey + ".sortDesc", Descending);
        }
        _settings?.RequestSave();
        // Keyword filtering must never become the next stable sort's input.
        _nameView.Arrange(Ordered, SortKey, Descending, _hiddenExtensions);
        AdvanceViewGeneration();
        if (IsStarted) PublishDisplay();
    }

    // 표면이 표시하던 폴더를 전달해야 이동 중이거나 숨겨진 옛 표면의 요청을 거부할 수 있다.
    public bool SelectSort(string? folder, ExplorerListing.SortKey key)
    {
        if (_disposed || !Enum.IsDefined(key) || !CanChangeFolderPreferences(folder)) return false;
        SelectSort(key);
        return true;
    }

    public bool SelectViewMode(string? folder, ExplorerPresentationState.ViewMode mode)
    {
        if (_disposed || !Enum.IsDefined(mode) || !CanChangeFolderPreferences(folder) ||
            Presentation.Mode == mode) return false;
        var key = ExplorerFolderPreferences.KeyFor(CurrentFolder)!;
        InitializeMissingFolderPreferences(key);
        Presentation.Mode = mode;
        PreferenceStore.Set(key + ".view",
            mode == ExplorerPresentationState.ViewMode.List ? "list" : "preview");
        _settings?.RequestSave();
        return true;
    }

    // 첫 변경 때 당시의 전체 설정을 기억한다. 기존 필드는 다른 창의 최신값이므로 보존한다.
    private void InitializeMissingFolderPreferences(string key)
    {
        var store = PreferenceStore;
        if (store.Get<string?>(key + ".view", null) is null)
            store.Set(key + ".view", Presentation.Mode == ExplorerPresentationState.ViewMode.List ? "list" : "preview");
        if (store.Get<string?>(key + ".sort", null) is null)
            store.Set(key + ".sort", SortKey.ToString().ToLowerInvariant());
        if (store.Get<bool?>(key + ".sortDesc", null) is null)
            store.Set(key + ".sortDesc", Descending);
    }

    private bool CanChangeFolderPreferences(string? folder)
    {
        var normalized = ExplorerFolderPreferences.Normalize(folder);
        return normalized is not null && normalized == ExplorerFolderPreferences.Normalize(CurrentFolder) &&
            IsDisplayingFolder(CurrentFolder);
    }

    private void RestoreFolderPreferences()
    {
        var store = PreferenceStore;
        var defaultSort = ExplorerFolderPreferences.ReadSort(store, SortSettingKey, ExplorerListing.SortKey.Name);
        var defaultDirection = store.Get(SortDescSettingKey, DefaultDescending(defaultSort));
        var key = ExplorerFolderPreferences.KeyFor(CurrentFolder);
        var folderSort = key is null ? null : ExplorerFolderPreferences.ReadSort(store, key + ".sort");
        SortKey = folderSort ?? defaultSort;
        Descending = key is null ? defaultDirection : store.Get(key + ".sortDesc",
            folderSort.HasValue ? DefaultDescending(SortKey) : defaultDirection);
        Presentation.Mode = key is null ? ExplorerPresentationState.ViewMode.Preview :
            ExplorerFolderPreferences.ReadView(store, key + ".view");
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
        var normalized = ExplorerFolderPreferences.Normalize(folder);
        var preferencesChanged = changed && (normalized is null ||
            normalized != ExplorerFolderPreferences.Normalize(CurrentFolder));
        if (changed) _checkedPaths.Clear();
        CurrentFolder = folder;
        if (preferencesChanged) RestoreFolderPreferences();
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
