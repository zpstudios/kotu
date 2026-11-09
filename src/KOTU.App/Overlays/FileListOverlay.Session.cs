using KOTU.Core.Routing;

namespace KOTU.App.Overlays;

public sealed partial class FileListOverlay
{
    internal int MetadataGeneration => _session.MetadataGeneration;
    internal BrowseCheckSummary CheckSummary => _session.CheckSummary;
    internal ExplorerPresentationState Presentation => _session.Presentation;

    internal bool SelectViewMode(string? folder, ExplorerPresentationState.ViewMode mode) =>
        _session.SelectViewMode(folder, mode);

    private int _viewPublications;
    private int _fillPublications;

    internal (ExplorerListing.SortKey Key, bool Descending, bool Available) CurrentSort =>
        (_session.SortKey, _session.Descending, _session.IsDisplayingFolder(CurrentFolder));
    internal void SelectSort(string? folder, ExplorerListing.SortKey key)
    {
        // During a real folder transition the pending scan must not be cancelled by old entries.
        if (CurrentSort.Available) _session.SelectSort(folder, key);
    }

    internal bool TrySeedCurrentDisplay(IReadOnlyList<string> extensions,
        Action<string, IReadOnlyList<ExplorerListing.Entry>> render) =>
        _session.TrySeedDisplay(CurrentFolder, extensions, render);

    private void OnSessionChecksChanged() => ChecksChanged?.Invoke();

    private void OnSessionNavigationStarted(string folder) => NavigationStarted?.Invoke(folder);

    private void OnSessionViewChanged(string folder, IReadOnlyList<ExplorerListing.Entry> entries)
    {
        ++_viewPublications;
        ViewChanged?.Invoke(folder, entries);
        if (!_explorerDisposed && _session.IsDisplayingFolder(folder) && ReferenceEquals(entries, _session.Display))
            SyncTreeToFolder(folder);
    }

    private void OnSessionFillCompleted(IReadOnlyList<ExplorerListing.Entry> entries)
    {
        ++_fillPublications;
        NavDiagnostics.Mark("fillN");
        NavDiagnostics.ArmPaint("paint");
        FillCompleted?.Invoke(entries);
    }

    // Timing follows the nonvisual owner even when the legacy view has never been constructed.
    private void OnSessionUpdated(ExplorerSessionUpdate update)
    {
        switch (update.Kind)
        {
            case ExplorerSessionUpdateKind.PreparingNavigation:
                NavDiagnostics.Begin();
                break;
            case ExplorerSessionUpdateKind.FolderRequested:
                NavDiagnostics.Mark("pre");
                break;
            case ExplorerSessionUpdateKind.NavigationStarted:
                NavDiagnostics.Mark("load");
                break;
            case ExplorerSessionUpdateKind.Scanning:
                if (!update.FolderChanged) NavDiagnostics.Mark("same");
                if (NavDiagnostics.Enabled)
                {
                    var navigation = NavDiagnostics.Session;
                    DispatcherQueue.TryEnqueue(() => NavDiagnostics.MarkFor(navigation, "yield"));
                }
                break;
            case ExplorerSessionUpdateKind.Scanned:
                NavDiagnostics.Mark("scan");
                break;
            case ExplorerSessionUpdateKind.ScanFailed:
                NavDiagnostics.Mark("fail");
                NavDiagnostics.ArmPaint("paint");
                break;
        }
    }

    // Read-only instrumentation. Does not FindName or touch the deferred host.
    internal object CaptureExplorerDiagnostics() => new
    {
        IsLegacyListEnabled,
        ExplorerPaneCreated = ExplorerPane.ConstructionCount,
        ListPaneCreated = ExplorerPane.ListPaneConstructionCount,
        LegacyAttached = _list is not null,
        LegacyUsesSameSession = _list is null ? (bool?)null : ReferenceEquals(_list.Session, _session),
        ActiveWatchers = _session.ActiveWatcherCount,
        _session.WatchStatistics,
        _session.Generation,
        CurrentFolder,
        DisplayFolder,
        Entries = CurrentEntries.Select(entry => entry.Path).ToArray(),
        Checked = _session.CheckedPathsForFolder(DisplayFolder),
        ViewPublications = _viewPublications,
        FillPublications = _fillPublications,
        IsOpen,
    };
}
