using System.Runtime.InteropServices;
using KOTU.Module.Hardware;
using GdiColor = System.Drawing.Color;

namespace KOTU.App;

/// <summary>
/// 선택 센서 트레이 아이콘(A18) — 프로세스당 1개. 창별 TrayIcon과 별개로,
/// 사용자가 하드웨어 뷰 카드에서 고른 센서(최대 2개, 기본 CPU 온도/전력)의 값을
/// 16px 아이콘 한 개에 위아래 두 줄로 그려 보여준다(사용자 확정 방식).
/// A70: 데이터 원천 = **전역 1벌(마지막 커밋)** — 창마다 런타임 선택이 달라도(인스턴스 분리)
/// 마지막에 조작한 창의 선택이 여기 표시된다. 창별 아이콘으로의 이관·이 아이콘 폐지는 A101의 일.
///
/// - 표시 시점: 앱 실행 중 항상(사용자 확정) — 선택이 1개 이상이면 하드웨어 창이 없어도
///   센서 전용 구독(HardwareModule.SubscribeSensors)으로 폴러를 깨워 둔다.
///   이때 WMI 스펙 수집은 돌지 않는다(HardwareModule이 뷰 구독과 구분). 선택 0개면 구독 해제 —
///   아이콘도 폴링도 사라져 비용 0.
/// - 렌더링: System.Drawing(GDI+)으로 시스템 스몰 아이콘 크기에 동적 렌더.
///   글자 색 = 채널 액센트, 배경 = 반투명 다크 배지(밝은 작업표시줄에서도 대비 확보).
///   표기는 SensorChannels.FormatCompact("62°"·"45W"·"4.6"·"1.4k").
/// - 갱신: 폴링은 200ms지만 표기 문자열이 바뀔 때만 아이콘을 다시 그린다(반올림 덕에 드묾).
/// - 좌클릭 = H/W 창 열기/활성화, 우클릭 = 메뉴(열기/트레이 숨김). 툴팁 = 전체 값.
/// - UI 스레드에서 생성해야 한다(숨은 창의 메시지 루프). 센서 콜백은 워커 스레드 →
///   문자열 키 비교 후 필요할 때만 UI 디스패처로 넘긴다.
/// </summary>
internal sealed class SensorTray : IDisposable
{
    // ---------- 상수 (TrayIcon.cs와 동일 규약) ----------
    private const uint WmTrayCallback = 0x8002;          // WM_APP + 2 (창 트레이와 구분)
    private const uint NifMessage = 0x01, NifIcon = 0x02, NifTip = 0x04;
    private const uint NimAdd = 0, NimModify = 1, NimDelete = 2;
    private const uint WmLButtonUp = 0x0202, WmRButtonUp = 0x0205;
    private const uint MfString = 0x0, MfSeparator = 0x800;
    private const uint TpmReturnCmd = 0x0100, TpmRightButton = 0x0002;
    private const uint WsPopup = 0x80000000;
    private const int CmdOpen = 1, CmdHide = 2;
    private const int SmCxSmIcon = 49;

    private readonly WindowManager _manager;
    private readonly Microsoft.UI.Dispatching.DispatcherQueue _dispatcher;
    private readonly string _className;
    private readonly WndProcDelegate _wndProc; // 델리게이트 GC 방지 — 반드시 필드로 유지
    private readonly uint _taskbarCreatedMsg;
    private readonly IntPtr _hwnd;
    private IntPtr _hIcon;
    private string _tip = Branding.AppName + " sensors";
    private bool _added;
    private volatile bool _disposed;

    private IDisposable? _subscription;          // 센서 전용 폴러 구독 (선택 0개면 null)
    private SensorFrame _lastFrame = SensorFrame.Empty;
    private volatile string _lastKey = "";       // 마지막으로 그린 표기 — 같으면 재렌더 생략

    private static SensorTray? _instance;

