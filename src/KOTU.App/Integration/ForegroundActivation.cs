using System.Runtime.InteropServices;

namespace KOTU.App.Integration;

/// <summary>
/// 외부 진입 창의 전면 전환 보조 (A228, v0.237.0).
///
/// 배경: OS는 백그라운드 프로세스가 포그라운드를 뺏는 것을 막는다(SetForegroundWindow 잠금).
/// 탐색기에서 문서를 열면 새 프로세스(리다이렉터)가 떠서 주 인스턴스로 활성화를 넘기고
/// 죽는데, 주 인스턴스가 백그라운드면 창을 만들어 Activate()해도 앞으로 나오지 못했다.
/// 포그라운드 권한은 현재 권한을 가진 프로세스가 AllowSetForegroundWindow로 넘겨줄 수 있고,
/// 리다이렉터는 탐색기가 방금 시작한 프로세스라 보통 그 권한을 갖고 있다.
///
/// 역할 2개(둘 다 실패 무해 — 전부 try/catch로 삼킨다):
/// ① <see cref="AllowNextForegroundChange"/> — 리다이렉터 쪽. 리다이렉트 직전에 주 인스턴스
///    PID로 권한을 이양한다.
/// ② <see cref="EnsureForeground"/> — 주 프로세스 쪽. Activate() 뒤에 SetForegroundWindow를
///    시도하고, 실패(권한 없음)면 작업표시줄 점멸(FlashWindowEx)로 후퇴한다.
///
/// AllowSetForegroundWindow·FlashWindowEx는 저장소 최초 도입 P/Invoke — 규약(모듈 프로젝트
/// DllImport 금지)대로 셸(KOTU.App)의 이 파일에 격리한다. SetForegroundWindow는 TrayIcon.cs에
/// 선례가 있다(트레이 메뉴용 — 창 대상이 아니라 재사용하지 않고 여기 별도로 둔다).
/// </summary>
internal static class ForegroundActivation
{
    // FLASHWINFO.dwFlags: 캡션+작업표시줄 버튼을 함께(FLASHW_ALL) 창이 전면으로 올 때까지
    // 점멸(FLASHW_TIMERNOFG — 사용자가 창을 클릭하면 스스로 멎는다).
    private const uint FlashwAll = 0x00000003;
    private const uint FlashwTimerNoFg = 0x0000000C;

    /// <summary>
    /// 리다이렉터(두 번째 프로세스) 쪽: 주 인스턴스(processId)에 다음 포그라운드 전환 권한을
    /// 넘긴다. RedirectActivationToAsync 직전에 부를 것. 실패해도 기능 저하일 뿐이라
    /// (창은 뜨되 주 프로세스 쪽이 점멸 폴백으로 후퇴) 조용히 무시한다.
    /// </summary>
    internal static void AllowNextForegroundChange(uint processId)
    {
        try
        {
            _ = AllowSetForegroundWindow(processId);
        }
        catch
        {
            // 권한 이양 실패 = 점멸 폴백으로 충분 — 리다이렉트 자체를 막으면 안 된다.
        }
    }

    /// <summary>
    /// 주 프로세스 쪽: 창을 실제 포그라운드로 올린다. AppWindow.Show/Restore와 Activate()가
    /// 끝난 뒤에 부를 것(MainWindow.BringToFront 말미). SetForegroundWindow가 성공하면 그걸로
    /// 끝이고(점멸 없음 — 반환값 판정), 실패하면 작업표시줄 점멸로 사용자 주의만 끈다.
    /// 비공식 트릭(AttachThreadInput·minimize/restore 강제)은 쓰지 않는다.
    /// </summary>
    internal static void EnsureForeground(Microsoft.UI.Xaml.Window window)
    {
        try
        {
            var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(window);
            if (SetForegroundWindow(hwnd)) return;

            var info = new FLASHWINFO
            {
                cbSize = (uint)Marshal.SizeOf<FLASHWINFO>(),
                hwnd = hwnd,
                dwFlags = FlashwAll | FlashwTimerNoFg,
                uCount = 0,
                dwTimeout = 0,
            };
            _ = FlashWindowEx(ref info);
        }
        catch
        {
            // 전면 전환은 보조 동작 — 실패해도 창 자체(Show·Activate)는 이미 떠 있다.
        }
    }

    // ---------- P/Invoke ----------

    [StructLayout(LayoutKind.Sequential)]
    private struct FLASHWINFO
    {
        public uint cbSize;
        public IntPtr hwnd;
        public uint dwFlags;
        public uint uCount;
        public uint dwTimeout;
    }

    [DllImport("user32")]
    private static extern bool AllowSetForegroundWindow(uint dwProcessId);

    [DllImport("user32")]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport("user32")]
    private static extern bool FlashWindowEx(ref FLASHWINFO pwfi);
}
