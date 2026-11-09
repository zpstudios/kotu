using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.Foundation;
using Windows.System;

namespace KOTU.App.Overlays;

public sealed partial class FileListOverlay
{
    private UIElement? _treeFocusInputRoot;
    private TreeBackgroundFocusRequest? _treeBackgroundFocus;

    private sealed class TreeBackgroundFocusRequest(TreeViewNode node, UIElement root, string? folder,
        int generation, int expandSequence, uint pointerId, Point pressPoint)
    {
        internal TreeViewNode Node { get; } = node;
        internal UIElement Root { get; } = root;
        internal string? Folder { get; } = folder;
        internal int Generation { get; } = generation;
        internal int ExpandSequence { get; } = expandSequence;
        internal uint PointerId { get; } = pointerId;
        internal Point PressPoint { get; } = pressPoint;
        internal bool ReachedRoot { get; set; }
        internal bool Released { get; set; }
        internal object? ExpectedFocus { get; set; }
    }

    private void InitializeTreeBackgroundFocus()
    {
        TreeHost.AddHandler(UIElement.PointerPressedEvent, new PointerEventHandler(OnTreeBackgroundPressed), true);
        FolderTree.SelectionChanged += (_, _) => CancelTreeBackgroundFocus();
        PanelBorder.DragOver += (_, _) => CancelTreeBackgroundFocus();
        Loaded += (_, _) => AttachTreeFocusInput();
        Unloaded += (_, _) => DetachTreeFocusInput();
    }

    private void AttachTreeFocusInput()
    {
        if (XamlRoot?.Content is not UIElement root || ReferenceEquals(root, _treeFocusInputRoot)) return;
        DetachTreeFocusInput();
        _treeFocusInputRoot = root;
        root.AddHandler(UIElement.PointerPressedEvent, new PointerEventHandler(OnTreeFocusRootPressed), true);
        root.AddHandler(UIElement.PointerReleasedEvent, new PointerEventHandler(OnTreeBackgroundReleased), true);
        root.AddHandler(UIElement.PointerMovedEvent, new PointerEventHandler(OnTreeFocusPointerMoved), true);
        root.AddHandler(UIElement.PointerCanceledEvent, new PointerEventHandler(OnTreeFocusPointerCanceled), true);
        root.AddHandler(UIElement.PointerWheelChangedEvent, new PointerEventHandler(OnTreeFocusPointerCanceled), true);
        root.PreviewKeyDown += OnTreeFocusKey;
    }

    private void DetachTreeFocusInput()
    {
        CancelTreeBackgroundFocus();
        if (_treeFocusInputRoot is not { } root) return;
        _treeFocusInputRoot = null;
        root.RemoveHandler(UIElement.PointerPressedEvent, new PointerEventHandler(OnTreeFocusRootPressed));
        root.RemoveHandler(UIElement.PointerReleasedEvent, new PointerEventHandler(OnTreeBackgroundReleased));
        root.RemoveHandler(UIElement.PointerMovedEvent, new PointerEventHandler(OnTreeFocusPointerMoved));
        root.RemoveHandler(UIElement.PointerCanceledEvent, new PointerEventHandler(OnTreeFocusPointerCanceled));
        root.RemoveHandler(UIElement.PointerWheelChangedEvent, new PointerEventHandler(OnTreeFocusPointerCanceled));
        root.PreviewKeyDown -= OnTreeFocusKey;
    }

    private void CancelTreeBackgroundFocus() => _treeBackgroundFocus = null;
    private void OnTreeFocusKey(object sender, KeyRoutedEventArgs e) => CancelTreeBackgroundFocus();
    private void OnTreeFocusPointerCanceled(object sender, PointerRoutedEventArgs e) => CancelTreeBackgroundFocus();

