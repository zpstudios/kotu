using System.Runtime.InteropServices;

namespace KOTU.App;

/// <summary>
/// 창마다 하나씩 작업표시줄 우측 하단(알림 영역)에 표시되는 미니 아이콘.
/// Shell_NotifyIcon P/Invoke 직접 구현 — 외부 패키지 의존을 피하고,
/// 콜백 수신용 숨은 창(비표시 WS_POPUP)을 창마다 하나 만든다.
/// (message-only 창은 TaskbarCreated 브로드캐스트를 못 받으므로 쓰지 않는다.)
/// 기본 동작: 좌클릭=창 활성화, 우클릭=메뉴. 아이콘 모양은 추후 결정 — 지금은 앱 아이콘.
/// UI 스레드에서 생성해야 하며, 이벤트도 같은 스레드의 메시지 루프에서 올라온다.
/// </summary>
internal sealed class TrayIcon : IDisposable
{
    // ---------- 상수 ----------
    private const uint WmTrayCallback = 0x8001;          // WM_APP + 1
    private const uint NifMessage = 0x01, NifIcon = 0x02, NifTip = 0x04;
    private const uint NimAdd = 0, NimModify = 1, NimDelete = 2;
    private const uint WmLButtonUp = 0x0202, WmRButtonUp = 0x0205;
    private const uint MfString = 0x0, MfSeparator = 0x800;
    private const uint TpmReturnCmd = 0x0100, TpmRightButton = 0x0002;
    private const uint WsPopup = 0x80000000;
    private const int CmdActivate = 1, CmdClose = 2, CmdExitAll = 3, CmdMinimizeToTray = 4;
    private const uint ImageIcon = 1, LrLoadFromFile = 0x10;
    private const int SmCxSmIcon = 49, SmCySmIcon = 50;

    // A100: Win11은 guidItem 없는 아이콘의 식별을 exe 경로 + 창(클래스명 등) 정보로 해시해
    // HKCU\Control Panel\NotifyIconSettings 항목을 만든다 — 식별이 실행마다 바뀌면 설정 항목이
    // 증식하고 새 항목은 기본 '끔'이라 아이콘이 안 보인다. 그래서 클래스명·uID를 프로세스 내
    // 단조 증가 슬롯 번호로 결정화한다(창 생성 순서 = 슬롯 — 실행이 달라도 같은 순서로 열면 같은 식별).
    // 슬롯을 인스턴스 "번호"에 묶지 않는 이유: 번호는 창이 닫히면 재배정되지만(A2/A68) 클래스명은
    // 생성 후 못 바꾸고, 번호 재사용 시점에 옛 창의 클래스가 아직 살아 있어 등록이 충돌한다.
    // guidItem을 안 쓰는 이유: GUID가 exe 경로에 묶여 개발 빌드/설치본 병행 시 NIM_ADD가 실패한다.
    private static int _slotSeq;

    private readonly uint _uid;
    private readonly string _className;

    /// <summary>
    /// 이 아이콘의 A100 슬롯 번호(창 생성 단조 시퀀스·수명 불변). A105 ①이 창별
    /// AppUserModelID의 시퀀스로 재사용한다 — 트레이 식별과 태스크바 식별이 한 시퀀스로 움직인다.
    /// </summary>
    internal int Slot { get; }
    private readonly WndProcDelegate _wndProc; // 델리게이트 GC 방지 — 반드시 필드로 유지
    private readonly uint _taskbarCreatedMsg;
    private readonly IntPtr _hwnd;
    private readonly EventHandler _processExitHandler; // 창 Closed를 못 거치는 종료 경로 방어 (A54)
    private IntPtr _hIcon;
    private bool _ownsIcon;
    private string _tip = Branding.AppName;
    private bool _added;
    private volatile bool _disposed; // ProcessExit(비 UI 스레드)와 창 Closed 양쪽에서 읽힌다 (A54 — 구 SensorTray와 동일 규약)

    /// <summary>트레이 아이콘 좌클릭(또는 메뉴의 '창 활성화').</summary>
    public event Action? ActivateRequested;

    /// <summary>메뉴의 '이 창 닫기'.</summary>
    public event Action? CloseRequested;

    /// <summary>메뉴의 'Exit KOTU' (모든 창 닫기).</summary>
    public event Action? ExitAllRequested;

