using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.ApplicationModel.DataTransfer;
using Windows.System;
using KOTU.App.Controls;
using KOTU.App.Overlays;
using KOTU.Core.Cli;
using KOTU.Core.Contracts;
using KOTU.Core.Routing;
using KOTU.Core.Settings;

namespace KOTU.App;

public sealed partial class MainWindow : Window
{
    private static readonly string IconPath =
        Path.Combine(AppContext.BaseDirectory, "Assets", "app.ico");

    /// <summary>하단 바 고정 두께(A40) — XAML BottomBarRow 기본값 44와 같아야 한다.</summary>
    private const double BottomBarHeight = 44;

    private readonly FileTypeRouter _router;
    private readonly WindowManager _manager;
    private readonly TrayIcon _tray;
    private readonly ISettingsService _settings;
    private double _uiScaleFactor = 1.0; // 시스템 DPI 대비 상대 배율 (1.0 = 오버라이드 없음)
    private bool _xamlRootHooked;

    // ---- 내장 탐색기 + 좌/우 오버레이 입력 상태 머신 (A58 — v0.25.0 홀드·v0.32.0 2연타 고정 대체) ----
    // 키 할당(부록 B 26번): Alt = 좌측 파일 리스트 / Shift = 우측 정보 (Ctrl은 오버레이에서 손 뗌 —
    //   v0.45.0 Ctrl→Shift, A32에서 Ctrl 회귀를 거쳐 A58에서 Shift로 확정).
    // 사이드마다 4상태: Closed(닫힘) / Holding(키 홀드 — 반투명 덮기, 메인 크기 불변) /
    //   TranslucentPinned(2초 이상 홀드 — 키를 떼도 반투명 유지) /
    //   OpaqueDocked(2연타 — 불투명 + 메인을 반대쪽 7*로 축소, 양쪽이면 3:4:3).
    // 고정 해제는 반투명 고정·불투명 밀어내기 둘 다 2연타. 홀드 판정은 다른 키·포인터(클릭·휠)가
    // 함께 개입하면 취소된다(OS Alt 메뉴 모드와 같은 규칙 — A84 Shift 조합 단축키·Shift+휠 줌 안전장치).
    private IModule? _currentModule;      // 지금 보여주는 모듈 (탐색기 필터·리스트 오버레이에 사용)
    private string? _currentFilePath;     // 현재 콘텐츠 파일 (null = 빈 상태 → 탐색기 표시)
    private ExplorerPane? _emptyExplorer; // 빈 상태 중앙 탐색기 (지연 생성)

    // ---- 하단 바 드라이브 줄 (A22, v0.108.0) ----
    // 표시 컨트롤은 셸에 하나(공용 DriveStrip)만 두고 모듈 하단 바가 슬롯을 내준다(IDriveStripHost).
    // 보임 조건은 "파일이 열려 있지 않을 때"뿐이라, 새 상태 플래그 없이 _currentFilePath를 그대로 쓴다.
    private DriveStrip? _driveStrip;          // 지금 모듈 바에 끼워둔 줄 (뷰마다 새로 만든다)
    private IDriveStripHost? _driveStripHost; // 그 줄을 받은 모듈 뷰

    private enum OverlayState { Closed, Holding, TranslucentPinned, OpaqueDocked }

    /// <summary>오버레이 한쪽(좌 = Alt 리스트 / 우 = Shift 정보)의 입력·표시 상태.</summary>
    private sealed class OverlaySide
    {
        public OverlayState State;
        public bool KeyIsDown;          // 물리 키가 눌려 있는지 — 수정자 조합(Alt+Shift) 감지용
        public bool HoldSessionActive;  // 이번 누름이 홀드 판정 세션인지 (2연타·조합·취소면 아님)
        public DateTime LastTapDown = DateTime.MinValue; // 2연타 판정 (down→down)
        public DispatcherTimer? PinTimer; // 2초 홀드 → 반투명 고정 승격 (생성자에서 배선)
    }

    private const double OverlayDoubleTapMs = 450;  // 2연타 판정 창 (v0.32.0 값 유지)
    private const double OverlayPinHoldMs = 2000;   // 홀드 → 반투명 고정 승격 시간 (A58)

    private readonly OverlaySide _listSide = new(); // 좌측 파일 리스트 (Alt)
    private readonly OverlaySide _infoSide = new(); // 우측 정보 (Shift)

    /// <summary>지금 보여주는 모듈 ID. 빈 셸·설정·미지원 파일 안내면 null. 창 재사용 판단에 쓴다.</summary>
    public string? CurrentModuleId { get; private set; }

    /// <summary>아직 아무 콘텐츠도 안 연 빈 셸인지. 창 재사용 판단에 쓴다.</summary>
    public bool IsUntouched { get; private set; } = true;

    public MainWindow(WindowManager manager)
    {
        InitializeComponent();
        Title = Branding.AppName;
        _manager = manager;
        _router = App.Services.GetRequiredService<FileTypeRouter>();
        _settings = App.Services.GetRequiredService<ISettingsService>();

        // 좌측 파일 리스트 오버레이(A57 ②) 배선 — 열기 이벤트는 기존 Alt 리스트와 동일 경로
        ListOverlay.Settings = _settings;                                  // 정렬 키 저장(A5)
        ListOverlay.FileActivated += OpenFileRouted;                       // 재사용 규칙 적용(A24)
        ListOverlay.FileActivatedNewWindow += _manager.OpenFileInNewWindow; // Shift+더블클릭·우클릭 메뉴

        BuildStartMenu();
        RegisterShortcuts(); // `·숫자 단독 키(A32) + Shift+N 새 창(A84 — 기존 Ctrl+N 전환)
        RestoreWindowBounds(); // 마지막 창 크기·위치 복원 + 닫을 때 저장 (v0.55.0 크기, A55 위치·최대화)
        WindowMinSize.Apply(this); // 최소 창 크기 720×540 DIP 강제 (A40) — 창 생성 경로는 이 생성자 하나뿐
        // 광고 로테이션: 메뉴가 열릴 때 현재 분 기준 이미지로 갱신 (같은 분 = 같은 이미지)
        StartFlyout.Opening += (_, _) => UpdateSponsorImage();

        // UI 스케일 오버라이드(v0.24.0): 설정값 적용 + 설정 변경·창 크기·모니터 DPI 변화에 추종.
        // RasterizationScale은 XamlRoot 준비 후에만 유효하므로 Loaded에서 시작한다.
        RootLayout.Loaded += (_, _) =>
        {
            if (!_xamlRootHooked && RootLayout.XamlRoot is { } xr)
            {
                _xamlRootHooked = true;
                xr.Changed += (_, _) => ApplyUiScale(); // 모니터 간 이동 등으로 시스템 DPI가 바뀔 때
            }
            ApplyUiScale();
        };
        ScaleHost.SizeChanged += (_, _) => LayoutUiScale();
        UiScale.Changed += ApplyUiScale;
        Closed += (_, _) => UiScale.Changed -= ApplyUiScale;

        // Alt/Shift 오버레이 입력 감지(A58 — v0.25.0 Alt/Ctrl 홀드 대체): 포커스가 모듈 뷰 안에
        // 있어도 받도록 창 루트에서 handledEventsToo로 구독한다. 포인터 개입(클릭·휠)도 홀드 판정
        // 취소 트리거(A58 안전장치 — Shift+클릭 다중 선택·Shift+더블클릭이 오버레이를 물지 않게.
        // 휠은 A84에서 추가: Shift+휠 줌은 KeyDown이 아니라 다른-키 취소에 안 걸리므로 여기서 방어).
        // 창 비활성화로 KeyUp을 놓치면 홀드 판정·키 상태를 초기화(고정·불투명 상태는 유지).
        RootLayout.AddHandler(UIElement.KeyDownEvent, new KeyEventHandler(OnRootKeyDown), handledEventsToo: true);
        RootLayout.AddHandler(UIElement.KeyUpEvent, new KeyEventHandler(OnRootKeyUp), handledEventsToo: true);
        RootLayout.AddHandler(UIElement.PointerPressedEvent,
            new PointerEventHandler(OnRootPointerIntervened), handledEventsToo: true);
        RootLayout.AddHandler(UIElement.PointerWheelChangedEvent,
            new PointerEventHandler(OnRootPointerIntervened), handledEventsToo: true);
        _listSide.PinTimer = MakePinTimer(_listSide);
        _infoSide.PinTimer = MakePinTimer(_infoSide);
        Activated += (_, e) =>
        {
            if (e.WindowActivationState == WindowActivationState.Deactivated)
            {
                _listSide.KeyIsDown = false;
                _infoSide.KeyIsDown = false;
                ResetOverlayInput();
            }
        };

        // 타이틀바·작업표시줄 아이콘 (unpackaged는 exe 아이콘만으로는 타이틀바가 비어 보인다)
        if (File.Exists(IconPath))
        {
            _moduleIconPath = IconPath; // 인스턴스 번호가 생기면 이 경로로 다시 합성 (A68)
            AppWindow.SetIcon(IconPath);
            WindowIcon.Apply(this, IconPath); // 작업표시줄 기본 문서 아이콘 문제 보정 (실기기)
        }

        // 창 헤더만 브랜드 색(#15072E) — 본문은 시스템 테마 기본값
        TitleBarTheming.Apply(AppWindow.TitleBar);

        // 전체화면(동영상 Enter/F11)에서는 하단 바를 통째로 숨긴다 —
        // 재생줄이 하단 바로 통합되면서(v0.21.0) 전체화면 겹침도 여기서 함께 해결
        AppWindow.Changed += (sender, args) =>
        {
            if (!args.DidPresenterChange) return;
            var full = sender.Presenter.Kind == Microsoft.UI.Windowing.AppWindowPresenterKind.FullScreen;
            BottomBar.Visibility = full ? Visibility.Collapsed : Visibility.Visible;
            BottomBarRow.Height = full ? new GridLength(0) : new GridLength(BottomBarHeight); // 평소 고정 44 (A40)
        };

        // 문서 편집 미저장 확인(A37): X 버튼/Alt+F4를 가로채 저장/버리기/취소를 묻는다
        AppWindow.Closing += OnAppWindowClosing;

        // 창별 트레이 미니 아이콘: 좌클릭=활성화, 우클릭=메뉴, 툴팁=창 제목
        _tray = new TrayIcon(File.Exists(IconPath) ? IconPath : null);
        _tray.ActivateRequested += BringToFront;
        _tray.CloseRequested += () => _ = ConfirmThenCloseAsync(); // 닫기도 미저장 가드 경유 (A37)
        _tray.ExitAllRequested += _manager.CloseAll;
        Closed += (_, _) => _tray.Dispose();

        // A69: 최소화 = 트레이로 숨김 (전 모듈). 감지는 AppWindow.Changed의 프레젠터 상태 검사 —
        // A55 TrackNormalBounds가 이미 실증한 이벤트 경로. WindowMinSize 서브클래스에
        // WM_SYSCOMMAND(SC_MINIMIZE)를 더하는 대안은 최소화 애니메이션 전에 개입하게 되는 데다
        // wParam 하위 4비트 마스킹 등 판정 부담이 있어 채택하지 않았다.
        AppWindow.Changed += OnMinimizeStateChanged;
    }

