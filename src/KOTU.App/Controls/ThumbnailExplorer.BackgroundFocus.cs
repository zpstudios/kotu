using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;

namespace KOTU.App.Controls;

public sealed partial class ThumbnailExplorer
{
    private UIElement? _backgroundFocusInputRoot;
    private BackgroundPressRequest? _backgroundPressRequest;
    private BackgroundFocusRequest? _backgroundFocusRequest;

    private sealed class BackgroundPressRequest(ListViewBase owner, UIElement root, string? folder,
        int generation, long revision, uint pointerId)
    {
        internal ListViewBase Owner { get; } = owner;
        internal UIElement Root { get; } = root;
        internal string? Folder { get; } = folder;
        internal int Generation { get; } = generation;
        internal long Revision { get; } = revision;
        internal uint PointerId { get; } = pointerId;
        internal object? SelectedItem { get; } = owner.SelectedItem;
        internal object[] Selection { get; } = owner.SelectedItems.ToArray();
        internal bool PressReachedRoot { get; set; }
        internal bool Released { get; set; }
    }

    private sealed class BackgroundFocusRequest(ListViewBase owner, ExplorerEntryVm target,
        string? folder, int generation, long revision, uint pointerId)
    {
        internal ListViewBase Owner { get; } = owner;
        internal ExplorerEntryVm Target { get; } = target;
        internal string? Folder { get; } = folder;
        internal int Generation { get; } = generation;
        internal long Revision { get; } = revision;
        internal uint PointerId { get; } = pointerId;
        internal bool PressReachedRoot { get; set; }
        internal bool Released { get; set; }
        internal DependencyObject? ExpectedFocus { get; set; }
        internal Control? ShellFallbackFocus { get; set; }
    }

    private void InitializeBackgroundFocusSettlement()
    {
        Loaded += (_, _) => AttachBackgroundFocusInput();
        Unloaded += (_, _) => DetachBackgroundFocusInput();
    }

    private void AttachBackgroundFocusInput()
    {
        if (XamlRoot?.Content is not UIElement root || ReferenceEquals(root, _backgroundFocusInputRoot)) return;
        DetachBackgroundFocusInput();
        _backgroundFocusInputRoot = root;
        root.AddHandler(UIElement.PointerPressedEvent, new PointerEventHandler(OnBackgroundFocusRootPressed), true);
        root.AddHandler(UIElement.PointerReleasedEvent, new PointerEventHandler(OnBackgroundFocusRootReleased), true);
        root.AddHandler(UIElement.PointerCanceledEvent, new PointerEventHandler(OnBackgroundFocusRootCanceled), true);
        root.AddHandler(UIElement.PointerWheelChangedEvent, new PointerEventHandler(OnBackgroundFocusRootWheel), true);
        root.PreviewKeyDown += OnBackgroundFocusRootKey;
    }

    private void DetachBackgroundFocusInput()
    {
        CancelBackgroundFocus();
        if (_backgroundFocusInputRoot is not { } root) return;
        _backgroundFocusInputRoot = null;
        root.RemoveHandler(UIElement.PointerPressedEvent, new PointerEventHandler(OnBackgroundFocusRootPressed));
        root.RemoveHandler(UIElement.PointerReleasedEvent, new PointerEventHandler(OnBackgroundFocusRootReleased));
        root.RemoveHandler(UIElement.PointerCanceledEvent, new PointerEventHandler(OnBackgroundFocusRootCanceled));
        root.RemoveHandler(UIElement.PointerWheelChangedEvent, new PointerEventHandler(OnBackgroundFocusRootWheel));
        root.PreviewKeyDown -= OnBackgroundFocusRootKey;
    }

    private void ArmBackgroundPress(uint pointerId)
    {
        AttachBackgroundFocusInput();
        if (_backgroundFocusInputRoot is not { } root) return;
        // 눌림에서는 의도만 보관한다. 선택·스크롤은 실제 빈 곳에서 놓았는지 확인한 뒤에 바꾼다.
        _backgroundPressRequest = new(ActiveItems, root, CurrentFolder, _showSeq, _surfaceFocusRevision, pointerId);
    }

    private void CancelBackgroundFocus()
    {
        _backgroundPressRequest = null;
        _backgroundFocusRequest = null;
    }

