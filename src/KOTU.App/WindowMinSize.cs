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
/// 이 서브클래스는 HWND당 1회 규칙이라, 창 메시지 관찰이 필요한 다른 기능도 여기 얹는다 —
/// (A185의 SC_MINIMIZE 관찰이 동승했었으나 A218/2026-08-24에서 소비자와 함께 제거 —
/// 복원 참조 = A218 이전 git 이력. 새 동승자는 같은 방식으로 케이스만 추가하면 된다.)
/// </summary>
internal static class WindowMinSize
{
    /// <summary>최소 논리 크기(DIP). 물리 픽셀 하한 = 이 값 × (모니터 DPI / 96).</summary>
    public const int MinWidthDip = 720;
    public const int MinHeightDip = 540;

    // A238(v0.253.0): A61 접힘용 창별 최소 높이 오버라이드(s_minHeightOverrides·
    // SetMinHeightOverride)는 접힘 폐기와 함께 제거됐다 — 하한은 다시 모든 창 공통
    // 720×540 하나다(복원 참조 = A238 이전 git 이력).

    private const uint WmGetMinMaxInfo = 0x0024;
    private const uint WmNcDestroy = 0x0082;
    private const int GwlpWndProc = -4;

    private delegate IntPtr WndProcDelegate(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam);

    /// <summary>델리게이트 GC 방지 — 반드시 static 필드로 유지 (TrayIcon과 동일 규칙).</summary>
    private static readonly WndProcDelegate s_proc = Hook;
    private static readonly IntPtr s_procPtr = Marshal.GetFunctionPointerForDelegate(s_proc);

    /// <summary>HWND별 교체 전 WndProc — 체인 유지용. 모든 창이 한 UI 스레드라 잠금 불필요.</summary>
    private static readonly Dictionary<IntPtr, IntPtr> s_prevProcs = new();

    /// <summary>저장 기하를 복원하는 동기 호출 동안만 적용할 HWND별 상한 예외(A442).</summary>
    private static readonly Dictionary<IntPtr, RestoreSizeScope> s_restoreSizes = new();

    /// <summary>MainWindow가 정상 상태에서 관측한 최신 크기. 복원 상한 계산에만 쓴다(A442).</summary>
    private static readonly Dictionary<IntPtr, (int Width, int Height)> s_normalSizes = new();

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

    /// <summary>현재 모니터 배율 기준 최소 물리 픽셀. 창 크기 복원(v0.55.0)의 하한과
    /// 핀의 1회 축소(A238 — MainWindow.ShrinkToMinimum)의 목표 크기에도 쓴다.</summary>
    public static (int Width, int Height) MinPhysical(IntPtr hwnd)
    {
        var dpi = GetDpiForWindow(hwnd);
        if (dpi == 0) dpi = 96; // 유효하지 않은 HWND 등 — 100%로 간주
        return ((int)Math.Round(MinWidthDip * dpi / 96.0), (int)Math.Round(MinHeightDip * dpi / 96.0));
    }

    /// <summary>
    /// 저장된 물리 크기가 현재 화면의 기본 최대 추적 크기보다 커도 복원할 수 있게 한다(A442).
    /// Windows는 AppWindow.MoveAndResize/Resize에도 WM_GETMINMAXINFO를 보내므로, 복원 중
    /// 잘린 크기가 다음 저장 때 원래 값을 덮을 수 있다. using 범위를 복원 호출로 한정하고
    /// 최대화 크기·위치는 변경하지 않는다. 스코프 밖 상한은 시스템 기본값과 현재 정상
    /// 복원 크기 중 큰 값만 허용한다. UI 스레드 전용.
    /// </summary>
    internal static IDisposable AllowRestoreSize(Microsoft.UI.Xaml.Window window, int width, int height)
        => new RestoreSizeScope(WinRT.Interop.WindowNative.GetWindowHandle(window), width, height);

    /// <summary>실제 정상 크기 추적과 함께 갱신한다. 크기를 적용하거나 저장값을 복구하지 않는다.</summary>
    internal static void RememberNormalSize(Microsoft.UI.Xaml.Window window, int width, int height)
    {
        var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(window);
        if (width > 0 && height > 0 && s_prevProcs.ContainsKey(hwnd))
            s_normalSizes[hwnd] = (width, height);
    }