    // ---------- UI 스케일 오버라이드 (v0.24.0) ----------

    /// <summary>
    /// 설정의 배율(%)을 시스템 DPI 대비 상대 배율로 환산한다.
    /// 예: 설정 100% + 시스템 150% 모니터 → 2/3배. 설정 0(시스템 기본)이면 1.0.
    /// </summary>
    private void ApplyUiScale()
    {
        // 설정 화면(다른 창)에서 바꿔도 이 창에 적용되도록 이벤트로 불린다 — UI 스레드 보장.
        if (DispatcherQueue is { } dq && !dq.HasThreadAccess)
        {
            dq.TryEnqueue(ApplyUiScale);
            return;
        }

        var percent = _settings.Get(UiScale.SettingKey, 0);
        var system = RootLayout.XamlRoot?.RasterizationScale ?? 1.0;
        _uiScaleFactor = percent <= 0 ? 1.0 : Math.Clamp(percent / 100.0 / system, 0.25, 4.0);
        LayoutUiScale();
    }

    /// <summary>
    /// RootLayout에 ScaleTransform(배율)과 역수 크기를 적용한다.
    /// 레이아웃은 1/배율 크기로 계산되고 렌더링에서 배율만큼 확대되므로 창을 꽉 채운다.
    /// </summary>
    private void LayoutUiScale()
    {
        if (Math.Abs(_uiScaleFactor - 1.0) < 0.001)
        {
            RootLayout.RenderTransform = null;
            RootLayout.ClearValue(FrameworkElement.WidthProperty);
            RootLayout.ClearValue(FrameworkElement.HeightProperty);
            RootLayout.HorizontalAlignment = HorizontalAlignment.Stretch;
            RootLayout.VerticalAlignment = VerticalAlignment.Stretch;
            return;
        }

        RootLayout.HorizontalAlignment = HorizontalAlignment.Left;
        RootLayout.VerticalAlignment = VerticalAlignment.Top;
        RootLayout.RenderTransformOrigin = new Windows.Foundation.Point(0, 0);
        RootLayout.RenderTransform = new ScaleTransform { ScaleX = _uiScaleFactor, ScaleY = _uiScaleFactor };
        if (ScaleHost.ActualWidth > 0)
        {
            RootLayout.Width = ScaleHost.ActualWidth / _uiScaleFactor;
            RootLayout.Height = ScaleHost.ActualHeight / _uiScaleFactor;
        }
    }

    /// <summary>
    /// 창 제목과 트레이 툴팁을 함께 갱신한다.
    /// 최종 형태(A56): "● [2] KOTU Document — sample.pdf"
    /// — ● = 미저장 변경(A37), [2] = 인스턴스 번호(창이 2개 이상일 때만).
    /// </summary>
    private void SetTitle(string title)
    {
        _baseTitle = title;
        ApplyTitle();
    }

    private string _baseTitle = Branding.AppName;
    private bool _titleDirtyMark; // 현재 뷰의 미저장 표시(A37 — ICloseGuard.UnsavedChanged)
    private int _instanceNumber;  // 0 = 창이 하나뿐 → 번호 표시 안 함 (A56)

    private void ApplyTitle()
    {
        // 순서: 상태(●) → 인스턴스([n]) → 내용. 작업표시줄·Alt+Tab에서 잘려도
        // 앞쪽 두 표식이 남도록 상태와 번호를 앞에 둔다.
        var title = _instanceNumber > 0 ? $"[{_instanceNumber}] {_baseTitle}" : _baseTitle;
        if (_titleDirtyMark) title = "● " + title;
        Title = title;
        _tray.SetTooltip(title);
    }

    // ---------- 창 크기·위치 저장/복원 (v0.55.0 크기만 → A55에서 위치·최대화 추가) ----------

    /// <summary>
    /// 마지막 "일반 상태(Restored)"의 위치·크기(물리 픽셀) — 최대화·전체화면·최소화로 닫혀도
    /// 최대화 직전 값을 저장할 수 있게 AppWindow.Changed에서 추적한다(A55).
    /// </summary>
    private Windows.Graphics.PointInt32? _lastNormalPos;
    private Windows.Graphics.SizeInt32? _lastNormalSize;

    /// <summary>
    /// 마지막으로 닫힌 창의 크기·위치(물리 픽셀)를 복원한다(v0.55.0 크기, A55 위치).
    /// 저장값이 없으면 기본 크기·위치. 다중 인스턴스(A24)는 저장 위치에서 창마다
    /// +32px 계단식 오프셋으로 열고, 오프셋 결과까지 화면 밖 보정을 거친다.
    /// 최대화로 닫혔으면 최대화로 열되, 복원(Restore Down) 시 돌아갈 일반 크기·위치는
    /// 먼저 적용해 둔 저장값이 된다.
    /// </summary>
    private void RestoreWindowBounds()
    {
        var w = _settings.Get("window.width", 0);
        var h = _settings.Get("window.height", 0);
        var x = _settings.Get("window.x", int.MinValue);
        var y = _settings.Get("window.y", int.MinValue);
        if (w >= 320 && h >= 240)
        {
            try
            {
                // A40: 구버전 저장값이 새 최소(720×540 DIP)보다 작으면 최소로 올려 연다 —
                // WM_GETMINMAXINFO는 사용자 리사이즈만 막고 프로그램 Resize는 안 막기 때문.
                var (minW, minH) = WindowMinSize.MinPhysical(
                    WinRT.Interop.WindowNative.GetWindowHandle(this));
                w = Math.Max(w, minW);
                h = Math.Max(h, minH);
                if (x != int.MinValue && y != int.MinValue)
                {
                    // 계단식 오프셋(A55): 생성자 시점 OpenWindowCount = 기존 창 수 = 인스턴스 - 1.
                    // 클램프는 최소 크기 반영 후의 최종 크기로 계산해야 맞는다(A40 정합).
                    var offset = 32 * _manager.OpenWindowCount;
                    AppWindow.MoveAndResize(ClampToWorkArea(
                        new Windows.Graphics.RectInt32(x + offset, y + offset, w, h)));
                }
                else
                {
                    AppWindow.Resize(new Windows.Graphics.SizeInt32(w, h)); // 구버전 저장값(위치 없음)
                }
            }
            catch { /* 모니터 구성이 바뀌었어도 열리기는 해야 한다 */ }
        }

        try
        {
            // 일반 상태 기준값을 "지금 적용한 값"으로 먼저 기록하고 나서 추적·최대화 순서 —
            // 최대화 복원 직후 닫아도 직전 일반 크기·위치가 저장되게(A55).
            _lastNormalPos = AppWindow.Position;
            _lastNormalSize = AppWindow.Size;
            AppWindow.Changed += TrackNormalBounds;
            if (_settings.Get("window.maximized", false)
                && AppWindow.Presenter is Microsoft.UI.Windowing.OverlappedPresenter op)
            {
                op.Maximize();
            }
        }
        catch { /* 프레젠터 조작 실패가 시작을 막으면 안 된다 */ }

        Closed += (_, _) => SaveWindowBounds();
    }