    /// <summary>앱 시작 시 1회(UI 스레드) — 프로세스 싱글턴 생성.</summary>
    public static void Initialize(WindowManager manager) => _instance ??= new SensorTray(manager);

    private SensorTray(WindowManager manager)
    {
        _manager = manager;
        _dispatcher = Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread();

        _taskbarCreatedMsg = RegisterWindowMessageW("TaskbarCreated");
        // A100: 결정적 식별 — 랜덤 접미사가 실행마다 새 NotifyIconSettings 항목(기본 '끔')을
        // 만들던 원인이라 고정 이름으로 교체. 프로세스당 1개 싱글턴(_instance ??=)이라 충돌 없음.
        // uID는 종전 2 유지(TrayIcon의 100번대와 대역 분리 — 그쪽 주석 참조).
        _className = Branding.AppName + "SensorTrayWnd";
        _wndProc = WndProc;
        var hInstance = GetModuleHandleW(null);
        var wc = new WNDCLASSW
        {
            lpfnWndProc = Marshal.GetFunctionPointerForDelegate(_wndProc),
            hInstance = hInstance,
            lpszClassName = _className,
        };
        // TrayIcon(A100)과 같은 방어: 등록 실패 시 잔재 해제 후 재시도 → 랜덤 접미사 폴백.
        if (RegisterClassW(ref wc) == 0)
        {
            _ = UnregisterClassW(_className, hInstance);
            if (RegisterClassW(ref wc) == 0)
            {
                _className = Branding.AppName + "SensorTrayWnd_" + Guid.NewGuid().ToString("N");
                wc.lpszClassName = _className;
                _ = RegisterClassW(ref wc);
            }
        }
        _hwnd = CreateWindowExW(0, _className, string.Empty, WsPopup,
            0, 0, 0, 0, IntPtr.Zero, IntPtr.Zero, hInstance, IntPtr.Zero);

        TraySensors.Changed += OnSelectionChanged;
        // 어떤 종료 경로(모든 창 닫기·관리자 승격 재시작 등)든 아이콘이 트레이에 남지 않게.
        AppDomain.CurrentDomain.ProcessExit += (_, _) => Dispose();
        OnSelectionChanged(); // 저장된 선택(기본 CPU 온도/전력)으로 즉시 표시
    }

    // ---------- 선택·프레임 반영 ----------

    /// <summary>선택 변경(UI 스레드, 카드 클릭·트레이 메뉴) — 구독·아이콘 유무를 맞추고 다시 그린다.</summary>
    private void OnSelectionChanged()
    {
        if (_disposed) return;
        if (TraySensors.Selected.Count == 0)
        {
            _subscription?.Dispose();
            _subscription = null;
            RemoveIcon();
            return;
        }
        _subscription ??= HardwareModule.SubscribeSensors(OnFrame);
        _lastKey = "";
        Render(_lastFrame); // 값이 오기 전엔 "—"로라도 즉시 표시
    }

    /// <summary>폴러 콜백(워커 스레드). 표기 문자열이 지난번과 같으면 아무것도 안 한다.</summary>
    private void OnFrame(SensorFrame frame)
    {
        if (_disposed) return;
        _lastFrame = frame;
        var key = ComposeKey(frame);
        if (key == _lastKey) return;
        _lastKey = key;
        _dispatcher.TryEnqueue(() => Render(_lastFrame));
    }

    private static string ComposeKey(SensorFrame frame)
    {
        var parts = new List<string>();
        foreach (var id in TraySensors.Selected)
        {
            if (SensorChannels.ById(id) is not { } channel) continue;
            var value = frame.Timestamp == DateTime.MinValue ? null : channel.Select(frame);
            parts.Add($"{id}={(value is { } v ? channel.FormatCompact(v) : "—")}");
        }
        return string.Join('|', parts);
    }

