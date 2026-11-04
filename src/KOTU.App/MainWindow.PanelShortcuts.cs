using KOTU.Core.Contracts;
using KOTU.Input;

namespace KOTU.App;

public sealed partial class MainWindow
{
    private bool HandlePanelShortcut(OverlaySide side, bool isRepeat)
    {
        // S4 consumes without action. Both keyboard surfaces use the A176 first-down toggle.
        if (IsOpenFileBrowsing) return true;
        if (!HasPanelContext) return false;
        if (!isRepeat) OnOverlaySideDown(side);
        return true;
    }

    private void OnEditorPanelShortcutRequested(EditorPanelShortcut request)
    {
        if (IsFocusWithin(JobsLayer) || !HotkeySupport.IsVirtualTextInputFocused(RootLayout)) return;
        var side = request.Side switch
        {
            EditorPanelSide.Left => _listSide,
            EditorPanelSide.Right => _infoSide,
            _ => null
        };
        // Do not arm native Alt-up suppression from a message delivered after Alt was released.
        if (side is not null && HandlePanelShortcut(side, request.IsRepeat) && request.Alt && IsAltDown())
            _altComboUsed = true;
    }
}
