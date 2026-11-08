using KOTU.Core.Routing;
using KOTU.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.Foundation;
using Windows.System;

namespace KOTU.App.Controls;

public sealed partial class ThumbnailExplorer
{
    internal ExplorerPresentationState Presentation { get; set; } = new();
    private ExplorerPresentationState.ViewMode _viewMode;
    private ListViewBase ActiveItems => _viewMode == ExplorerPresentationState.ViewMode.List ? DetailList : TileGrid;
    private long _previewTextRequests;
    private long _previewShellRequests;
    internal (long Text, long Shell) PreviewRequestCounts => (_previewTextRequests, _previewShellRequests);
    private string? _focusedPath;
    private bool _parentFocused;
    private ScrollViewer? _presentationScroll;
    private readonly ExplorerScrollState _scrollState = new();
    private bool _narrowList = true;
    private bool _listLayoutQueued;
    private bool _anchorCapturePending;
    private bool _anchorCaptureQueued;
    private bool _anchorScrollIntermediate;
    private long _anchorCaptureRevision;

    private void InitializeListSurface()
    {
        DetailList.Tag = HotkeySupport.PassThroughTag;
        DetailList.PreviewKeyDown += OnParentTilePreviewKeyDown;
        DetailList.AddHandler(UIElement.KeyDownEvent, new KeyEventHandler(OnGridKeyDown), true);
        DetailList.SelectionChanged += OnTileSelectionChanged;
        DetailList.ContextFlyout = MakeSurfaceMenu();
        DetailList.SizeChanged += OnListSurfaceSizeChanged;
        foreach (var surface in new ListViewBase[] { TileGrid, DetailList })
        {
            surface.PreviewKeyDown += OnPresentationNavigationKey;
            surface.AddHandler(UIElement.PointerWheelChangedEvent, new PointerEventHandler(OnPresentationUserPointer), true);
            surface.AddHandler(UIElement.PointerPressedEvent, new PointerEventHandler(OnPresentationUserPointer), true);
        }
        TileGrid.GotFocus += OnPresentationFocus;
        DetailList.GotFocus += OnPresentationFocus;
        LayoutUpdated += OnPresentationLayout;
        Unloaded += (_, _) =>
        {
            if (_presentationScroll is not null) _presentationScroll.ViewChanged -= OnPresentationScroll;
            _presentationScroll = null;
            _anchorCapturePending = false;
            _scrollState.BeginRestore(_scrollState.Logical);
        };
        PreviewViewButton.Click += (_, _) => ApplyViewMode(ExplorerPresentationState.ViewMode.Preview, restore: true);
        ListViewButton.Click += (_, _) => ApplyViewMode(ExplorerPresentationState.ViewMode.List, restore: true);
        SyncViewTools();
    }

    private void SyncViewTools()
    {
        PreviewViewButton.IsChecked = _viewMode == ExplorerPresentationState.ViewMode.Preview;
        ListViewButton.IsChecked = _viewMode == ExplorerPresentationState.ViewMode.List;
        LayoutToolGroups(ExplorerTools.ActualWidth);
    }

    private void ApplyViewMode(ExplorerPresentationState.ViewMode mode, bool restore)
    {
        if (_viewMode == mode) { SyncViewTools(); return; }
        // Toolbar clicks finish inline editing by the existing focus-loss contract. A programmatic
        // mode request must not remove an editor still owned by a recycled container.
        if (ExplorerRenameBox.IsEditing) { SyncViewTools(); return; }
        CommitUserAnchor();
        var previous = CaptureDisplaySelection(true);
        _restoringSelection = true;
        try
        {
            _viewMode = mode;
            Presentation.Mode = mode;
            TileGrid.Visibility = mode == ExplorerPresentationState.ViewMode.Preview ? Visibility.Visible : Visibility.Collapsed;
            DetailList.Visibility = mode == ExplorerPresentationState.ViewMode.List ? Visibility.Visible : Visibility.Collapsed;
            ListHost.Visibility = DetailList.Visibility;
            UpdateListLayout();
            ActiveItems.UpdateLayout();
            ApplyTileSize();
            if (restore) RestoreDisplaySelection(previous);
        }
        finally { _restoringSelection = false; }
        _lastClick = null;
        _lastPress = null;
        SyncViewTools();
        if (restore) SavePresentation(previous);
    }

