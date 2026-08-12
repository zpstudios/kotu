using System.Runtime.InteropServices;

namespace KOTU.App;

/// <summary>
/// 창의 Alt+Tab 순환 목록 제외 토글 (A69 — 최소화 시 트레이로 숨김).
/// WS_EX_TOOLWINDOW 확장 스타일을 켜고 끈다. AppWindow.Hide()만으로도 작업표시줄·Alt+Tab에서
/// 빠지지만, 사양 메모(부록 B 18번)대로 숨김 동안 이 스타일을 함께 지정해
/// 숨김 창을 순환 목록에 남기는 셸 변형(서드파티 Alt+Tab 대체물 등)까지 방어한다.
/// 프로젝트가 x64 전용(PlatformTarget)이라 LongPtr 변형만으로 충분 — WindowMinSize와 같은 규칙.
/// UI 스레드에서만 부른다.
/// </summary>
internal static class AltTabExclusion
{
    private const int GwlExStyle = -20;             // GWL_EXSTYLE
    private const long WsExToolWindow = 0x00000080; // WS_EX_TOOLWINDOW

    /// <summary>excluded = true면 WS_EX_TOOLWINDOW를 켜고, false면 원래대로 끈다.</summary>
    public static void Set(Microsoft.UI.Xaml.Window window, bool excluded)
    {
        var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(window);
        var style = GetWindowLongPtrW(hwnd, GwlExStyle).ToInt64();
        var updated = excluded ? style | WsExToolWindow : style & ~WsExToolWindow;
        // 실패해도 앱은 살아야 한다 — Hide/Show가 주 동작이고 이 스타일은 보조 방어선이다.
        if (updated != style) _ = SetWindowLongPtrW(hwnd, GwlExStyle, new IntPtr(updated));
    }

    // ---------- P/Invoke ----------

    [DllImport("user32", EntryPoint = "GetWindowLongPtrW", SetLastError = true)]
    private static extern IntPtr GetWindowLongPtrW(IntPtr hWnd, int nIndex);

    [DllImport("user32", EntryPoint = "SetWindowLongPtrW", SetLastError = true)]
    private static extern IntPtr SetWindowLongPtrW(IntPtr hWnd, int nIndex, IntPtr dwNewLong);
}
