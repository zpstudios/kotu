using System.Runtime.InteropServices;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.ApplicationModel.DataTransfer;
using Windows.Storage.Streams;
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

    // 전역 accelerator가 아니라 이미지 뷰 안에서 올라온 키만 처리한다.
    private async void OnImageCopyKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Handled || e.Key != VirtualKey.C || !IsCopyModifierDown(VirtualKey.Control)
            || IsCopyModifierDown(VirtualKey.Menu) || IsCopyModifierDown(VirtualKey.Shift)
            || !CanCopyImageHere()) return;
        e.Handled = true;
        if (e.KeyStatus.WasKeyDown || !_copyGate.TryBegin(out var request)) return;
        var bytes = _printBytes!;
        var rotation = TotalRotation;
        var focused = FocusManager.GetFocusedElement(XamlRoot);
        var clipboardSequence = GetClipboardSequenceNumber();
        try
        {
            // 디코드/회전/인코딩은 워커. 클립보드 호출만 UI 스레드로 돌아온다.
            var png = await Worker.Run(_ => ImageClipboardSnapshot.EncodePngAsync(bytes, rotation)
                .GetAwaiter().GetResult());
            if (!_copyGate.IsCurrent(request) || !CanCopyImageHere()
                || !ReferenceEquals(bytes, _printBytes) || rotation != TotalRotation
                || !ReferenceEquals(focused, FocusManager.GetFocusedElement(XamlRoot))
                || clipboardSequence != GetClipboardSequenceNumber()) return;
            var stream = new MemoryStream(png); // 패키지의 참조가 소유한다. Flush 실패에도 스트림을 닫지 않는다.
            var data = new DataPackage { RequestedOperation = DataPackageOperation.Copy };
            data.SetBitmap(RandomAccessStreamReference.CreateFromStream(stream.AsRandomAccessStream()));
            Clipboard.SetContent(data);
            Clipboard.Flush(); // 원본 파일/뷰가 닫혀도 다른 앱에서 붙여넣을 수 있게 소유권을 넘긴다.
        }
        catch (Exception ex)
        {
            // 실패 시 Clear나 빈 패키지를 게시하지 않는다.
            DiagTrace.Write("image", "Clipboard copy failed: " + ex.Message);
        }
        finally { _copyGate.Finish(request); }
    }

    private bool CanCopyImageHere()
    {
        if (!IsLoaded || !_copyReady || !CanPrintNow || XamlRoot is not { } root
            || HotkeySupport.ShouldPassThrough(this)) return false;
        try
        {
            if (GetForegroundWindow() != Microsoft.UI.Win32Interop.GetWindowFromWindowId(
                root.ContentIslandEnvironment.AppWindowId)) return false;
            foreach (var popup in VisualTreeHelper.GetOpenPopupsForXamlRoot(root))
                if (popup.Child is not ToolTip) return false;
            for (var node = FocusManager.GetFocusedElement(root) as DependencyObject;
                 node is not null; node = VisualTreeHelper.GetParent(node))
            {
                if (node is TextBox or PasswordBox or RichEditBox or TextBlock or RichTextBlock)
                    return false;
                if (ReferenceEquals(node, this)) return true;
            }
        }
        catch { return false; }
        return false;
    }

    private static bool IsCopyModifierDown(VirtualKey key) =>
        (InputKeyboardSource.GetKeyStateForCurrentThread(key) & CoreVirtualKeyStates.Down) != 0;

    [DllImport("user32.dll")]
    private static extern nint GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern uint GetClipboardSequenceNumber();
}
