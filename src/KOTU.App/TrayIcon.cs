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
    private const int CmdActivate = 1, CmdClose = 2, CmdExitAll = 3;
    private const uint ImageIcon = 1, LrLoadFromFile = 0x10;
    private const int SmCxSmIcon = 49, SmCySmIcon = 50;

    private readonly string _className;
    private readonly WndProcDelegate _wndProc; // 델리게이트 GC 방지 — 반드시 필드로 유지
    private readonly uint _taskbarCreatedMsg;
    private readonly IntPtr _hwnd;
    private IntPtr _hIcon;
    private bool _ownsIcon;
    private string _tip = Branding.AppName;
    private bool _added;
    private bool _disposed;

    /// <summary>트레이 아이콘 좌클릭(또는 메뉴의 '창 활성화').</summary>
    public event Action? ActivateRequested;

    /// <summary>메뉴의 '이 창 닫기'.</summary>
    public event Action? CloseRequested;

    /// <summary>메뉴의 'Exit KOTU' (모든 창 닫기).</summary>
    public event Action? ExitAllRequested;

    /// <param name="iconPath">.ico 파일 경로. 없거나 로드 실패 시 시스템 기본 아이콘.</param>
    public TrayIcon(string? iconPath)
    {
        _taskbarCreatedMsg = RegisterWindowMessageW("TaskbarCreated");
        _className = Branding.AppName + "Tray_" + Guid.NewGuid().ToString("N");
        _wndProc = WndProc;

        var hInstance = GetModuleHandleW(null);
        var wc = new WNDCLASSW
        {
            lpfnWndProc = Marshal.GetFunctionPointerForDelegate(_wndProc),
            hInstance = hInstance,
            lpszClassName = _className,
        };
        RegisterClassW(ref wc);
        _hwnd = CreateWindowExW(0, _className, string.Empty, WsPopup,
            0, 0, 0, 0, IntPtr.Zero, IntPtr.Zero, hInstance, IntPtr.Zero);

        (_hIcon, _ownsIcon) = LoadTrayIcon(iconPath);
        AddOrUpdate(NimAdd);
        _added = true;
    }

    /// <summary>
    /// 트레이 아이콘 교체 — 현재 모듈 색 아이콘 표시(v0.26.0). 로드 실패 시 기존 유지.
    /// instanceNumber &gt; 0이면(창 2개 이상, A68) 인스턴스 색 테두리·원형 번호 배지를
    /// 합성한 아이콘을 쓴다. 합성 핸들은 InstanceIcon의 프로세스 수명 캐시 소유
    /// (owns=false — 여기서 파괴하지 않음). 합성 실패 시 무테두리 원본으로 폴백.
    /// ※ 센서 트레이(SensorTray, A18)는 값 표시가 우선이라 인스턴스 테두리를 적용하지 않는다.
    /// </summary>
    public void SetIcon(string? iconPath, int instanceNumber = 0)
    {
        if (_disposed) return;
        var icon = IntPtr.Zero;
        var owns = false;
        if (instanceNumber > 0 && iconPath is not null)
        {
            icon = InstanceIcon.GetComposed(iconPath, instanceNumber,
                Math.Max(16, GetSystemMetrics(SmCxSmIcon)));
        }
        if (icon == IntPtr.Zero) (icon, owns) = LoadTrayIcon(iconPath);
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

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        if (_added)
        {
            var data = MakeData();
            _ = Shell_NotifyIconW(NimDelete, ref data);
            _added = false;
        }
        if (_hwnd != IntPtr.Zero) _ = DestroyWindow(_hwnd);
        _ = UnregisterClassW(_className, GetModuleHandleW(null));
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
        uID = 1,
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
