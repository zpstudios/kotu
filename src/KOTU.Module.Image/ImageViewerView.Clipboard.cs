using System.Runtime.InteropServices;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.System;
using Windows.UI.Core;
using KOTU.Core.Diagnostics;
using KOTU.Input;

namespace KOTU.Module.Image;

public sealed partial class ImageViewerView
{
    private readonly ImageCopyGate _copyGate = new();
    private bool _copyReady;
    private bool _imageLoading;
    private int _loadSequence;
    private MenuFlyoutItem? _copyImageMenuItem;

    // 셸이 없는 호스트에서도 뷰 내부의 키는 같은 명령으로 처리한다.
    private void OnImageCopyKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Handled || e.Key != VirtualKey.C || !IsCopyModifierDown(VirtualKey.Control)
            || IsCopyModifierDown(VirtualKey.Menu) || IsCopyModifierDown(VirtualKey.Shift)) return;
        e.Handled = TryCopyImage(e.KeyStatus.WasKeyDown);
    }

    public bool TryCopyImage(bool repeat = false, object? focusScope = null)
    {
        if (!CanCopyImageHere(focusScope)) return false;
        if (!repeat) _ = CopyCurrentImageAsync();
        return true;
    }

    // 키와 명시적 메뉴 명령이 공유할 유일한 복사 경로. 호출 뒤 포커스 이동은 취소가 아니다.
    private async Task CopyCurrentImageAsync()
    {
        if (!CanCopyCurrentImage || !_copyGate.TryBegin(out var request)) return;
        UpdateImageCopyMenuState();
        var bytes = _printBytes!;
        var rotation = TotalRotation;
        var loadSequence = _loadSequence;
        try
        {
            var owner = Microsoft.UI.Win32Interop.GetWindowFromWindowId(XamlRoot.ContentIslandEnvironment.AppWindowId);
            var clipboardSequence = Win32ImageClipboard.Instance.Sequence;
            // 선읽기 큐와 분리하되 게이트로 한 번에 하나만 실행한다. 큰 픽셀 복사도 워커에서 끝낸다.
            using var prepared = await Task.Run(async () =>
                ImageClipboardWriter.Prepare(await ImageClipboardSnapshot.CreateAsync(bytes, rotation)));
            bool IsCurrent() => _copyGate.IsCurrent(request) && CanCopyCurrentImage
                && loadSequence == _loadSequence && ReferenceEquals(bytes, _printBytes) && rotation == TotalRotation;
            await prepared.PublishAsync(owner, clipboardSequence, IsCurrent);
        }
        catch (Exception ex)
        {
            DiagTrace.Write("image", $"Clipboard copy failed: {ex.GetType().Name} (0x{ex.HResult:X8})");
        }
        finally
        {
            _copyGate.Finish(request);
            UpdateImageCopyMenuState();
        }
    }

    private bool CanCopyCurrentImage => IsLoaded && _copyReady && CanPrintNow && XamlRoot is not null;

    private void UpdateImageCopyMenuState()
    {
        if (_copyImageMenuItem is { } item) item.IsEnabled = CanCopyCurrentImage && !_copyGate.IsPending;
    }

    private async void OnCopyImageMenuClick(object sender, RoutedEventArgs e)
    {
        if (!ReferenceEquals(sender, _copyImageMenuItem) || !CanCopyFromImageMenu()) return;
        await CopyCurrentImageAsync();
    }

    private bool CanCopyFromImageMenu()
    {
        if (!CanCopyCurrentImage || _copyGate.IsPending || _copyImageMenuItem is not { } item ||
            XamlRoot is not { } root) return false;
        try
        {
            if (GetForegroundWindow() != Microsoft.UI.Win32Interop.GetWindowFromWindowId(
                root.ContentIslandEnvironment.AppWindowId)) return false;
            // 명시적 메뉴 명령은 자신의 팝업만 허용한다. 이미 닫혔어도 현재 이미지로 실행한다.
            foreach (var popup in VisualTreeHelper.GetOpenPopupsForXamlRoot(root))
                if (popup.Child is not ToolTip && (popup.Child is not { } child || !IsWithin(item, child)))
                    return false;
            return true;
        }
        catch { return false; }
    }

    private bool CanCopyImageHere(object? focusScope)
    {
        if (!CanCopyCurrentImage || XamlRoot is not { } root
            || HotkeySupport.ShouldPassThrough(this)) return false;
        try
        {
            if (GetForegroundWindow() != Microsoft.UI.Win32Interop.GetWindowFromWindowId(
                root.ContentIslandEnvironment.AppWindowId)) return false;
            foreach (var popup in VisualTreeHelper.GetOpenPopupsForXamlRoot(root))
                if (popup.Child is not ToolTip) return false;
            var focused = FocusManager.GetFocusedElement(root);
            // 통합 뷰 자신에 포커스가 복원된 경우만 허용한다. 다른 형제 콘텐츠는 허용하지 않는다.
            if (focusScope is DependencyObject scope && ReferenceEquals(focused, scope) && IsWithin(this, scope))
                return true;
            var node = focused as DependencyObject;
            for (var depth = 0; node is not null && depth < 64; depth++, node = VisualTreeHelper.GetParent(node))
            {
                if (node is TextBox or PasswordBox or RichEditBox or TextBlock or RichTextBlock)
                    return false;
                if (ReferenceEquals(node, this) || ReferenceEquals(node, StatusBar)) return true;
            }
        }
        catch { return false; }
        return false;
    }

    private static bool IsWithin(DependencyObject element, DependencyObject ancestor)
    {
        DependencyObject? node = element;
        for (var depth = 0; node is not null && depth < 64; depth++, node = VisualTreeHelper.GetParent(node))
            if (ReferenceEquals(node, ancestor)) return true;
        return false;
    }

    private static bool IsCopyModifierDown(VirtualKey key) =>
        (InputKeyboardSource.GetKeyStateForCurrentThread(key) & CoreVirtualKeyStates.Down) != 0;

    [DllImport("user32.dll")]
    private static extern nint GetForegroundWindow();

}