    /// <summary>
    /// 일반 상태(OverlappedPresenter.Restored)의 위치·크기만 기록한다(A55).
    /// 최대화·전체화면(별도 프레젠터)·최소화(좌표 -32000)는 State/Kind 검사로 자연히 걸러진다.
    /// </summary>
    private void TrackNormalBounds(Microsoft.UI.Windowing.AppWindow sender,
        Microsoft.UI.Windowing.AppWindowChangedEventArgs args)
    {
        if (!args.DidSizeChange && !args.DidPositionChange) return;
        if (sender.Presenter is not Microsoft.UI.Windowing.OverlappedPresenter
            { State: Microsoft.UI.Windowing.OverlappedPresenterState.Restored }) return;
        _lastNormalPos = sender.Position;
        _lastNormalSize = sender.Size;
    }

    /// <summary>
    /// 화면 밖 보정(A55): 모니터 분리 등으로 저장 위치가 현재 구성에서 안 보이면
    /// 가장 가까운 WorkArea 안으로 클램프한다. 타이틀바를 마우스로 잡을 수 있는 정도
    /// (가로 노출 48px + 타이틀바 세로 밴드가 WorkArea 안)면 그대로 통과.
    /// </summary>
    private static Windows.Graphics.RectInt32 ClampToWorkArea(Windows.Graphics.RectInt32 rect)
    {
        const int MinVisible = 48; // 타이틀바를 잡을 수 있는 최소 노출(물리 px)
        try
        {
            var area = Microsoft.UI.Windowing.DisplayArea.GetFromRect(
                    rect, Microsoft.UI.Windowing.DisplayAreaFallback.Nearest)
                ?? Microsoft.UI.Windowing.DisplayArea.Primary;
            if (area is null) return rect; // 디스플레이 정보를 못 얻으면 무보정
            var wa = area.WorkArea;

            var overlapW = Math.Min(rect.X + rect.Width, wa.X + wa.Width) - Math.Max(rect.X, wa.X);
            var titleVisible = rect.Y >= wa.Y && rect.Y <= wa.Y + wa.Height - MinVisible;
            if (overlapW >= MinVisible && titleVisible) return rect;

            // 창이 WorkArea보다 크면 Math.Max가 상한을 원점으로 눌러 좌상단 기준이 된다
            var x = Math.Clamp(rect.X, wa.X, Math.Max(wa.X, wa.X + wa.Width - rect.Width));
            var y = Math.Clamp(rect.Y, wa.Y, Math.Max(wa.Y, wa.Y + wa.Height - rect.Height));
            return new Windows.Graphics.RectInt32(x, y, rect.Width, rect.Height);
        }
        catch
        {
            return rect; // 보정 실패해도 열리기는 해야 한다
        }
    }

    /// <summary>
    /// 마지막으로 닫힌 창이 이긴다(현행 규칙 — 창별 저장은 A70 별도).
    /// 최대화로 닫히면 window.maximized=true + 직전 일반 크기·위치를,
    /// 전체화면·최소화로 닫히면 직전 일반 크기·위치만 저장한다(전체화면은 일시 모드 — A55).
    /// </summary>
    private void SaveWindowBounds()
    {
        try
        {
            var presenter = AppWindow.Presenter;
            if (presenter.Kind == Microsoft.UI.Windowing.AppWindowPresenterKind.FullScreen)
            {
                SaveBounds(_lastNormalSize, _lastNormalPos, maximized: false);
            }
            else if (presenter is Microsoft.UI.Windowing.OverlappedPresenter p
                && p.State != Microsoft.UI.Windowing.OverlappedPresenterState.Restored)
            {
                SaveBounds(_lastNormalSize, _lastNormalPos,
                    maximized: p.State == Microsoft.UI.Windowing.OverlappedPresenterState.Maximized);
            }
            else
            {
                SaveBounds(AppWindow.Size, AppWindow.Position, maximized: false);
            }
        }
        catch
        {
            // 저장 실패가 종료를 막으면 안 된다.
        }
    }

    /// <summary>크기가 유효할 때만 크기·위치를 덮어쓴다 — 추적값이 없으면 기존 저장값 유지.</summary>
    private void SaveBounds(Windows.Graphics.SizeInt32? size, Windows.Graphics.PointInt32? pos, bool maximized)
    {
        if (size is { Width: >= 320, Height: >= 240 } s)
        {
            _settings.Set("window.width", s.Width);
            _settings.Set("window.height", s.Height);
            if (pos is { } p)
            {
                _settings.Set("window.x", p.X);
                _settings.Set("window.y", p.Y);
            }
        }
        _settings.Set("window.maximized", maximized);
        _settings.Save();
    }

    // ---------- 단축키 (v0.45.0 사용자 지정) ----------

    /// <summary>
    /// 모듈 번호(메뉴 아래→위 순서): 1=이미지, 2=영상, 3=오디오, 4=문서, 5=압축, 6=하드웨어.
    /// A10: 오디오 모듈이 3번에 삽입되며 문서 이후가 한 칸씩 밀림(사용자 확정).
    /// A32: Ctrl 없이 숫자 단독(사용자 확정) — Ctrl은 정보 오버레이로 회귀.
    /// 힌트 문자열은 시작 메뉴 항목 마우스 오버 시 툴팁으로 보조 표시된다.
    /// </summary>
    private static readonly (string Id, VirtualKey Key, string Hint)[] ModuleShortcuts =
    [
        ("image", VirtualKey.Number1, "1"),
        ("video", VirtualKey.Number2, "2"),
        ("audio", VirtualKey.Number3, "3"),
        ("document", VirtualKey.Number4, "4"),
        ("archive", VirtualKey.Number5, "5"),
        ("hardware", VirtualKey.Number6, "6"),
    ];

    private const string SettingsShortcutHint = "0";

    /// <summary>
    /// `(1 왼쪽 키) = 시작 메뉴, 숫자 = 모듈 전환, 0 = Settings — 전부 수정자 없는 단독 키(A32).
    /// Shift+N = 새 창(A24 — A84에서 Ctrl+N을 Shift 계열로 전환. 앱에 남는 Ctrl 조합은
    /// 문서 Ctrl+S 하나뿐). 단독 키와 Shift 조합은 텍스트 입력란에 포커스가 있으면
    /// 가로채지 않고 통과시킨다(A32 예외 — 숫자 타이핑·Shift+N 대문자 입력이 우선).
    /// </summary>
    private void RegisterShortcuts()
    {
        // 액셀러레이터 키 이름 툴팁이 화면 중앙에 뜨는 WinUI 기본 동작 방지 (모듈 뷰들과 동일)
        RootLayout.KeyboardAcceleratorPlacementMode =
            Microsoft.UI.Xaml.Input.KeyboardAcceleratorPlacementMode.Hidden;

        AddShortcut((VirtualKey)192, () => StartFlyout.ShowAt(StartButton)); // VK_OEM_3 = `(~)
        foreach (var (id, key, _) in ModuleShortcuts)
            AddShortcut(key, () => OpenModuleById(id));
        AddShortcut(VirtualKey.Number0, () => OnSettingsClick(StartButton, new RoutedEventArgs()));
        // 새 창 = 지금 보는 모듈의 빈 인스턴스(A24 사용자 확정). 설정 화면 등 모듈 없는 창은 기본 화면으로.
        AddShortcut(VirtualKey.N, () => _manager.OpenNewWindow(CurrentModuleId),
            Windows.System.VirtualKeyModifiers.Shift); // A84: Ctrl+N → Shift+N
    }

    private void AddShortcut(VirtualKey key, Action action,
        Windows.System.VirtualKeyModifiers modifiers = Windows.System.VirtualKeyModifiers.None)
    {
        var accelerator = new KeyboardAccelerator { Key = key, Modifiers = modifiers };
        accelerator.Invoked += (_, e) =>
        {
            // A32 예외: 단독 키는 입력 컨트롤 타이핑을 뺏으면 안 된다.
            // A84: Shift 조합도 동일 — 에디터에서 Shift+글자는 대문자 입력이 우선(Shift+N 통과).
            if (modifiers is Windows.System.VirtualKeyModifiers.None
                    or Windows.System.VirtualKeyModifiers.Shift
                && IsTextInputFocused())
            {
                e.Handled = false; // 계속 흘려보내 컨트롤이 문자를 받게
                return;
            }
            e.Handled = true;
            action();
        };
        RootLayout.KeyboardAccelerators.Add(accelerator);
    }

    /// <summary>포커스가 텍스트 입력 컨트롤(TextBox·PasswordBox·RichEditBox 계열)에 있는지.</summary>
    private bool IsTextInputFocused()
        => RootLayout.XamlRoot is { } xr
           && Microsoft.UI.Xaml.Input.FocusManager.GetFocusedElement(xr)
               is TextBox or PasswordBox or RichEditBox;

    /// <summary>단축키·센서 트레이(A18)로 모듈 전환. 이미 그 모듈이면 아무것도 하지 않는다(보던 파일 보호).</summary>
    internal void OpenModuleById(string id)
    {
        if (CurrentModuleId == id) return;
        var module = _router.Modules.FirstOrDefault(m => m.Id == id);
        if (module is not null) OpenModule(module);
    }