    /// <summary>A218: 우클릭 메뉴 "Minimize to tray" — 이 창을 트레이로 숨긴다(명시 호출 전용).</summary>
    public event Action? MinimizeToTrayRequested;

    /// <param name="iconPath">.ico 파일 경로. 없거나 로드 실패 시 시스템 기본 아이콘.</param>
    public TrayIcon(string? iconPath)
    {
        _taskbarCreatedMsg = RegisterWindowMessageW("TaskbarCreated");
        // A100: 결정적 식별 — 슬롯 번호 기반 클래스명 + uID(100번대 = 구 SensorTray의 2와 대역 분리.
        // 구 창 트레이가 쓰던 1도 피해서, 남아 있는 옛 NotifyIconSettings 항목과 섞이지 않는다.
        // A101에서 SensorTray가 폐지됐어도 100번대는 유지 — 사용자 기기에 남은 옛 항목과의
        // 분리라는 근거가 그대로다).
        var slot = System.Threading.Interlocked.Increment(ref _slotSeq);
        Slot = slot;
        _uid = (uint)(100 + slot);
        _className = Branding.AppName + "TrayWnd_" + slot;
        _wndProc = WndProc;

        var hInstance = GetModuleHandleW(null);
        var wc = new WNDCLASSW
        {
            lpfnWndProc = Marshal.GetFunctionPointerForDelegate(_wndProc),
            hInstance = hInstance,
            lpszClassName = _className,
        };
        // 클래스 등록은 프로세스 스코프 — 단조 증가 슬롯이라 같은 프로세스 안에서 충돌할 일은
        // 없지만(재사용 없음), 만에 하나(이전 인스턴스의 해제 실패 잔재)를 위해 1회 지우고 재시도,
        // 그래도 실패하면 구 방식(랜덤 접미사)으로 폴백한다 — 아이콘이 아예 안 뜨는 것보다
        // 식별이 흔들리는 쪽이 낫다(A100 취지의 역순 폴백).
        if (RegisterClassW(ref wc) == 0)
        {
            _ = UnregisterClassW(_className, hInstance);
            if (RegisterClassW(ref wc) == 0)
            {
                _className = Branding.AppName + "TrayWnd_" + slot + "_" + Guid.NewGuid().ToString("N");
                wc.lpszClassName = _className;
                _ = RegisterClassW(ref wc);
            }
        }
        _hwnd = CreateWindowExW(0, _className, string.Empty, WsPopup,
            0, 0, 0, 0, IntPtr.Zero, IntPtr.Zero, hInstance, IntPtr.Zero);

        // A79: 첫 아이콘부터 브랜드 표식을 반영한다 — 생성자에 오는 것은 언제나 중립 아이콘(①).
        // 표식이 꺼져 있으면 0이 와서 지금까지처럼 파일을 그대로 로드한다.
        var branded = iconPath is null
            ? IntPtr.Zero
            : BrandIcons.GetBranded(iconPath, Math.Max(16, GetSystemMetrics(SmCxSmIcon)), null);
        if (branded != IntPtr.Zero)
        {
            _hIcon = branded;   // 프로세스 수명 캐시 소유 — 여기서 파괴하지 않는다
            _ownsIcon = false;
        }
        else
        {
            (_hIcon, _ownsIcon) = LoadTrayIcon(iconPath);
        }
        AddOrUpdate(NimAdd);
        _added = true;
        // A100 ②: 새 슬롯의 NotifyIconSettings 항목은 기본 '끔'으로 생긴다 — 자기 항목을 승격.
        Integration.TrayPromotion.Request();

        // A54 감사: 아이콘 제거는 창 Closed → Dispose 하나뿐이라, 창을 닫지 않고 프로세스가
        // 내려가는 경로(관리자 승격 재실행의 Application.Exit 등)에서 아이콘이 남을 수 있다.
        // 구 A18 SensorTray가 같은 이유로 쓰던 방어선을 창별 아이콘에도 건다(중복 호출은 무해 — _disposed 가드).
        _processExitHandler = (_, _) => Dispose();
        AppDomain.CurrentDomain.ProcessExit += _processExitHandler;
    }