    private void OnBackgroundFocusRootPressed(object sender, PointerRoutedEventArgs e)
    {
        // 표면에서 시작한 바로 그 눌림의 버블만 통과시킨다. 다음 눌림은 같은 마우스 ID라도 새 입력이다.
        if (_backgroundPressRequest is { PressReachedRoot: false, Released: false } request &&
            request.PointerId == e.Pointer.PointerId)
        {
            request.PressReachedRoot = true;
            return;
        }
        CancelBackgroundFocus();
    }

    private void OnBackgroundFocusRootKey(object sender, KeyRoutedEventArgs e) => CancelBackgroundFocus();
    private void OnBackgroundFocusRootWheel(object sender, PointerRoutedEventArgs e) => CancelBackgroundFocus();
    private void OnBackgroundFocusRootCanceled(object sender, PointerRoutedEventArgs e) => CancelBackgroundFocus();

    private void OnBackgroundFocusRootReleased(object sender, PointerRoutedEventArgs e)
    {
        if (_backgroundPressRequest is not { PressReachedRoot: true, Released: false } press ||
            press.PointerId != e.Pointer.PointerId) return;
        if (e.GetCurrentPoint(press.Owner).Properties.IsLeftButtonPressed ||
            !IsCurrentBackgroundPress(press, press.Revision) ||
            !ReferenceEquals(press.Owner.SelectedItem, press.SelectedItem) ||
            press.Owner.SelectedItems.Count != press.Selection.Length ||
            !press.Selection.All(press.Owner.SelectedItems.Contains) ||
            !IsBackgroundRelease(press.Owner, e) || XamlRoot is not { } root ||
            HasBlockingBackgroundFocusPopup(root))
        {
            if (ReferenceEquals(_backgroundPressRequest, press)) CancelBackgroundFocus();
            return;
        }
        // ScrollIntoView로 놓기 지점 아래 항목이 바뀌기 전에 판정을 끝낸다. 같은 놓기는 한 번만 소비한다.
        press.Released = true;
        var target = _vms.FirstOrDefault();
        if (target is null || !TrySelectFirstRealEntry(replaceSelection: true) ||
            !IsCurrentBackgroundPress(press, press.Revision + 1) ||
            !ReferenceEquals(_vms.FirstOrDefault(), target) ||
            press.Owner.SelectedItems.Count != 1 || !ReferenceEquals(press.Owner.SelectedItem, target) ||
            !press.Owner.SelectedItems.Contains(target) || HasBlockingBackgroundFocusPopup(root))
        {
            if (ReferenceEquals(_backgroundPressRequest, press)) CancelBackgroundFocus();
            return;
        }
        // 선택 콜백이 새 입력·재표시로 보류 객체를 취소하거나 바꾸었다면 여기까지 오지 않는다.
        _backgroundPressRequest = null;
        var request = new BackgroundFocusRequest(press.Owner, target, press.Folder, press.Generation,
            _surfaceFocusRevision, press.PointerId) { PressReachedRoot = true, Released = true };
        _backgroundFocusRequest = request;
        request.ExpectedFocus = FocusManager.GetFocusedElement(root) as DependencyObject;
        QueueBackgroundFocus(request, 0);
    }

    private bool IsCurrentBackgroundPress(BackgroundPressRequest press, long revision) =>
        ReferenceEquals(_backgroundPressRequest, press) && revision == _surfaceFocusRevision &&
        press.Generation == _showSeq && press.Folder == CurrentFolder && ReferenceEquals(press.Owner, ActiveItems) &&
        ReferenceEquals(_backgroundFocusInputRoot, press.Root) && ReferenceEquals(XamlRoot?.Content, press.Root) &&
        IsToolDescendant(press.Owner, press.Root) && !_restoringSelection && !_parentNavigationPending &&
        !ExplorerRenameBox.IsEditing && IsVisibleSurface() &&
        (ColumnSession is not { } session || session.IsDisplayingFolder(CurrentFolder));

    private bool IsBackgroundRelease(ListViewBase owner, PointerRoutedEventArgs e)
    {
        if (IsToolSource(e.OriginalSource) || !IsExplorerBackground(e.OriginalSource) ||
            FindRetryDescendant<ScrollContentPresenter>(owner) is not { } viewport) return false;
        var point = e.GetCurrentPoint(viewport).Position;
        if (point.X < 0 || point.Y < 0 || point.X >= viewport.ActualWidth || point.Y >= viewport.ActualHeight) return false;
        // 캡처된 스크롤 표면이 원본이어도 실제 항목 위에서 놓았거나 밖으로 끌고 나가면 정착을 취소한다.
        if (owner.ItemsPanelRoot is Panel panel)
            foreach (var item in panel.Children.OfType<SelectorItem>())
            {
                var position = e.GetCurrentPoint(item).Position;
                if (position.X >= 0 && position.Y >= 0 && position.X < item.ActualWidth && position.Y < item.ActualHeight) return false;
            }
        return true;
    }