    private sealed class RestoreSizeScope : IDisposable
    {
        private readonly IntPtr _hwnd;
        private readonly RestoreSizeScope? _prior;
        private bool _disposed;
        internal int Width { get; }
        internal int Height { get; }

        internal RestoreSizeScope(IntPtr hwnd, int width, int height)
        {
            _hwnd = hwnd;
            Width = width;
            Height = height;
            // 훅 설치 실패·이미 닫힌 창이면 예외 상태도 등록하지 않는다.
            if (!s_prevProcs.ContainsKey(hwnd)) return;
            s_restoreSizes.TryGetValue(hwnd, out _prior);
            s_restoreSizes[hwnd] = this;
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            // 닫힌 HWND가 재사용되어도 과거 스코프가 새 창의 상태를 덮지 않는다.
            if (!s_restoreSizes.TryGetValue(_hwnd, out var current) || !ReferenceEquals(current, this)) return;
            var prior = _prior;
            while (prior is { _disposed: true }) prior = prior._prior;
            if (prior is null) s_restoreSizes.Remove(_hwnd);
            else s_restoreSizes[_hwnd] = prior;
        }
    }

    private static IntPtr Hook(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam)
    {
        if (!s_prevProcs.TryGetValue(hwnd, out var prev))
            return DefWindowProcW(hwnd, msg, wParam, lParam); // 정상 경로에선 오지 않는 방어선

        // 원래 체인이 복원 도중 native 정상 위치를 먼저 바꿀 수 있으므로 호출 전에 읽는다.
        // WinUI를 이 메시지 안에서 조회하지 않고 MainWindow가 전달한 두 정수만 사용한다.
        (int Width, int Height) normalSize = default;
        if (msg == WmGetMinMaxInfo) s_normalSizes.TryGetValue(hwnd, out normalSize);

        // 원래 프로시저(XAML/AppWindow 체인)를 먼저 태우고 나서 하한을 덮어쓴다 —
        // 프레젠터도 이 메시지로 최소/최대를 쓸 수 있으므로 나중에 쓰는 쪽이 이긴다.
        var result = CallWindowProcW(prev, hwnd, msg, wParam, lParam);

        if (msg == WmGetMinMaxInfo && lParam != IntPtr.Zero)
        {
            var info = Marshal.PtrToStructure<MINMAXINFO>(lParam);
            // A442: 시스템·모듈에서 시작한 최대화·최소화·전체화면 복귀도 같은 상한을 쓴다.
            // 사용자 resize/Snap 후에는 추적값도 교체되므로 과거 저장 크기를 강제하지 않는다.
            // 최대화 영역(ptMaxSize/ptMaxPosition)은 원래 체인이 계산한 값을 유지한다.
            info.ptMaxTrackSize.X = Math.Max(info.ptMaxTrackSize.X, normalSize.Width);
            info.ptMaxTrackSize.Y = Math.Max(info.ptMaxTrackSize.Y, normalSize.Height);
            if (s_restoreSizes.TryGetValue(hwnd, out var requested))
            {
                info.ptMaxTrackSize.X = Math.Max(info.ptMaxTrackSize.X, requested.Width);
                info.ptMaxTrackSize.Y = Math.Max(info.ptMaxTrackSize.Y, requested.Height);
            }
            var (minW, minH) = MinPhysical(hwnd);
            info.ptMinTrackSize.X = Math.Max(info.ptMinTrackSize.X, minW);
            info.ptMinTrackSize.Y = Math.Max(info.ptMinTrackSize.Y, minH);
            Marshal.StructureToPtr(info, lParam, fDeleteOld: false);
        }
        else if (msg == WmNcDestroy)
        {
            s_prevProcs.Remove(hwnd); // 창 소멸 — 이후 이 HWND로 메시지는 오지 않는다
            s_restoreSizes.Remove(hwnd);
            s_normalSizes.Remove(hwnd);
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