    /// <summary>
    /// 트레이 아이콘 교체 — 현재 모듈 색 아이콘 표시(v0.26.0). 로드 실패 시 기존 유지.
    /// ring이 있으면(A102 — 모듈 색, 창 개수 무관) 그 색 테두리를 합성한 아이콘을 쓴다.
    /// 합성 핸들은 InstanceIcon의 프로세스 수명 캐시 소유(owns=false — 여기서 파괴하지 않음).
    /// 합성 실패 시 무테두리 원본으로 폴백.
    /// ※ 이 경로는 표시할 값이 없는 화면(설정·미지원 파일 안내)의 중립 아이콘 폴백으로만 쓴다 —
    ///   값 텍스트를 그리는 경로는 <see cref="SetRenderedIcon"/>이다.
    /// ※ A102(v0.130.0): 원형 번호 배지는 창 아이콘에서도 사라졌다(렌더 코드째 제거) —
    ///   번호는 창 제목의 접두 숫자(A103)가 전담한다.
    /// ※ 정보(H/W) 모듈 화면은 값 표시가 우선이라 테두리를 적용하지 않는다(A102 —
    ///   Branding.IconRing이 null을 준다. 구 A18 SensorTray의 무테두리 규칙을 승계한 것).
    /// ※ A79(v0.119.0): 브랜드 표식(BrandIcons)이 켜져 있으면 링이 없어도 합성본을 쓴다.
    ///   값 텍스트를 그리는 <see cref="SetRenderedIcon"/> 경로는 건드리지 않는다 —
    ///   ①(중립 발바닥)이 A54의 트레이 글자를 덮으면 안 되기 때문.
    /// </summary>
    /// <param name="accent">현재 아이콘의 모듈 색(중립 아이콘이면 null) — A79 표식 판단용.</param>
    /// <param name="ring">테두리 링 색(A102, Branding.IconRing) — null이면 링 없음.</param>
    public void SetIcon(string? iconPath, Windows.UI.Color? accent = null, Windows.UI.Color? ring = null)
    {
        if (_disposed) return;
        var icon = IntPtr.Zero;
        var owns = false;
        var size = Math.Max(16, GetSystemMetrics(SmCxSmIcon));
        if (iconPath is not null)
        {
            // label: null — 트레이 글자는 TrayStatusIcon(A54) 소관이라 이 폴백 경로는 무글자.
            // A105 ②의 3자 표기는 창(태스크바) 아이콘 전용이다(트레이 무수정 — 구현 시 결정).
            icon = ring is { } ringColor
                ? InstanceIcon.GetComposed(iconPath, size, accent, ringColor, label: null)
                : BrandIcons.GetBranded(iconPath, size, accent);
        }
        if (icon == IntPtr.Zero) (icon, owns) = LoadTrayIcon(iconPath);
        Swap(icon, owns);
    }

    /// <summary>
    /// 값 텍스트를 그린 아이콘으로 교체한다(A54 — <see cref="TrayStatusIcon"/>이 만든 HICON).
    /// 핸들 소유권을 이 인스턴스가 가져가며, 다음 교체·Dispose 때 DestroyIcon 한다
    /// (창마다 곱해지는 GDI 핸들이라 캐시하지 않고 즉시 회수하는 쪽을 택했다).
    /// </summary>
    public void SetRenderedIcon(IntPtr icon)
    {
        if (_disposed || icon == IntPtr.Zero)
        {
            if (icon != IntPtr.Zero) _ = DestroyIcon(icon); // 이미 정리된 뒤라면 넘겨받은 핸들만 회수
            return;
        }
        Swap(icon, owns: true);
    }

    /// <summary>아이콘 핸들 교체 공용부 — 알림 영역 갱신 후 이전 핸들을 소유했을 때만 파괴한다.</summary>
    private void Swap(IntPtr icon, bool owns)
    {
        if (icon == IntPtr.Zero) return;
        if (icon == _hIcon) return; // 캐시 재사용으로 같은 핸들이면 교체할 것 없음

        var oldIcon = _hIcon;
        var oldOwns = _ownsIcon;
        _hIcon = icon;
        _ownsIcon = owns;
        if (_added) AddOrUpdate(NimModify);
        if (oldOwns && oldIcon != IntPtr.Zero) _ = DestroyIcon(oldIcon);
    }

    /// <summary>알림 영역 툴팁 갱신(127자 제한). 열린 파일명·모듈 표시에 쓴다.</summary>
    public void SetTooltip(string text)
    {
        _tip = text.Length > 127 ? text[..127] : text;
        if (_added && !_disposed) AddOrUpdate(NimModify);
    }