    private bool CanSettleBackgroundFocus(BackgroundFocusRequest request) =>
        ReferenceEquals(_backgroundFocusRequest, request) && request.Revision == _surfaceFocusRevision &&
        request.Generation == _showSeq && request.Folder == CurrentFolder && ReferenceEquals(request.Owner, ActiveItems) &&
        !_restoringSelection && !_parentNavigationPending && !ExplorerRenameBox.IsEditing && IsVisibleSurface() &&
        (ColumnSession is not { } session || session.IsDisplayingFolder(CurrentFolder)) &&
        _vms.Contains(request.Target) && ActiveItems.SelectedItems.Count == 1 &&
        ReferenceEquals(ActiveItems.SelectedItem, request.Target) && ActiveItems.SelectedItems.Contains(request.Target);

    // 셸과 놓기 콜백이 함께 쓴다. 현재 루트에서 분리된 실제 포커스만 복구하고 살아 있는 요소는 거절한다.
    internal bool TryRecoverBackgroundFocusOrphan(XamlRoot root, DependencyObject orphanedFocus)
    {
        if (_backgroundFocusRequest is not { } request ||
            !CanUseBackgroundFocusRequest(request, root) || IsToolDescendant(orphanedFocus, root.Content) ||
            !ReferenceEquals(FocusManager.GetFocusedElement(root), orphanedFocus) ||
            request.Owner.ContainerFromItem(request.Target) is not SelectorItem container ||
            !IsBackgroundFocusContainer(request, root, container)) return false;
        EnsureBackgroundSelectionAnchor(request);
        // 피어 선택의 콜백 이후에도 동일한 요청·고아·컨테이너여야 한다.
        if (!CanUseBackgroundFocusRequest(request, root) ||
            !ReferenceEquals(FocusManager.GetFocusedElement(root), orphanedFocus) ||
            !IsBackgroundFocusContainer(request, root, container)) return false;
        // 같은 놓기 요청을 여기서 소비한다. 예약된 낮은 우선순위 콜백은 더 이상 포커스를 쓰지 않는다.
        CancelBackgroundFocus();
        return container.Focus(FocusState.Keyboard);
    }

    // 셸이 바로 수행할 Focus 한 번의 전후를 묶는다. 일반 생존 포커스에는 이 권한을 만들지 않는다.
    internal Action<Control>? CaptureBackgroundFocusFallback(XamlRoot root, DependencyObject orphanedFocus)
    {
        if (_backgroundFocusRequest is not { ShellFallbackFocus: null } request ||
            !CanUseBackgroundFocusRequest(request, root) || IsToolDescendant(orphanedFocus, root.Content) ||
            !ReferenceEquals(FocusManager.GetFocusedElement(root), orphanedFocus)) return null;
        var accepted = false;
        return fallback =>
        {
            if (accepted) return;
            accepted = true;
            if (request.ShellFallbackFocus is not null || !CanUseBackgroundFocusRequest(request, root) ||
                !IsToolDescendant(fallback, root.Content) ||
                !ReferenceEquals(FocusManager.GetFocusedElement(root), fallback)) return;
            request.ShellFallbackFocus = fallback;
        };
    }

    private bool CanUseBackgroundFocusRequest(BackgroundFocusRequest request, XamlRoot root) =>
        request.PressReachedRoot && request.Released && XamlRoot is { } currentRoot &&
        ReferenceEquals(currentRoot.Content, root.Content) && ReferenceEquals(_backgroundFocusInputRoot, root.Content) &&
        CanSettleBackgroundFocus(request) && !HasBlockingBackgroundFocusPopup(root);

    private static bool IsBackgroundFocusContainer(BackgroundFocusRequest request, XamlRoot root, SelectorItem container) =>
        ReferenceEquals(request.Owner.ContainerFromItem(request.Target), container) &&
        ReferenceEquals(container.Content, request.Target) && container.IsLoaded &&
        IsToolDescendant(container, request.Owner) && IsToolDescendant(request.Owner, root.Content);

