using KOTU.Core.Routing;
using KOTU.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.System;
using Column = KOTU.Core.Routing.ExplorerColumnLayout.Column;

namespace KOTU.App.Controls;

public sealed partial class ThumbnailExplorer
{
    internal ExplorerSession? ColumnSession { get; set; }
    private ExplorerColumnLayout _columnLayout = ExplorerColumnLayout.Default;
    private double[] _columnWidths = ExplorerColumnLayout.Default.Measure(800);
    private double _columnRowWidth = 800;
    private bool _columnGeometryQueued;
    private readonly Dictionary<Column, ToggleMenuFlyoutItem> _columnOptions = new();
    private readonly Dictionary<Column, ContentControl> _columnBoundaries = new();
    private ColumnDrag? _columnDrag;

    private sealed class ColumnDrag(Column column, ExplorerColumnLayout layout, double width,
        double origin, string? folder, int generation, uint pointerId, Action release)
    {
        internal Column Column { get; } = column;
        internal ExplorerColumnLayout Layout { get; } = layout;
        internal double Width { get; } = width;
        internal double Origin { get; } = origin;
        internal string? Folder { get; } = folder;
        internal int Generation { get; } = generation;
        internal uint PointerId { get; } = pointerId;
        internal Action Release { get; } = release;
        internal double Delta { get; set; }
    }