    /// <summary>
    /// 알림 영역에서 아이콘을 지우고 창·클래스·아이콘 핸들을 정리한다.
    /// 창 Closed와 ProcessExit(비 UI 스레드) 양쪽에서 불릴 수 있다 — 알림 영역 제거(스레드 무관)를
    /// 먼저 하고, 창 정리는 실패해도 무방하다(구 A18 SensorTray.Dispose에서 온 규칙).
    /// </summary>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        AppDomain.CurrentDomain.ProcessExit -= _processExitHandler;

        if (_added)
        {
            var data = MakeData();
            _ = Shell_NotifyIconW(NimDelete, ref data);
            _added = false;
        }
        try
        {
            if (_hwnd != IntPtr.Zero) _ = DestroyWindow(_hwnd); // 생성 스레드가 아니면 실패해도 무방
            _ = UnregisterClassW(_className, GetModuleHandleW(null));
        }
        catch
        {
            // 종료 경로 — 창·클래스 정리는 프로세스 종료가 대신한다.
        }
        if (_ownsIcon && _hIcon != IntPtr.Zero) _ = DestroyIcon(_hIcon);
    }

    // ---------- 내부 구현 ----------

    private static (IntPtr icon, bool owns) LoadTrayIcon(string? iconPath)
    {
        if (iconPath is not null && File.Exists(iconPath))
        {
            // 시스템 스몰 아이콘 크기(DPI 반영)로 파일에서 로드
            var handle = LoadImageW(IntPtr.Zero, iconPath, ImageIcon,
                GetSystemMetrics(SmCxSmIcon), GetSystemMetrics(SmCySmIcon), LrLoadFromFile);
            if (handle != IntPtr.Zero) return (handle, true);
        }
        // 폴백: 시스템 기본 애플리케이션 아이콘 (공유 리소스 — 파괴 금지)
        return (LoadIconW(IntPtr.Zero, new IntPtr(32512) /* IDI_APPLICATION */), false);
    }

    private NOTIFYICONDATAW MakeData() => new()
    {
        cbSize = (uint)Marshal.SizeOf<NOTIFYICONDATAW>(),
        hWnd = _hwnd,
        uID = _uid, // A100: 슬롯 기반(100+n) — 구 고정값 1은 폐기
        uFlags = NifMessage | NifIcon | NifTip,
        uCallbackMessage = WmTrayCallback,
        hIcon = _hIcon,
        szTip = _tip,
        // ByValTStr 필드는 null이면 마샬링에서 예외 — 안 쓰는 필드도 빈 문자열로 채운다
        szInfo = string.Empty,
        szInfoTitle = string.Empty,
    };

    private void AddOrUpdate(uint command)
    {
        var data = MakeData();
        _ = Shell_NotifyIconW(command, ref data);
    }

    private IntPtr WndProc(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam)
    {
        if (msg == WmTrayCallback)
        {
            switch ((uint)(lParam.ToInt64() & 0xFFFF))
            {
                case WmLButtonUp:
                    ActivateRequested?.Invoke();
                    break;
                case WmRButtonUp:
                    ShowContextMenu();
                    break;
            }
            return IntPtr.Zero;
        }

        // 탐색기 재시작 시 알림 영역이 초기화되므로 아이콘을 다시 등록한다
        if (msg == _taskbarCreatedMsg && _added && !_disposed)
        {
            AddOrUpdate(NimAdd);
            // A100 ②: 재등록으로 항목이 새로 생겼을 수 있다 — 승격도 다시 보장.
            Integration.TrayPromotion.Request();
            return IntPtr.Zero;
        }

        return DefWindowProcW(hwnd, msg, wParam, lParam);
    }

    private void ShowContextMenu()
    {
        _ = GetCursorPos(out var pt);
        var menu = CreatePopupMenu();
        try
        {
            _ = AppendMenuW(menu, MfString, CmdActivate, "Activate window");
            // A218: 트레이 숨김은 자동(A69/A185 — 폐지)이 아니라 이 명시 항목으로만 들어간다.
            _ = AppendMenuW(menu, MfString, CmdMinimizeToTray, "Minimize to tray");
            _ = AppendMenuW(menu, MfString, CmdClose, "Close this window");
            _ = AppendMenuW(menu, MfSeparator, 0, null);
            _ = AppendMenuW(menu, MfString, CmdExitAll, $"Exit {Branding.AppName}");
            _ = SetMenuDefaultItem(menu, CmdActivate, 0);

            // 메뉴 밖 클릭으로 닫히게 하려면 먼저 포그라운드가 되어야 한다(Win32 관례)
            _ = SetForegroundWindow(_hwnd);
            var cmd = TrackPopupMenuEx(menu, TpmReturnCmd | TpmRightButton,
                pt.X, pt.Y, _hwnd, IntPtr.Zero);

            switch (cmd)
            {
                case CmdActivate: ActivateRequested?.Invoke(); break;
                case CmdMinimizeToTray: MinimizeToTrayRequested?.Invoke(); break;
                case CmdClose: CloseRequested?.Invoke(); break;
                case CmdExitAll: ExitAllRequested?.Invoke(); break;
            }
        }
        finally
        {
            _ = DestroyMenu(menu);
        }
    }

    // ---------- P/Invoke ----------

    private delegate IntPtr WndProcDelegate(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WNDCLASSW
    {
        public uint style;
        public IntPtr lpfnWndProc;
        public int cbClsExtra;
        public int cbWndExtra;
        public IntPtr hInstance;
        public IntPtr hIcon;
        public IntPtr hCursor;
        public IntPtr hbrBackground;
        [MarshalAs(UnmanagedType.LPWStr)] public string? lpszMenuName;
        [MarshalAs(UnmanagedType.LPWStr)] public string lpszClassName;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct NOTIFYICONDATAW
    {
        public uint cbSize;
        public IntPtr hWnd;
        public uint uID;
        public uint uFlags;
        public uint uCallbackMessage;
        public IntPtr hIcon;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string szTip;
        public uint dwState;
        public uint dwStateMask;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] public string szInfo;
        public uint uTimeoutOrVersion;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)] public string szInfoTitle;
        public uint dwInfoFlags;
        public Guid guidItem;
        public IntPtr hBalloonIcon;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT { public int X; public int Y; }

    [DllImport("shell32", CharSet = CharSet.Unicode)]
    private static extern bool Shell_NotifyIconW(uint dwMessage, ref NOTIFYICONDATAW lpData);

    [DllImport("user32", CharSet = CharSet.Unicode)]
    private static extern ushort RegisterClassW(ref WNDCLASSW lpWndClass);

    [DllImport("user32", CharSet = CharSet.Unicode)]
    private static extern bool UnregisterClassW(string lpClassName, IntPtr hInstance);

    [DllImport("user32", CharSet = CharSet.Unicode)]
    private static extern IntPtr CreateWindowExW(uint dwExStyle, string lpClassName,
        string lpWindowName, uint dwStyle, int x, int y, int nWidth, int nHeight,
        IntPtr hWndParent, IntPtr hMenu, IntPtr hInstance, IntPtr lpParam);

    [DllImport("user32")]
    private static extern bool DestroyWindow(IntPtr hWnd);

    [DllImport("user32", CharSet = CharSet.Unicode)]
    private static extern IntPtr DefWindowProcW(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32", CharSet = CharSet.Unicode)]
    private static extern uint RegisterWindowMessageW(string lpString);

    [DllImport("kernel32", CharSet = CharSet.Unicode)]
    private static extern IntPtr GetModuleHandleW(string? lpModuleName);

    [DllImport("user32", CharSet = CharSet.Unicode)]
    private static extern IntPtr LoadImageW(IntPtr hInst, string name, uint type,
        int cx, int cy, uint fuLoad);

    [DllImport("user32")]
    private static extern IntPtr LoadIconW(IntPtr hInstance, IntPtr lpIconName);

    [DllImport("user32")]
    private static extern bool DestroyIcon(IntPtr hIcon);

    [DllImport("user32")]
    private static extern int GetSystemMetrics(int nIndex);

    [DllImport("user32")]
    private static extern bool GetCursorPos(out POINT lpPoint);

    [DllImport("user32")]
    private static extern IntPtr CreatePopupMenu();

    [DllImport("user32", CharSet = CharSet.Unicode)]
    private static extern bool AppendMenuW(IntPtr hMenu, uint uFlags, nuint uIDNewItem, string? lpNewItem);

    [DllImport("user32")]
    private static extern bool SetMenuDefaultItem(IntPtr hMenu, uint uItem, uint fByPos);

    [DllImport("user32")]
    private static extern bool DestroyMenu(IntPtr hMenu);

    [DllImport("user32")]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport("user32")]
    private static extern int TrackPopupMenuEx(IntPtr hMenu, uint uFlags, int x, int y,
        IntPtr hwnd, IntPtr lptpm);
}