    private void OnListContainerContentChanging(ListViewBase sender, ContainerContentChangingEventArgs args)
    {
        if (args.ItemContainer is not SelectorItem item) return;
        if (args.InRecycleQueue)
        {
            if (item.ContentTemplateRoot is Grid root) ExplorerRenameBox.ForceFinish(root);
            item.AllowDrop = false;
            return;
        }
        if (args.Item is ExplorerParentTile)
        {
            item.AllowDrop = item.CanDrag = item.IsDoubleTapEnabled = false;
            EnsureParentContextMenu(item);
            return;
        }
        if (args.Item is not ExplorerEntryVm vm) return;
        EnsureTileHooks(item);
        item.CanDrag = item.IsDoubleTapEnabled = true;
        item.AllowDrop = vm.IsFolder;
        // No RegisterUpdateCallback: all list values are already in Entry.
    }

    private void InitializeListEntry(ExplorerEntryVm vm)
    {
        ExplorerFileOps.ApplyCutMark(vm);
        vm.SetNarrowList(_narrowList);
    }

    private void UpdateListLayout()
    {
        var width = DetailList.ActualWidth > 0 ? DetailList.ActualWidth : ActualWidth;
        if (width <= 0) return;
        var narrow = width < 700;
        if (_narrowList == narrow) return;
        _narrowList = narrow;
        _parentTile.SetNarrowList(narrow);
        foreach (var vm in _vms) vm.SetNarrowList(narrow);
        SyncViewTools();
    }

