using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;

namespace KOTU.App.Controls;

public sealed partial class ThumbnailExplorer
{
    private long _surfaceFocusRevision;
    private (ListViewBase Owner, ExplorerEntryVm Target, string? Folder, int Generation, long Revision)? _defaultSelectionAnchor;

    private bool HasValidEntrySelection() => ActiveItems.SelectedItems.OfType<ExplorerEntryVm>().Any(_vms.Contains);

    private bool TrySelectFirstRealEntry(bool replaceSelection)
    {
        if (_restoringSelection || _parentNavigationPending || ExplorerRenameBox.IsEditing || !IsVisibleSurface() ||
            (ColumnSession is { } session && !session.IsDisplayingFolder(CurrentFolder)) ||
            (!replaceSelection && HasValidEntrySelection()) || _vms.FirstOrDefault() is not { } first) return false;
        ++_surfaceFocusRevision;
        // 표시 VM에는 상위 이동이 없다. 검색과 같은 선택·포커스 경로만 쓰며 독립 체크는 건드리지 않는다.
        if (!SelectNameMatch(first.Path)) return false;
        ResetDefaultSelectionAnchor(first);
        return true;
    }

    private void ResetDefaultSelectionAnchor(ExplorerEntryVm target)
    {
        var owner = ActiveItems;
        var revision = _surfaceFocusRevision;
        var folder = CurrentFolder;
        var generation = _showSeq;
        if (_restoringSelection || _parentNavigationPending || ExplorerRenameBox.IsEditing || !IsVisibleSurface() ||
            (ColumnSession is { } session && !session.IsDisplayingFolder(CurrentFolder)) || !_vms.Contains(target) ||
            !owner.IsEnabled || owner.SelectedItems.Count != 1 || !ReferenceEquals(owner.SelectedItem, target) ||
            owner.ContainerFromItem(target) is not SelectorItem container || !container.IsEnabled || !container.IsLoaded ||
            !ReferenceEquals(container.Content, target) || !IsToolDescendant(container, owner)) return;
        try
        {
            if (FrameworkElementAutomationPeer.CreatePeerForElement(owner) is not SelectorAutomationPeer peer ||
                revision != _surfaceFocusRevision || generation != _showSeq || folder != CurrentFolder ||
                !ReferenceEquals(owner, ActiveItems) ||
                owner.SelectedItems.Count != 1 || !ReferenceEquals(owner.SelectedItem, target) ||
                !container.IsEnabled || !container.IsLoaded || !ReferenceEquals(container.Content, target) ||
                !ReferenceEquals(owner.ContainerFromItem(target), container) || !IsToolDescendant(container, owner)) return;
            // 공개 단일 선택 경로는 WinUI의 Shift 기준점도 갱신한다. 검색의 일반 선택 경로는 바꾸지 않는다.
            new SelectorItemAutomationPeer(target, peer).Select();
            _defaultSelectionAnchor = (owner, target, folder, generation, revision);
        }
        catch
        {
            // 피어 생성·비활성·컨테이너 분리 실패는 이미 선택한 항목과 독립 체크를 그대로 둔다.
        }
    }

    private void OnPresentationGettingFocus(UIElement sender, GettingFocusEventArgs e)
    {
        if (!ReferenceEquals(sender, ActiveItems) || _restoringSelection || _parentNavigationPending ||
            ExplorerRenameBox.IsEditing || !IsVisibleSurface() || e.FocusState != FocusState.Keyboard ||
            HasValidEntrySelection() || e.NewFocusedElement is not { } next ||
            !IsToolDescendant(next, ActiveItems) || IsPresentationTextInput(next) || IsInCheckBox(next) ||
            IsScrollBarSource(next) ||
            (e.OldFocusedElement is { } previous && IsToolDescendant(previous, ActiveItems))) return;
        var owner = ActiveItems;
        var revision = ++_surfaceFocusRevision;
        var generation = _showSeq;
        var folder = CurrentFolder;
        // 외부에서 키보드로 들어오는 경우만 한 번 예약한다. 부모를 향한 내부 이동·포인터 선택·복원은 제외한다.
        // 현재 포커스 변경이 끝난 뒤 같은 대상인지 확인하므로 GettingFocus 안에서 재진입하지 않는다.
        DispatcherQueue.TryEnqueue(() =>
        {
            if (revision != _surfaceFocusRevision || generation != _showSeq || folder != CurrentFolder ||
                !ReferenceEquals(owner, ActiveItems) || XamlRoot is not { } root ||
                !ReferenceEquals(FocusManager.GetFocusedElement(root), next)) return;
            TrySelectFirstRealEntry(replaceSelection: false);
        });
    }

    private bool IsExplorerBackground(object source)
    {
        for (var node = source as DependencyObject; node is not null; node = VisualTreeHelper.GetParent(node))
        {
            if (node is SelectorItem or ScrollBar or ButtonBase or TextBox or RichEditBox or PasswordBox) return false;
            if (ReferenceEquals(node, ActiveItems) || ReferenceEquals(node, LayoutRoot)) return true;
        }
        return false;
    }

    private static bool IsScrollBarSource(object source)
    {
        for (var node = source as DependencyObject; node is not null; node = VisualTreeHelper.GetParent(node))
        {
            if (node is ScrollBar) return true;
            if (node is ListViewBase) break;
        }
        return false;
    }
}