    // ---------- 시작 메뉴 (하단 바에서 위로 떠오르는 플라이아웃) ----------

    /// <summary>
    /// 시작 메뉴 구성. 패널은 위→아래 순서로 채우므로, 사용자가 정한 "아래부터" 순서
    /// (사진-영상-문서, 여백, 압축, 여백x2, 광고)를 뒤집어 넣는다.
    /// </summary>
    private void BuildStartMenu()
    {
        StartMenuPanel.Children.Clear();

        // 최상단: 스폰서(광고) 자리 — 지금은 MSI 로고 플레이스홀더, 파일 교체만으로 변경 가능
        StartMenuPanel.Children.Add(BuildSponsorCard());
        StartMenuPanel.Children.Add(Divider()); // 그룹 경계는 구분선으로 명확히 (v0.26.0 사용자 요청)

        // New Instance 항목은 A65(v0.92.0)에서 제거 — 메뉴 항목만 뺐고,
        // Shift+N(A84 — 기존 Ctrl+N)·탐색기 Shift+더블클릭·우클릭 "Open in new instance"·
        // 설정 토글 진입로는 그대로다.
        // Settings·Hardware-info 묶음 (사용자 지정: zip 위에 공백 두고 Hardware-info, 그 위 Settings)
        AddSettingsItem();
        AddModuleItem("hardware");
        StartMenuPanel.Children.Add(Divider());

        AddModuleItem("archive");
        StartMenuPanel.Children.Add(Divider());

        // 사진-영상-오디오-문서 그룹 (아래부터 사진 → 위로 갈수록 문서)
        AddModuleItem("document"); // v0.44.0 실제 모듈로 교체 (텍스트·마크다운 1단계)
        AddModuleItem("audio"); // 음악 재생 분리 (A10, v0.75.0)
        AddModuleItem("video");
        AddModuleItem("image");
        // 하단 바 우측 Info·Settings 아이콘은 제거(v0.28.2) — 시작 메뉴 항목으로 일원화.
    }

    private void AddModuleItem(string moduleId)
    {
        var module = _router.Modules.FirstOrDefault(m => m.Id == moduleId);
        if (module is null) return;

        var hint = ModuleShortcuts.FirstOrDefault(s => s.Id == moduleId).Hint;
        var item = MakeMenuItem(module.IconGlyph, module.DisplayName, hint);
        item.Click += (_, _) =>
        {
            StartFlyout.Hide();
            OpenModule(module);
        };
        StartMenuPanel.Children.Add(item);
    }

    /// <summary>시작 메뉴의 Settings 항목 — 하단 바 우측 아이콘과 같은 동작.</summary>
    private void AddSettingsItem()
    {
        var item = MakeMenuItem("\uE713", "Settings", SettingsShortcutHint);
        item.Click += (_, _) =>
        {
            StartFlyout.Hide();
            OnSettingsClick(item, new RoutedEventArgs());
        };
        StartMenuPanel.Children.Add(item);
    }

    /// <summary>
    /// 시작 메뉴 항목. shortcutHint("1" 등)가 있으면 표준 툴팁으로 단다 —
    /// 다른 버튼들과 같은 지연(약 1초)·모양으로 표시된다(A1, v0.57.0 —
    /// v0.45.0의 즉시 인라인 힌트를 사용자 지시로 교체).
    /// </summary>
    private static Button MakeMenuItem(string glyph, string label, string? shortcutHint = null)
    {
        var content = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 12 };
        content.Children.Add(new FontIcon { Glyph = glyph, FontSize = 16 });
        content.Children.Add(new TextBlock
        {
            Text = label,
            VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis,
        });