    private void EnsureBackgroundSelectionAnchor(BackgroundFocusRequest request)
    {
        if (_defaultSelectionAnchor is { } anchor && ReferenceEquals(anchor.Owner, request.Owner) &&
            ReferenceEquals(anchor.Target, request.Target) && anchor.Folder == request.Folder &&
            anchor.Generation == request.Generation && anchor.Revision == request.Revision) return;
        // 가상화로 초기 피어 선택을 못 했을 때만 기존 정착 횟수 안에서 보완한다.
        ResetDefaultSelectionAnchor(request.Target);
    }

    private static bool HasBlockingBackgroundFocusPopup(XamlRoot root)
    {
        try
        {
            foreach (var popup in VisualTreeHelper.GetOpenPopupsForXamlRoot(root))
                // 비대화형 툴팁만 제외한다. 메뉴·대화상자·알 수 없는 팝업은 기존처럼 양보한다.
                if (popup.Child is not ToolTip) return true;
            return false;
        }
        catch { return true; }
    }

    private bool HasUnchangedBackgroundFocus(BackgroundFocusRequest request, XamlRoot root)
    {
        var focused = FocusManager.GetFocusedElement(root) as DependencyObject;
        if (request.ShellFallbackFocus is { } fallback)
            return ReferenceEquals(focused, fallback) && IsToolDescendant(fallback, root.Content);
        if (focused is null || ReferenceEquals(focused, request.ExpectedFocus)) return true;
        if (!IsToolDescendant(focused, request.Owner) || IsToolSource(focused) || IsPresentationTextInput(focused) ||
            IsInCheckBox(focused) || IsScrollBarSource(focused)) return false;
        return IsExplorerBackground(focused) ||
            (EntryFromSource(focused) is { } entry && entry.Path == request.Target.Path);
    }

    private void QueueBackgroundFocus(BackgroundFocusRequest request, int pass)
    {
        // 놓기와 셸 후처리 뒤 초점과 미완료 기준점을 정착한다. 기본 선택 대상은 바꾸지 않는다.
        if (!DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () =>
        {
            if (!CanSettleBackgroundFocus(request) || XamlRoot is not { } root)
            {
                if (ReferenceEquals(_backgroundFocusRequest, request)) CancelBackgroundFocus();
                return;
            }
            // 셸 후처리가 놓기보다 먼저 끝났어도 같은 분리 포커스는 여기서 동일한 가드로 복구한다.
            if (FocusManager.GetFocusedElement(root) is DependencyObject orphan &&
                TryRecoverBackgroundFocusOrphan(root, orphan)) return;
            // 복구가 요청을 소비한 뒤 Focus가 실패해도 낡은 콜백으로 선택·초점을 다시 쓰지 않는다.
            if (!ReferenceEquals(_backgroundFocusRequest, request)) return;
            if (!HasUnchangedBackgroundFocus(request, root)) { CancelBackgroundFocus(); return; }
            if (HasBlockingBackgroundFocusPopup(root)) { CancelBackgroundFocus(); return; }
            request.Owner.UpdateLayout();
            if (!CanUseBackgroundFocusRequest(request, root) || !HasUnchangedBackgroundFocus(request, root) ||
                request.Owner.ContainerFromItem(request.Target) is not SelectorItem container ||
                !IsBackgroundFocusContainer(request, root, container)) { CancelBackgroundFocus(); return; }
            EnsureBackgroundSelectionAnchor(request);
            if (!CanUseBackgroundFocusRequest(request, root) || !HasUnchangedBackgroundFocus(request, root) ||
                !IsBackgroundFocusContainer(request, root, container))
            {
                if (ReferenceEquals(_backgroundFocusRequest, request)) CancelBackgroundFocus();
                return;
            }
            if (request.ShellFallbackFocus is not null)
            {
                // 정확한 셸 fallback만 한 번 이어받고 먼저 소비한다. 실패해도 셸 복구를 미루지 않는다.
                CancelBackgroundFocus();
                container.Focus(FocusState.Keyboard);
                return;
            }
            container.Focus(FocusState.Keyboard);
            request.ExpectedFocus = FocusManager.GetFocusedElement(root) as DependencyObject;
            // 네이티브 포인터 정리와 겹치는 마지막 레이아웃 한 번까지만 확인하며 반복 감시는 하지 않는다.
            if (pass == 0) QueueBackgroundFocus(request, 1);
            else CancelBackgroundFocus();
        }) && ReferenceEquals(_backgroundFocusRequest, request)) CancelBackgroundFocus();
    }
}
