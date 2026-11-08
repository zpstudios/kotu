using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;

namespace KOTU.App.Controls;

public sealed partial class ThumbnailExplorer
{
    private bool _restoringSelection;
    private long _tileSelectionRevision;
    private Action? _pendingTileSelectionSync;
    private sealed record DisplaySelection(string? Active, string[] Selected, string? Focused,
        bool ParentFocused, string? Anchor, bool ParentAnchor, bool RestoreFocus);

    private DisplaySelection CaptureDisplaySelection(bool sameFolder)
    {
        if (!sameFolder) return new(null, [], null, false, null, true, false);
        var focused = XamlRoot is { } root ? FocusManager.GetFocusedElement(root) : null;
        var ownsFocus = false;
        for (var node = focused as DependencyObject; node is not null; node = VisualTreeHelper.GetParent(node))
        {
            if (!ReferenceEquals(node, ActiveItems)) continue;
            ownsFocus = true;
            if (focused is not null && EntryFromSource(focused) is { } entry)
            { _focusedPath = entry.Path; _parentFocused = false; }
            else if (focused is not null && IsParentTileSource(focused))
            { _focusedPath = null; _parentFocused = true; }
            break;
        }
        var anchor = _scrollState.Logical;
        return new(SelectedEntry?.Path, SelectedPaths().ToArray(), _focusedPath, _parentFocused,
            anchor.Path, anchor.Parent, ownsFocus);
    }

    private void RestoreDisplaySelection(DisplaySelection previous)
    {
        _tileSelectionRevision++;
        _scrollState.BeginRestore(new(previous.Anchor, previous.ParentAnchor));
        _focusedPath = previous.Focused;
        _parentFocused = previous.ParentFocused;
        // Focus can make a native container selected. Restore the independent selection afterwards.
        if (previous.RestoreFocus) FocusPresentationItem();
        var paths = new HashSet<string>(previous.Selected, StringComparer.OrdinalIgnoreCase);
        var selected = _vms.Where(vm => paths.Contains(vm.Path)).ToArray();
        var active = selected.FirstOrDefault(vm => string.Equals(vm.Path, previous.Active,
            StringComparison.OrdinalIgnoreCase));
        ActiveItems.SelectedItems.Clear();
        if (active is not null) ActiveItems.SelectedItem = active;
        foreach (var vm in selected)
            if (!ActiveItems.SelectedItems.Contains(vm)) ActiveItems.SelectedItems.Add(vm);
        RestoreAnchor(previous.Anchor, previous.ParentAnchor);
        ReconcileTileSelection(immediate: true);
    }
}
