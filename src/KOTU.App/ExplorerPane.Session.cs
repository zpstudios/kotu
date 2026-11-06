using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using KOTU.Core.Routing;

namespace KOTU.App;

public sealed partial class ExplorerPane
{
    private int _lastSessionGeneration;
    private string? _restoreNameSelectionPath;
    private bool _presentationDisposed;
    private bool _needsPresentationRefresh;
    private bool _presentationUnloaded;

    // Presentation-only adapter. The host, not this control's Loaded/Unloaded, owns the runtime.
    private void OnSessionUpdated(ExplorerSessionUpdate update)
    {
        if (_presentationDisposed) return;
        switch (update.Kind)
        {
            case ExplorerSessionUpdateKind.FolderRequested:
                SyncSessionGeneration();
                break;
            case ExplorerSessionUpdateKind.ClearingEntries:
                foreach (var vm in _displayVms) vm.Requests.Dispose();
                _displayVms.Clear();
                break;
            case ExplorerSessionUpdateKind.NavigationStarted:
                ClearListVisuals("Loading...");
                NavigationStarted?.Invoke(update.Folder);
                break;
            case ExplorerSessionUpdateKind.PreparingDisplay:
                SyncSessionGeneration();
                SyncSortHeaders();
                if (!_presentationUnloaded) PrepareNameView();
                break;
            case ExplorerSessionUpdateKind.DisplayChanged:
                if (!_presentationUnloaded) ApplyNameView(_loadSeq);
                else
                {
                    // Data keeps feeding the central surfaces, but no legacy row/loader is recreated.
                    var entries = _session.Display;
                    ViewChanged?.Invoke(update.Folder, entries);
                    if (_session.IsStarted && ReferenceEquals(entries, _session.Display))
                        FillCompleted?.Invoke(entries);
                }
                break;
            case ExplorerSessionUpdateKind.ScanFailed:
                ClearListVisuals("Cannot read this folder: " + update.Error!.Message);
                ViewChanged?.Invoke(update.Folder, []);
                break;
        }
    }

    private void SyncSessionGeneration()
    {
        if (_lastSessionGeneration == _session.Generation) return;
        _lastSessionGeneration = _session.Generation;
        ++_loadSeq;
    }

    private void ClearListVisuals(string message)
    {
        IconGrid.Items.Clear();
        ListPane.ItemsSource = null;
        EmptyText.Text = message;
        EmptyText.Visibility = Visibility.Visible;
    }

    private void OnPresentationLoaded(object sender, RoutedEventArgs args)
    {
        if (_presentationDisposed) return;
        _presentationUnloaded = false;
        ExplorerFileOps.CutMarksChanged -= ApplyCutMarks;
        ExplorerFileOps.CutMarksChanged += ApplyCutMarks;
        // A reattached view rebuilds canceled row requests from the current session, without a scan.
        if (_needsPresentationRefresh && _session.IsDisplayingFolder(_folder))
        {
            _needsPresentationRefresh = false;
            PrepareNameView();
            ApplyNameView(_loadSeq, publish: false);
        }
    }

    private void OnPresentationUnloaded(object sender, RoutedEventArgs args)
    {
        ExplorerFileOps.CutMarksChanged -= ApplyCutMarks;
        _presentationUnloaded = true;
        ReleasePresentationRequests();
        _needsPresentationRefresh = true;
    }

    private void ReleasePresentationRequests()
    {
        ++_loadSeq;
        foreach (var vm in _displayVms) vm.Requests.Dispose();
        _fetchPool?.Dispose();
        _fetchPool = null;
    }

    internal void DisposePresentation()
    {
        if (_presentationDisposed) return;
        _presentationDisposed = true;
        _session.Updated -= OnSessionUpdated;
        _session.ChecksChanged -= OnSessionChecksChanged;
        PathBar.Detach();
        ExplorerFileOps.CutMarksChanged -= ApplyCutMarks;
        Loaded -= OnPresentationLoaded;
        Unloaded -= OnPresentationUnloaded;
        ReleasePresentationRequests();
    }
}