    /// <summary>UI 스레드: 현재 선택·프레임으로 아이콘 비트맵을 다시 그려 교체한다.</summary>
    private void Render(SensorFrame frame)
    {
        if (_disposed || TraySensors.Selected.Count == 0) return;

        var lines = new List<(string Text, GdiColor Color)>();
        var tips = new List<string>();
        foreach (var id in TraySensors.Selected)
        {
            if (SensorChannels.ById(id) is not { } channel) continue;
            var value = frame.Timestamp == DateTime.MinValue ? null : channel.Select(frame);
            var accent = channel.Accent;
            lines.Add((value is { } v ? channel.FormatCompact(v) : "—",
                Lighten(GdiColor.FromArgb(accent.R, accent.G, accent.B))));
            tips.Add($"{channel.Title} {(value is { } fv ? channel.FormatFull(fv) : "—")}");
        }
        if (lines.Count == 0) { RemoveIcon(); return; }

        var tip = string.Join("  ·  ", tips);
        _tip = tip.Length > 127 ? tip[..127] : tip;

        IntPtr icon;
        try
        {
            icon = CreateSensorIcon(lines);
        }
        catch
        {
            return; // GDI 리소스 고갈 등 일시 실패 — 다음 값 변화 때 다시 그린다
        }
        var old = _hIcon;
        _hIcon = icon;
        // A100 ②: 승격 요청은 첫 NimAdd 전이에서만 — Render는 200ms 폴링으로 수시 불리므로
        // NimModify 경로에서 요청하면 스캔 태스크가 무한 증식한다.
        var firstAdd = !_added;
        AddOrUpdate(_added ? NimModify : NimAdd);
        _added = true;
        if (firstAdd) Integration.TrayPromotion.Request();
        if (old != IntPtr.Zero) _ = DestroyIcon(old);
    }

    private void RemoveIcon()
    {
        if (_added)
        {
            var data = MakeData();
            _ = Shell_NotifyIconW(NimDelete, ref data);
            _added = false;
        }
        if (_hIcon != IntPtr.Zero)
        {
            _ = DestroyIcon(_hIcon);
            _hIcon = IntPtr.Zero;
        }
        _lastKey = "";
    }

    // ---------- 아이콘 렌더링 (GDI+) ----------

    /// <summary>어두운 배지 배경 위에서 읽히도록 채널 색을 살짝 밝힌다.</summary>
    private static GdiColor Lighten(GdiColor c) => GdiColor.FromArgb(
        c.R + (255 - c.R) * 30 / 100,
        c.G + (255 - c.G) * 30 / 100,
        c.B + (255 - c.B) * 30 / 100);

    /// <summary>
    /// 값 1~2줄을 시스템 스몰 아이콘 크기에 그린 HICON을 만든다(호출자가 DestroyIcon).
    /// 두 줄이면 위아래 절반씩, 한 줄이면 중앙에 크게. 폭을 넘치면 글꼴을 줄여서 맞춘다.
    /// </summary>
    private static IntPtr CreateSensorIcon(IReadOnlyList<(string Text, GdiColor Color)> lines)
    {
        var size = Math.Max(16, GetSystemMetrics(SmCxSmIcon));
        using var bitmap = new System.Drawing.Bitmap(size, size,
            System.Drawing.Imaging.PixelFormat.Format32bppArgb);
        using (var g = System.Drawing.Graphics.FromImage(bitmap))
        {
            g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            // ClearType은 배경 없는 32bpp에 알파를 망가뜨린다 — 반드시 회색조 AA를 쓴다.
            g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAliasGridFit;

            using (var path = RoundedRect(size, size, Math.Max(2, size * 3 / 16)))
            using (var background = new System.Drawing.SolidBrush(GdiColor.FromArgb(0xE0, 0x20, 0x20, 0x24)))
                g.FillPath(background, path);

            var lineHeight = (float)size / lines.Count;
            using var format = new System.Drawing.StringFormat(System.Drawing.StringFormat.GenericTypographic)
            {
                Alignment = System.Drawing.StringAlignment.Center,
                LineAlignment = System.Drawing.StringAlignment.Center,
                FormatFlags = System.Drawing.StringFormatFlags.NoWrap,
            };
            for (var i = 0; i < lines.Count; i++)
            {
                var fontPx = lines.Count == 1 ? size * 0.58f : lineHeight * 0.94f;
                var font = MakeFont(fontPx);
                // 폭 초과 시 5px까지 축소 ("1.4k"·"104" 같은 넉 자 표기 대비)
                while (fontPx > 5f
                       && g.MeasureString(lines[i].Text, font, int.MaxValue, format).Width > size - 1)
                {
                    font.Dispose();
                    fontPx -= 0.5f;
                    font = MakeFont(fontPx);
                }
                using (font)
                using (var brush = new System.Drawing.SolidBrush(lines[i].Color))
                {
                    g.DrawString(lines[i].Text, font, brush,
                        new System.Drawing.RectangleF(0, i * lineHeight, size, lineHeight), format);
                }
            }
        }
        return bitmap.GetHicon();
    }