    private void OnListSurfaceSizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (_listLayoutQueued) return;
        _listLayoutQueued = true;
        // All existing and future rows get the layout flag before realization. Never notify a VM
        // from ContainerContentChanging or change each row's height after its own first measure.
        DispatcherQueue.TryEnqueue(() =>
        {
            _listLayoutQueued = false;
            PreserveLogicalAnchor();
            UpdateListLayout();
        });
    }

    private void OnToolsSizeChanged(object sender, SizeChangedEventArgs e) => LayoutToolGroups(e.NewSize.Width);

    private void LayoutToolGroups(double width)
    {
        if (width <= 0 || ViewGroup is null) return;
        var columns = _viewMode == ExplorerPresentationState.ViewMode.List && !_narrowList;
        var focused = XamlRoot is { } xamlRoot ? FocusManager.GetFocusedElement(xamlRoot) : null;
        var moveSortFocus = focused is not null && IsToolDescendant(focused, columns ? SortGroup : ListColumnHeaders);
        ListColumnHeaders.Visibility = columns ? Visibility.Visible : Visibility.Collapsed;
        SortGroup.Visibility = columns ? Visibility.Collapsed : Visibility.Visible;
        if (moveSortFocus)
        {
            var key = SortStateProvider?.Invoke().Key ?? ExplorerListing.SortKey.Name;
            (columns ? (Control)_sortHeaders[key].Button : SortKeyButton).Focus(FocusState.Programmatic);
        }
        if (columns)
        {
            Grid.SetRow(ViewGroup, 0);
            Grid.SetColumn(ViewGroup, 0);
            Grid.SetColumnSpan(ViewGroup, 2);
            ViewGroup.Margin = new Thickness(0);
            ToolSeparator.Visibility = Visibility.Collapsed;
            return;
        }
        // The group widths are measured as units; their individual controls never wrap.
        SortGroup.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        ViewGroup.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        var viewWidth = ViewGroup.DesiredSize.Width - ViewGroup.Margin.Left - ViewGroup.Margin.Right
            + (ToolSeparator.Visibility == Visibility.Collapsed ? 9 : 0);
        var wrap = width - 20 < SortGroup.DesiredSize.Width + viewWidth + 16;
        Grid.SetRow(ViewGroup, wrap ? 1 : 0);
        Grid.SetColumn(ViewGroup, wrap ? 0 : 1);
        Grid.SetColumnSpan(ViewGroup, wrap ? 2 : 1);
        Grid.SetColumnSpan(SortGroup, wrap ? 2 : 1);
        ViewGroup.Margin = wrap ? new Thickness(0, 8, 0, 0) : new Thickness(16, 0, 0, 0);
        ToolSeparator.Visibility = wrap ? Visibility.Collapsed : Visibility.Visible;
    }

    private void OnPresentationFocus(object sender, RoutedEventArgs e)
    {
        if (_restoringSelection || !ReferenceEquals(sender, ActiveItems)) return;
        if (EntryFromSource(e.OriginalSource) is { } entry) { _focusedPath = entry.Path; _parentFocused = false; }
        else if (IsParentTileSource(e.OriginalSource)) { _focusedPath = null; _parentFocused = true; }
        SavePresentation();
    }

    private void OnPresentationLayout(object? sender, object args)
    {
        SyncListHeaderBounds();
        var scroll = FindRetryDescendant<ScrollViewer>(ActiveItems);
        if (!ReferenceEquals(scroll, _presentationScroll))
        {
            if (_presentationScroll is not null) _presentationScroll.ViewChanged -= OnPresentationScroll;
            _presentationScroll = scroll;
            _anchorScrollIntermediate = false;
            if (scroll is not null) scroll.ViewChanged += OnPresentationScroll;
        }
        // A final ViewChanged may precede virtualized container realization. Keep the request
        // alive until layout supplies a valid sample; never poll or force unrelated rows into view.
        if (_anchorCapturePending)
        {
            _anchorCaptureRevision++;
            QueueUserAnchorCapture();
        }
    }

    private void SyncListHeaderBounds()
    {
        if (!IsLoaded || !ListHost.IsLoaded || ListColumnHeaders.Visibility != Visibility.Visible ||
            DetailList.ItemsPanelRoot is not Panel panel) return;
        // 기본 컨테이너 여백과 스크롤바를 추정하지 않고 현재 행의 실제 가용 폭에 맞춘다.
        var row = panel.Children.OfType<SelectorItem>().Select(item => item.ContentTemplateRoot switch
        {
            Grid grid => grid,
            Button { Content: Grid grid } => grid,
            _ => null
        }).FirstOrDefault(grid => grid is { IsLoaded: true, ActualWidth: > 0 });
        if (row is null) return;
        var bounds = row.TransformToVisual(ListHost).TransformBounds(new Rect(0, 0, row.ActualWidth, row.ActualHeight));
        var left = Math.Max(0, bounds.Left);
        var right = Math.Max(0, ListHost.ActualWidth - bounds.Right);
        if (Math.Abs(ListColumnHeaders.Margin.Left - left) > 0.25 || Math.Abs(ListColumnHeaders.Margin.Right - right) > 0.25)
            ListColumnHeaders.Margin = new Thickness(left, 0, right, 0);
    }

    private void OnPresentationScroll(object? sender, ScrollViewerViewChangedEventArgs args)
    {
        if (!ReferenceEquals(sender, _presentationScroll)) return;
        _anchorScrollIntermediate = args.IsIntermediate;
        _anchorCaptureRevision++;
        _scrollState.InvalidatePhysicalSample();
        if (!_restoringSelection && _scrollState.UserNavigation && IsVisibleSurface())
        {
            _anchorCapturePending = true;
            if (!args.IsIntermediate) QueueUserAnchorCapture();
        }
    }

    private bool IsVisibleSurface()
    {
        for (DependencyObject? node = this; node is not null; node = VisualTreeHelper.GetParent(node))
            if (node is UIElement element && element.Visibility != Visibility.Visible) return false;
        return IsLoaded;
    }

    private (string? Path, bool Parent) CaptureAnchor() =>
        TryCaptureAnchor(out var anchor) ? (anchor.Path, anchor.Parent) : (null, false);

    private bool TryCaptureAnchor(out ExplorerScrollState.Anchor anchor)
    {
        anchor = default;
        if (ActiveItems.ItemsPanelRoot is not Panel panel ||
            FindRetryDescendant<ScrollContentPresenter>(ActiveItems) is not { ActualHeight: > 0 } viewport) return false;
        var visible = new List<(SelectorItem Item, Rect Bounds)>();
        foreach (var item in panel.Children.OfType<SelectorItem>())
        {
            if (item.ActualWidth <= 0 || item.ActualHeight <= 0) continue;
            var bounds = item.TransformToVisual(viewport).TransformBounds(new Rect(0, 0, item.ActualWidth, item.ActualHeight));
            if (bounds.Bottom > 0 && bounds.Top < viewport.ActualHeight)
                visible.Add((item, bounds));
        }
        var first = visible.OrderBy(candidate => candidate.Bounds.Top).ThenBy(candidate => candidate.Bounds.Left).FirstOrDefault().Item;
        if (first is null || first.Content is not { } content ||
            !ReferenceEquals(ActiveItems.ContainerFromItem(content), first)) return false;
        if (ReferenceEquals(content, _parentTile)) { anchor = new(null, true); return true; }
        if (VmOf(first) is not { } vm || !ReferenceEquals(FindVmByPath(vm.Path), vm)) return false;
        anchor = new(vm.Path, false);
        return true;
    }

    private void SavePresentation(DisplaySelection? restored = null)
    {
        if (_restoringSelection || CurrentFolder is not { } folder || !IsVisibleSurface()) return;
        var snapshot = CaptureDisplaySelection(true);
        if (restored is not null) snapshot = snapshot with { Anchor = restored.Anchor, ParentAnchor = restored.ParentAnchor };
        Presentation.Save(folder, new(snapshot.Active, snapshot.Selected, snapshot.Focused,
            snapshot.ParentFocused, snapshot.Anchor, snapshot.ParentAnchor));
    }

    private DisplaySelection SharedSelection(string folder, IEnumerable<string> displayed)
    {
        // Read against the incoming Display before reconciliation changes native selection.
        var snapshot = Presentation.Read(folder, displayed);
        return new(snapshot.Active, snapshot.Selected, snapshot.Focused, snapshot.ParentFocused,
            snapshot.Anchor, snapshot.ParentAnchor, false);
    }


    private void FocusPresentationItem()
    {
        var target = _parentFocused ? (object)_parentTile : _focusedPath is { } path ? FindVmByPath(path) : null;
        target ??= ActiveItems.SelectedItem;
        if (target is null) { ActiveItems.Focus(FocusState.Programmatic); return; }
        var restoring = _restoringSelection;
        var anchor = _scrollState.Logical;
        var selection = ActiveItems.SelectedItems.ToArray();
        var active = ActiveItems.SelectedItem;
        _restoringSelection = true;
        try
        {
            ActiveItems.ScrollIntoView(target);
            ActiveItems.UpdateLayout();
            if (ActiveItems.ContainerFromItem(target) is Control container) container.Focus(FocusState.Programmatic);
            else ActiveItems.Focus(FocusState.Programmatic);
        }
        finally
        {
            ActiveItems.SelectedItems.Clear();
            if (active is not null) ActiveItems.SelectedItem = active;
            foreach (var item in selection) if (!ActiveItems.SelectedItems.Contains(item)) ActiveItems.SelectedItems.Add(item);
            RestoreAnchor(anchor.Path, anchor.Parent);
            _restoringSelection = restoring;
        }
    }

    private void RestoreAnchor(string? path, bool parent)
    {
        var epoch = _scrollState.BeginRestore(new(path, parent));
        object? target = parent ? _parentTile : path is not null ? FindVmByPath(path) : null;
        if (target is not null)
        {
            ActiveItems.ScrollIntoView(target, ScrollIntoViewAlignment.Leading);
            ActiveItems.UpdateLayout();
        }
        DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low,
            () => _scrollState.CompleteRestore(epoch));
    }

    private void PreserveLogicalAnchor()
    {
        var epoch = _scrollState.BeginRestore(_scrollState.Logical);
        DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low,
            () => _scrollState.CompleteRestore(epoch));
    }

    private bool CommitUserAnchor()
    {
        if (!_scrollState.UserNavigation || _restoringSelection || _anchorScrollIntermediate || !IsVisibleSurface()) return false;
        if (!TryCaptureAnchor(out var physical) || _presentationScroll is not { } scroll)
        {
            _scrollState.InvalidatePhysicalSample();
            return false;
        }
        if (_scrollState.ObserveSettledPhysical(physical, scroll.VerticalOffset, _scrollState.Epoch))
        {
            _anchorCapturePending = false;
            SavePresentation();
        }
        return true;
    }

    private void QueueUserAnchorCapture(int pass = 0)
    {
        if (_anchorCaptureQueued || !_anchorCapturePending || _anchorScrollIntermediate ||
            !_scrollState.UserNavigation || _restoringSelection || !IsVisibleSurface()) return;
        var epoch = _scrollState.Epoch;
        var generation = _showSeq;
        var folder = CurrentFolder;
        var owner = ActiveItems;
        var revision = _anchorCaptureRevision;
        _anchorCaptureQueued = true;
        if (!DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () =>
        {
            _anchorCaptureQueued = false;
            if (epoch != _scrollState.Epoch || generation != _showSeq || folder != CurrentFolder ||
                !ReferenceEquals(owner, ActiveItems) || !IsVisibleSurface())
            {
                // A newer real input may have coalesced behind this obsolete callback. Hand off
                // its pending request once; the guard rejects restored, hidden or unloaded owners.
                QueueUserAnchorCapture();
                return;
            }
            // Two matching observations are necessary. A real layout/final-scroll event may have
            // coalesced while this callback waited; its revision grants one fresh confirmation.
            // Without another native event, at most two passes run. No idle polling occurs.
            if (CommitUserAnchor() && _anchorCapturePending &&
                (pass == 0 || revision != _anchorCaptureRevision)) QueueUserAnchorCapture(1);
        })) _anchorCaptureQueued = false;
    }

    private static bool IsPresentationTextInput(object source)
    {
        for (var node = source as DependencyObject; node is not null; node = VisualTreeHelper.GetParent(node))
        {
            if (node is TextBox or RichEditBox or PasswordBox) return true;
            if (node is SelectorItem) break;
        }
        return false;
    }

    private void OnPresentationUserPointer(object sender, PointerRoutedEventArgs e)
    {
        if (!ReferenceEquals(sender, ActiveItems) || IsPresentationTextInput(e.OriginalSource)) return;
        _scrollState.BeginUserNavigation();
        _anchorCapturePending = true;
        QueueUserAnchorCapture();
    }

    private void OnPresentationNavigationKey(object sender, KeyRoutedEventArgs e)
    {
        if (e.Handled || !ReferenceEquals(sender, ActiveItems) || ExplorerRenameBox.IsEditing ||
            HotkeySupport.IsVirtualTextInputEvent(this, e) || PresentationSystemModifierDown() ||
            IsPresentationTextInput(e.OriginalSource) || e.Key is not (VirtualKey.Up or VirtualKey.Down or
                VirtualKey.Left or VirtualKey.Right or VirtualKey.Home or VirtualKey.End or
                VirtualKey.PageUp or VirtualKey.PageDown)) return;
        BeginUserFocusNavigation(); // Observe native navigation; do not handle or replace the key.
    }

    private static bool PresentationSystemModifierDown() =>
        new[] { VirtualKey.Menu, VirtualKey.LeftWindows, VirtualKey.RightWindows }.Any(key =>
            Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(key)
                .HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down));

    private void BeginUserFocusNavigation()
    {
        var epoch = _scrollState.BeginUserNavigation();
        _anchorCapturePending = true;
        QueueFocusVisibility(epoch, 0);
    }

    private void QueueFocusVisibility(long epoch, int pass, string? expectedPath = null)
    {
        var owner = ActiveItems;
        var generation = _showSeq;
        var folder = CurrentFolder;
        DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () =>
        {
            if (epoch != _scrollState.Epoch || generation != _showSeq || folder != CurrentFolder || !ReferenceEquals(owner, ActiveItems) ||
                !IsVisibleSurface() || ExplorerRenameBox.IsEditing || XamlRoot is not { } root) return;
            owner.UpdateLayout();
            var focused = FocusManager.GetFocusedElement(root);
            if (focused is null || IsPresentationTextInput(focused) || EntryFromSource(focused) is not { } entry ||
                FindVmByPath(entry.Path) is not { } vm || owner.ContainerFromItem(vm) is not SelectorItem container ||
                !ReferenceEquals(container.Content, vm)) return;
            if (expectedPath is not null && !string.Equals(entry.Path, expectedPath, StringComparison.OrdinalIgnoreCase)) return;
            var viewport = FindRetryDescendant<ScrollContentPresenter>(owner);
            var scroll = FindRetryDescendant<ScrollViewer>(owner);
            if (viewport is null || scroll is null) return;
            var bounds = container.TransformToVisual(viewport).TransformBounds(new Rect(0, 0, container.ActualWidth, container.ActualHeight));
            var offset = ExplorerScrollState.VisibleOffset(scroll.VerticalOffset, scroll.ScrollableHeight,
                viewport.ActualHeight, bounds.Top, bounds.Height);
            if (Math.Abs(offset - scroll.VerticalOffset) > 0.5)
                scroll.ChangeView(null, offset, null, disableAnimation: true);
            // One additional layout turn covers native realization/scroll completion. No timer, loop,
            // selection assignment or forced realization of the rest of the directory is involved.
            if (pass == 0) QueueFocusVisibility(epoch, 1, entry.Path);
            else QueueUserAnchorCapture();
        });
    }
}