    private void InitializeColumnTools()
    {
        ColumnsButton.Tag = HotkeySupport.PassThroughTag;
        var menu = new MenuFlyout();
        foreach (var column in ExplorerColumnLayout.Order.Where(column => column != Column.Name))
        {
            var item = new ToggleMenuFlyoutItem { Text = column.ToString(), IsChecked = true };
            item.Click += (_, _) =>
            {
                if (CanEditColumns()) ColumnSession?.SetColumnVisible(CurrentFolder, column, item.IsChecked);
                RefreshColumns();
            };
            _columnOptions.Add(column, item);
            menu.Items.Add(item);
        }
        menu.Items.Add(new MenuFlyoutSeparator());
        var reset = new MenuFlyoutItem { Text = "Restore default layout" };
        reset.Click += (_, _) =>
        {
            if (CanEditColumns()) ColumnSession?.ResetColumns(CurrentFolder);
            RefreshColumns();
        };
        menu.Items.Add(reset);
        menu.Opening += (_, _) => RefreshColumns();
        ColumnsButton.Flyout = menu;
        foreach (var column in ExplorerColumnLayout.Order.Where(column => column != Column.Modified))
        {
            // 독립 경계는 정렬 버튼의 자식이 아니다. 포인터·키 입력은 경계에서만 처리한다.
            var boundary = new ContentControl
            {
                Width = 8, Height = 24, MinWidth = 0, MinHeight = 0, Padding = new Thickness(0),
                Margin = new Thickness(0, 0, -8, 0), HorizontalAlignment = HorizontalAlignment.Right,
                VerticalAlignment = VerticalAlignment.Center, Tag = HotkeySupport.PassThroughTag,
                Content = new TextBlock { Text = "│", FontSize = 12, Opacity = 0.45 },
                Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent),
                BorderThickness = new Thickness(0), IsTabStop = true, UseSystemFocusVisuals = true,
                HorizontalContentAlignment = HorizontalAlignment.Center,
            };
            Grid.SetColumn(boundary, (int)column + 2);
            boundary.AddHandler(UIElement.PointerPressedEvent, new PointerEventHandler((_, e) => StartColumnDrag(column, boundary, e)), true);
            boundary.PointerMoved += OnColumnDragMoved;
            boundary.PointerReleased += OnColumnDragReleased;
            boundary.PointerCaptureLost += (_, _) => CancelColumnDrag();
            boundary.PreviewKeyDown += (_, e) => OnColumnBoundaryKey(column, e);
            _columnBoundaries.Add(column, boundary);
            ListColumnHeaders.Children.Add(boundary);
        }
        Unloaded += (_, _) => CancelColumnDrag();
        Loaded += (_, _) => RefreshColumns();
        ApplyColumnGeometry();
    }

    private bool CanEditColumns() => ColumnSession is not null && IsVisibleSurface() &&
        _viewMode == ExplorerPresentationState.ViewMode.List && !ExplorerRenameBox.IsEditing;

    private void RefreshColumns()
    {
        CancelColumnDrag();
        _columnLayout = ColumnSession?.ReadColumns() ?? _columnLayout;
        PreserveLogicalAnchor();
        ApplyColumnGeometry();
        SyncViewTools();
    }

    private void QueueColumnGeometry()
    {
        if (_columnGeometryQueued) return;
        _columnGeometryQueued = true;
        if (!DispatcherQueue.TryEnqueue(() =>
        {
            _columnGeometryQueued = false;
            if (!IsLoaded) return;
            if (_columnDrag is { } drag && Math.Abs(drag.Width - _columnRowWidth) > 0.25) CancelColumnDrag();
            if (_columnDrag is null) _columnLayout = ColumnSession?.Columns ?? _columnLayout;
            PreserveLogicalAnchor();
            ApplyColumnGeometry();
        })) _columnGeometryQueued = false;
    }

    private void ApplyColumnGeometry()
    {
        _columnWidths = _columnLayout.Measure(_columnRowWidth);
        foreach (var vm in _vms) vm.SetListColumns(_narrowList, _columnLayout, _columnWidths);
        var focused = XamlRoot is { } xamlRoot ? FocusManager.GetFocusedElement(xamlRoot) : null;
        var visible = ExplorerColumnLayout.Order.Where(_columnLayout.IsVisible).ToArray();
        foreach (var column in ExplorerColumnLayout.Order)
        {
            var header = _sortHeaders[SortKeyFor(column)].Button;
            var shown = _columnLayout.IsVisible(column);
            if (!shown && focused is not null && IsToolDescendant(focused, header)) SortKeyButton.Focus(FocusState.Programmatic);
            header.Visibility = shown ? Visibility.Visible : Visibility.Collapsed;
            if (column != Column.Name)
                ListColumnHeaders.ColumnDefinitions[(int)column + 2].Width = new GridLength(_columnWidths[(int)column]);
            if (_columnOptions.TryGetValue(column, out var option)) option.IsChecked = shown;
            if (!_columnBoundaries.TryGetValue(column, out var boundary)) continue;
            var index = Array.IndexOf(visible, column);
            var hasNext = index >= 0 && index < visible.Length - 1;
            if (!hasNext && focused is not null && IsToolDescendant(focused, boundary)) ColumnsButton.Focus(FocusState.Programmatic);
            boundary.Visibility = hasNext ? Visibility.Visible : Visibility.Collapsed;
            if (hasNext)
            {
                var text = $"Resize {column} and {visible[index + 1]} columns. Left or Right adjusts by 16 pixels";
                AutomationProperties.SetName(boundary, text);
                ToolTipService.SetToolTip(boundary, text);
            }
        }
    }

    private static ExplorerListing.SortKey SortKeyFor(Column column) => column switch
    {
        Column.Size => ExplorerListing.SortKey.Size, Column.Type => ExplorerListing.SortKey.Type,
        Column.Created => ExplorerListing.SortKey.Created, Column.Modified => ExplorerListing.SortKey.Modified,
        _ => ExplorerListing.SortKey.Name,
    };

    private void OnColumnBoundaryKey(Column column, KeyRoutedEventArgs e)
    {
        if (e.Key == VirtualKey.Escape && _columnDrag is not null)
        {
            CancelColumnDrag();
            e.Handled = true;
            return;
        }
        if (e.Key is not (VirtualKey.Left or VirtualKey.Right) || PresentationSystemModifierDown() ||
            HotkeySupport.IsVirtualTextInputEvent(this, e)) return;
        e.Handled = true;
        if (e.KeyStatus.WasKeyDown || !CanEditColumns()) return;
        CancelColumnDrag();
        var session = ColumnSession!;
        var current = session.ReadColumns();
        session.ResizeColumns(CurrentFolder, current, _columnRowWidth, column, e.Key == VirtualKey.Left ? -16 : 16);
        RefreshColumns();
    }

    private void StartColumnDrag(Column column, ContentControl boundary, PointerRoutedEventArgs e)
    {
        var point = e.GetCurrentPoint(ListColumnHeaders);
        if (point.Properties.PointerUpdateKind != Microsoft.UI.Input.PointerUpdateKind.LeftButtonPressed) return;
        e.Handled = true;
        boundary.Focus(FocusState.Pointer);
        if (!CanEditColumns() || _narrowList) return;
        CancelColumnDrag();
        if (!boundary.CapturePointer(e.Pointer)) return;
        var current = ColumnSession!.ReadColumns();
        _columnDrag = new(column, current, _columnRowWidth, point.Position.X, CurrentFolder, _showSeq,
            e.Pointer.PointerId, () => boundary.ReleasePointerCapture(e.Pointer));
        PreserveLogicalAnchor();
    }

    private bool IsCurrentColumnDrag(ColumnDrag drag) => CanEditColumns() && !_narrowList &&
        drag.Generation == _showSeq && drag.Folder == CurrentFolder &&
        drag.Layout == ColumnSession?.Columns && Math.Abs(drag.Width - _columnRowWidth) <= 0.25;

    private void OnColumnDragMoved(object sender, PointerRoutedEventArgs e)
    {
        if (_columnDrag is not { } drag || drag.PointerId != e.Pointer.PointerId) return;
        e.Handled = true;
        if (!IsCurrentColumnDrag(drag)) { CancelColumnDrag(); return; }
        drag.Delta = e.GetCurrentPoint(ListColumnHeaders).Position.X - drag.Origin;
        _columnLayout = drag.Layout.Resize(drag.Width, drag.Column, drag.Delta);
        ApplyColumnGeometry();
    }

    private void OnColumnDragReleased(object sender, PointerRoutedEventArgs e)
    {
        if (_columnDrag is not { } drag || drag.PointerId != e.Pointer.PointerId) return;
        e.Handled = true;
        _columnDrag = null;
        if (IsCurrentColumnDrag(drag))
            ColumnSession?.ResizeColumns(drag.Folder, drag.Layout, drag.Width, drag.Column,
                e.GetCurrentPoint(ListColumnHeaders).Position.X - drag.Origin);
        drag.Release();
        RefreshColumns();
    }

    private void CancelColumnDrag()
    {
        if (_columnDrag is not { } drag) return;
        _columnDrag = null;
        drag.Release();
        _columnLayout = ColumnSession?.Columns ?? drag.Layout;
        PreserveLogicalAnchor();
        ApplyColumnGeometry();
    }
}
