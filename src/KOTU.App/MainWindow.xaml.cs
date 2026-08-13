using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.ApplicationModel.DataTransfer;
using Windows.System;
using KOTU.App.Controls;
using KOTU.App.Overlays;
using KOTU.Input;
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

    // ---- 내장 탐색기 + 좌/우 오버레이 입력 상태 머신 (A58 상태 전이 + A86 키·일괄 토글) ----
    // 키 할당(A86, v0.121.0 — A58의 Alt/Shift·부록 B 26번을 대체): Z = 좌측 파일 리스트 / X = 우측 정보.
    // 사이드마다 4상태: Closed(닫힘) / Holding(키 홀드 — 반투명 덮기, 메인 크기 불변) /
    //   TranslucentPinned(2초 이상 홀드 — 키를 떼도 반투명 유지) /
    //   OpaqueDocked(2연타 — 불투명 + 메인을 반대쪽 7*로 축소, 양쪽이면 3:4:3).
    // 2연타 = 불투명 도크 고정/해제(A58 유지). A86 추가: **열림 상태(고정·도크)에서 해당 키 1회 = 그 쪽 닫기**
    //   (keymap S3 행 — 판정 충돌은 "첫 탭 이전 상태" 기준 2연타로 푼다, OnOverlaySideDown 참고).
    // 셸 수준 구성 상태(S1~S4, ShellState)는 Enter 일괄 토글·경계 버튼의 분배 기준 — 아래 CurrentShellState.
    // 홀드 판정은 다른 키·포인터(클릭·휠 — Ctrl+휠 줌 포함)가 함께 개입하면 취소된다(A58 안전장치 유지).
    // Alt의 OS 메뉴 모드 회피 로직은 제거 — Z/X는 문자 키라 근거가 사라졌다(A86 확정).
    private IModule? _currentModule;      // 지금 보여주는 모듈 (탐색기 필터·리스트 오버레이에 사용)
    private string? _currentFilePath;     // 현재 콘텐츠 파일 (null = 빈 상태 → 탐색기 표시)
    private ThumbnailExplorer? _thumbnailExplorer; // S1 중앙 썸네일 뷰 (A93, 지연 생성 — 구 ExplorerPane 대체)
    // S4('오픈 파일' 탐색, A90) 중앙 반투명 썸네일 — S1 인스턴스와 공유하지 않는 별도 인스턴스.
    // 두 그리드가 동시에 뜨는 상태는 없지만(S1에서는 S4 진입 자체가 없음 — 강조만), 공유하면
    // 부모(ExplorerHost/S4CenterHost) 사이를 옮겨 다니는 reparent 함정(옛 부모에서 먼저 제거 —
    // v0.111.0 COMException 전례)에 걸려서다.
    private ThumbnailExplorer? _s4Explorer;

    // ---- 하단 바 드라이브 줄 (A22, v0.108.0) ----
    // 표시 컨트롤은 셸에 하나(공용 DriveStrip)만 두고 모듈 하단 바가 슬롯을 내준다(IDriveStripHost).
    // 보임 조건은 "파일이 열려 있지 않을 때"뿐이라, 새 상태 플래그 없이 _currentFilePath를 그대로 쓴다.
    private DriveStrip? _driveStrip;          // 지금 모듈 바에 끼워둔 줄 (뷰마다 새로 만든다)
    private IDriveStripHost? _driveStripHost; // 그 줄을 받은 모듈 뷰

    private enum OverlayState { Closed, Holding, TranslucentPinned, OpaqueDocked }

    /// <summary>
    /// 셸 수준 "구성 상태" (A86 keymap): A58의 오버레이별 4상태를 대체하는 게 아니라 그 위에서
    /// "지금 화면 구성이 어떤 조합인가"를 요약한다 — Enter 일괄 토글·경계 버튼 분배의 기준.
    /// None = 오버레이 컨텍스트 없음(빈 셸·설정·H/W·미지원 안내) — keymap 표 밖, 셸 키 무동작.
    /// S4('오픈 파일' 탐색 모드)의 진입/복귀는 A90(v0.122.0) — 아래 '오픈 파일' 버튼 절 참고.
    /// </summary>
    private enum ShellState { None, S1, S2, S3L, S3R, S3B, S4 }

    /// <summary>'오픈 파일' 탐색 모드(S4, A90) 진행 중인지 — A86이 심어 둔 훅이 실제 상태가 됐다.</summary>
    private bool IsOpenFileBrowsing => _openFileBrowsing;

    private bool _openFileBrowsing;

    /// <summary>
    /// S4 진입 직전의 좌/우 오버레이 상태 스냅샷(A90) — Esc/'오픈 파일' 재누름 복귀의 원본.
    /// A86 <see cref="_lastBatchStates"/>(Enter 일괄 닫기의 복원 기억)와는 별개 개념이라 섞지 않는다.
    /// 파일이 열려 S4가 자동 종료되면 버린다(복귀 없음 — 새 콘텐츠가 화면을 차지).
    /// </summary>
    private (OverlayState List, OverlayState Info)? _s4Restore;

    /// <summary>A86 keymap의 구성 상태 판정. 홀드(반투명 덮기)도 "열림"으로 센다 — 표의 상태 기준.</summary>
    private ShellState CurrentShellState
    {
        get
        {
            if (IsOpenFileBrowsing) return ShellState.S4; // A90 — '오픈 파일' 탐색 진입 중
            if (IsEmptyFileModule) return ShellState.S1;
            if (_currentFilePath is null) return ShellState.None;
            var left = _listSide.State != OverlayState.Closed;
            var right = _infoSide.State != OverlayState.Closed;
            return (left, right) switch
            {
                (true, true) => ShellState.S3B,
                (true, false) => ShellState.S3L,
                (false, true) => ShellState.S3R,
                _ => ShellState.S2,
            };
        }
    }

    /// <summary>오버레이 한쪽(좌 = Z 리스트 / 우 = X 정보)의 입력·표시 상태.</summary>
    private sealed class OverlaySide
    {
        public OverlayState State;
        public bool KeyIsDown;          // 물리 키가 눌려 있는지 — Z+X 동시 누름(조합) 감지용
        public bool HoldSessionActive;  // 이번 누름이 홀드 판정 세션인지 (2연타·조합·취소면 아님)
        public DateTime LastTapDown = DateTime.MinValue; // 2연타 판정 (down→down)
        public OverlayState TapStartState;               // 첫 탭 "이전" 상태 — A86 2연타 판정 기준
        public DispatcherTimer? PinTimer; // 2초 홀드 → 반투명 고정 승격 (생성자에서 배선)
    }

    private const double OverlayDoubleTapMs = 450;  // 2연타 판정 창 (v0.32.0 값 유지)
    private const double OverlayPinHoldMs = 2000;   // 홀드 → 반투명 고정 승격 시간 (A58)

    private readonly OverlaySide _listSide = new(); // 좌측 파일 리스트 (Z)
    private readonly OverlaySide _infoSide = new(); // 우측 정보 (X)

    /// <summary>
    /// Enter 일괄 닫기 직전의 좌/우 구성 — "직전 구성 복원"(A86 keymap Q3)의 세션 한정 기억.
    /// null = 아직 일괄 닫기를 안 했다 → 복원 시 A81 기본 세트(좌+우 불투명 도크). 저장하지 않는다.
    /// </summary>
    private (OverlayState List, OverlayState Info)? _lastBatchStates;

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

        // 좌측 파일 리스트 오버레이(A57 ②) 배선 — 열기 이벤트는 기존(v0.25.0) 홀드 리스트와 동일 경로
        ListOverlay.Settings = _settings;                                  // 정렬 키 저장(A5)
        ListOverlay.FileActivated += OpenFileRouted;                       // 재사용 규칙 적용(A24)
        ListOverlay.FileActivatedNewWindow += _manager.OpenFileInNewWindow; // Shift+더블클릭·우클릭 메뉴
        // A93: S1 중앙 썸네일 뷰는 좌 리스트(ExplorerPane)와 폴더·필터(A7)·정렬(A5) 상태를 공유한다 —
        // 리스트가 다시 그려질 때마다 같은 목록을 받아 타일로 그린다.
        // A90: S4('오픈 파일' 탐색)가 떠 있는 동안은 같은 목록이 S4 오버레이 썸네일로만 흐른다 —
        // 데이터 경로는 좌 리스트 하나뿐이고, 받는 그리드가 상태에 따라 갈릴 뿐이다
        // (S1과 S4는 동시에 성립하지 않는다: S1에서는 S4 진입 자체가 없음 — 강조만).
        ListOverlay.ViewChanged += (folder, entries) =>
        {
            if (_openFileBrowsing) _s4Explorer?.ShowEntries(folder, entries);
            else if (IsEmptyFileModule) _thumbnailExplorer?.ShowEntries(folder, entries);
        };
        // A93 드랍 규칙: 우측 인포 영역 드랍 = 그 파일 열기 — 콘텐츠가 없으면 OpenFile의
        // 라우터(A59)가 담당 모듈로 전환한 뒤 여는 기존 경로를 그대로 쓴다.
        InfoOverlay.FileDropped += OpenFile;

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

        // Z/X 오버레이 입력 감지(A58 전이 + A86 키 — v0.25.0 Alt/Ctrl 홀드 → A58 Alt/Shift 대체):
        // 포커스가 모듈 뷰 안에 있어도 받도록 창 루트에서 handledEventsToo로 구독한다.
        // 포인터 개입(클릭·휠)도 홀드 판정 취소 트리거(A58 안전장치 유지 — 휠은 A84에서 추가,
        // Ctrl+휠 줌 개입도 같은 경로로 취소된다: A86 확정. 줌 재정의 자체는 A98 몫).
        // 창 비활성화로 KeyUp을 놓치면 홀드 판정·키 상태를 초기화(고정·불투명 상태는 유지).
        RootLayout.AddHandler(UIElement.KeyDownEvent, new KeyEventHandler(OnRootKeyDown), handledEventsToo: true);
        RootLayout.AddHandler(UIElement.KeyUpEvent, new KeyEventHandler(OnRootKeyUp), handledEventsToo: true);
        RootLayout.AddHandler(UIElement.PointerPressedEvent,
            new PointerEventHandler(OnRootPointerIntervened), handledEventsToo: true);
        RootLayout.AddHandler(UIElement.PointerWheelChangedEvent,
            new PointerEventHandler(OnRootPointerIntervened), handledEventsToo: true);
        // A86 경계 버튼: 마우스가 경계 근처에 왔을 때만 보이므로 이동·이탈을 창 루트에서 감시한다
        // (handledEventsToo — 오버레이·모듈 뷰가 이동 이벤트를 소비해도 근접 판정은 계속 돌아야 한다).
        RootLayout.AddHandler(UIElement.PointerMovedEvent,
            new PointerEventHandler(OnRootPointerMoved), handledEventsToo: true);
        RootLayout.PointerExited += (_, _) => HideEdgeButtons();
        CenterArea.SizeChanged += (_, _) => UpdateEdgeButtons(); // 경계 x 좌표는 실폭 기준
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
    /// 저장값이 없으면 기본 크기·위치.
    /// A89(v0.114.0): 다중 인스턴스(A24)도 **저장값을 그대로 승계**한다 — A55의 +32px 계단식
    /// 오프셋은 폐기. 살아 있는 창과 정확히 겹쳐도 비켜 주지 않는다(사용자 확정: "그대로 승계").
    /// 화면 밖 보정(ClampToWorkArea)과 A40 최소 크기 클램프는 그대로 거친다.
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
                    // A89: 저장 위치를 오프셋 없이 그대로 쓴다(마지막에 닫은 창 승계).
                    // 클램프는 최소 크기 반영 후의 최종 크기로 계산해야 맞는다(A40 정합).
                    AppWindow.MoveAndResize(ClampToWorkArea(
                        new Windows.Graphics.RectInt32(x, y, w, h)));
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
    /// A61: 하단 바만 남긴 접힘 중에는 **높이만** 기록하지 않는다 — 접힌 높이가 저장돼서는 안 되고
    /// (A55와의 상호작용 조건), 접힌 채로 사용자가 바꾼 폭·위치는 그대로 저장돼야 하기 때문이다.
    /// </summary>
    private void TrackNormalBounds(Microsoft.UI.Windowing.AppWindow sender,
        Microsoft.UI.Windowing.AppWindowChangedEventArgs args)
    {
        if (!args.DidSizeChange && !args.DidPositionChange) return;
        if (sender.Presenter is not Microsoft.UI.Windowing.OverlappedPresenter
            { State: Microsoft.UI.Windowing.OverlappedPresenterState.Restored }) return;
        _lastNormalPos = sender.Position;
        if (_barOnlyCollapsed && _lastNormalSize is { } kept)
            _lastNormalSize = new Windows.Graphics.SizeInt32(sender.Size.Width, kept.Height); // 높이만 동결
        else
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
    /// 하단 바만 남긴 접힘(A61)도 일시 모드로 취급 — 접기 전 크기(TrackNormalBounds가 높이를
    /// 동결해 둔 값) + 현재 위치를 저장한다. 접힌 높이를 저장하면 다음 실행이 납작하게 열린다.
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
            else if (_barOnlyCollapsed)
            {
                SaveBounds(_lastNormalSize, _lastNormalPos, maximized: _preCollapseMaximized);
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

    // ---------- 하단 바만 남기는 접힘 (A61, v0.111.0) ----------

    /// <summary>지금 하단 바만 남기고 접혀 있는지. 접힘의 판단(핀 ON && 전체화면 아님)은 모듈 뷰가 한다.</summary>
    private bool _barOnlyCollapsed;

    /// <summary>접기 직전 창 높이(물리 픽셀) — 펼칠 때 그대로 되돌린다. 폭·위치는 건드리지 않는다.</summary>
    private int _preCollapseHeight;

    /// <summary>접기 직전이 최대화 상태였는지 — 최대화된 창은 Resize가 먹지 않아 먼저 Restore한다.</summary>
    private bool _preCollapseMaximized;

    /// <summary>
    /// 창을 "타이틀바 + 하단 바 44"만 남기고 접거나(A61) 접기 전 높이로 되돌린다.
    /// 타이틀바는 남긴다 — 드래그 이동·닫기 수단이고 인스턴스 번호 `[n]`(A56) 표기가 거기 있다.
    /// 폭·위치는 그대로 두고 높이만 바꾼다. 같은 상태 요청은 무시(멱등).
    /// 전체화면 중에는 접지 않는다(프레젠터가 OverlappedPresenter가 아니다) — 뷰가
    /// "핀 ON && 전체화면 아님"으로 계산해 보내므로 정상 경로에선 오지 않는 방어선이다.
    /// </summary>
    private void SetBarOnlyCollapsed(bool collapse)
    {
        if (collapse == _barOnlyCollapsed) return;
        if (AppWindow.Presenter is not Microsoft.UI.Windowing.OverlappedPresenter presenter)
        {
            // 전체화면 프레젠터에서는 창 크기를 만질 수 없다. 펼치기 요청이면 상태와 최소 높이
            // 제약만 정리해 둔다 — 창 모드로 돌아온 뒤의 접기 요청이 정상적으로 먹어야 한다.
            // (정상 경로에선 뷰가 전체화면 진입 **전에** 펼치기를 보내므로 여기 오지 않는다.)
            if (!collapse) ResetCollapseState();
            return;
        }

        try
        {
            if (collapse)
            {
                // 실측은 창 상태를 바꾸기 전에 — 지금 창 크기와 지금 레이아웃(ScaleHost)이 서로
                // 맞는 시점이어야 테두리 두께가 정확하다(Restore 직후엔 레이아웃이 아직 옛 값이다).
                var scale = RasterScale();
                if (scale <= 0) return;  // XamlRoot 준비 전 — 접지 않는다(무동작이 낫다)
                var height = CollapsedHeight();
                if (height <= 0) return; // 아직 레이아웃 전 = 실측 불가
                _preCollapseMaximized =
                    presenter.State == Microsoft.UI.Windowing.OverlappedPresenterState.Maximized;
                // 최대화 상태에서는 Resize가 먹지 않는다 — 일반 상태로 내린 뒤 접는다.
                // 되돌릴 높이는 A55가 추적해 둔 일반 높이(Restore 직후 Size는 아직 최대화 값일 수 있다).
                _preCollapseHeight = AppWindow.Size.Height;
                if (_preCollapseMaximized)
                {
                    if (_lastNormalSize is { } normal) _preCollapseHeight = normal.Height;
                    presenter.Restore();
                }
                _barOnlyCollapsed = true; // TrackNormalBounds가 높이를 동결하도록 Resize보다 먼저
                // A40의 최소 높이 540 DIP를 접힌 높이로 잠시 낮춘다 — 이걸 빼면 접히지 않는다.
                WindowMinSize.SetMinHeightOverride(this, height / scale);
                AppWindow.Resize(new Windows.Graphics.SizeInt32(AppWindow.Size.Width, height));
            }
            else
            {
                var restoreHeight = _preCollapseHeight;
                var wasMaximized = _preCollapseMaximized;
                // 플래그·최소 높이 하한을 Resize보다 먼저 원복한다 —
                // 그래야 이어지는 크기 변화를 TrackNormalBounds(A55)가 다시 정상 기록한다.
                ResetCollapseState();
                if (restoreHeight > 0)
                    AppWindow.Resize(new Windows.Graphics.SizeInt32(
                        AppWindow.Size.Width, restoreHeight)); // 폭은 접힘 중 값 그대로
                if (wasMaximized) presenter.Maximize(); // 최대화로 접었으면 최대화로 돌아간다
            }
        }
        catch
        {
            // 창 조작 실패가 앱을 멈추면 안 된다 — 접힘만 안 될 뿐 나머지는 그대로 동작한다.
        }
    }

    /// <summary>접힘 관련 상태와 최소 높이 오버라이드(A40 하한 복귀)를 한 번에 되돌린다.</summary>
    private void ResetCollapseState()
    {
        _barOnlyCollapsed = false;
        _preCollapseHeight = 0;
        _preCollapseMaximized = false;
        WindowMinSize.SetMinHeightOverride(this, null); // 720×540 하한 복귀
    }

    /// <summary>XamlRoot 배율(시스템 DPI / 96). 준비 전이면 0 — 호출부가 실측 실패로 처리한다.</summary>
    private double RasterScale() => RootLayout.XamlRoot?.RasterizationScale ?? 0;

    /// <summary>
    /// 접힌 창의 전체 높이(물리 픽셀) = 비클라이언트(타이틀바 + 테두리) + 하단 바 44 DIP.
    /// **타이틀바 높이를 하드코딩하지 않는다**(A61): 창 전체 높이 - 클라이언트 높이로 실측한다
    /// (ScaleHost는 UI 스케일 변환 밖의 최상위 그리드라 그 ActualHeight가 곧 클라이언트 DIP).
    /// 하단 바는 UI 스케일 오버라이드(v0.24.0)가 걸리면 실제로 그만큼 커지므로 배율을 함께 곱한다.
    /// 레이아웃 전이거나 배율을 못 읽으면 0을 돌려 접기를 포기한다.
    /// </summary>
    private int CollapsedHeight()
    {
        var scale = RasterScale();
        if (scale <= 0 || ScaleHost.ActualHeight <= 0) return 0;
        var frame = AppWindow.Size.Height - (int)Math.Round(ScaleHost.ActualHeight * scale);
        if (frame < 0) frame = 0; // 이론상 없음 — 음수 높이 방지
        return frame + (int)Math.Ceiling(BottomBarHeight * _uiScaleFactor * scale);
    }

    // ---------- 단축키 (v0.45.0 사용자 지정) ----------

    /// <summary>
    /// 모듈 번호(메뉴 아래→위 순서): 1=All Readable, 2=이미지, 3=영상, 4=오디오, 5=문서,
    /// 6=압축, 7=하드웨어.
    /// A96(v0.116.0, 2026-08-13 사용자 확정): All Readable을 **1번**으로 올리고 나머지는
    /// **기존 순서 그대로 한 칸씩 밀었다**. A59(v0.113.0)의 "1~6 근육기억 유지, 신설은 7"을 대체한다.
    /// A10: 오디오 모듈이 영상 다음에 삽입되며 문서 이후가 한 칸씩 밀림(사용자 확정).
    /// A32: Ctrl 없이 숫자 단독(사용자 확정) — Ctrl은 정보 오버레이로 회귀.
    /// 힌트 문자열은 시작 메뉴 항목 마우스 오버 시 툴팁으로 보조 표시된다.
    /// ⚠️ 번호를 또 바꾸면 <see cref="BuildStartMenu"/> 항목 순서 · A34 키 맵 표
    /// (docs/REQUIREMENTS.md) · docs/A86-keymap.md 를 **한 번에** 고쳐야 한다(번호가 사방에 박혀 있다).
    /// </summary>
    private static readonly (string Id, VirtualKey Key, string Hint)[] ModuleShortcuts =
    [
        (KOTU.Module.AllReadable.AllReadableModule.ModuleId, VirtualKey.Number1, "1"),
        ("image", VirtualKey.Number2, "2"),
        ("video", VirtualKey.Number3, "3"),
        ("audio", VirtualKey.Number4, "4"),
        ("document", VirtualKey.Number5, "5"),
        ("archive", VirtualKey.Number6, "6"),
        ("hardware", VirtualKey.Number7, "7"),
    ];

    private const string SettingsShortcutHint = "0";

    /// <summary>시작 메뉴 키 = `(숫자 1 왼쪽, VK_OEM_3). 툴팁 표기(A34)도 이 값으로 조립한다.</summary>
    private const string MenuShortcutHint = "`";

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
        // A34: 하단 바 메뉴 버튼도 툴팁에 키를 표기한다(문자열은 여기서만 만든다).
        ToolTipService.SetToolTip(StartButton, $"Menu ({MenuShortcutHint})");
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

    /// <summary>
    /// 포커스가 텍스트 입력 컨트롤(TextBox·PasswordBox·RichEditBox 계열)에 있는지.
    /// A34에서 판정 자체는 공용 헬퍼(HotkeySupport)로 옮겼다 — 모듈 버튼 핫키가 같은 규칙을 쓴다.
    /// 셸 키(숫자·`·Shift+N)는 파일 리스트 포커스에서는 계속 동작해야 하므로
    /// 리스트 통과까지 보는 ShouldPassThrough가 아니라 텍스트 입력만 보는 이 판정을 쓴다.
    /// </summary>
    private bool IsTextInputFocused() => HotkeySupport.IsTextInputFocused(RootLayout);

    /// <summary>단축키·센서 트레이(A18)로 모듈 전환. 이미 그 모듈이면 아무것도 하지 않는다(보던 파일 보호).</summary>
    internal void OpenModuleById(string id)
    {
        if (CurrentModuleId == id) return;
        var module = _router.Modules.FirstOrDefault(m => m.Id == id);
        if (module is not null) OpenModule(module);
    }

    // ---------- 시작 메뉴 (하단 바에서 위로 떠오르는 플라이아웃) ----------

    /// <summary>
    /// 시작 메뉴 구성. 패널은 **위→아래**로 채우는데 **번호는 아래에서 위로** 올라간다
    /// (1번이 메뉴 최하단) — 그래서 <see cref="ModuleShortcuts"/> 순서를 뒤집어 넣는다.
    /// A96(v0.116.0) 이후 배치(위→아래):
    /// 광고 · 구분선 · Settings(0) · **구분선** · 하드웨어(7) · 구분선 · 압축(6) · 구분선 ·
    /// 문서(5) · 오디오(4) · 영상(3) · 사진(2) · **구분선** · All Readable(1).
    /// 굵게 표시한 구분선 2개가 A96 신규다 — ① 1번과 2번 사이 ② 하드웨어와 Settings 사이
    /// (둘이 서로 붙어 보인다는 사용자 지적).
    /// </summary>
    private void BuildStartMenu()
    {
        StartMenuPanel.Children.Clear();

        // A79 ③: 워드마크는 메뉴의 맨 위 — 광고 카드 **위**다. 아래에 두면 "광고에 딸린 문구"로
        // 보이고, 위에 두면 메뉴 머리글이 된다. 꺼져 있으면 요소 자체를 만들지 않는다(빈 자리 없음).
        if (BrandAssets.CreateWordmark(32) is { } wordmark)
        {
            wordmark.Margin = new Thickness(6, 2, 6, 6);
            StartMenuPanel.Children.Add(wordmark);
        }

        // 최상단: 스폰서(광고) 자리 — 지금은 MSI 로고 플레이스홀더, 파일 교체만으로 변경 가능
        StartMenuPanel.Children.Add(BuildSponsorCard());
        StartMenuPanel.Children.Add(Divider()); // 그룹 경계는 구분선으로 명확히 (v0.26.0 사용자 요청)

        // New Instance 항목은 A65(v0.92.0)에서 제거 — 메뉴 항목만 뺐고,
        // Shift+N(A84 — 기존 Ctrl+N)·탐색기 Shift+더블클릭·우클릭 "Open in new instance"·
        // 설정 토글 진입로는 그대로다.
        AddSettingsItem(); // 키 0 — 번호 배열과 무관하게 항상 최상단(광고 바로 아래)
        StartMenuPanel.Children.Add(Divider()); // A96 신규 ②: 하드웨어 인포 ↔ Settings 분리

        AddModuleItem("hardware"); // 7
        StartMenuPanel.Children.Add(Divider());

        AddModuleItem("archive"); // 6
        StartMenuPanel.Children.Add(Divider());

        // 문서-오디오-영상-사진 그룹 (아래부터 사진 → 위로 갈수록 문서)
        AddModuleItem("document"); // 5 — v0.44.0 실제 모듈로 교체 (텍스트·마크다운 1단계)
        AddModuleItem("audio"); // 4 — 음악 재생 분리 (A10, v0.75.0)
        AddModuleItem("video"); // 3
        AddModuleItem("image"); // 2
        StartMenuPanel.Children.Add(Divider()); // A96 신규 ①: 1번 ↔ 2번 분리

        AddModuleItem(KOTU.Module.AllReadable.AllReadableModule.ModuleId); // 1 — A96에서 최하단으로
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
            // A31(v0.66.0): 히트 영역 확대 — 상하 패딩 8→12 + 최소 높이 44 (터치 타깃 권장 크기).
            // ⚠️ A96(v0.116.0)이 "항목 높이 −10%" 지시로 **44→40 · 상하 패딩 12→10**으로 조정했다.
            //    A31의 44는 이제 유효하지 않다 — 되돌리지 말 것(아이콘 16 + 상하 패딩 20 = 36이라
            //    실제 높이를 정하는 것은 MinHeight 쪽이다).
            // 좌우는 10 유지: 메뉴 폭 136(A96 — v0.35.0의 124에서 +10%) 안에서 라벨 말줄임을 늘리지 않기 위해.
            // A50(v0.92.0): 좌측 히트 영역은 플라이아웃 프레젠터 패딩 0(XAML)으로 확대 —
            // Stretch인 버튼이 메뉴 좌우 가장자리까지 닿아, 포인터가 라벨보다 왼쪽이어도 눌린다.
            Padding = new Thickness(10, 10, 10, 10),
            MinHeight = 40,
        };
        if (shortcutHint is not null)
            ToolTipService.SetToolTip(button, shortcutHint);
        return button;
    }

    private Image? _sponsorImage;

    /// <summary>광고 카드(A67) — 커서·툴팁·클릭을 이미지가 아니라 카드가 받는다.</summary>
    private SponsorCard? _sponsorCard;

    /// <summary>지금 보이는 광고의 링크(A67). null이면 클릭 무반응 — 링크 없는 광고의 현행 동작.</summary>
    private SponsorLink? _sponsorLink;

    private UIElement BuildSponsorCard()
    {
        // v0.43.0(사용자 스샷 피드백): 광고가 카드 전 영역을 차지하고(패딩 제거, 메뉴 폭에 맞춰 확대),
        // SPONSOR 라벨은 이미지 위 좌상단에 반투명 배지로 겹쳐서 아주 작게 표시한다.
        var host = new SponsorCard
        {
            CornerRadius = new CornerRadius(8),
            Background = (Brush)Application.Current.Resources["LayerFillColorDefaultBrush"],
            Padding = new Thickness(2),
            MinHeight = 66, // 아래 이미지 높이와 같은 값 — 광고가 없을 때도 카드 크기가 유지된다
        };
        _sponsorCard = host;
        // A67(v0.109.0): 이미지·SPONSOR 배지 어디를 눌러도 광고를 누른 것으로 친다
        // (Tapped는 자식에서 카드로 버블링된다). 링크가 없으면 핸들러가 그냥 돌아간다.
        host.Tapped += OnSponsorTapped;

        if (SponsorAds.Any)
        {
            // 광고 표시 규격 132×66 (v0.54.0 사용자 확대 지시 — 원본 2:1 비율 유지 확대).
            // 폭은 메뉴 폭에 묶여 있다: StartMenuPanel 136 − 카드 Padding 2×2 = 132.
            // A96(v0.116.0)에서 메뉴 폭 124→136이 되며 120×60 → 132×66으로 함께 커졌다.
            _sponsorImage = new Image
            {
                Width = 132,
                Height = 66,
                Stretch = Stretch.Uniform,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
            };
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

        UpdateSponsorImage(); // 첫 이미지·링크 반영 (커서는 카드가 로드된 뒤에 걸린다)
        return host;
    }

    /// <summary>
    /// 메뉴가 열릴 때 현재 분 기준 광고로 갱신한다(로직은 SponsorAds 공용, v0.50.0).
    /// A67(v0.109.0): 이미지와 함께 링크·커서·툴팁도 그 광고의 것으로 바꾼다 —
    /// 링크 없는 광고는 커서 기본·툴팁 없음·클릭 무반응(현행 유지).
    /// </summary>
    private void UpdateSponsorImage()
    {
        if (_sponsorImage is not null) SponsorAds.Apply(_sponsorImage);
        if (_sponsorCard is null) return;

        _sponsorLink = SponsorAds.CurrentLink();
        _sponsorCard.SetHandCursor(_sponsorLink is not null);
        ToolTipService.SetToolTip(_sponsorCard, _sponsorLink?.Tip);
    }

    /// <summary>
    /// 광고 클릭(A67, v0.109.0) — 스폰서 링크를 기본 브라우저로 연다.
    /// 확인 창은 두지 않는다(미션 문구의 "silent in-app ad"와 정합) — 대신 매핑을 읽을 때
    /// http/https만 통과시켜 안전을 확보한다. 클릭하면 시작 메뉴는 닫는다(사용자 확정).
    /// </summary>
    private void OnSponsorTapped(object sender, TappedRoutedEventArgs e)
    {
        if (_sponsorLink is not { } link) return; // 링크 없는 광고 = 아무 일도 일어나지 않는다
        e.Handled = true;
        StartFlyout.Hide();
        _ = Windows.System.Launcher.LaunchUriAsync(link.Url);
    }

    /// <summary>
    /// 광고 카드 호스트(A67, v0.109.0). WinUI 3에는 공개 커서 속성이 없고
    /// UIElement.ProtectedCursor가 protected라 파생 클래스에서만 지정할 수 있어 Grid를 상속했다.
    /// 커서는 자식(이미지·SPONSOR 배지) 위에서도 그대로 적용된다.
    /// ProtectedCursor는 비주얼 트리에 붙기 전에는 지정할 수 없으므로, 로드 전 요청은 예약만 하고
    /// Loaded에서 실제로 건다(플라이아웃 콘텐츠라 메뉴를 열 때마다 로드된다).
    /// </summary>
    private sealed partial class SponsorCard : Grid
    {
        private bool _loaded;
        private bool _hand;

        public SponsorCard()
        {
            Loaded += (_, _) =>
            {
                _loaded = true;
                ApplyCursor();
            };
            Unloaded += (_, _) => _loaded = false;
        }

        /// <summary>true = 손가락 커서(링크 있는 광고), false = 기본 화살표.</summary>
        public void SetHandCursor(bool hand)
        {
            _hand = hand;
            if (_loaded) ApplyCursor();
        }

        private void ApplyCursor()
        {
            try
            {
                ProtectedCursor = Microsoft.UI.Input.InputSystemCursor.Create(
                    _hand ? Microsoft.UI.Input.InputSystemCursorShape.Hand
                          : Microsoft.UI.Input.InputSystemCursorShape.Arrow);
            }
            catch
            {
                // 커서 하나 때문에 메뉴가 죽으면 안 된다 — 모양만 기본으로 남는다.
            }
        }
    }

    /// <summary>
    /// 시작 메뉴 그룹 구분선: 여백 + 1px 라인 (v0.26.0, 공백만으로는 정리가 안 보인다는 피드백).
    /// 상하 여백 8→3 (A50 항목 간격 축소, v0.92.0) — 항목 높이는 그대로 두고 사이 여백만 줄인다.
    /// ※ A50 기록의 "항목 높이 44(A31)"는 A96(v0.116.0)에서 **40**으로 조정됐다.
    /// 여백 3은 유지 — A96이 바꾼 것은 항목 높이·메뉴 폭이지 구분선 여백이 아니다.
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

    /// <summary>설정 화면 열기.</summary>
    public void ShowSettings() => _ = ShowSettingsAsync();

    private async Task ShowSettingsAsync()
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
    }

    // ---------- 파일 열기 ----------

    /// <summary>
    /// 내장 탐색기·좌 리스트 오버레이의 일반 더블클릭 열기(A24): 재사용 규칙이 "항상 새 창"이면
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
        // A59: 지금 뷰가 "내가 안에서 연다"고 하는 모듈(All Readable)이면 모듈을 바꾸지 않는다 —
        // 창은 그대로 두고 센터·하단 바만 그 파일 형식의 자식 모듈로 갈린다.
        // 새 창으로 여는 경로(A24: Shift+더블클릭·우클릭 새 인스턴스·탐색기 더블클릭)는 새 창에
        // 아직 뷰가 없어 여기 걸리지 않는다 = 전용 모듈로 열리는 현행 동작 그대로다.
        if (ModuleHost.Content is IFileOpenTarget target)
        {
            if (!await ConfirmDiscardAsync()) return; // 자식 문서의 미저장 변경 (A37)
            if (target.TryOpenFile(path))
            {
                _titleDirtyMark = false;
                SetTitle(_currentModule is { } host
                    ? $"{Branding.AppName} {host.DisplayName} — {Path.GetFileName(path)}"
                    : $"{Branding.AppName} — {Path.GetFileName(path)}");
                IsUntouched = false;
                SetContentState(_currentModule, path); // 모듈은 그대로, 파일만 바뀐다
                return;
            }
            // 자식 모듈이 없는 형식(예: 라우팅 재정의로만 열리는 .json)은 아래 일반 라우팅으로 —
            // 그 파일의 전용 모듈로 창이 바뀐다("Unsupported file type"보다 낫다).
        }

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

    // ---------- 창 전체 드래그&드롭 → 파일 라우팅 (A93 드랍 규칙의 "콘텐츠 영역" 폴백) ----------
    // 특정 영역이 먼저 소비한 드랍은 여기 오지 않는다: 우측 인포 = 열기(ContentInfoOverlay),
    // 좌 패널·중앙 썸네일(S1) = 이동/복사(A94 1차, v0.124.0 — FileListOverlay·ThumbnailExplorer.
    // A93 당시의 무동작 소비를 실동작으로 전환), 압축 뷰 = 압축 생성(ArchiveView).
    // 여기 남는 것은 콘텐츠 영역·하단 바 등이다.

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
        if (path is null) return;

        // A93: 콘텐츠가 열려 있을 때 콘텐츠 영역 드랍 — 같은 종류(현재 모듈 담당 확장자)면
        // 그 자리에서 새로 열고, 다른 종류면 담당 모듈의 새 인스턴스를 만들어 거기서 연다
        // (WindowManager의 "파일로 새 창" 경로 재사용 — 담당 모듈이 없는 확장자도 그 경로에서
        // 기존 "Unsupported file type" 안내로 떨어진다). 콘텐츠가 없으면 종전대로 현재 창 라우팅.
        if (_currentFilePath is not null && _currentModule is { } module
            && !ExplorerListing.MatchesExtension(path, module.SupportedExtensions))
        {
            _manager.OpenFileInNewWindow(path);
            return;
        }
        OpenFile(path);
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
        // 하단 바만 남기는 접힘(A61): 판단은 뷰(핀 ON && 전체화면 아님), 실행은 셸.
        // 접기는 지금 보이는 뷰의 요청만 받고, 펼치기는 뷰가 내려간 뒤(Unloaded)에도 받아준다 —
        // 그래야 "모듈을 바꾸면 접힘도 함께 풀린다"(A39 자동 해제와 같은 규칙)가 성립한다.
        // UI 스레드에서 온 요청은 **큐에 넣지 않고 즉시** 처리한다: 전체화면 진입 직전의
        // "먼저 펼치기"가 SetPresenter보다 늦게 실행되면 접힌 크기가 전체화면의 복원 크기로 굳는다.
        // 다른 스레드면 ApplyUiScale과 같은 방식으로 디스패치한다.
        if (view is IWindowCollapseSource collapseSource)
            collapseSource.CollapseRequested += collapse =>
            {
                if (collapse && !ReferenceEquals(ModuleHost.Content, view)) return;
                if (DispatcherQueue is { } dq && !dq.HasThreadAccess)
                    dq.TryEnqueue(() => SetBarOnlyCollapsed(collapse));
                else
                    SetBarOnlyCollapsed(collapse);
            };
        // 미저장 표시(A37): 창 제목·트레이 툴팁에 ● — 뷰가 이미 교체됐으면 무시
        if (view is ICloseGuard guard)
            guard.UnsavedChanged += dirty => DispatcherQueue.TryEnqueue(() =>
            {
                if (!ReferenceEquals(ModuleHost.Content, view)) return;
                _titleDirtyMark = dirty;
                ApplyTitle();
            });
        // 트레이 아이콘 내용(A54): 모듈은 값만 내주고 아이콘 합성은 셸이 한다.
        // UI 스레드 보장이 없는 계약이라 디스패치하고, 뷰가 이미 교체됐으면 무시한다(A37과 같은 가드).
        if (view is ITrayStatusProvider trayStatus)
            trayStatus.TrayStatusChanged += () => DispatcherQueue.TryEnqueue(() =>
            {
                if (!ReferenceEquals(ModuleHost.Content, view)) return;
                UpdateTrayIcon();
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
        // A90: 콘텐츠·모듈이 바뀌면 S4('오픈 파일' 탐색)는 자동 종료 — 파일 열기(더블클릭·Enter·인포
        // 드랍)는 물론 숫자 키 모듈 전환·설정 진입도 같은 경로로 닫힌다. 새 콘텐츠가 화면을 차지하므로
        // 복귀 스냅샷은 버리고(restore:false), 좌/우는 지금 상태 그대로 A86 "상태는 콘텐츠를 넘어 유지"
        // 규칙을 탄다(자연 상태). 표시 갱신은 아래 ApplyOverlayStates가 하므로 여기서는 생략(refresh:false).
        ExitOpenFileBrowsing(restore: false, refresh: false);
        HideS1Flash(); // A90-b 강조가 콘텐츠 전환 뒤까지 남지 않게
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
        UpdateTrayIcon(); // A54: 모듈 전환·설정 전환·A59 안에서의 파일 교체까지 이 한 지점으로 모인다
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
        // A90: 뷰 내부 열기도 "새 콘텐츠가 화면을 차지"이므로 S4 자동 종료(SetContentState와 동일 규칙).
        ExitOpenFileBrowsing(restore: false, refresh: false);
        _currentFilePath = path;
        InfoOverlay.InvalidateCache();
        RememberLastFolder(); // v0.55.0
        UpdateEmptyExplorer();
        UpdateDriveStrip();   // A22: 뷰가 파일을 열었다 → 드라이브 줄을 숨긴다
        ApplyOverlayStates(); // 폴더·정보가 바뀌었을 수 있다 — 떠 있는 오버레이·도크 갱신
        UpdateTrayIcon();     // A54: 유휴(3자) → 열림(2줄) 전환도 이 경로로 걸린다
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

    /// <summary>
    /// 파일 모듈(파일 없이 열면 빈 상태 탐색기·A81 빈 도크가 성립)인지 — H/W·설정은 제외.
    /// A59: All Readable도 파일 모듈이다 — 빈 상태의 탐색기·도크 필터가 전 모듈 확장자 합집합이 된다.
    /// </summary>
    private static bool IsFileModule(IModule? module) =>
        module is { Id: "archive" or "image" or "video" or "audio" or "document" }
        || module?.Id == KOTU.Module.AllReadable.AllReadableModule.ModuleId;

    /// <summary>
    /// 파일 없이 연 파일 모듈(빈 모듈 상태 = A86 표의 S1)인지 — 중앙 썸네일 뷰
    /// (v0.25.0 탐색기 → A93)와 A81 빈 도크 오버레이가 공유하는 조건.
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
    /// 빈 상태(파일 없이 연 압축/이미지/동영상/오디오/문서 모듈)면 중앙에 썸네일 뷰를 띄운다
    /// (A93 — 구 ExplorerPane 중앙 탐색기 대체. A81의 "좌 도크 열림 시 숨김"도 대체 — 항상 표시).
    /// 시작 위치는 그 모듈의 마지막 폴더(v0.55.0, 없으면 바탕화면), 파일은 담당 확장자만.
    /// Hardware/Settings에는 띄우지 않는다.
    /// 목록의 원본은 좌 도크의 리스트(ExplorerPane) 하나다: 도크가 닫혀 있어도 NavigateList로
    /// 그 리스트를 항해시키면 ViewChanged가 돌아와 썸네일 뷰까지 같은 목록으로 채워진다.
    /// </summary>
    private void UpdateEmptyExplorer()
    {
        if (IsEmptyFileModule && _currentModule is { } module)
        {
            if (_thumbnailExplorer is null)
            {
                _thumbnailExplorer = new ThumbnailExplorer
                {
                    ModuleIdForFile = path => _router.Resolve(path)?.Id, // 액센트 색 타일(A93)
                };
                // 폴더 더블클릭 = 좌 리스트를 같은 폴더로 항해 — 결과가 ViewChanged로 돌아와
                // 양쪽이 함께 이동한다(A93 상태 공유). 파일 열기는 기존 라우팅 그대로(A24).
                _thumbnailExplorer.FolderActivated += folder => ListOverlay.NavigateList(folder);
                _thumbnailExplorer.FileActivated += OpenFileRouted;
                _thumbnailExplorer.FileActivatedNewWindow += _manager.OpenFileInNewWindow;
                ExplorerHost.Children.Add(_thumbnailExplorer);
            }
            ListOverlay.NavigateList(ModuleStartFolder(module), module.SupportedExtensions);
            ExplorerHost.Visibility = Visibility.Visible;
        }
        else
        {
            ExplorerHost.Visibility = Visibility.Collapsed;
        }
    }

    // ---------- 좌/우 오버레이 입력 상태 머신 (A58 전이 + A86 Z/X·Enter) ----------

    /// <summary>
    /// 키 → 오버레이 사이드 매핑 (A86 — A58의 Alt/Shift·부록 B 26번을 Z/X로 대체):
    /// Z = 좌측 파일 리스트, X = 우측 정보. 그 밖의 키는 null — 홀드 취소 트리거(다른 키 개입)로만 쓰인다.
    /// A34가 Z·X를 어느 모듈에도 배정하지 않고 비워 둔 것이 이 키의 예약이었다.
    /// </summary>
    private OverlaySide? SideForKey(VirtualKey key) => key switch
    {
        VirtualKey.Z => _listSide,
        VirtualKey.X => _infoSide,
        _ => null,
    };

    private void OnRootKeyDown(object sender, KeyRoutedEventArgs e)
    {
        // A90: Esc는 텍스트 입력 판정보다 먼저 본다 — keymap 포커스 예외가 "텍스트 입력에서도
        // Esc만은 통과"라서다(필터 입력란에 포커스를 둔 채로도 S4 복귀가 성립해야 한다).
        if (e.Key == VirtualKey.Escape)
        {
            OnShellEscape(e);
            return;
        }

        // A86 포커스 예외 ①(A32 통과 규칙 재사용): 텍스트 입력 컨트롤에 포커스가 있으면
        // Z·X·Enter 전부 입력이 우선이다(문서 에디터의 z/x 타이핑·Enter 줄바꿈을 뺏으면 안 된다).
        // Esc는 위에서 따로 처리했다 — 셸이 Esc를 소비하는 상태는 S4뿐(그 외는 종전대로 무개입).
        // 어떤 키든 홀드 판정·2연타 카운트만 리셋하고 흘려보낸다.
        if (IsTextInputFocused())
        {
            ResetOverlayInput();
            return;
        }

        if (e.Key == VirtualKey.Enter)
        {
            OnShellEnter(e); // A86 일괄 토글 — 원 기능 우선 판정 포함
            return;
        }

        var side = SideForKey(e.Key);
        if (side is null)
        {
            // 다른 키가 함께 눌림 → 진행 중 홀드 세션 취소(이미 떠 있으면 즉시 내림) +
            // 2연타 카운트 리셋 (A58 공통 안전장치 유지 — Shift+더블클릭 새 인스턴스·Shift+N 등
            // 조합 입력이 오버레이를 물지 않게).
            // 비수정자 키를 먼저 누르고 있던 조합은 그 키의 반복 입력이 곧 도착해 같은 경로로 취소된다.
            ResetOverlayInput();
            return;
        }

        // A86 포커스 예외 ②(Q4): 리스트/트리/썸네일 포커스에서는 Z/X가 타이핑 탐색(첫 글자 점프)
        // 우선이다 — 키를 삼키지 않고 판정만 접는다. 오버레이 여닫기는 Enter·경계 버튼·마우스 몫.
        if (HotkeySupport.ShouldPassThrough(RootLayout))
        {
            ResetOverlayInput();
            return;
        }

        // A90 keymap S4 행: Z/X·2연타 = 무동작 (Q5 확정) — 판정에 태우지 않고 소비만 한다.
        // (통과 표면 포커스는 위에서 이미 타이핑 탐색으로 흘렀다 — Q4 예외는 S4에서도 유효.)
        if (IsOpenFileBrowsing)
        {
            e.Handled = true;
            return;
        }

        var other = ReferenceEquals(side, _listSide) ? _infoSide : _listSide;
        side.KeyIsDown = true; // 반복 입력에서도 갱신 — Z+X 동시 누름 감지의 근거
        if (!e.KeyStatus.WasKeyDown) // 문자 키 오토리피트(홀드 중 반복 down)는 전이에 안 태운다
        {
            if (other.KeyIsDown)
                ResetOverlayInput(); // Z+X 오버레이 키끼리의 조합 — 양쪽 다 홀드 판정 없음
            else
                OnOverlaySideDown(side);
        }

        // Z/X는 오버레이 전용 키다(A34가 비워 둠) — 여기까지 왔으면(텍스트 입력·탐색기 표면 아님)
        // 흘려보낼 곳이 없어 컨텍스트가 있으면 소비한다(오토리피트 포함 — 반복 문자가 새지 않게).
        // Alt 시절의 "오버레이가 떠 있을 때만 소비"(OS 메뉴 모드 회피)는 근거가 사라져 제거(A86).
        if (_currentFilePath is not null || IsEmptyFileModule) e.Handled = true;
    }

    /// <summary>
    /// 사이드 키 최초 down(반복·조합 제외)의 상태 전이 (A58 전이 + A86 keymap):
    /// 닫힘에서 단독 down = 홀드 세션 시작(반투명 덮기 + 2초 승격 타이머) — "닫힌 오버레이 꺼내기".
    /// **열림(반투명 고정·불투명 도크)에서 단독 down = 그 쪽 닫기** (A86 신설 — keymap S3 행:
    /// S3L에서 Z = 좌 닫기. A58에서는 무동작이던 자리).
    /// 2연타 = 불투명 도크 고정/해제(A58 유지). 첫 탭이 이미 상태를 옮기므로 판정은
    /// **첫 탭 이전 상태(TapStartState)** 기준이다: 닫힘에서 시작한 2연타 = 도크,
    /// 열림에서 시작한 2연타 = 해제(첫 탭이 이미 닫았다 — 두 번째 탭은 그대로 둔다).
    /// </summary>
    private void OnOverlaySideDown(OverlaySide side)
    {
        if (IsOpenFileBrowsing) return; // A90 keymap S4: Z/X = 무동작 — OnRootKeyDown 가드의 이중 방어선

        // 오버레이 컨텍스트가 없으면(설정·H/W·미지원 파일 안내) 판정도 없다. 파일 없이 연
        // 파일 모듈(빈 모듈 상태)은 A81부터 컨텍스트에 포함 — 기본 도크를 키로 닫고
        // 다시 여는 입력이 성립해야 한다.
        if (_currentFilePath is null && !IsEmptyFileModule) return;

        var now = DateTime.UtcNow;
        if ((now - side.LastTapDown).TotalMilliseconds < OverlayDoubleTapMs)
        {
            side.LastTapDown = DateTime.MinValue;
            CancelHoldCore(side); // 이번 누름은 홀드 세션이 아니다 — 계속 눌러도 승격 없음
            side.State = side.TapStartState == OverlayState.Closed
                ? OverlayState.OpaqueDocked // 닫힘에서 시작한 2연타 = 불투명 밀어내기 (A58)
                : OverlayState.Closed;      // 열림에서 시작한 2연타 = 해제 — 첫 탭이 이미 닫힌 상태 유지
        }
        else
        {
            side.LastTapDown = now;
            side.TapStartState = side.State; // 다음 탭이 2연타가 되면 이 값이 판정 기준
            if (side.State == OverlayState.Closed)
            {
                side.State = OverlayState.Holding;
                side.HoldSessionActive = true;
                side.PinTimer?.Start(); // 2초 경과 시 TranslucentPinned로 승격
            }
            else if (side.State is OverlayState.TranslucentPinned or OverlayState.OpaqueDocked)
            {
                side.State = OverlayState.Closed; // A86: 열림 상태에서 해당 키 1회 = 그 쪽 닫기
            }
        }
        ApplyOverlayStates();
    }

    private void OnRootKeyUp(object sender, KeyRoutedEventArgs e)
    {
        var side = SideForKey(e.Key);
        if (side is null) return;

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
        // Alt 시절의 KeyUp 소비(OS 메뉴 모드가 Alt up에서 발동)는 문자 키 Z/X에서는 불필요 — 제거(A86).
    }

    // ---------- Enter 일괄 토글 (A86 keymap) ----------

    /// <summary>
    /// Enter 분배 (A86 keymap): S1 = 선택 파일 있으면 열기, 없으면 일괄 토글 /
    /// S2 = 일괄 토글(직전 구성 복원 — 세션 한정, 기억 없으면 A81 기본 세트 좌+우 도크) /
    /// S3L·S3R·S3B = 일괄 닫기 / S4 = 선택 열기 우선, 없으면 복귀와 동일(A90 — keymap S4 행).
    /// 원 기능 우선 예외: ① 텍스트 입력(에디터 줄바꿈)은 호출 전에 걸러진다 ②
    /// 탐색기 리스트/트리/썸네일 포커스 = 선택 항목 열기 우선(통과 표면 판정 — 단 S4 그리드는
    /// 예외에서 제외한다: 그 표면의 "원 기능"이 곧 아래 S4 분기 자신이다) ③ 영상 콘텐츠 =
    /// 전체화면 진입 — 모듈 액셀러레이터가 먼저 처리(Handled)하고, 이벤트 순서가 그 표식을
    /// 안 물고 올 가능성에 대비해 모듈 ID 가드를 이중으로 둔다(S4에서는 Enter가 셸 몫이라 제외 — A90.
    /// 영상 쪽도 통과 표면 포커스에서는 Enter 액셀러레이터를 흘린다: VideoPlayerView.OnFullScreenEnterInvoked).
    /// </summary>
    private void OnShellEnter(KeyRoutedEventArgs e)
    {
        if (e.KeyStatus.WasKeyDown) return; // 오토리피트 — 토글이 연사되면 안 된다

        // 구성 스냅샷은 홀드 취소보다 먼저 뜬다: 홀드(반투명 덮기)도 keymap 기준 "열림"이고
        // 복원 구성(Q3)에도 들어가야 한다. 홀드는 키를 떼면 사라지는 과도 상태라 반투명 고정으로 기억.
        var state = CurrentShellState;
        var snapshot = (List: Sticky(_listSide.State), Info: Sticky(_infoSide.State));
        ResetOverlayInput(); // Enter도 "다른 키 개입"이다 — 홀드 취소·2연타 리셋(A58 안전장치 유지)

        // 영상 Enter=전체화면 액셀러레이터·탐색기 그리드(Enter=선택 항목 열기, A90)가 이미 소비 — 원 기능 우선
        if (e.Handled) return;
        // 탐색기 표면 — 선택 항목 열기 우선. 단 S4 그리드 포커스는 통과시키지 않는다(A90):
        // 그리드가 선택을 직접 열었으면 위 Handled에서 이미 끝났고, 선택이 없어 안 삼킨 Enter는
        // keymap S4 행의 "없으면 복귀와 동일"이라 아래 S4 분기가 받아야 한다.
        if (HotkeySupport.ShouldPassThrough(RootLayout)
            && !(state == ShellState.S4 && IsFocusWithin(_s4Explorer))) return;
        if (state != ShellState.S4
            && _currentModule?.Id == "video" && _currentFilePath is not null) return; // 영상 이중 방어(위 요약 ③)

        switch (state)
        {
            case ShellState.S1:
                // 선택 파일 있으면 열기(keymap S1 행): 중앙 썸네일 우선, 다음 좌 리스트(떠 있을 때만).
                var selected = _thumbnailExplorer?.SelectedFilePath ?? ListOverlay.SelectedFilePath;
                e.Handled = true;
                if (selected is not null) OpenFileRouted(selected);
                else BatchToggleOverlays(snapshot);
                return;
            case ShellState.S2:
            case ShellState.S3L:
            case ShellState.S3R:
            case ShellState.S3B:
                // S2 = 되살리기, S3* = 일괄 닫기 — 분기는 스냅샷의 "하나라도 열림"이 겸한다.
                e.Handled = true;
                BatchToggleOverlays(snapshot);
                return;
            case ShellState.S4: // A90 keymap S4 행: 선택 열기 우선, 없으면 복귀와 동일
                e.Handled = true;
                if (_s4Explorer?.SelectedEntry is { } s4Entry)
                {
                    // 폴더 = 좌 리스트 항해(A93 상태 공유의 되돌이 경로 — ViewChanged로 그리드도 이동)
                    if (s4Entry.IsFolder) ListOverlay.NavigateList(s4Entry.Path);
                    else OpenFileRouted(s4Entry.Path); // 열리면 SetContentState가 S4를 자동 종료한다
                }
                else
                {
                    ExitOpenFileBrowsing(restore: true);
                }
                return;
            default:            // None — 오버레이 컨텍스트 없음(빈 셸·설정·H/W): 무동작, 삼키지도 않는다
                return;
        }
    }

    // ---------- Esc (A90 — 셸이 소비하는 상태는 S4뿐) ----------

    /// <summary>
    /// Esc 분배 (keymap): S4 = 진입 전 상태로 복귀 — 셸이 Esc를 소비하는 유일한 상태다.
    /// 그 외(S2 = 전체화면 해제 — 모듈 액셀러레이터 몫 / S3* = 무동작 Q8)는 종전대로 건드리지 않는다.
    /// 영상 전체화면 Esc와의 우선순위: 모듈 액셀러레이터가 이 루트 핸들러보다 먼저 돈다(A86 Enter에서
    /// 실증된 순서 — handledEventsToo 구독은 그 결과의 Handled를 본다). 그래서 셸이 역전시킬 수 없고,
    /// S4와 전체화면이 겹쳐 있으면(S4 중에도 모듈 하단 바 전체화면 버튼·비통과 포커스의 F11로 진입
    /// 가능) **첫 Esc = 전체화면 해제(액셀러레이터 소비), 다음 Esc = S4 복귀** 순서로 정리한다.
    /// 보통의 S4(전체화면 아님)에서는 액셀러레이터가 소비하지 않아 곧바로 S4 복귀다 — 이중 발화 없음.
    /// </summary>
    private void OnShellEscape(KeyRoutedEventArgs e)
    {
        ResetOverlayInput(); // 종전에도 Esc는 "다른 키 개입"으로 홀드 취소·2연타 리셋만 했다(A58 유지)
        if (!IsOpenFileBrowsing || e.KeyStatus.WasKeyDown || e.Handled) return;
        e.Handled = true;
        ExitOpenFileBrowsing(restore: true);
    }

    /// <summary>포커스 요소가 주어진 루트의 비주얼 트리 안에 있는지 (A90 — S4 그리드 포커스 판정).</summary>
    private bool IsFocusWithin(UIElement? root)
        => root is not null && RootLayout.XamlRoot is { } xr
           && FocusManager.GetFocusedElement(xr) is DependencyObject focused
           && IsWithin(focused, root);

    /// <summary>복원 기억용 상태 정규화: 홀드(키 홀드 중)는 반투명 고정으로 승격해 기억한다.</summary>
    private static OverlayState Sticky(OverlayState state) =>
        state == OverlayState.Holding ? OverlayState.TranslucentPinned : state;

    /// <summary>
    /// Enter 일괄 토글 실행부: 하나라도 열려 있으면 전부 닫고(직전 구성을 세션 한정 기억 — Q3),
    /// 전부 닫혀 있으면 기억한 구성으로, 기억이 없으면 A81 기본 세트(좌+우 불투명 도크)로 되살린다.
    /// </summary>
    private void BatchToggleOverlays((OverlayState List, OverlayState Info) snapshot)
    {
        if (snapshot.List != OverlayState.Closed || snapshot.Info != OverlayState.Closed)
        {
            _lastBatchStates = snapshot;
            _listSide.State = OverlayState.Closed;
            _infoSide.State = OverlayState.Closed;
        }
        else
        {
            var (list, info) = _lastBatchStates ?? (OverlayState.OpaqueDocked, OverlayState.OpaqueDocked);
            _listSide.State = list;
            _infoSide.State = info;
        }
        ApplyOverlayStates();
    }

    /// <summary>
    /// 포인터 개입(클릭·휠 — Ctrl+휠 줌 포함, A86 확정)도 홀드 판정을 취소한다(A58 안전장치,
    /// 휠은 A84에서 추가) — 클릭 다중 선택·더블클릭 새 인스턴스(A24)·휠 줌이
    /// 오버레이를 물고 있지 않게. 단, 그 오버레이 자신 안에서의 클릭·스크롤은 예외 —
    /// Z를 쥔 채 리스트에서 파일을 더블클릭해 열거나 목록을 휠로 넘기는
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

        // 불투명 밀어내기(OpaqueDocked)만 실제 공간을 차지한다: 도크 컬럼을 키워
        // 메인(ModuleHost/ExplorerHost)을 반대쪽으로 축소한다.
        // 도크 폭(A93): S1(빈 파일 모듈) = 25%씩 → 3구획 25:50:25 / 콘텐츠 상태 = 30%씩
        // (A57/A58 그대로 — 한쪽 3:7, 양쪽 3:4:3). 오버레이 내부 별 분할(SetPanelPercent)을
        // 같은 %로 맞춰야 불투명 밀어내기에서 도크 컬럼과 픽셀 단위로 정렬된다.
        var dockPercent = emptyModule ? 25.0 : 30.0;
        ListOverlay.SetPanelPercent(dockPercent);
        InfoOverlay.SetPanelPercent(dockPercent);
        var left = ListOverlay.IsOpen && _listSide.State == OverlayState.OpaqueDocked ? dockPercent / 10 : 0;
        var right = InfoOverlay.IsOpen && _infoSide.State == OverlayState.OpaqueDocked ? dockPercent / 10 : 0;
        LeftDockColumn.Width = new GridLength(left, GridUnitType.Star);
        RightDockColumn.Width = new GridLength(right, GridUnitType.Star);
        CenterColumn.Width = new GridLength(10 - left - right, GridUnitType.Star);

        // A93: S1 중앙은 항상 썸네일 뷰다 — A81의 "좌 도크가 불투명이면 중앙 탐색기 숨김"
        // (중복 목록 제거)을 대체한다. 중앙이 리스트가 아니라 타일이라 중복으로 보이지 않는다.
        // 열 수 = 좌우 도크가 둘 다 (불투명으로) 열려 있으면 4, 하나라도 닫히면 8 (A63 대체 —
        // 타일 크기는 SizeChanged에서 floor(실폭/열수)로 따라온다). 반투명(홀드·고정)은
        // 공간을 차지하지 않으므로 "닫힘"과 같게 센다 — 덮인 동안에도 뒤 타일은 전폭 기준.
        if (emptyModule)
            _thumbnailExplorer?.SetColumns(left > 0 && right > 0 ? 4 : 8);

        // A90 S4: 중앙 오버레이 썸네일 영역을 좌/우 패널이 덮는 폭만큼 비켜 세운다.
        // 반투명(TranslucentPinned) 패널은 도크 컬럼을 차지하지 않으므로(위 left/right 계산은
        // OpaqueDocked만 — S4의 반투명 추가가 도크 폭·경계 버튼 계산을 오염시키지 않는 근거)
        // S4 호스트가 스스로 같은 %의 스페이서를 잡아야 패널과 픽셀 정렬된다(SetPanelPercent 산식).
        // 열 수는 A93 규칙 준용(좌우 모두 떠 있으면 4, 아니면 8) — S4는 양쪽을 항상 채우므로 통상 4,
        // 폴더 소실로 리스트가 못 뜬 경우(IsOpen=false)만 8이 된다.
        if (_openFileBrowsing)
        {
            var s4Left = ListOverlay.IsOpen ? dockPercent : 0;
            var s4Right = InfoOverlay.IsOpen ? dockPercent : 0;
            S4LeftSpacer.Width = new GridLength(s4Left, GridUnitType.Star);
            S4RightSpacer.Width = new GridLength(s4Right, GridUnitType.Star);
            S4CenterColumn.Width = new GridLength(100 - s4Left - s4Right, GridUnitType.Star);
            _s4Explorer?.SetColumns(s4Left > 0 && s4Right > 0 ? 4 : 8);
        }

        UpdateEdgeButtons(); // A86 경계 버튼 — 경계 x·글리프가 상태를 따라온다 (S4에서는 숨김 — A90)
    }

    // ---------- 경계 버튼 (A86 keymap Q7) ----------

    /// <summary>경계 버튼이 경계선에서 메인 쪽으로 걸치는 깊이 — 버튼 폭 20의 절반(반씩 걸침).</summary>
    private const double EdgeButtonOverlap = 10;

    /// <summary>
    /// 근접 판정 반경(경계선 좌우 각각): 터치 타깃 관례 44px보다 약간 넓은 48 — 버튼(20px)을
    /// 노리고 다가가면 확실히 뜨되, 화면을 가로지르는 이동에 스치기만 해도 뜰 만큼 넓지는 않게.
    /// </summary>
    private const double EdgeProximity = 48;

    private double _leftEdgeX;   // 좌 경계선 x (CenterArea 기준) — 닫힘이면 0(창 가장자리)
    private double _rightEdgeX;  // 우 경계선 x — 닫힘이면 실폭(창 가장자리)

    /// <summary>
    /// 경계 버튼 위치·글리프 갱신 (A86): 경계선 = 그 쪽 패널의 화면 폭.
    /// 불투명 도크든 반투명(홀드·고정) 덮기든 열려 있으면 dockPercent%(25/30 — ApplyOverlayStates와
    /// 같은 값), 닫혀 있으면 0 = 창 가장자리(닫힌 상태에서도 같은 자리에서 꺼낼 수 있어야 한다).
    /// 버튼은 경계선에서 메인 쪽으로 절반(EdgeButtonOverlap) 걸친다 — "메인을 살짝 덮게"(A86 원문).
    /// 표시 여부는 근접 판정(OnRootPointerMoved)이 정하고, 여기서는 컨텍스트가 사라졌을 때만 감춘다.
    /// </summary>
    private void UpdateEdgeButtons()
    {
        var width = CenterArea.ActualWidth;
        if (width <= 0) return;
        var context = _currentFilePath is not null || IsEmptyFileModule;
        if (!context || IsOpenFileBrowsing) // S4에서는 표시 안 함(keymap) — A86 훅이 A90에서 살았다
        {
            HideEdgeButtons();
            return;
        }

        var dockPercent = IsEmptyFileModule ? 25.0 : 30.0; // ApplyOverlayStates의 상태별 폭과 동일(A93)
        _leftEdgeX = ListOverlay.IsOpen ? width * dockPercent / 100 : 0;
        _rightEdgeX = InfoOverlay.IsOpen ? width - width * dockPercent / 100 : width;
        LeftEdgeButton.Margin = new Thickness(Math.Max(0, _leftEdgeX - EdgeButtonOverlap), 0, 0, 0);
        RightEdgeButton.Margin = new Thickness(0, 0, Math.Max(0, width - _rightEdgeX - EdgeButtonOverlap), 0);
        // 글리프 = 누르면 일어날 일의 방향: 도크가 아니면 "불투명 도크로 밀어내기"(안쪽), 도크면 닫기(바깥쪽).
        LeftEdgeGlyph.Glyph = _listSide.State == OverlayState.OpaqueDocked ? "\uE76B" : "\uE76C";
        RightEdgeGlyph.Glyph = _infoSide.State == OverlayState.OpaqueDocked ? "\uE76C" : "\uE76B";
    }

    /// <summary>
    /// 마우스 근접 시에만 경계 버튼 표시 (A86 원문: "마우스가 근처에 갔을 때만").
    /// 경계선 x에서 EdgeProximity 이내이고 콘텐츠 영역 세로 범위 안일 때만 보인다.
    /// </summary>
    private void OnRootPointerMoved(object sender, PointerRoutedEventArgs e)
    {
        var context = _currentFilePath is not null || IsEmptyFileModule;
        if (!context || IsOpenFileBrowsing)
        {
            HideEdgeButtons();
            return;
        }
        var p = e.GetCurrentPoint(CenterArea).Position;
        var insideY = p.Y >= 0 && p.Y <= CenterArea.ActualHeight;
        LeftEdgeButton.Visibility = insideY && Math.Abs(p.X - _leftEdgeX) <= EdgeProximity
            ? Visibility.Visible : Visibility.Collapsed;
        RightEdgeButton.Visibility = insideY && Math.Abs(p.X - _rightEdgeX) <= EdgeProximity
            ? Visibility.Visible : Visibility.Collapsed;
    }

    private void HideEdgeButtons()
    {
        LeftEdgeButton.Visibility = Visibility.Collapsed;
        RightEdgeButton.Visibility = Visibility.Collapsed;
    }

    /// <summary>경계 버튼 동작 = 불투명 도크 토글 (A86 keymap Q7 확정 — 좌).</summary>
    private void OnLeftEdgeToggle(object sender, RoutedEventArgs e) => ToggleOpaqueDock(_listSide);

    /// <summary>경계 버튼 동작 = 불투명 도크 토글 (A86 keymap Q7 확정 — 우).</summary>
    private void OnRightEdgeToggle(object sender, RoutedEventArgs e) => ToggleOpaqueDock(_infoSide);

    /// <summary>불투명 도크면 닫고, 그 외(닫힘·홀드·반투명 고정)면 불투명 도크로 (Q7).</summary>
    private void ToggleOpaqueDock(OverlaySide side)
    {
        CancelHoldCore(side);
        side.LastTapDown = DateTime.MinValue; // 버튼 클릭이 키 2연타 판정에 섞이지 않게
        side.State = side.State == OverlayState.OpaqueDocked
            ? OverlayState.Closed
            : OverlayState.OpaqueDocked;
        ApplyOverlayStates();
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

    // ---------- '오픈 파일' 버튼 · S4 탐색 모드 (A90) ----------

    /// <summary>
    /// 하단 바 '오픈 파일' 버튼 (A90 — 시작 메뉴 버튼 바로 옆): 네이티브 파일 대화상자를 띄우지 않고
    /// 자체 탐색기를 쓴다. 분배는 keymap '오픈 파일' 행 그대로 — S1 = "이미 열려 있음" 강조만(A90-b,
    /// 복귀 개념 없음) / S2·S3* = S4 진입 / S4 = 진입 전 상태로 복귀(재누름).
    /// None(빈 셸·설정·H/W·미지원 안내)은 keymap 표 밖 — 띄울 탐색기 컨텍스트가 없어 무동작(구현 결정).
    /// 전체화면 중에는 하단 바가 통째로 숨어(AppWindow.Changed) 이 버튼 자체를 누를 수 없다 —
    /// 사양 밖이라 특별 처리하지 않는다(A90 확인 사항).
    /// </summary>
    private void OnOpenFileClick(object sender, RoutedEventArgs e)
    {
        switch (CurrentShellState)
        {
            case ShellState.S4:
                ExitOpenFileBrowsing(restore: true); // 재누름 = Esc와 동일(keymap '오픈 파일' 행)
                break;
            case ShellState.S1:
                FlashAlreadyOpen(); // A90-b: 새로 띄울 게 없다 — 의사 표시만
                break;
            case ShellState.S2:
            case ShellState.S3L:
            case ShellState.S3R:
            case ShellState.S3B:
                EnterOpenFileBrowsing();
                break;
            default:
                break; // None — 무동작(위 요약)
        }
    }

    /// <summary>
    /// S4 진입 (A90, keymap "S4 구성 규칙"): 이미 떠 있는 구획은 그대로 두고(다시 얹지 않음),
    /// 없는 구획만 반투명 고정(A58 TranslucentPinned 재사용 — 새 표시 모드를 만들지 않는다.
    /// 덮기 표시라 공간을 차지하지 않아 도크 폭·썸네일 열 수 계산도 오염시키지 않는다)으로 추가하고,
    /// 중앙 콘텐츠는 반투명 썸네일 탐색기(S4 전용 인스턴스)로 덮는다. 포커스는 썸네일 그리드로.
    /// 좌 리스트는 **현재 콘텐츠 파일의 폴더**로 항해한다(ApplyOverlayStates → ShowListOverlay가
    /// 파일 폴더 기준 — "보던 파일 근처에서 다음 파일을 고른다"가 자연스러워서다. S2·S3*에서만
    /// 진입하므로 파일은 항상 있고, 폴더가 사라진 경우만 모듈 시작 폴더로 폴백).
    /// 목록 원본은 S1과 같은 좌 리스트 하나 — 결과가 ViewChanged로 S4 그리드로 흐른다(생성자 배선).
    /// </summary>
    private void EnterOpenFileBrowsing()
    {
        if (_openFileBrowsing) return;
        if (_currentFilePath is null || _currentModule is null) return; // S2·S3* 전용 — 방어선
        ResetOverlayInput(); // Holding(키 홀드 과도 상태)이 스냅샷에 섞이지 않게 — 이후는 안정 상태 3종뿐
        _s4Restore = (_listSide.State, _infoSide.State);
        if (_listSide.State == OverlayState.Closed) _listSide.State = OverlayState.TranslucentPinned;
        if (_infoSide.State == OverlayState.Closed) _infoSide.State = OverlayState.TranslucentPinned;
        _openFileBrowsing = true;
        EnsureS4Explorer();
        S4Host.Visibility = Visibility.Visible;
        ApplyOverlayStates(); // ShowListOverlay가 현재 파일의 폴더로 Show → ViewChanged → S4 그리드 채움
        if (!ListOverlay.IsOpen) // 파일 폴더 소실(드라이브 탈착 등) — 모듈 시작 폴더로라도 목록을 만든다
            ListOverlay.NavigateList(ModuleStartFolder(_currentModule), _currentModule.SupportedExtensions);
        _s4Explorer?.FocusGrid();
    }

    /// <summary>
    /// S4 종료 (A90): restore=true(Esc·재누름·Enter 빈 선택) = 진입 전 스냅샷으로 복귀 —
    /// 이번에 추가된 구획만 내려가고 원래 있던 구획은 원래 모습(불투명이면 불투명) 그대로.
    /// S4 중에는 Z/X·경계 버튼이 전부 무동작이라 좌/우 상태가 변할 길이 없어, 스냅샷 전체 대입이
    /// 곧 "추가분만 되돌리기"와 같다. restore=false(콘텐츠 전환 = SetContentState/OnContentOpened) =
    /// 스냅샷을 버리고 좌/우는 지금 상태 그대로 A86 "상태는 콘텐츠를 넘어 유지" 규칙을 탄다.
    /// refresh=false는 호출부가 곧바로 ApplyOverlayStates를 부르는 경로(콘텐츠 전환)용.
    /// </summary>
    private void ExitOpenFileBrowsing(bool restore, bool refresh = true)
    {
        if (!_openFileBrowsing) return;
        _openFileBrowsing = false;
        S4Host.Visibility = Visibility.Collapsed;
        if (restore && _s4Restore is { } snap)
        {
            _listSide.State = snap.List;
            _infoSide.State = snap.Info;
        }
        _s4Restore = null;
        if (!refresh) return; // 콘텐츠 전환 경로 — 호출부(SetContentState 등)가 곧 표시를 다시 그린다
        ApplyOverlayStates();
        // 포커스가 방금 사라진 S4 그리드에 남지 않게 모듈 뷰로 되돌린다 — 실패해도 무해(포커스만 표류)
        (ModuleHost.Content as Control)?.Focus(FocusState.Programmatic);
    }

    /// <summary>
    /// S4 전용 썸네일 인스턴스 지연 생성 (A90). S1의 <see cref="_thumbnailExplorer"/>와 공유하지
    /// 않는다 — 같은 UIElement를 두 부모(ExplorerHost/S4CenterHost) 사이에서 옮기면 reparent 함정
    /// (옛 부모에서 먼저 제거 — v0.111.0 COMException 전례)에 걸린다. 배선은 S1 쪽과 동일 구성.
    /// </summary>
    private void EnsureS4Explorer()
    {
        if (_s4Explorer is not null) return;
        _s4Explorer = new ThumbnailExplorer
        {
            ModuleIdForFile = path => _router.Resolve(path)?.Id, // 액센트 색 타일(A93)
        };
        _s4Explorer.UseTranslucentBackground(); // 중앙을 "반투명으로 덮는다"(A90 원문 — A33 아크릴)
        _s4Explorer.FolderActivated += folder => ListOverlay.NavigateList(folder);
        _s4Explorer.FileActivated += OpenFileRouted; // 열리면 SetContentState가 S4를 자동 종료한다
        // 새 창 열기(Shift+더블클릭·우클릭)는 이 창의 콘텐츠가 안 바뀌므로 S4를 유지한다(구현 결정 —
        // 다른 창에 하나 열고 계속 고르는 흐름이 자연스럽다).
        _s4Explorer.FileActivatedNewWindow += _manager.OpenFileInNewWindow;
        S4CenterHost.Children.Add(_s4Explorer);
    }

    /// <summary>A90-b "이미 열려 있음" 강조 지속 시간 — 2연타 판정 창(450ms)과 같은 "잠깐"의 감각.</summary>
    private const double S1FlashMs = 450;

    private DispatcherTimer? _s1FlashTimer;

    /// <summary>
    /// A90-b: S1에서 '오픈 파일' = 새로 띄울 것 없음 — 중앙 썸네일 뷰 둘레의 액센트 테두리를 잠깐
    /// 비춘다. Storyboard 애니메이션은 CI(컴파일 전용)로 검증할 수 없고 실패 시 상태가 남을 수 있어
    /// (A92 페이드 선례) 타이머 + Visibility 두 단계로만 구현 — 최악의 실패도 "강조가 안 보인다"로
    /// 끝난다(A90 안전 요건).
    /// </summary>
    private void FlashAlreadyOpen()
    {
        try
        {
            S1FlashBorder.Visibility = Visibility.Visible;
            _s1FlashTimer ??= MakeS1FlashTimer();
            _s1FlashTimer.Stop(); // 연타 시 표시 시간 되감기 (A92 관용구)
            _s1FlashTimer.Start();
        }
        catch
        {
            HideS1Flash(); // 실패 시 테두리 잔존 방지 — 강조만 포기한다
        }
    }

    private DispatcherTimer MakeS1FlashTimer()
    {
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(S1FlashMs) };
        timer.Tick += (_, _) =>
        {
            timer.Stop(); // 반복 타이머 — Tick에서 반드시 멈춘다(MakePinTimer와 같은 관용구)
            S1FlashBorder.Visibility = Visibility.Collapsed;
        };
        return timer;
    }

    /// <summary>강조 즉시 종료 — 콘텐츠 전환(SetContentState)에서 테두리 잔존을 막는 안전선.</summary>
    private void HideS1Flash()
    {
        _s1FlashTimer?.Stop();
        S1FlashBorder.Visibility = Visibility.Collapsed;
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

    /// <summary>
    /// 지금 그려진 아이콘의 모듈 색 — 중립 아이콘이면 null (A79 브랜드 표식 ①/② 판단).
    /// 모듈 ID가 아니라 <b>실제로 고른 .ico</b>를 기준으로 정한다: 모듈 .ico가 없어 중립으로
    /// 폴백했으면 중립 표식이 맞기 때문.
    /// </summary>
    private Windows.UI.Color? _moduleIconAccent;

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
            KOTU.Module.AllReadable.AllReadableModule.ModuleId => "app-allreadable.ico", // A59

            _ => "app.ico", // 빈 셸·설정·미지원 파일 = 중립(브랜드 색)
        };
        var path = Path.Combine(AppContext.BaseDirectory, "Assets", name);
        if (!File.Exists(path)) path = IconPath;
        if (!File.Exists(path)) return;

        _moduleIconPath = path;
        _moduleIconAccent = path == IconPath ? null : Branding.ModuleAccent(moduleId); // A79
        RefreshShellIcons();
    }

    /// <summary>
    /// 창·트레이 아이콘을 현재 모듈 색 + 인스턴스 번호로 다시 지정한다(A68).
    /// 창이 2개 이상이면(_instanceNumber &gt; 0) 창 아이콘은 인스턴스 색 테두리와 원형 번호 배지를
    /// 합성한 것, 하나뿐이면 무테두리 원본 — 배지·제목 번호 숨김 규칙과 일관.
    /// 모듈 전환(ApplyWindowIcon)과 번호 변경(SetInstanceNumber) 양쪽에서 불린다.
    /// AppWindow.SetIcon은 원본 경로 유지 — 실제 표시는 직후 WM_SETICON(WindowIcon)이 덮는다.
    /// ※ A54(v0.118.0): 트레이 아이콘은 더 이상 모듈 .ico가 아니라 값 텍스트를 그린다
    ///   (<see cref="UpdateTrayIcon"/>). 인스턴스 표식은 테두리만 남고 번호 배지는 창 아이콘 전용.
    /// </summary>
    private void RefreshShellIcons()
    {
        if (_moduleIconPath is { } path && File.Exists(path))
        {
            AppWindow.SetIcon(path);
            WindowIcon.Apply(this, path, _moduleIconAccent, _instanceNumber);
        }
        UpdateTrayIcon();
    }

    // ---------- 트레이 아이콘 내용 (A54, v0.118.0) ----------

    /// <summary>마지막으로 그린 트레이 아이콘의 키 — 같으면 GDI 재합성을 통째로 건너뛴다(A18 방식).</summary>
    private string _trayIconKey = string.Empty;

    /// <summary>
    /// 모듈이 내준 <see cref="TrayStatus"/>를 16px 아이콘으로 합성해 트레이에 올린다(A54).
    /// 값을 내주지 않는 화면(설정·미지원 파일 안내)은 모듈 ID → 3자 표기 표로 유휴 아이콘을 그리고,
    /// 그 표에도 없으면(설정·빈 셸) 중립 모듈 .ico로 폴백한다 — 인스턴스당 아이콘 1개는 언제나 유지된다.
    /// 호출 시점: 모듈 전환·설정 전환·파일 열기(IContentStateSource)·모듈의 TrayStatusChanged·
    /// 인스턴스 번호 변경. 값이 그대로면 아무 일도 하지 않는다.
    /// </summary>
    private void UpdateTrayIcon()
    {
        var status = (ModuleHost.Content as ITrayStatusProvider)?.GetTrayStatus()
            ?? (IdleTrayLabel(CurrentModuleId) is { Length: > 0 } label ? TrayStatus.Idle(label) : null);

        var key = TrayStatusIcon.ComposeKey(status, CurrentModuleId, _instanceNumber);
        if (key == _trayIconKey) return;
        _trayIconKey = key;

        if (status is null)
        {
            _tray.SetIcon(_moduleIconPath, _moduleIconAccent, _instanceNumber);
            return;
        }

        var icon = TrayStatusIcon.Compose(status, Branding.ModuleAccent(CurrentModuleId), _instanceNumber);
        if (icon == IntPtr.Zero)
        {
            _trayIconKey = string.Empty; // 합성 실패(GDI 고갈 등) — 다음 갱신 때 다시 시도
            return;
        }
        _tray.SetRenderedIcon(icon);
    }

    /// <summary>
    /// 콘텐츠를 안 열고 있을 때의 모듈 3자 표기(A54 — 사용자 확정: IMG/VID/AUD/DOC/ARC/ALL).
    /// 정보(하드웨어) 모듈은 열 파일이 없어 값이 상수라 계약 대신 이 표가 담당한다 — 표기는 INF
    /// (BrandName "KOTU-info"와 정합. 2자 "HW"는 3자 규칙에서 벗어나고 "HWM"은 조어라 채택 안 함).
    /// 표에 없는 화면(설정·미지원 파일 안내)은 빈 문자열 → 중립 모듈 아이콘 폴백.
    /// </summary>
    private static string IdleTrayLabel(string? moduleId) => moduleId switch
    {
        "image" => "IMG",
        "video" => "VID",
        "audio" => "AUD",
        "document" => "DOC",
        "archive" => "ARC",
        "hardware" => "INF",
        KOTU.Module.AllReadable.AllReadableModule.ModuleId => "ALL",
        _ => string.Empty,
    };

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
