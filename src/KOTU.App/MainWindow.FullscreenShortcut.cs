using KOTU.Core.Contracts;
using KOTU.Input;
using Windows.System;

namespace KOTU.App;

public sealed partial class MainWindow
{
    private static bool FullscreenKeyDown(VirtualKey key) =>
        Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(key)
            .HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down);

    private static bool IsPlainAltEnter() => FullscreenKeyDown(VirtualKey.Menu) &&
        !FullscreenKeyDown(VirtualKey.Control) && !FullscreenKeyDown(VirtualKey.Shift) &&
        !FullscreenKeyDown(VirtualKey.LeftWindows) && !FullscreenKeyDown(VirtualKey.RightWindows);

    private void OnEditorFullscreenShortcutRequested(EditorFullscreenShortcut request)
    {
        if (!request.IsCurrent || IsOpenFileBrowsing || IsFocusWithin(JobsLayer) || IsAnyPopupOpen() ||
            ExplorerRenameBox.IsEditing || !HotkeySupport.IsVirtualTextInputFocused(RootLayout)) return;
        if (!request.IsRepeat) JumpViewMode();
        if (IsAltDown()) _altComboUsed = true;
    }
}
