using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.System;

namespace KOTU.App.Controls;

public sealed partial class ThumbnailExplorer
{
    private readonly ExplorerParentTile _parentTile = new();
    private readonly KOTU.Core.Collections.LeadingItemCollection<ExplorerEntryVm> _tileItems;
    private bool _parentNavigationPending;
    private bool _removingParentSelection;
    private readonly System.Runtime.CompilerServices.ConditionalWeakTable<GridViewItem, object> _tileHooks = new();

    private static bool IsParentTileSource(object source)
    {
        var node = source as DependencyObject;
        for (var depth = 0; node is not null && depth < 64; depth++)
        {
            if (node is GridViewItem item) return item.Content is ExplorerParentTile;
            node = VisualTreeHelper.GetParent(node);
        }
        return false;
    }

    private void OnParentTileLoaded(object sender, RoutedEventArgs e)
    {
        if (sender is Button button) button.Tag = KOTU.Input.HotkeySupport.PassThroughTag;
    }

    private void OnParentTilePreviewKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Handled || !IsParentTileSource(e.OriginalSource) ||
            e.Key is not (VirtualKey.Enter or VirtualKey.Space)) return;
        e.Handled = true;
        if (e.Key == VirtualKey.Enter && !e.KeyStatus.WasKeyDown) OnParentFolderClick(sender, e);
    }

    private bool IsParentTileDrop(DragEventArgs e)
    {
        if (IsParentTileSource(e.OriginalSource)) return true;
        // AllowDrop=false can route directly to LayoutRoot; also inspect the realized tile bounds.
        if (TileGrid.ContainerFromItem(_parentTile) is not FrameworkElement parent) return false;
        var point = e.GetPosition(parent);
        return point.X >= 0 && point.Y >= 0 && point.X < parent.ActualWidth && point.Y < parent.ActualHeight;
    }

    private void OnTileSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_removingParentSelection) return;
        // Also covers Ctrl+A and Shift ranges. Selection metadata must never describe navigation.
        if (TileGrid.SelectedItems.Contains(_parentTile))
        {
            _removingParentSelection = true;
            try { TileGrid.SelectedItems.Remove(_parentTile); }
            finally { _removingParentSelection = false; }
        }
        SelectionChanged?.Invoke();
    }
}