    private static System.Drawing.Font MakeFont(float px)
        => new("Segoe UI", px, System.Drawing.FontStyle.Bold, System.Drawing.GraphicsUnit.Pixel);

    private static System.Drawing.Drawing2D.GraphicsPath RoundedRect(int w, int h, int r)
    {
        var d = r * 2;
        var path = new System.Drawing.Drawing2D.GraphicsPath();
        path.AddArc(0, 0, d, d, 180, 90);
        path.AddArc(w - d, 0, d, d, 270, 90);
        path.AddArc(w - d, h - d, d, d, 0, 90);
        path.AddArc(0, h - d, d, d, 90, 90);
        path.CloseFigure();
        return path;
    }

    // ---------- 알림 영역·메뉴 ----------

    private NOTIFYICONDATAW MakeData() => new()
    {
        cbSize = (uint)Marshal.SizeOf<NOTIFYICONDATAW>(),
        hWnd = _hwnd,
        uID = 2, // 창 트레이(A100부터 100번대 슬롯)와 구분 — hwnd가 달라 실제로는 독립이지만 명시적으로
        uFlags = NifMessage | NifIcon | NifTip,
        uCallbackMessage = WmTrayCallback,
        hIcon = _hIcon,
        szTip = _tip,
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
                    _manager.ShowHardware();
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
            _ = AppendMenuW(menu, MfString, CmdOpen, "Open H/W monitor");
            _ = AppendMenuW(menu, MfSeparator, 0, null);
            _ = AppendMenuW(menu, MfString, CmdHide, "Hide tray sensors");
            _ = SetMenuDefaultItem(menu, CmdOpen, 0);

            _ = SetForegroundWindow(_hwnd);
            var cmd = TrackPopupMenuEx(menu, TpmReturnCmd | TpmRightButton,
                pt.X, pt.Y, _hwnd, IntPtr.Zero);

            switch (cmd)
            {
                case CmdOpen: _manager.ShowHardware(); break;
                case CmdHide: TraySensors.Clear(); break; // Changed → 구독 해제·아이콘 제거
            }
        }
        finally
        {
            _ = DestroyMenu(menu);
        }
    }

    /// <summary>
    /// 아이콘 제거·구독 해제. ProcessExit(비 UI 스레드)에서도 불릴 수 있어
    /// 알림 영역 제거(스레드 무관)를 먼저, 창 정리는 시도만 한다.
    /// </summary>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        TraySensors.Changed -= OnSelectionChanged;
        _subscription?.Dispose();
        _subscription = null;
        RemoveIcon();
        try
        {
            if (_hwnd != IntPtr.Zero) _ = DestroyWindow(_hwnd); // 생성 스레드가 아니면 실패해도 무방
            _ = UnregisterClassW(_className, GetModuleHandleW(null));
        }
        catch
        {
            // 종료 경로 — 창·클래스 정리는 프로세스 종료가 대신한다.
        }
    }

    // ---------- P/Invoke (TrayIcon.cs와 동일 서명) ----------

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
