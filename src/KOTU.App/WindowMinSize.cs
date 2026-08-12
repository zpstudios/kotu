using System.Runtime.InteropServices;

namespace KOTU.App;

/// <summary>
/// 최소 창 크기 720×540 DIP 강제 (A40, 2026-08-10 사용자 확정 수치).
/// OverlappedPresenter.PreferredMinimumWidth/Height(WinAppSDK 1.4+, 현재 1.8)를 쓰지 않은 이유:
/// ① DPI 미인지 버그 — 값이 물리 픽셀로 그대로 쓰여 150% 모니터에서는 실효 최소가
///    480×360 DIP로 줄어든다(microsoft-ui-xaml #10475, 1.7 기준으로도 미수정).
/// ② SetPresenter로 전체화면을 오가면 OverlappedPresenter가 재생성되어 설정이 초기화된다
///    (A39에서 IsAlwaysOnTop으로 실증된 것과 같은 원인) — 프레젠터 변경마다 재적용 훅이 또 필요해진다.
/// 대신 창 HWND를 서브클래싱해 WM_GETMINMAXINFO의 ptMinTrackSize를 물리 픽셀로 답한다.
/// DPI는 메시지가 올 때마다 GetDpiForWindow(PerMonitorV2 — app.manifest)로 읽으므로
/// 모니터 이동·배율 변경에 항상 옳고, HWND 서브클래스는 프레젠터 교체와 무관하게 유지된다.
/// UI 스레드에서만 부른다(WndProc도 같은 스레드로 온다 — WindowManager 스레드 규칙과 동일).
/// </summary>
internal static class WindowMinSize
{
    /// <summary>최소 논리 크기(DIP). 물리 픽셀 하한 = 이 값 × (모니터 DPI / 96).</summary>
    public const int MinWidthDip = 720;
    public const int MinHeightDip = 540;

    /// <summary>
    /// 창별 최소 높이 임시 오버라이드(DIP, A61) — 하단 바만 남기는 접힘 동안에만 540보다 낮은
    /// 하한을 허용한다. 이게 없으면 접힌 높이(타이틀바 + 44)가 540 하한에 걸려 기능이 통째로
    /// 무동작이 된다. **폭 하한 720은 접힘 중에도 그대로**다 — 하단 바가 그 폭을 전제로
    /// 축약 설계돼 있다(A40 LayoutSensorCards/UpdateBarDensity).
    /// 값은 DIP로 들고 있어 모니터를 옮겨도 <see cref="MinPhysical"/>의 DPI 환산이 그대로 옳다.
    /// </summary>
    private static readonly Dictionary<IntPtr, double> s_minHeightOverrides = new();

    private const uint WmGetMinMaxInfo = 0x0024;
    private const uint WmNcDestroy = 0x0082;
    private const int GwlpWndProc = -4;

    private delegate IntPtr WndProcDelegate(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam);

    /// <summary>델리게이트 GC 방지 — 반드시 static 필드로 유지 (TrayIcon과 동일 규칙).</summary>
    private static readonly WndProcDelegate s_proc = Hook;
    private static readonly IntPtr s_procPtr = Marshal.GetFunctionPointerForDelegate(s_proc);

    /// <summary>HWND별 교체 전 WndProc — 체인 유지용. 모든 창이 한 UI 스레드라 잠금 불필요.</summary>
    private static readonly Dictionary<IntPtr, IntPtr> s_prevProcs = new();

    /// <summary>창 HWND를 서브클래싱한다. 창마다 1회 — MainWindow 생성자에서 부른다.</summary>
    public static void Apply(Microsoft.UI.Xaml.Window window)
    {
        var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(window);
        if (s_prevProcs.ContainsKey(hwnd)) return;
        // 프로젝트가 x64 전용(PlatformTarget)이라 SetWindowLongPtr 변형만으로 충분
        var prev = SetWindowLongPtrW(hwnd, GwlpWndProc, s_procPtr);
        if (prev == IntPtr.Zero) return; // 실패해도 앱은 살아야 한다 — 최소 크기 강제만 빠진다
        s_prevProcs[hwnd] = prev;
    }