    private void OnTreeBackgroundPressed(object sender, PointerRoutedEventArgs e)
    {
        CancelTreeBackgroundFocus();
        if (IsLegacyListEnabled || !IsTreeFocusSurfaceVisible() || ExplorerRenameBox.IsEditing || TreeFocusModifierDown() ||
            e.GetCurrentPoint(FolderTree).Properties.PointerUpdateKind != Microsoft.UI.Input.PointerUpdateKind.LeftButtonPressed ||
            !IsTreeBackgroundSource(e.OriginalSource) || !IsTreeBackgroundPoint(e) ||
            FolderTree.SelectedNode is not { Content: FolderNode } node || !IsCurrentTreeNode(node)) return;
        AttachTreeFocusInput();
        if (_treeFocusInputRoot is not { } root) return;
        // 누르는 동안 선택·스크롤·초점을 바꾸면 놓기 지점의 항목이 달라진다. 의도만 보관한다.
        _treeBackgroundFocus = new(node, root, CurrentFolder, _session.Generation, _expandSeq,
            e.Pointer.PointerId, e.GetCurrentPoint(TreeHost).Position);
    }

    private void OnTreeFocusRootPressed(object sender, PointerRoutedEventArgs e)
    {
        if (_treeBackgroundFocus is { ReachedRoot: false, Released: false } request && request.PointerId == e.Pointer.PointerId)
            request.ReachedRoot = true;
        else CancelTreeBackgroundFocus();
    }

    private void OnTreeFocusPointerMoved(object sender, PointerRoutedEventArgs e)
    {
        if (_treeBackgroundFocus is { Released: false } request && request.PointerId == e.Pointer.PointerId &&
            !IsTreeClickPosition(request, e)) CancelTreeBackgroundFocus();
    }

