using KOTU.Core.Content;
using KOTU.Core.Contracts;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace KOTU.App;

public sealed partial class MainWindow
{
    private bool _updateFrozen, _updateGuardActivation;
    private bool _updateHitTest, _updateWasHidden;
    private object? _updateContent;
    private ContentContractSession? _updateSession;
    private readonly List<(Control Control, bool Enabled)> _updateControls = [];

    internal bool IsClosedForUpdates => _contentClosed;
    internal bool HasUpdateBlockingUi => _contentClosed || _closeInProgress || _confirmInProgress || _updateFrozen ||
        !RootLayout.IsLoaded || IsAnyPopupOpen();
    internal bool CanHostUpdate => !_contentClosed && !_closeInProgress && !_confirmInProgress && !_updateFrozen &&
        !_hiddenInTray && RootLayout.IsLoaded && AppWindow.IsVisible &&
        AppWindow.Presenter is not Microsoft.UI.Windowing.OverlappedPresenter
            { State: Microsoft.UI.Windowing.OverlappedPresenterState.Minimized } && !IsAnyPopupOpen();

    internal bool TryFreezeForUpdate()
    {
        if (_contentClosed || _closeInProgress || _confirmInProgress || _updateFrozen || IsAnyPopupOpen()) return false;
        _updateFrozen = true;
        _updateHitTest = RootLayout.IsHitTestVisible;
        _updateWasHidden = _hiddenInTray;
        _updateContent = ModuleHost.Content;
        _updateSession = _contentSession;
        RootLayout.IsHitTestVisible = false;
        // 부모 Control을 비활성화하면 네이티브 편집 컨트롤의 키 입력도 막는다.
        // ContentDialog는 XamlRoot의 별도 popup 트리에 생성되므로 이 목록에 포함하지 않는다.
        void Freeze(DependencyObject element)
        {
            if (element is Control control)
            {
                _updateControls.Add((control, control.IsEnabled));
                control.IsEnabled = false;
                return;
            }
            for (var i = 0; i < VisualTreeHelper.GetChildrenCount(element); i++) Freeze(VisualTreeHelper.GetChild(element, i));
        }
        try { Freeze(RootLayout); return true; }
        catch { ThawAfterUpdate(); throw; }
    }

    internal bool IsUpdateSnapshotCurrent => _updateFrozen && !_contentClosed &&
        ReferenceEquals(_updateContent, ModuleHost.Content) && ReferenceEquals(_updateSession, _contentSession);

    internal async Task<IAsyncDisposable?> PrepareUpdateCloseAsync()
    {
        if (!IsUpdateSnapshotCurrent) return null;
        // 명시적인 Install and restart 이후의 미저장 확인만 숨김 창을 일시 복귀시킨다.
        if (ModuleHost.Content is ICloseGuard { HasUnsavedChanges: true })
        {
            _updateGuardActivation = true;
            try { BringToFront(); }
            finally { _updateGuardActivation = false; }
        }
        return await ConfirmDiscardAsync(forUpdate: true);
    }

    internal void ThawAfterUpdate()
    {
        if (!_updateFrozen) return;
        foreach (var (control, enabled) in _updateControls) control.IsEnabled = enabled;
        _updateControls.Clear();
        RootLayout.IsHitTestVisible = _updateHitTest;
        _updateFrozen = false;
        _updateContent = null; _updateSession = null;
        if (_updateWasHidden && !_hiddenInTray && !_contentClosed)
        {
            _hiddenInTray = true;
            AltTabExclusion.Set(this, true);
            AppWindow.Hide();
        }
    }
}