    /// <summary>
    /// 접힘(A61) 동안 최소 높이를 낮춰 끼운다. heightDip이 null이면 기본값(540)으로 되돌린다.
    /// 창마다 독립이며 창이 소멸하면(WM_NCDESTROY) 자동으로 지워진다.
    /// UI 스레드에서만 부른다(WndProc도 같은 스레드 — 이 클래스 전체 규칙).
    /// </summary>
    public static void SetMinHeightOverride(Microsoft.UI.Xaml.Window window, double? heightDip)
    {
        var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(window);
        if (heightDip is { } dip && dip > 0) s_minHeightOverrides[hwnd] = dip;
        else s_minHeightOverrides.Remove(hwnd);
    }

    /// <summary>현재 모니터 배율 기준 최소 물리 픽셀. 창 크기 복원(v0.55.0)의 하한에도 쓴다.</summary>
    public static (int Width, int Height) MinPhysical(IntPtr hwnd)
    {
        var dpi = GetDpiForWindow(hwnd);
        if (dpi == 0) dpi = 96; // 유효하지 않은 HWND 등 — 100%로 간주
        // 높이만 오버라이드 대상(A61) — 폭은 언제나 720 DIP.
        var minHeightDip = s_minHeightOverrides.TryGetValue(hwnd, out var dip) ? dip : MinHeightDip;
        return ((int)Math.Round(MinWidthDip * dpi / 96.0), (int)Math.Round(minHeightDip * dpi / 96.0));
    }

    private static IntPtr Hook(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam)
    {
        if (!s_prevProcs.TryGetValue(hwnd, out var prev))
            return DefWindowProcW(hwnd, msg, wParam, lParam); // 정상 경로에선 오지 않는 방어선

        // 원래 프로시저(XAML/AppWindow 체인)를 먼저 태우고 나서 하한을 덮어쓴다 —
        // 프레젠터도 이 메시지로 최소/최대를 쓸 수 있으므로 나중에 쓰는 쪽이 이긴다.
        var result = CallWindowProcW(prev, hwnd, msg, wParam, lParam);

        if (msg == WmGetMinMaxInfo && lParam != IntPtr.Zero)
        {
            var info = Marshal.PtrToStructure<MINMAXINFO>(lParam);
            var (minW, minH) = MinPhysical(hwnd);
            info.ptMinTrackSize.X = Math.Max(info.ptMinTrackSize.X, minW);
            info.ptMinTrackSize.Y = Math.Max(info.ptMinTrackSize.Y, minH);
            Marshal.StructureToPtr(info, lParam, fDeleteOld: false);
        }
        else if (msg == WmNcDestroy)
        {
            s_prevProcs.Remove(hwnd); // 창 소멸 — 이후 이 HWND로 메시지는 오지 않는다
            s_minHeightOverrides.Remove(hwnd); // 접힌 채 닫힌 창의 오버라이드도 함께 정리 (A61)
        }
        return result;
    }

    // ---------- P/Invoke ----------

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT { public int X; public int Y; }

    /// <summary>WM_GETMINMAXINFO의 lParam. 필드 순서는 Win32 정의 그대로여야 한다.</summary>
    [StructLayout(LayoutKind.Sequential)]
    private struct MINMAXINFO
    {
        public POINT ptReserved;
        public POINT ptMaxSize;
        public POINT ptMaxPosition;
        public POINT ptMinTrackSize;
        public POINT ptMaxTrackSize;
    }

    [DllImport("user32", EntryPoint = "SetWindowLongPtrW", SetLastError = true)]
    private static extern IntPtr SetWindowLongPtrW(IntPtr hWnd, int nIndex, IntPtr dwNewLong);

    [DllImport("user32")]
    private static extern IntPtr CallWindowProcW(IntPtr lpPrevWndFunc, IntPtr hWnd, uint msg,
        IntPtr wParam, IntPtr lParam);

    [DllImport("user32")]
    private static extern IntPtr DefWindowProcW(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32")]
    private static extern uint GetDpiForWindow(IntPtr hWnd);
}