    private static bool TreeFocusModifierDown() =>
        new[] { VirtualKey.Control, VirtualKey.Shift, VirtualKey.Menu, VirtualKey.LeftWindows, VirtualKey.RightWindows }
            .Any(key => Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(key)
                .HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down));

    private bool IsTreeClickPosition(TreeBackgroundFocusRequest request, PointerRoutedEventArgs e)
    {
        var point = e.GetCurrentPoint(TreeHost).Position;
        // 빈 곳끼리의 끌기도 클릭으로 재해석하지 않는다. 작은 포인터 흔들림만 허용한다.
        return Math.Abs(point.X - request.PressPoint.X) <= 4 && Math.Abs(point.Y - request.PressPoint.Y) <= 4;
    }

    private void OnTreeBackgroundReleased(object sender, PointerRoutedEventArgs e)
    {
        if (_treeBackgroundFocus is not { ReachedRoot: true, Released: false } request ||
            request.PointerId != e.Pointer.PointerId) return;
        if (!IsCurrentTreeFocusRequest(request) || TreeFocusModifierDown() ||
            e.GetCurrentPoint(FolderTree).Properties.IsLeftButtonPressed || !IsTreeClickPosition(request, e) ||
            !IsTreeBackgroundSource(e.OriginalSource) || !IsTreeBackgroundPoint(e) || XamlRoot is not { } root || HasBlockingTreePopup())
        {
            CancelTreeBackgroundFocus();
            return;
        }
        // 검증된 배경 클릭을 소비해 바깥 ScrollViewer의 포인터 초점 처리가 덮어쓰지 않게 한다.
        e.Handled = true;
        request.Released = true;
        request.ExpectedFocus = FocusManager.GetFocusedElement(root);
        if (TryFocusSelectedTreeNode(request)) return;
        if (!IsCurrentTreeFocusRequest(request)) return;
        // 가상화된 선택 노드만 기존 스크롤 경로로 실체화한다. 한 번의 후속 외에는 기다리지 않는다.
        ScrollTreeTo(request.Node);
        if (!IsCurrentTreeFocusRequest(request) || !DispatcherQueue.TryEnqueue(
            Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () =>
            {
                if (!IsCurrentTreeFocusRequest(request))
                {
                    if (ReferenceEquals(_treeBackgroundFocus, request)) CancelTreeBackgroundFocus();
                    return;
                }
                FolderTree.UpdateLayout();
                TryFocusSelectedTreeNode(request);
                if (ReferenceEquals(_treeBackgroundFocus, request)) CancelTreeBackgroundFocus();
            }))
        {
            if (ReferenceEquals(_treeBackgroundFocus, request)) CancelTreeBackgroundFocus();
        }
    }

    private bool TryFocusSelectedTreeNode(TreeBackgroundFocusRequest request)
    {
        if (!IsCurrentTreeFocusRequest(request) || XamlRoot is not { } root || HasBlockingTreePopup() ||
            !ReferenceEquals(FocusManager.GetFocusedElement(root), request.ExpectedFocus))
        {
            if (ReferenceEquals(_treeBackgroundFocus, request)) CancelTreeBackgroundFocus();
            return false;
        }
        if (FolderTree.ContainerFromNode(request.Node) is not TreeViewItem { IsLoaded: true, IsEnabled: true } item ||
            !ReferenceEquals(FolderTree.NodeFromContainer(item), request.Node) || !IsTreeDescendant(item, FolderTree) ||
            !IsTreeElementVisible(item)) return false;
        // 현재 노드를 그대로 둔다. Focus만 소비하고 방향키·Enter·항해는 네이티브 트리에 맡긴다.
        CancelTreeBackgroundFocus();
        return item.Focus(FocusState.Keyboard);
    }

    private bool IsCurrentTreeFocusRequest(TreeBackgroundFocusRequest request) =>
        ReferenceEquals(_treeBackgroundFocus, request) && !_explorerDisposed && IsTreeFocusSurfaceVisible() &&
        !ExplorerRenameBox.IsEditing && request.Generation == _session.Generation && request.ExpandSequence == _expandSeq &&
        request.Folder == CurrentFolder && ReferenceEquals(request.Root, _treeFocusInputRoot) &&
        ReferenceEquals(request.Root, XamlRoot?.Content) && ReferenceEquals(FolderTree.SelectedNode, request.Node) &&
        IsCurrentTreeNode(request.Node);

    private bool IsCurrentTreeNode(TreeViewNode node)
    {
        for (TreeViewNode? current = node; current is not null; current = current.Parent)
        {
            if (FolderTree.RootNodes.Contains(current)) return true;
            if (current.Parent is not { IsExpanded: true } parent || !parent.Children.Contains(current)) return false;
        }
        return false;
    }

    private bool IsTreeFocusSurfaceVisible() => !_explorerDisposed && IsOpen && IsLoaded && FolderTree.IsLoaded &&
        FolderTree.IsEnabled && IsTreeElementVisible(FolderTree) && XamlRoot?.Content is { } root && IsTreeDescendant(FolderTree, root);

    private static bool IsTreeElementVisible(DependencyObject element)
    {
        for (DependencyObject? node = element; node is not null; node = VisualTreeHelper.GetParent(node))
            if (node is UIElement { Visibility: not Visibility.Visible }) return false;
        return true;
    }

    private static bool IsTreeDescendant(DependencyObject element, DependencyObject ancestor)
    {
        for (DependencyObject? node = element; node is not null; node = VisualTreeHelper.GetParent(node))
            if (ReferenceEquals(node, ancestor)) return true;
        return false;
    }

    private bool IsTreeBackgroundSource(object source)
    {
        for (var node = source as DependencyObject; node is not null; node = VisualTreeHelper.GetParent(node))
        {
            if (node is TreeViewItem or ScrollBar or ButtonBase or TextBox or RichEditBox or PasswordBox) return false;
            if (ReferenceEquals(node, TreeHost)) return true;
        }
        return false;
    }

    private bool IsTreeBackgroundPoint(PointerRoutedEventArgs e)
    {
        var point = e.GetCurrentPoint(FolderTree).Position;
        return point.X >= 0 && point.Y >= 0 && point.X < FolderTree.ActualWidth && point.Y < FolderTree.ActualHeight &&
            !HitsTreeControl(FolderTree, e);
    }

    private static bool HitsTreeControl(DependencyObject root, PointerRoutedEventArgs e)
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is UIElement { Visibility: not Visibility.Visible }) continue;
            if (child is FrameworkElement element && child is TreeViewItem or ScrollBar or ButtonBase)
            {
                var point = e.GetCurrentPoint(element).Position;
                if (point.X >= 0 && point.Y >= 0 && point.X < element.ActualWidth && point.Y < element.ActualHeight) return true;
            }
            if (HitsTreeControl(child, e)) return true;
        }
        return false;
    }

    private bool HasBlockingTreePopup()
    {
        if (XamlRoot is not { } root) return true;
        try
        {
            foreach (var popup in VisualTreeHelper.GetOpenPopupsForXamlRoot(root))
                if (popup.Child is not ToolTip) return true;
            return false;
        }
        catch { return true; }
    }
}
