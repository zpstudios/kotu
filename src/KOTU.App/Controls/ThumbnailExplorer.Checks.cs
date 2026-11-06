using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace KOTU.App.Controls;

public sealed partial class ThumbnailExplorer
{
    internal Func<string?, IReadOnlyList<string>>? CheckedPathsProvider { get; set; }
    internal Action<string?, string, bool>? CheckRequested { get; set; }

    private IReadOnlyList<string> CheckedPaths() => CheckedPathsProvider?.Invoke(CurrentFolder) ?? [];

    internal void RefreshChecks()
    {
        var paths = new HashSet<string>(CheckedPaths(), StringComparer.OrdinalIgnoreCase);
        foreach (var vm in _vms) vm.IsChecked = paths.Contains(vm.Path);
    }

    internal bool ToggleFocusedCheck(DependencyObject focused, bool repeat)
    {
        var node = focused;
        while (node is not null && !ReferenceEquals(node, ActiveItems)) node = VisualTreeHelper.GetParent(node);
        if (node is null) return false;
        // A Ctrl+arrow focus move need not change SelectedItem. Resolve the focused container first.
        var entry = EntryFromSource(focused) ?? (ReferenceEquals(focused, ActiveItems) ? SelectedEntry : null);
        if (entry is null || !_vms.Any(vm => ReferenceEquals(vm.Entry, entry))) return false;
        _lastClick = null;
        _lastPress = null;
        if (!repeat)
            CheckRequested?.Invoke(CurrentFolder, entry.Path,
                !CheckedPaths().Contains(entry.Path, StringComparer.OrdinalIgnoreCase));
        return true;
    }

    private void OnTileCheckClick(object sender, RoutedEventArgs e)
    {
        if (sender is not CheckBox box || EntryFromSource(box) is not { } entry) return;
        _lastClick = null;
        _lastPress = null;
        CheckRequested?.Invoke(CurrentFolder, entry.Path, box.IsChecked == true);
        RefreshChecks(); // also restores the visual if a stale item was rejected during navigation
    }

    private static bool IsInCheckBox(object source)
    {
        var node = source as DependencyObject;
        for (var depth = 0; node is not null && depth < 64; depth++)
        {
            if (node is CheckBox) return true;
            if (node is SelectorItem) return false;
            node = VisualTreeHelper.GetParent(node);
        }
        return false;
    }

    private IReadOnlyList<string> WorkingPaths()
    {
        var checks = CheckedPaths();
        return checks.Count > 0 ? checks : SelectedPaths();
    }

    private IReadOnlyList<string> WorkingFilePaths()
    {
        var checks = CheckedPaths();
        if (checks.Count == 0) return SelectedFilePaths();
        var paths = new HashSet<string>(checks, StringComparer.OrdinalIgnoreCase);
        return _vms.Where(vm => !vm.IsFolder && paths.Contains(vm.Path)).Select(vm => vm.Path).ToArray();
    }
}
