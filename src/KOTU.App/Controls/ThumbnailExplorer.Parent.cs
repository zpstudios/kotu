using Microsoft.UI.Xaml.Controls.Primitives;
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
    private readonly System.Runtime.CompilerServices.ConditionalWeakTable<SelectorItem, object> _tileHooks = new();

    private readonly System.Runtime.CompilerServices.ConditionalWeakTable<SelectorItem, object> _tileActivationHooks = new();

    private void EnsureTileActivationHook(SelectorItem item)
    {
        if (_tileActivationHooks.TryGetValue(item, out _)) return;
        _tileActivationHooks.Add(item, new object());
        // 버튼이 제스처를 처리해도 한 번 받는다. 재사용 뒤 현재 Content는 핸들러에서 다시 확인한다.
        item.AddHandler(UIElement.DoubleTappedEvent, new DoubleTappedEventHandler(OnItemDoubleTapped), true);
    }

    private void EnsureParentContextMenu(SelectorItem item)
    {
        // 자체 메뉴가 없으면 WinUI 행 클래스가 목록의 빈 공간 메뉴를 대신 연다.
        // 공통 메뉴는 현재 VM이 없는 부모 행에서 Opening 중 동기 취소된다.
        if (item.ContextFlyout is null) AttachContextMenu(item);
        EnsureTileActivationHook(item);
    }

    private static bool IsParentTileSource(object source)
    {
        var node = source as DependencyObject;
        for (var depth = 0; node is not null && depth < 64; depth++)
        {
            if (node is SelectorItem item) return item.Content is ExplorerParentTile;
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
        if (ActiveItems.ContainerFromItem(_parentTile) is not FrameworkElement parent) return false;
        var point = e.GetPosition(parent);
        return point.X >= 0 && point.Y >= 0 && point.X < parent.ActualWidth && point.Y < parent.ActualHeight;
    }

    private void OnTileSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_removingParentSelection || _restoringSelection || !ReferenceEquals(sender, ActiveItems)) return;
        // Also covers Ctrl+A and Shift ranges. Selection metadata must never describe navigation.
        if (ActiveItems.SelectedItems.Contains(_parentTile))
        {
            _removingParentSelection = true;
            try { ActiveItems.SelectedItems.Remove(_parentTile); }
            finally { _removingParentSelection = false; }
        }
        SavePresentation();
        SelectionChanged?.Invoke();
    }
}