        var button = new Button
        {
            Content = content,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Left,
            Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent),
            BorderThickness = new Thickness(0),
            // A31: 히트 영역 확대 — 상하 패딩 8→12 + 최소 높이 44 (터치 타깃 권장 크기).
            // 좌우는 10 유지: 메뉴 폭 124(v0.35.0 축소 확정) 안에서 라벨 말줄임을 늘리지 않기 위해.
            // A50(v0.92.0): 좌측 히트 영역은 플라이아웃 프레젠터 패딩 0(XAML)으로 확대 —
            // Stretch인 버튼이 메뉴 좌우 가장자리까지 닿아, 포인터가 라벨보다 왼쪽이어도 눌린다.
            Padding = new Thickness(10, 12, 10, 12),
            MinHeight = 44,
        };
        if (shortcutHint is not null)
            ToolTipService.SetToolTip(button, shortcutHint);
        return button;
    }

    private Image? _sponsorImage;

    private UIElement BuildSponsorCard()
    {
        // v0.43.0(사용자 스샷 피드백): 광고가 카드 전 영역을 차지하고(패딩 제거, 메뉴 폭에 맞춰 확대),
        // SPONSOR 라벨은 이미지 위 좌상단에 반투명 배지로 겹쳐서 아주 작게 표시한다.
        var host = new Grid
        {
            CornerRadius = new CornerRadius(8),
            Background = (Brush)Application.Current.Resources["LayerFillColorDefaultBrush"],
            Padding = new Thickness(2),
            MinHeight = 60,
        };

        if (SponsorAds.Any)
        {
            // 광고 표시 규격 120×60 (v0.54.0 사용자 확대 지시 — 원본 2:1 비율 유지 확대)
            _sponsorImage = new Image
            {
                Width = 120,
                Height = 60,
                Stretch = Stretch.Uniform,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
            };
            SponsorAds.Apply(_sponsorImage);
            host.Children.Add(_sponsorImage);
        }

        host.Children.Add(new Border
        {
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(4),
            Background = new SolidColorBrush(Windows.UI.Color.FromArgb(0x59, 0x00, 0x00, 0x00)),
            CornerRadius = new CornerRadius(3),
            Padding = new Thickness(4, 1, 4, 1),
            Child = new TextBlock
            {
                Text = "SPONSOR",
                FontSize = 7,
                Foreground = new SolidColorBrush(Microsoft.UI.Colors.White),
                Opacity = 0.9,
            },
        });

        return host;
    }

    /// <summary>메뉴가 열릴 때 현재 분 기준 광고로 갱신한다(로직은 SponsorAds 공용, v0.50.0).</summary>
    private void UpdateSponsorImage()
    {
        if (_sponsorImage is not null) SponsorAds.Apply(_sponsorImage);
    }

    /// <summary>
    /// 시작 메뉴 그룹 구분선: 여백 + 1px 라인 (v0.26.0, 공백만으로는 정리가 안 보인다는 피드백).
    /// 상하 여백 8→3 (A50 항목 간격 축소, v0.92.0) — 항목 높이 44(A31)는 그대로 두고 사이 여백만 줄인다.
    /// </summary>
    private static Border Divider() => new()
    {
        Height = 1,
        Margin = new Thickness(4, 3, 4, 3),
        Background = (Brush)Application.Current.Resources["DividerStrokeColorDefaultBrush"],
    };

    private void OpenModule(IModule module)
        => ShowModule(module, OpenContext.Empty, $"{Branding.AppName} {module.DisplayName}");

    /// <summary>
    /// 앱 첫 화면 기본 뷰(Info/하드웨어). 사용자가 고른 화면이 아니므로
    /// IsUntouched를 되돌려서, 첫 파일 열기가 새 창을 만들지 않고 이 창을 쓰게 한다.
    /// </summary>
    public void ShowDefaultModule()
    {
        var module = _router.Modules.FirstOrDefault(m => m.Id == "hardware");
        if (module is null) return;
        OpenModule(module);
        IsUntouched = true;
    }

    private void OnSettingsClick(object sender, RoutedEventArgs e) => ShowSettings();

    /// <summary>
    /// 설정 화면 열기. scrollToUpdates면 업데이트 섹션까지 스크롤한다
    /// (A26, v0.105.0 — 업데이트 토스트 클릭 진입).
    /// </summary>
    public void ShowSettings(bool scrollToUpdates = false) => _ = ShowSettingsAsync(scrollToUpdates);

    private async Task ShowSettingsAsync(bool scrollToUpdates)
    {
        if (!await ConfirmDiscardAsync()) return; // 문서 편집 미저장 가드 (A37)
        _titleDirtyMark = false;
        SetTitle($"{Branding.AppName} Settings");
        var settings = new SettingsView(_router);
        ModuleHost.Content = settings;
        // 설정도 하단 바 제공(광고 + ⛶, v0.50.0) — 모듈들과 같은 통합 방식
        ModuleBarHost.Content = settings.TakeBottomBar() as UIElement;
        AttachDriveStrip(null); // 설정 바에는 드라이브 줄이 없다 — 이전 뷰 참조를 끊는다 (A22)
        CurrentModuleId = null;
        IsUntouched = false;
        UpdateModeIndicator(null, isSettings: true);
        SetContentState(null, null);
        if (scrollToUpdates) settings.ScrollToUpdates();
    }

    // ---------- 파일 열기 ----------

    /// <summary>
    /// 내장 탐색기·Alt 리스트의 일반 더블클릭 열기(A24): 재사용 규칙이 "항상 새 창"이면
    /// WindowManager로 넘겨 새 창에 열고, 아니면(기본) 이 창에서 그대로 연다.
    /// 외부 진입(WindowManager가 창을 이미 골라 OpenFile을 부르는 경로)과 섞이지 않게 별도 메서드.
    /// </summary>
    private void OpenFileRouted(string path)
    {
        if (_settings.Get(WindowManager.AlwaysNewWindowKey, false))
            _manager.OpenFileInNewWindow(path);
        else
            OpenFile(path);
    }

    /// <summary>파일 라우팅의 종착점: 확장자로 모듈을 찾아 뷰를 띄운다.</summary>
    public async void OpenFile(string path)
    {
        var module = _router.Resolve(path);
        if (module is null)
        {
            if (!await ConfirmDiscardAsync()) return; // 문서 편집 미저장 가드 (A37)
            _titleDirtyMark = false;
            ApplyTitle();
            ModuleHost.Content = new TextBlock
            {
                Text = $"Unsupported file type: {Path.GetFileName(path)}",
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
            };
            ModuleBarHost.Content = null;
            AttachDriveStrip(null); // 미지원 파일 안내 화면 — 모듈 바와 함께 드라이브 줄도 내린다 (A22)
            CurrentModuleId = null;
            IsUntouched = false;
            UpdateModeIndicator(null);
            SetContentState(null, null);
            return;
        }
        ShowModule(module, OpenContext.ForFile(path),
            $"{Branding.AppName} {module.DisplayName} — {Path.GetFileName(path)}");
    }

    /// <summary>탐색기 우클릭 동사(여기에 풀기/압축) 진입점. 동사는 압축 모듈이 처리한다.</summary>
    public void OpenVerb(LaunchRequest request)
    {
        if (request.FilePath is not { } file) return;

        var module = _router.Modules.FirstOrDefault(m => m.Id == "archive");
        if (module is null || request.VerbToken is not { } token)
        {
            OpenFile(file);
            return;
        }

        ShowModule(module, new OpenContext { FilePath = file, Arguments = [token] },
            $"{Branding.AppName} {module.DisplayName} — {Path.GetFileName(file)}");
    }

    // ---------- 창 전체 드래그&드롭 → 파일 라우팅 ----------

    private void OnWindowDragOver(object sender, DragEventArgs e)
    {
        if (e.Handled) return; // 압축 뷰 등 모듈이 이미 소비한 드래그
        if (e.DataView.Contains(StandardDataFormats.StorageItems))
            e.AcceptedOperation = DataPackageOperation.Copy;
    }

    private async void OnWindowDrop(object sender, DragEventArgs e)
    {
        if (e.Handled) return;
        if (!e.DataView.Contains(StandardDataFormats.StorageItems)) return;
        var items = await e.DataView.GetStorageItemsAsync();
        var path = items.OfType<Windows.Storage.StorageFile>()
            .Select(f => f.Path)
            .FirstOrDefault(p => !string.IsNullOrEmpty(p));
        if (path is not null) OpenFile(path);
    }

    private async void ShowModule(IModule module, OpenContext context, string title)
    {
        // 현재 뷰에 미저장 변경이 있으면 먼저 정리(저장/버리기/취소) — 취소면 아무것도 안 바꾼다 (A37).
        // 제목 변경도 가드 뒤로 미뤄서, 취소 시 제목이 어긋나지 않는다.
        if (!await ConfirmDiscardAsync()) return;
        _titleDirtyMark = false;
        SetTitle(title);

        var view = (UIElement)module.CreateView(context);
        ModuleHost.Content = view;
        // 모듈이 제공하는 하단 바 줄(동영상 트랜스포트 등)을 셸 하단 바에 통합 (v0.21.0)
        ModuleBarHost.Content = (view as IBottomBarProvider)?.TakeBottomBar() as UIElement;
        AttachDriveStrip(view as IDriveStripHost); // A22: 하단 바 드라이브 줄 주입(파일 없을 때만 표시)
        CurrentModuleId = module.Id;
        IsUntouched = false;
        UpdateModeIndicator(module);

        // 뷰 내부 열기(열기 버튼·◀/▶ 탐색·테스트 클립)도 셸과 동기화 (v0.25.0)
        if (view is IContentStateSource source)
            source.ContentOpened += path => DispatcherQueue.TryEnqueue(() => OnContentOpened(path));
        // 미저장 표시(A37): 창 제목·트레이 툴팁에 ● — 뷰가 이미 교체됐으면 무시
        if (view is ICloseGuard guard)
            guard.UnsavedChanged += dirty => DispatcherQueue.TryEnqueue(() =>
            {
                if (!ReferenceEquals(ModuleHost.Content, view)) return;
                _titleDirtyMark = dirty;
                ApplyTitle();
            });
        SetContentState(module, context.FilePath);
    }

    // ---------- 미저장 가드 (A37) ----------

    private bool _confirmInProgress; // ContentDialog는 동시에 1개만 — 중복 진입 방지
    private bool _closeConfirmed;    // 확인을 마친 뒤의 재진입 Close 허용

    /// <summary>현재 뷰(ICloseGuard)에 미저장 변경이 있으면 사용자에게 확인. true = 계속 진행.</summary>
    private async Task<bool> ConfirmDiscardAsync()
    {
        if (ModuleHost.Content is not ICloseGuard { HasUnsavedChanges: true } guard) return true;
        if (_confirmInProgress) return false;
        _confirmInProgress = true;
        try
        {
            return await guard.ConfirmCloseAsync();
        }
        finally
        {
            _confirmInProgress = false;
        }
    }

    /// <summary>트레이 닫기·X 버튼 공용: 미저장 확인 후 닫는다.</summary>
    private async Task ConfirmThenCloseAsync()
    {
        // A69: 트레이로 숨긴 창의 닫기(트레이 Close·Exit KOTU)에서 미저장 확인(A37)이 필요하면
        // 대화상자가 보이도록 먼저 복귀시킨다 — 숨긴 채 ContentDialog를 띄우면 응답할 방법이 없다.
        if (_hiddenInTray && ModuleHost.Content is ICloseGuard { HasUnsavedChanges: true })
            BringToFront();
        if (!await ConfirmDiscardAsync()) return;
        _closeConfirmed = true;
        Close();
    }

    /// <summary>X 버튼/Alt+F4 닫기 가로채기(A37) — 미저장 변경이 있을 때만 개입한다.</summary>
    private void OnAppWindowClosing(Microsoft.UI.Windowing.AppWindow sender,
        Microsoft.UI.Windowing.AppWindowClosingEventArgs args)
    {
        if (_closeConfirmed) return;
        if (ModuleHost.Content is not ICloseGuard { HasUnsavedChanges: true }) return;
        args.Cancel = true;
        _ = ConfirmThenCloseAsync();
    }

    // ---------- 내장 탐색기 + 좌/우 오버레이 (v0.25.0 → A58 상태 머신) ----------

    /// <summary>현재 모듈·파일 상태를 바꾸고 탐색기/오버레이 표시를 갱신한다.</summary>
    private void SetContentState(IModule? module, string? filePath)
    {
        _currentModule = module;
        _currentFilePath = filePath;
        InfoOverlay.InvalidateCache();
        RememberLastFolder(); // 모듈별 마지막 폴더 저장 (v0.55.0)
        UpdateEmptyExplorer();
        UpdateDriveStrip(); // A22: 파일 유무가 바뀌면 드라이브 줄도 함께 켜고 끈다
        // 홀드 판정만 리셋하고(A58), 반투명 고정·불투명 밀어내기 상태는 유지한 채
        // 새 콘텐츠(파일·모듈) 기준으로 다시 그린다 — 기존 "고정은 콘텐츠를 넘어 유지" 규칙.
        ResetOverlayInput();
        ApplyOverlayStates();
    }

    /// <summary>현재 파일의 폴더를 모듈별 설정("lastFolder.{id}")에 기억한다 (v0.55.0).</summary>
    private void RememberLastFolder()
    {
        if (_currentModule is null || _currentFilePath is null) return;
        if (Path.GetDirectoryName(_currentFilePath) is not { Length: > 0 } folder) return;
        if (_settings.Get($"lastFolder.{_currentModule.Id}", string.Empty) == folder) return;
        _settings.Set($"lastFolder.{_currentModule.Id}", folder);
        _settings.Save();
    }

    /// <summary>모듈 뷰가 파일을 열었다는 알림(IContentStateSource) — 탐색기를 내리고 기준 경로 갱신.</summary>
    private void OnContentOpened(string path)
    {
        _currentFilePath = path;
        InfoOverlay.InvalidateCache();
        RememberLastFolder(); // v0.55.0
        UpdateEmptyExplorer();
        UpdateDriveStrip();   // A22: 뷰가 파일을 열었다 → 드라이브 줄을 숨긴다
        ApplyOverlayStates(); // 폴더·정보가 바뀌었을 수 있다 — 떠 있는 오버레이·도크 갱신
    }

    // ---------- 하단 바 드라이브 줄 (A22, v0.108.0) ----------

    /// <summary>
    /// 새 모듈 바에 드라이브 줄을 끼운다(A57 ②의 공용 오버레이 주입과 같은 방식).
    /// 뷰마다 새 인스턴스를 만드는 이유: 같은 UIElement를 다른 부모로 옮기면 XAML이 예외를 던진다.
    /// 드라이브 줄을 받지 않는 화면(설정·정보·미지원 파일 안내)에서는 null로 참조만 끊는다.
    /// </summary>
    private void AttachDriveStrip(IDriveStripHost? host)
    {
        _driveStripHost = host;
        _driveStrip = null;
        if (host is null) return;

        _driveStrip = new DriveStrip();
        host.AttachDriveStrip(_driveStrip);
        // 보임 여부는 곧바로 이어지는 SetContentState → UpdateDriveStrip이 정한다.
        // 여기서 미리 켜면 파일을 열고 있는 중에도 WMI 조회가 한 번 돌아 낭비다.
    }

    /// <summary>
    /// 표시 시점(A22 — v0.47.0의 반대): 파일이 열려 있지 않을 때만 보인다.
    /// 숨기면 컨트롤이 30초 갱신·자동 스크롤도 함께 멈춘다.
    /// </summary>
    private void UpdateDriveStrip()
    {
        if (_driveStrip is null || _driveStripHost is null) return;
        var show = _currentFilePath is null;
        _driveStrip.SetActive(show);
        _driveStripHost.ShowDriveStrip(show);
    }

    /// <summary>파일 모듈(파일 없이 열면 빈 상태 탐색기·A81 빈 도크가 성립)인지 — H/W·설정은 제외.</summary>
    private static bool IsFileModule(IModule? module) =>
        module is { Id: "archive" or "image" or "video" or "audio" or "document" };

    /// <summary>
    /// 파일 없이 연 파일 모듈(빈 모듈 상태)인지 — 중앙 탐색기(v0.25.0)와
    /// A81 빈 도크 오버레이가 공유하는 조건.
    /// </summary>
    private bool IsEmptyFileModule => _currentFilePath is null && IsFileModule(_currentModule);

    /// <summary>
    /// 빈 상태의 시작 폴더: 그 모듈의 마지막 폴더(v0.55.0), 없으면 바탕화면.
    /// 중앙 탐색기와 A81 빈 도크의 리스트 오버레이가 같은 규칙을 공유한다.
    /// </summary>
    private string ModuleStartFolder(IModule module)
    {
        var start = _settings.Get($"lastFolder.{module.Id}", string.Empty);
        if (string.IsNullOrEmpty(start) || !Directory.Exists(start))
            start = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
        return start;
    }

    /// <summary>
    /// 빈 상태(파일 없이 연 압축/이미지/동영상/오디오/문서 모듈)면 중앙에 탐색기를 띄운다.
    /// 시작 위치는 그 모듈의 마지막 폴더(v0.55.0, 없으면 바탕화면), 파일은 담당 확장자만.
    /// Hardware/Settings에는 띄우지 않는다.
    /// 좌측 리스트가 불투명 도크로 떠 있으면(A81 기본 도크) 중복이라 ApplyOverlayStates가 다시 숨긴다.
    /// </summary>
    private void UpdateEmptyExplorer()
    {
        if (IsEmptyFileModule && _currentModule is { } module)
        {
            if (_emptyExplorer is null)
            {
                _emptyExplorer = new ExplorerPane { Settings = _settings };           // 정렬 키 저장(A5)
                _emptyExplorer.FileActivated += OpenFileRouted;                       // 재사용 규칙 적용(A24)
                _emptyExplorer.FileActivatedNewWindow += _manager.OpenFileInNewWindow; // Shift+더블클릭·우클릭 메뉴
                ExplorerHost.Children.Add(_emptyExplorer);
            }
            _emptyExplorer.NavigateTo(ModuleStartFolder(module), module.SupportedExtensions);
            ExplorerHost.Visibility = Visibility.Visible;
        }
        else
        {
            ExplorerHost.Visibility = Visibility.Collapsed;
        }
    }

    // ---------- 좌/우 오버레이 입력 상태 머신 (A58) ----------

    /// <summary>
    /// 키 → 오버레이 사이드 매핑 (부록 B 26번): Alt = 좌측 파일 리스트, Shift = 우측 정보.
    /// 그 밖의 키는 null — 홀드 취소 트리거(다른 키 개입)로만 쓰인다.
    /// </summary>
    private OverlaySide? SideForKey(VirtualKey key) => key switch
    {
        VirtualKey.Menu or VirtualKey.LeftMenu or VirtualKey.RightMenu => _listSide,
        VirtualKey.Shift or VirtualKey.LeftShift or VirtualKey.RightShift => _infoSide,
        _ => null,
    };

    private void OnRootKeyDown(object sender, KeyRoutedEventArgs e)
    {
        // A32 통과 규칙 유지: 텍스트 입력 컨트롤에 포커스가 있으면 오버레이 입력을 받지 않는다 —
        // Shift는 대문자 입력이 우선(에디터 타이핑을 오버레이가 방해하면 안 된다).
        // 어떤 키든 홀드 판정·2연타 카운트만 리셋하고 흘려보낸다.
        if (IsTextInputFocused())
        {
            ResetOverlayInput();
            return;
        }

        var side = SideForKey(e.Key);
        if (side is null)
        {
            // 다른 키가 함께 눌림 → 진행 중 홀드 세션 취소(이미 떠 있으면 즉시 내림) +
            // 2연타 카운트 리셋 — OS의 Alt 메뉴 모드와 같은 규칙 (A58 공통 안전장치:
            // Shift+더블클릭 새 인스턴스·A84 Shift 조합(Shift+N 등)이 오버레이를 물지 않게).
            // 비수정자 키를 먼저 누르고 있던 조합은 그 키의 반복 입력이 곧 도착해 같은 경로로 취소된다.
            ResetOverlayInput();
            return;
        }

        var other = ReferenceEquals(side, _listSide) ? _infoSide : _listSide;
        side.KeyIsDown = true; // 반복 입력에서도 갱신 — 수정자 조합 감지의 근거
        if (!e.KeyStatus.WasKeyDown)
        {
            if (other.KeyIsDown)
                ResetOverlayInput(); // Alt+Shift 같은 오버레이 키끼리의 조합 — 양쪽 다 홀드 판정 없음
            else
                OnOverlaySideDown(side);
        }

        // Alt 기본 동작(OS 메뉴 모드 진입)과의 충돌 방지 — 기존 방식 유지(v0.25.0):
        // 좌측 오버레이가 떠 있을 때만 KeyDown을 소비한다.
        if (ReferenceEquals(side, _listSide) && ListOverlay.IsOpen) e.Handled = true;
    }

    /// <summary>
    /// 사이드 키 최초 down(반복·조합 제외)의 상태 전이 (A58 표):
    /// 2연타 = 고정 상태(반투명 고정·불투명 밀어내기)면 해제, 닫힘이면 불투명 밀어내기.
    /// 단독 down = 닫힘 상태에서만 홀드 세션 시작(반투명 덮기 + 2초 승격 타이머).
    /// </summary>
    private void OnOverlaySideDown(OverlaySide side)
    {
        // 오버레이 컨텍스트가 없으면(설정·H/W·미지원 파일 안내) 판정도 없다. 파일 없이 연
        // 파일 모듈(빈 모듈 상태)은 A81부터 컨텍스트에 포함 — 기본 도크를 2연타로 닫고
        // 다시 여는 입력이 성립해야 한다. 상태 전이(홀드/2초/2연타) 자체는 A58 그대로.
        if (_currentFilePath is null && !IsEmptyFileModule) return;

        var now = DateTime.UtcNow;
        if ((now - side.LastTapDown).TotalMilliseconds < OverlayDoubleTapMs)
        {
            side.LastTapDown = DateTime.MinValue;
            CancelHoldCore(side); // 이번 누름은 홀드 세션이 아니다 — 계속 눌러도 승격 없음
            side.State = side.State is OverlayState.TranslucentPinned or OverlayState.OpaqueDocked
                ? OverlayState.Closed         // 고정 해제 — 두 고정 상태 모두 2연타로 집어넣는다
                : OverlayState.OpaqueDocked;  // 닫힘(첫 탭은 이미 내려간 상태)에서 2연타 = 불투명 밀어내기
        }
        else
        {
            side.LastTapDown = now;
            if (side.State == OverlayState.Closed)
            {
                side.State = OverlayState.Holding;
                side.HoldSessionActive = true;
                side.PinTimer?.Start(); // 2초 경과 시 TranslucentPinned로 승격
            }
            // 이미 고정·불투명이면 홀드는 의미 없음 — 탭 시각만 기록(다음 2연타 판정용)
        }
        ApplyOverlayStates();
    }

    private void OnRootKeyUp(object sender, KeyRoutedEventArgs e)
    {
        var side = SideForKey(e.Key);
        if (side is null) return;

        var sawDown = side.KeyIsDown;
        side.KeyIsDown = false;
        if (side.HoldSessionActive)
        {
            side.HoldSessionActive = false;
            side.PinTimer?.Stop();
            if (side.State == OverlayState.Holding)
            {
                side.State = OverlayState.Closed; // 2초 미만 홀드 — 키를 떼면 내려간다
                ApplyOverlayStates();
            }
            // 타이머가 이미 TranslucentPinned로 승격했다면 그대로 유지 —
            // "2초 넘겨 뗐을 때 = 고정 유지" (A58 확정 해석)
        }

        // Alt 기본 동작 충돌 방지 — 기존 방식 유지: OS 메뉴 모드는 Alt KeyUp에서 발동하므로
        // down을 우리가 본 Alt의 up은 소비한다(기존 _altHeld 소비와 같은 범위).
        if (ReferenceEquals(side, _listSide) && sawDown) e.Handled = true;
    }

    /// <summary>
    /// 포인터 개입(클릭·휠)도 홀드 판정을 취소한다(A58 안전장치, 휠은 A84에서 추가) —
    /// Shift+클릭 다중 선택, Shift+더블클릭 새 인스턴스(A24), Shift+휠 줌(A84)이
    /// 오버레이를 물고 있지 않게. 단, 그 오버레이 자신 안에서의 클릭·스크롤은 예외 —
    /// Alt를 쥔 채 리스트에서 파일을 더블클릭해 열거나 목록을 휠로 넘기는
    /// 기존 흐름(v0.25.0)을 끊으면 안 된다.
    /// </summary>
    private void OnRootPointerIntervened(object sender, PointerRoutedEventArgs e)
    {
        var origin = e.OriginalSource as DependencyObject;
        var changed = false;
        if (!IsWithin(origin, ListOverlay)) changed |= CancelHoldCore(_listSide);
        if (!IsWithin(origin, InfoOverlay)) changed |= CancelHoldCore(_infoSide);
        _listSide.LastTapDown = DateTime.MinValue; // 2연타 카운트 리셋
        _infoSide.LastTapDown = DateTime.MinValue;
        if (changed) ApplyOverlayStates();
    }

    /// <summary>element가 root의 비주얼 트리 안에 있는지 (팝업 등 별도 트리는 false).</summary>
    private static bool IsWithin(DependencyObject? element, UIElement root)
    {
        while (element is not null)
        {
            if (ReferenceEquals(element, root)) return true;
            element = VisualTreeHelper.GetParent(element);
        }
        return false;
    }

    /// <summary>2초 홀드 승격 타이머(A58): 홀드 세션이 아직 살아 있으면 반투명 고정으로 승격.</summary>
    private DispatcherTimer MakePinTimer(OverlaySide side)
    {
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(OverlayPinHoldMs) };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            if (side.HoldSessionActive && side.State == OverlayState.Holding)
            {
                side.State = OverlayState.TranslucentPinned; // 이제 키를 떼도 유지된다
                ApplyOverlayStates(); // 안내 문구(Pinned)가 이 시점부터 보인다
            }
        };
        return timer;
    }

    /// <summary>
    /// 홀드 판정 리셋(A58): 다른 키·포인터 개입, 창 비활성화(KeyUp 유실), 콘텐츠 전환에서
    /// 부른다. 홀드 중(Holding)이던 오버레이만 즉시 내리고, 반투명 고정·불투명 밀어내기
    /// 상태는 유지한다. 2연타 카운트도 함께 리셋한다.
    /// </summary>
    private void ResetOverlayInput()
    {
        var changed = CancelHoldCore(_listSide) | CancelHoldCore(_infoSide);
        _listSide.LastTapDown = DateTime.MinValue;
        _infoSide.LastTapDown = DateTime.MinValue;
        if (changed) ApplyOverlayStates();
    }

    /// <summary>홀드 세션만 종료한다. 반환값 = 표시가 바뀌어야 하는지(Holding을 닫았는지).</summary>
    private static bool CancelHoldCore(OverlaySide side)
    {
        side.PinTimer?.Stop();
        side.HoldSessionActive = false;
        if (side.State != OverlayState.Holding) return false;
        side.State = OverlayState.Closed;
        return true;
    }

    /// <summary>
    /// 외부에서 좌/우 오버레이의 불투명 밀어내기 상태를 지정한다 — 시작 경로별 기본 표시
    /// 상태(A81: 파일 인자 없이 모듈로 연 창은 양쪽 불투명, 부록 B 30번)용 공개 API.
    /// WindowManager가 창 생성 진입에서 1회만 부른다 — 이후 모듈 전환·파일 열기는
    /// 사용자가 바꾼 상태를 그대로 유지(재적용 없음)하고, 세션 간 저장도 없다(A55 미포함).
    /// true = OpaqueDocked, false = 닫힘. 반투명 고정은 키 입력 전용이라 여기서 만들지 않는다.
    /// </summary>
    public void SetDockedState(bool listDocked, bool infoDocked)
    {
        CancelHoldCore(_listSide);
        CancelHoldCore(_infoSide);
        _listSide.State = listDocked ? OverlayState.OpaqueDocked : OverlayState.Closed;
        _infoSide.State = infoDocked ? OverlayState.OpaqueDocked : OverlayState.Closed;
        ApplyOverlayStates();
    }

    /// <summary>
    /// 상태 머신 → 화면 반영 (A58). 표시 여부·모드(반투명/불투명)·안내 문구·도크 컬럼을
    /// 한 곳에서 일괄 갱신한다. 오버레이 컨텍스트가 없으면(빈 셸·설정·H/W) 상태와 무관하게 숨긴다 —
    /// 상태 자체는 남아 있어 다음 파일을 열면 같은 모드로 되살아난다(기존 고정 유지 규칙).
    /// 파일 없이 연 파일 모듈(빈 모듈 상태)도 컨텍스트다(A81): 좌측 리스트는 모듈 시작 폴더,
    /// 우측 정보는 "No file open" 플레이스홀더를 보여준다.
    /// </summary>
    private void ApplyOverlayStates()
    {
        var hasFile = _currentFilePath is not null;
        var emptyModule = IsEmptyFileModule; // 파일 없이 연 파일 모듈 — A81부터 오버레이 컨텍스트
        var listShow = (hasFile || emptyModule) && _listSide.State != OverlayState.Closed;
        var infoShow = (hasFile || emptyModule) && _infoSide.State != OverlayState.Closed;

        if (listShow) ShowListOverlay();
        else ListOverlay.Hide();
        // ShowListOverlay가 폴더 부재 등으로 못 띄웠을 수 있다 — 문구는 컨트롤이 IsOpen 기준으로 판단
        ListOverlay.SetState(
            _listSide.State == OverlayState.OpaqueDocked
                ? OverlayMode.OpaqueDocked : OverlayMode.TranslucentOver,
            pinned: _listSide.State == OverlayState.TranslucentPinned);

        if (infoShow && hasFile)
            InfoOverlay.ShowFor(_currentFilePath!, ModuleHost.Content as IContentInfoProvider);
        else if (infoShow)
            InfoOverlay.ShowPlaceholder(); // 빈 모듈 상태 — 보여줄 파일 정보가 없다 (A81)
        else
            InfoOverlay.Hide();
        InfoOverlay.SetState(
            _infoSide.State == OverlayState.OpaqueDocked
                ? OverlayMode.OpaqueDocked : OverlayMode.TranslucentOver,
            pinned: _infoSide.State == OverlayState.TranslucentPinned);

        // 불투명 밀어내기(OpaqueDocked)만 실제 공간을 차지한다: 도크 컬럼을 3*로 키워
        // 메인(ModuleHost/ExplorerHost)을 반대쪽으로 축소 — 한쪽 3:7, 양쪽 3:4:3 (좌우 30% 유지).
        // 오버레이 내부 3:7(좌)·7:3(우) 분할이 전폭 기준 정확히 30%라 도크 컬럼과 정렬된다.
        var left = ListOverlay.IsOpen && _listSide.State == OverlayState.OpaqueDocked ? 3 : 0;
        var right = InfoOverlay.IsOpen && _infoSide.State == OverlayState.OpaqueDocked ? 3 : 0;
        LeftDockColumn.Width = new GridLength(left, GridUnitType.Star);
        RightDockColumn.Width = new GridLength(right, GridUnitType.Star);
        CenterColumn.Width = new GridLength(10 - left - right, GridUnitType.Star);

        // A81: 빈 모듈 상태에서 좌측 리스트가 불투명 도크로 떠 있으면 중앙 탐색기는 숨긴다 —
        // 같은 폴더의 파일 목록이 나란히 두 번 보이는 중복 제거. 도크를 닫으면 다시 나타난다.
        // 반투명(홀드·고정)은 중앙을 덮는 표시라 그대로 둔다(아크릴 아래로 비쳐 보이는 게 정상).
        if (emptyModule)
            ExplorerHost.Visibility = left > 0 ? Visibility.Collapsed : Visibility.Visible;
    }

    /// <summary>
    /// 좌측 리스트 오버레이 표시: 현재 파일이 있는 폴더 + 현재 모듈의 담당 확장자(A57 ③)를
    /// 주입한다 — A7 드롭다운은 리스트 안에서 그 목록을 추가로 좁힌다.
    /// 파일이 없으면(빈 모듈 상태 — A81) 모듈 시작 폴더(마지막 폴더/바탕화면)를 대신 쓴다 —
    /// 중앙 빈 상태 탐색기와 같은 규칙.
    /// 폴더가 사라졌으면(이동식 드라이브 탈착 등) 띄우지 않는다 — 문구·도크는 IsOpen 기준으로 따라온다.
    /// </summary>
    private void ShowListOverlay()
    {
        if (_currentModule is null)
        {
            ListOverlay.Hide();
            return;
        }
        var folder = _currentFilePath is not null
            ? Path.GetDirectoryName(_currentFilePath)
            : ModuleStartFolder(_currentModule);
        if (folder is not { Length: > 0 } || !Directory.Exists(folder))
        {
            ListOverlay.Hide();
            return;
        }
        ListOverlay.Show(folder, _currentModule.SupportedExtensions);
    }

    // ---------- 현재 모드 시각 표시 (v0.20.0 → v0.26.0 개편) ----------

    /// <summary>
    /// 현재 모드 표시 갱신: 색 구분은 하단 바 스트립/칩 색 대신 창(타이틀바·작업표시줄)·
    /// 트레이의 모듈 색 KOTU 아이콘이 담당한다(사용자 요청, v0.26.0).
    /// 칩은 모듈 글리프+브랜드명을 중립색으로만 표시.
    /// </summary>
    private void UpdateModeIndicator(IModule? module, bool isSettings = false)
    {
        ApplyWindowIcon(module?.Id);

        if (module is null && !isSettings)
        {
            ModeChip.Visibility = Visibility.Collapsed;
            return;
        }

        // 모듈이 하단 바 줄을 차지하면 칩은 생략 (v0.21.0)
        ModeChip.Visibility = ModuleBarHost.Content is null
            ? Visibility.Visible : Visibility.Collapsed;

        if (isSettings || module is null)
        {
            ModeChipIcon.Glyph = "\uE713"; // Settings gear
            ModeChipText.Text = "Settings";
        }
        else
        {
            ModeChipIcon.Glyph = module.IconGlyph;
            ModeChipText.Text = module.BrandName;
        }
    }

    /// <summary>현재 모듈 색 .ico 경로 — 인스턴스 번호 변경 시 재합성 기준(A68).</summary>
    private string? _moduleIconPath;

    /// <summary>타이틀바·작업표시줄·트레이 아이콘을 현재 모듈 색 KOTU 아이콘으로 교체(v0.26.0).</summary>
    private void ApplyWindowIcon(string? moduleId)
    {
        var name = moduleId switch
        {
            "archive" => "app-archive.ico",
            "image" => "app-image.ico",
            "video" => "app-video.ico",
            "audio" => "app-audio.ico",
            "hardware" => "app-hardware.ico",
            "document" => "app-document.ico", // 아직 미생성 — 아래 File.Exists로 중립 아이콘 대체

            _ => "app.ico", // 빈 셸·설정·미지원 파일 = 중립(브랜드 색)
        };
        var path = Path.Combine(AppContext.BaseDirectory, "Assets", name);
        if (!File.Exists(path)) path = IconPath;
        if (!File.Exists(path)) return;

        _moduleIconPath = path;
        RefreshShellIcons();
    }

    /// <summary>
    /// 창·트레이 아이콘을 현재 모듈 색 + 인스턴스 번호로 다시 지정한다(A68).
    /// 창이 2개 이상이면(_instanceNumber &gt; 0) 인스턴스 색 테두리와 원형 번호 배지를
    /// 합성한 아이콘, 하나뿐이면 무테두리 원본 — 배지·제목 번호 숨김 규칙과 일관.
    /// 모듈 전환(ApplyWindowIcon)과 번호 변경(SetInstanceNumber) 양쪽에서 불린다.
    /// AppWindow.SetIcon은 원본 경로 유지 — 실제 표시는 직후 WM_SETICON(WindowIcon)이 덮는다.
    /// </summary>
    private void RefreshShellIcons()
    {
        if (_moduleIconPath is not { } path || !File.Exists(path)) return;

        AppWindow.SetIcon(path);
        WindowIcon.Apply(this, path, _instanceNumber);
        _tray.SetIcon(path, _instanceNumber);
    }

    // ---------- 최소화 = 트레이로 숨김 (A69) ----------

    /// <summary>
    /// 트레이로 숨긴 상태(A69) — 작업표시줄·Alt+Tab에 없고 창별 트레이 아이콘으로만 복귀한다.
    /// 숨김은 닫힘이 아니다: WindowManager의 창 목록 제거는 Closed에서만 일어나므로
    /// 마지막 창까지 숨겨도 열린 창으로 계산되어 프로세스가 유지된다(창 0개 = 종료 로직과 무충돌).
    /// </summary>
    private bool _hiddenInTray;

    /// <summary>
    /// 최소화 전이 감지(A69). 이 이벤트가 올 땐 창이 이미 -32000으로 이동한 뒤라(A55와 같은 관찰)
    /// 최소화 애니메이션이 끝나 있다 — "애니메이션 후 Hide" 순서가 자연히 성립한다.
    /// A39 핀(always on top) 창도 예외 없다: 사용자가 직접 띄워둔 창이라도 최소화 버튼을
    /// 눌렀다는 사실이 우선(일관성). 실제 숨김은 큐로 미뤄 Changed 디스패치 중의 재진입을 피한다.
    /// </summary>
    private void OnMinimizeStateChanged(Microsoft.UI.Windowing.AppWindow sender,
        Microsoft.UI.Windowing.AppWindowChangedEventArgs args)
    {
        if (_hiddenInTray) return;
        if (sender.Presenter is not Microsoft.UI.Windowing.OverlappedPresenter
            { State: Microsoft.UI.Windowing.OverlappedPresenterState.Minimized }) return;

        _hiddenInTray = true; // 연쇄 Changed(위치·크기·Z순서)로 중복 큐잉되지 않게 먼저 표시
        DispatcherQueue.TryEnqueue(() =>
        {
            if (!_hiddenInTray) return; // 큐 대기 중 트레이 좌클릭으로 이미 복귀했으면 숨기지 않는다
            // 숨김 동안만 WS_EX_TOOLWINDOW(부록 B 18번 사양 메모) — Hide가 주 동작이고
            // 스타일은 숨김 창을 순환 목록에 남기는 셸 변형에 대한 보조 방어선이다.
            AltTabExclusion.Set(this, true);
            AppWindow.Hide(); // 작업표시줄 버튼 제거. 트레이 아이콘(_tray)은 창 수명 내내 남는다
        });
    }

    /// <summary>
    /// 창을 앞으로 — 트레이 좌클릭·메뉴 'Activate window'·파일 열기 재사용(A24)·재전달 공용.
    /// A69: 트레이로 숨긴 창이면 Alt+Tab 제외를 풀고 다시 보인 뒤 최소화까지 해제한다 —
    /// 숨김 상태의 복귀 경로는 전부 이 메서드로 모인다.
    /// </summary>
    public void BringToFront()
    {
        if (_hiddenInTray)
        {
            _hiddenInTray = false;
            AltTabExclusion.Set(this, false); // 보이기 전에 스타일부터 원복 — Alt+Tab·작업표시줄 정상 노출
            AppWindow.Show();
        }
        // Show(SW_SHOW)는 최소화 상태를 바꾸지 않는다 — 숨김 전이 최소화였으므로 명시적으로
        // 복원한다. 숨김이 아니어도(숨김 실패·경합) 최소화된 창의 활성화면 같은 복원이 필요하다.
        if (AppWindow.Presenter is Microsoft.UI.Windowing.OverlappedPresenter
            { State: Microsoft.UI.Windowing.OverlappedPresenterState.Minimized } op)
        {
            op.Restore();
        }
        AppWindow.MoveInZOrderAtTop();
        Activate();
    }

    // ---------- 인스턴스 번호 배지 (A2, v0.58.0) ----------
    // 색 팔레트는 InstanceIcon.ColorFor로 이동(A68) — 배지·아이콘 테두리·트레이가 공유한다.

    /// <summary>
    /// 인스턴스 번호 설정. 0 = 창이 하나뿐 → 배지·제목 번호 모두 숨김.
    /// 창이 2개가 되는 순간 1번 창에도 생기고, 중간 창이 닫히면
    /// WindowManager가 번호를 당겨서 다시 부른다.
    /// 표시는 세 곳: 타이틀바 원형 색상 배지(A2 — 색이 9개뿐이라 1~9만),
    /// 제목 문자열 접두 "[n]"(A56 — 개수 제한 없음, 작업표시줄·Alt+Tab에서도 구분되게),
    /// 창·트레이 아이콘의 인스턴스 색 테두리 + 원형 번호(A68 — 10번째부터 색 순환).
    /// </summary>
    public void SetInstanceNumber(int number)
    {
        var previous = _instanceNumber;
        _instanceNumber = number > 0 ? number : 0;
        ApplyTitle();
        if (_instanceNumber != previous) RefreshShellIcons(); // 아이콘 테두리·트레이 갱신 (A68)

        if (number is <= 0 or > 9)
        {
            InstanceBadge.Visibility = Visibility.Collapsed;
            return;
        }
        InstanceBadge.Visibility = Visibility.Visible;
        InstanceBadge.Background = new SolidColorBrush(InstanceIcon.ColorFor(number));
        InstanceBadgeText.Text = number.ToString();
    }
}
