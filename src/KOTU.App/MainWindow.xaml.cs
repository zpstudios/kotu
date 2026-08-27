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

    /// <summary>하단 바 고정 두께(A40) — XAML BottomBar의 Height 44와 같아야 한다
    /// (A152: 구 BottomBarRow 행 분할은 폐지 — 바는 콘텐츠 위 오버레이).</summary>
    private const double BottomBarHeight = 44;

    private readonly FileTypeRouter _router;
    private readonly WindowManager _manager;
    private readonly TrayIcon _tray;
    private readonly ISettingsService _settings;
    private double _uiScaleFactor = 1.0; // 시스템 DPI 대비 상대 배율 (1.0 = 오버라이드 없음)
    private bool _xamlRootHooked;

    // ---- 내장 탐색기 + 좌/우 패널 입력 (A176 단타 토글 — 구 A58 상태 머신 폐지) ----
    // 키 할당(A158 — A118의 F1/F2를 대체): **F11** = 좌측 파일 리스트 / **F12** = 우측 정보.
    // 계보: A58 Alt/Shift → A86 Z/X → A107 Alt+Z/X → A118 F1/F2 → A158 F11/F12.
    // 단독 F키는 문자를 만들지 않아 문자 입력·리스트 첫 글자 점프와
    // 원천 무충돌 — 텍스트 입력 중에도 동작한다. 키 정본 = SideForKey 위 LeftPanelKey/RightPanelKey.
    // 수신 층(A226): F11/F12만 터널링(RootLayout.PreviewKeyDown — OnRootPreviewKeyDown)으로
    //   내장 소비보다 앞서 받고, 그 밖의 셸 키는 종전대로 버블(OnRootKeyDown)이다.
    // **A176(반투명 오버레이 폐지)**: 사이드마다 2상태 — Closed(닫힘) /
    //   OpaqueDocked(사이드바 — 불투명 + 메인을 반대쪽으로 축소. 폭은 전 상태 공통
    //   SidebarPercent(A116): 한쪽 25:75, 양쪽 25:50:25). **키 단타 = 그 쪽 토글**이 전부다.
    //   A58 계보의 홀드(반투명 덮기)/2초 홀드(반투명 피닝)/2연타(불투명) 판정 기계 — PinTimer·
    //   2연타 창·TapStartState·홀드 취소 안전장치(다른 키·포인터 개입)·A154 peek — 는 통째로
    //   철거됐다(반투명 표시 축 자체가 소멸). F11+F12를 이어 누르면 각 down이 독립 토글이라
    //   구 "동시 호출"도 자연 성립한다(특례 코드 불요).
    // 셸 수준 구성 상태(S1~S4, ShellState)는 '오픈 파일' 버튼·경계 버튼의 분배 기준 — 아래 CurrentShellState.
    // A186(모드2 폐지): Enter는 A151의 3단 순환에서 **Alt+Enter와 동일한 전체화면 토글**로
    // 단순화됐다 — 아래 ShellViewMode/_viewMode 절 참고.
    // A119(v0.145.0): 패널 컨텍스트에 "패널 제공 뷰"(ISidePanelProvider — 정보 모듈)가 추가됐다.
    //   그 뷰에서는 좌/우 패널 자리에 파일 리스트/정보 대신 모듈 고유 콘텐츠(SidePanelHost 호스트)가
    //   뜨고, 키·힌트·경계 버튼은 전부 같은 경로다. A196(게이트 완화)부터는 설정·미지원 안내·
    //   무제 문서(A189)도 컨텍스트다(좌 = 전역 마지막 폴더 리스트 / 우 = 플레이스홀더) —
    //   무소비로 남는 화면은 빈 셸(중앙에 아무 뷰도 없음)뿐이고 S4 중 무동작은 별도 게이트(사양).
    // Alt 단독 OS 메뉴 모드 회피(A86 제거 → A107 재도입)는 A176 뒤에도 존치 — A147(v0.163.0)이
    // Alt+숫자·Alt+0을 폐지한 뒤에도 **Alt+` 액셀러레이터 하나가 여전히 쓴다**(지우면 Alt를 눌렀다
    // 뗄 때마다 창 메뉴 모드로 빠진다). 우리 조합에 쓰인 Alt의 단독 up만 조건 소비
    // (_altComboUsed, OnRootKeyUp).
    private IModule? _currentModule;      // 지금 보여주는 모듈 (탐색기 필터·리스트 오버레이에 사용)
    private string? _currentFilePath;     // 현재 콘텐츠 파일 (null = 빈 상태 → 탐색기 표시)

    /// <summary>
    /// A189: 무제 문서(경로 없는 콘텐츠 — IUntitledContentSource, 문서 모듈 'New text file')가
    /// 중앙을 차지 중인지. <c>_currentFilePath</c>가 null이어도 빈 상태(S1 탐색기·드라이브 줄)로
    /// 취급하면 안 되는 상태 축이다 — 소비처는 IsEmptyFileModule·UpdateDriveStrip·TryNavigateBack
    /// (A202부터 TryCloseContent)·**HasPanelContext(A196 — 패널 컨텍스트 편입: F11/F12·경계 버튼이
    /// 동작하고 좌 리스트 = 전역 마지막 폴더 + 문서 모듈 필터, 우 정보 = 플레이스홀더)**다.
    /// 나머지 경로 기반 축(S4·32px 아이콘·마지막 폴더)은 종전 null 경로 폴백 그대로
    /// (무제는 보여줄 파일 정보가 없다 — 구현 결정).
    /// 세우는 곳 = OnUntitledOpened, 내리는 곳 = SetContentState(모듈 전환·실경로 열기)와
    /// OnContentOpened(무제 첫 저장 → 경로 확정).
    /// </summary>
    private bool _untitledContent;
    private ThumbnailExplorer? _thumbnailExplorer; // S1 중앙 썸네일 뷰 (A93, 지연 생성 — 구 ExplorerPane 대체)
    // S4('오픈 파일' 탐색, A90) 중앙 반투명 썸네일 — S1 인스턴스와 공유하지 않는 별도 인스턴스.
    // 두 그리드가 동시에 뜨는 상태는 없지만(S1에서는 S4 진입 자체가 없음 — 강조만), 공유하면
    // 부모(ExplorerHost/S4CenterHost) 사이를 옮겨 다니는 reparent 함정(옛 부모에서 먼저 제거 —
    // v0.111.0 COMException 전례)에 걸려서다.
    private ThumbnailExplorer? _s4Explorer;

    /// <summary>
    /// A200: 중앙 썸네일뷰(S1/_thumbnailExplorer · S4/_s4Explorer)의 파일 선택 축 — 값이 있으면
    /// 우측 정보 패널이 열린 콘텐츠 대신 **이 선택 파일**의 정보를 보여준다(탐색 중 문맥 우선,
    /// 해제되면 종전 열린 콘텐츠 기준으로 복귀). 폴더 선택·무선택 = null(파일만 — 구현 결정).
    /// IsPlaceholder(A175)를 같이 들고 다녀 선택 조회의 하이드레이션 가드에 쓴다.
    /// 세우는 곳 = OnBrowseSelectionChanged 하나, 내리는 곳 = 같은 메서드(해제) +
    /// SetContentState/OnContentOpened(열기 = 선택 축 리셋 — 열기 직후 선택이 열린 콘텐츠
    /// 정보를 가리는 역전 방지)/OnUntitledOpened/ExitOpenFileBrowsing/ViewChanged(목록 재작성 =
    /// 타일 전부 새로 만듦 — stale 경로 방지).
    /// </summary>
    private (string Path, bool IsPlaceholder)? _selectedBrowse;

    // ---- 하단 바 드라이브 줄 (A22, v0.108.0) ----
    // 표시 컨트롤은 셸에 하나(공용 DriveStrip)만 두고 모듈 하단 바가 슬롯을 내준다(IDriveStripHost).
    // 보임 조건은 "파일이 열려 있지 않을 때"뿐이라, 새 상태 플래그 없이 _currentFilePath를 그대로 쓴다.
    private DriveStrip? _driveStrip;          // 지금 모듈 바에 끼워둔 줄 (뷰마다 새로 만든다)
    private IDriveStripHost? _driveStripHost; // 그 줄을 받은 모듈 뷰

    /// <summary>좌/우 패널 상태 (A176 — 구 4상태(Holding·TranslucentPinned 포함)에서 반투명 축
    /// 제거): Closed = 닫힘 / OpaqueDocked = 사이드바(불투명 도크 — 유일한 열림 상태.
    /// 값 이름은 A108 이전 그대로 유지 — 식별자 보존 규칙).</summary>
    private enum OverlayState { Closed, OpaqueDocked }

    /// <summary>
    /// 셸 수준 "구성 상태" (A86 keymap): 패널별 상태를 대체하는 게 아니라 그 위에서
    /// "지금 화면 구성이 어떤 조합인가"를 요약한다 — '오픈 파일' 버튼·경계 버튼 분배의 기준.
    /// (A186: Enter는 이 상태와 무관한 전체화면 토글이다 — A151의 순환도, A86의 일괄 토글도 폐지.)
    /// None = 패널 컨텍스트 없음 — keymap 표 밖. A196부터 빈 셸(중앙에 아무 뷰도 없음)과
    /// **설정 화면(A205 — 사이드바 전면 배제로 되돌림)**뿐이다: 미지원 안내·무제 문서는 게이트
    /// 완화로 패널 컨텍스트에 편입돼 남는다(HasPanelContext).
    /// A119(v0.145.0): 정보(H/W)는 None에서 빠졌다 — 패널 제공 뷰(ISidePanelProvider)는 파일이
    /// 없어도 좌/우 조합으로 S2/S3*에 분류되어 경계 버튼이 파일 모듈과 같은 표를 탄다.
    /// S4('오픈 파일' 탐색 모드)의 진입/복귀는 A90(v0.122.0) — 아래 '오픈 파일' 버튼 절 참고.
    /// </summary>
    private enum ShellState { None, S1, S2, S3L, S3R, S3B, S4 }

    /// <summary>'오픈 파일' 탐색 모드(S4, A90) 진행 중인지 — A86이 심어 둔 훅이 실제 상태가 됐다.</summary>
    private bool IsOpenFileBrowsing => _openFileBrowsing;

    private bool _openFileBrowsing;

    /// <summary>
    /// S4 진입 직전의 좌/우 패널 상태 스냅샷(A90) — Esc/'오픈 파일' 재누름 복귀의 원본(A176: 2상태).
    /// A151 <see cref="_fullScreenRestore"/>(모드3 복귀 스냅샷)와는 별개 개념이라 섞지 않는다.
    /// 파일이 열려 S4가 자동 종료되면 버린다(복귀 없음 — 새 콘텐츠가 화면을 차지).
    /// </summary>
    private (OverlayState List, OverlayState Info)? _s4Restore;

    /// <summary>A86 keymap의 구성 상태 판정 (A176: 열림 = 사이드바 하나 — 반투명 축 소멸).
    /// A119: 패널 제공 뷰(정보 모듈)는 파일이 없어도 아래 좌/우 조합 분기로 떨어진다.
    /// ('오픈 파일' 버튼의 S2/S3* 분기는 EnterOpenFileBrowsing의 파일 가드가 무동작으로 거른다.)</summary>
    private ShellState CurrentShellState
    {
        get
        {
            if (IsOpenFileBrowsing) return ShellState.S4; // A90 — '오픈 파일' 탐색 진입 중
            if (IsEmptyFileModule) return ShellState.S1;
            // A196: None 판정을 게이트 공용 판정(HasPanelContext)에 묶는다 — 무제 문서·미지원
            // 안내도 좌/우 조합으로 S2/S3*에 분류된다(파일이 없어 '오픈 파일' 버튼은
            // EnterOpenFileBrowsing의 파일 가드가 종전과 같은 무동작으로 거른다).
            // A205: 설정 화면은 게이트에서 빠져 A196 이전 분류(None)로 복귀한다 — 빈 셸과 같은
            // 행이라 경계 버튼·'오픈 파일' 버튼 분배가 keymap 표 밖으로 돌아간다.
            if (!HasPanelContext) return ShellState.None;
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

    /// <summary>패널 한쪽(좌 = F11 리스트 / 우 = F12 정보 — A118·A158)의 표시 상태.
    /// A176: 홀드 세션·2연타 판정·PinTimer·peek 스냅샷 필드는 반투명 축과 함께 철거 —
    /// 남는 것은 상태 하나다(클래스 틀은 좌/우 공용 참조 관용구 유지).</summary>
    private sealed class OverlaySide
    {
        public OverlayState State;
    }

    private readonly OverlaySide _listSide = new(); // 좌측 파일 리스트 (F11)
    private readonly OverlaySide _infoSide = new(); // 우측 정보 (F12)

    /// <summary>
    /// 셸 표시 모드(A151 3단 → **A186 2단**): Windowed = 창 + 하단 바 / FullScreen = 전체화면
    /// (작업표시줄까지 없음). A151의 FullWindow(모드2 — 바·패널만 숨김)는 A186에서 폐지됐고,
    /// Enter·Alt+Enter·Full screen 버튼이 전부 같은 전체화면 토글(ToggleFullScreen — 진입 시
    /// 복귀 스냅샷 기억)이다. A86의 "Enter = 좌/우 일괄 토글"은 A151이 폐지했다(부록 B 67).
    /// </summary>
    private enum ShellViewMode { Windowed, FullScreen }

    /// <summary>
    /// 현재 셸 표시 모드 — 창별 상태·저장하지 않는다(A151 ⑥, A110 상태 소유 규칙 정합).
    /// 하단 바 가시성의 두 입력 축(모드, A186 자동 숨김) 중 하나다: 바를 켜고 끄는 코드는
    /// <see cref="UpdateShellChrome"/> 하나뿐이고, 프레젠터 변화(생성자 AppWindow.Changed 구독)는
    /// 이 값을 동기화한 뒤 같은 함수를 부른다(외부 경로 전체화면과 되밟지 않게).
    /// </summary>
    private ShellViewMode _viewMode = ShellViewMode.Windowed;

    /// <summary>
    /// 전체화면 복귀 스냅샷(A151 — Enter/Alt+Enter/Esc/버튼 공용): 진입 직전의 좌/우 패널 상태.
    /// A186: 모드 축이 2단이 되어 스냅샷의 모드 항목은 제거(복귀 = 항상 창 모드) —
    /// A176: 패널 축도 2상태(닫힘/도크)로 축소. A90 <see cref="_s4Restore"/>와 같은 관용구.
    /// 외부 경로 전체화면 해제·모드 리셋(모듈 전환·파일 열기)이 버린다.
    /// null이면(외부 경로 진입) 복귀는 패널 무변경으로 창 모드 폴백.
    /// </summary>
    private (OverlayState List, OverlayState Info)? _fullScreenRestore;

    /// <summary>지금 보여주는 모듈 ID. 빈 셸·설정·미지원 파일 안내면 null. 창 재사용 판단에 쓴다.</summary>
    public string? CurrentModuleId { get; private set; }

    /// <summary>아직 아무 콘텐츠도 안 연 빈 셸인지. 창 재사용 판단에 쓴다.</summary>
    public bool IsUntouched { get; private set; } = true;

    /// <summary>
    /// A219: 이 창에 콘텐츠가 표시 중인지 — 실경로 파일(_currentFilePath) 또는 무제 문서
    /// (A189 _untitledContent) 어느 쪽이든 true. 창 재사용 판단(WindowManager.FindReusable의
    /// 문서 모듈 특칙)에 쓴다 — 셸이 이미 추적하는 두 상태 축의 노출일 뿐 새 추적은 없다.
    /// </summary>
    public bool HasOpenContent => _currentFilePath is not null || _untitledContent;

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
            RememberBrowsedFolder(folder); // A174 — 브라우징 위치도 전역 마지막 폴더로(같은 값이면 무동작)
            // A200: 목록 재작성(폴더 이동·정렬·감시 재스캔)은 타일을 전부 새로 만든다 — 선택 축도
            // 여기서 확정 리셋한다(그리드 SelectionChanged가 Clear에서 안 와도 stale 경로가 우측
            // 정보에 남지 않게). 이미 없으면 무동작 — 재스캔마다 정보 패널을 다시 그리지 않는다.
            if (_selectedBrowse is not null)
            {
                _selectedBrowse = null;
                RefreshInfoOverlayForSelection();
            }
            if (_openFileBrowsing) _s4Explorer?.ShowEntries(folder, entries);
            else if (IsEmptyFileModule) _thumbnailExplorer?.ShowEntries(folder, entries);
        };
        // A93 드랍 규칙: 우측 인포 영역 드랍 = 그 파일 열기 — 콘텐츠가 없으면 OpenFile의
        // 라우터(A59)가 담당 모듈로 전환한 뒤 여는 기존 경로를 그대로 쓴다.
        InfoOverlay.FileDropped += OpenFile;
        // A119: 모듈 고유 패널(ISidePanelProvider — 정보 모듈) 호스트의 좌/우 방향 1회 조립.
        // 배경·힌트·상태 규칙은 파일 오버레이와 동일하게 호스트가 재현한다(SidePanelHost 참조).
        LeftPanelHost.Initialize(panelOnRight: false);
        RightPanelHost.Initialize(panelOnRight: true);

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

        // A234 배치 1: 셸 키(F11/F12) 진단 오버레이 — 설정의 숨김 토글(diag.shellKeyOverlay,
        // 기본 꺼짐)로 켜고 끈다. 배선은 바로 위 UiScale 관용구 복제: 다른 창의 설정 화면
        // (다른 UI 스레드)에서 발화할 수 있어 ApplyShellDiagnostics가 스스로 마샬링하고,
        // 구독 해제는 같은 자리(Closed)다. 폴링 타이머는 끄기 분기에 더해 창이 닫힐 때도
        // 확실히 멈춘다(누수 금지 — 진단 오버레이 절 참고).
        ShellDiagnostics.Changed += ApplyShellDiagnostics;
        Closed += (_, _) => ShellDiagnostics.Changed -= ApplyShellDiagnostics;
        Closed += (_, _) => _diagTimer?.Stop();
        ApplyShellDiagnostics(); // 저장된 값의 초기 적용 — 재시작 후에도 켠 상태가 유지된다(사양)

        // A206(v0.215.0): 업데이트 자동 확인은 '설정 화면 열림' 참조 카운트가 0이 아닌 동안에만
        // 돈다. 카운트를 놓는 정상 경로는 SettingsView의 Unloaded지만, 설정을 띄운 채 창을 닫으면
        // Unloaded가 오지 않을 수 있어(A41에서 확인된 한계) 카운트가 샌다 — 그러면 아무도 보지
        // 않는 확인이 계속 돌게 되므로 창이 내려갈 때 한 번 더 놓아 준다.
        // 뷰 쪽 해제가 멱등(_updateWatchHeld)이라 Unloaded와 겹쳐도 두 번 빠지지 않는다.
        Closed += (_, _) => (ModuleHost.Content as SettingsView)?.ReleaseUpdateWatch();

        // A226: F11/F12 패널 키는 **터널링**(PreviewKeyDown — 창 루트가 포커스 요소보다 먼저
        // 받는다)으로 승격. A212 감사에서 앱 코드의 선소비는 0건이었는데도 사용자 재보고
        // (2026-08-25: 어떤 모듈이든 특정 영역 한 번 클릭 후 F11/F12 무반응)가 전 모듈에서
        // 재현됐다 — 남는 용의자는 WinUI 컨트롤 내장 처리(ScrollViewer·ListViewBase·Slider 등)의
        // Handled 선점이라, 버블 수신 + 양보(구 게이트 ①)로는 원천 봉쇄가 불가능하다. 구독
        // 형태는 저장소 유일 선례(DocumentView.xaml.cs A121 — 인스턴스 이벤트 += 직접 구독)를
        // 복제한다. F키는 문자를 만들지 않아 선취해도 뺏을 텍스트 입력이 없다(A118 확정 취지 —
        // 그래서 IsTextInputFocused 게이트를 여기 두지 않는다. 두면 에디터 포커스 중 패널이
        // 안 열려 A118의 목적이 무너진다).
        RootLayout.PreviewKeyDown += OnRootPreviewKeyDown;
        // 그 밖의 셸 키(Esc·Enter·Alt·GoBack 등) 감지(A176 단타 토글 — v0.25.0 Alt/Ctrl 홀드 →
        // A58 → A86 → A107 → A118 → A158 계보의 홀드/2연타 판정 기계는 A176에서 철거):
        // 포커스가 모듈 뷰 안에 있어도 받도록 창 루트에서 handledEventsToo로 구독한다.
        // Alt(Menu) down/up도 같은 KeyDown/KeyUp 층으로 온다(A58 실증 — SystemKey도 이 핸들러가 받는다).
        // A226: F11/F12 분기는 위 터널링으로 이관돼 이 버블 층에는 더 없다.
        RootLayout.AddHandler(UIElement.KeyDownEvent, new KeyEventHandler(OnRootKeyDown), handledEventsToo: true);
        RootLayout.AddHandler(UIElement.KeyUpEvent, new KeyEventHandler(OnRootKeyUp), handledEventsToo: true);
        // A112: 마우스 뒤로가기(XButton1) = '뒤로'(전체화면 해제 → S4 복귀 → 콘텐츠 닫기 → S1).
        // handledEventsToo: 리스트·뷰가 눌림을 소비해도 전역 '뒤로'는 셸이 받아야 한다.
        RootLayout.AddHandler(UIElement.PointerPressedEvent,
            new PointerEventHandler(OnRootPointerBack), handledEventsToo: true);
        // A186: 클릭·터치 탭 = 영상 하단 바 자동 숨김의 "입력"(재표시·재대기). '뒤로'와 역할이
        // 달라 핸들러를 분리해 얹는다 — 둘은 서로 독립이라 무해.
        RootLayout.AddHandler(UIElement.PointerPressedEvent,
            new PointerEventHandler(OnRootPointerInput), handledEventsToo: true);
        // A226: 클릭 시점 포커스 고아 방어 — A209(RecoverChromeFocusOrphan)는 크롬 붕괴 "전이
        // 시점"만, A212(썸네일 FocusGrid)는 썸네일 표면만 지켜 다른 표면의 클릭발 고아는 무방비였다.
        // 순수 관찰(handledEventsToo·무소비)로 얹고, 판정은 같은 A209 관용구를 재사용한다 —
        // 정상 포커스(에디터·이름변경·콤보·팝업)면 무개입 규칙까지 통째로 상속. 위 두 핸들러와
        // 역할이 달라 분리해 얹는다('뒤로'·자동 숨김과 서로 독립 — 순서 무관).
        RootLayout.AddHandler(UIElement.PointerPressedEvent,
            new PointerEventHandler(OnRootPointerFocusGuard), handledEventsToo: true);
        // A86 경계 버튼: 마우스가 경계 근처에 왔을 때만 보이므로 이동·이탈을 창 루트에서 감시한다
        // (handledEventsToo — 패널·모듈 뷰가 이동 이벤트를 소비해도 근접 판정은 계속 돌아야 한다).
        // A186: 같은 이동 이벤트가 하단 바 자동 숨김의 입력이기도 하다(OnRootPointerMoved 안).
        RootLayout.AddHandler(UIElement.PointerMovedEvent,
            new PointerEventHandler(OnRootPointerMoved), handledEventsToo: true);
        // A41: Ctrl+휠 = UI 배율 한 단계 증/감. handledEventsToo **없이** 구독한다 — 모듈
        // 콘텐츠가 소비한 휠(사진 줌 A98·문서/PDF 줌·영상 볼륨)은 여기 오지 않고, 안 소비하고
        // 흘린 휠도 표면 판정(IsUiScaleWheelSurface — 하단 바·빈 셸만)이 다시 거른다(이중 방어).
        RootLayout.PointerWheelChanged += OnRootPointerWheel;
        RootLayout.PointerExited += (_, _) => HideEdgeButtons();
        CenterArea.SizeChanged += (_, _) => UpdateEdgeButtons(); // 경계 x 좌표는 실폭 기준
        Activated += (_, e) =>
        {
            if (e.WindowActivationState == WindowActivationState.Deactivated)
            {
                // Alt up을 못 본 채 비활성화(Alt+Tab 등) — 다음 활성 세션에 소비가 새면 안 된다(A107)
                _altComboUsed = false;
                // A234 배치 3: 비활성 창에서는 포커스 주기 감시를 반드시 멈춘다 — 비활성 창의
                // FocusManager.GetFocusedElement는 null을 돌려줄 수 있어, 계속 돌면 백그라운드
                // 창이 500ms마다 자기 모듈 뷰에 포커스를 꽂는 오작동이 된다. 트레이 숨김·최소화도
                // 비활성화를 동반하므로 이 한 곳이 그 갈래까지 덮는다(활성 복귀 시 아래서 재개).
                _focusWatchTimer?.Stop();
            }
            else
            {
                // A234 배치 3: 활성(첫 표시 포함 — Activate 호출이 이 이벤트를 낸다) = 포커스
                // 주기 감시 시작. 지연 생성 + Stop 후 Start 되감기(_diagTimer 관용구 복제).
                // 진단 오버레이와 무관하게 항상 돈다 — 수리용이라 오버레이(기본 꺼짐)에 묶으면
                // 수리가 안 된다(포커스 주기 감시 절 참고).
                _focusWatchTimer ??= MakeFocusWatchTimer();
                _focusWatchTimer.Stop(); // DispatcherTimer 되감기 관용구(Stop 후 Start)
                _focusWatchTimer.Start();
            }
        };
        // A234 배치 3: 창 닫힘 = 주기 감시 정지(누수 금지 — _diagTimer의 Closed 배선과 같은 형태)
        Closed += (_, _) => _focusWatchTimer?.Stop();

        // 타이틀바·작업표시줄 아이콘 (unpackaged는 exe 아이콘만으로는 타이틀바가 비어 보인다)
        if (File.Exists(IconPath))
        {
            _moduleIconPath = IconPath; // 모듈이 정해지면 그 색 .ico로 갈아탄다 (A102 링 합성 기준)
            AppWindow.SetIcon(IconPath);
            WindowIcon.Apply(this, IconPath); // 작업표시줄 기본 문서 아이콘 문제 보정 (실기기)
        }

        // 창 헤더만 브랜드 색(#15072E) — 본문은 시스템 테마 기본값
        TitleBarTheming.Apply(AppWindow.TitleBar);

        // A151: 프레젠터 변화 → 모드 동기화. 하단 바 가시성은 여기서 직접 만지지 않는다 —
        // 표시는 UpdateShellChrome 한 함수만 정한다(구 v0.21.0의 "전체화면이면 바 숨김"
        // 직접 대입을 대체). 셸 밖 경로(이미지 더블클릭 토글 등)로 프레젠터가 바뀌어도 모드가 따라온다.
        AppWindow.Changed += (sender, args) =>
        {
            if (!args.DidPresenterChange) return;
            var full = sender.Presenter.Kind == Microsoft.UI.Windowing.AppWindowPresenterKind.FullScreen;
            if (full && _viewMode != ShellViewMode.FullScreen)
            {
                _viewMode = ShellViewMode.FullScreen; // 외부 진입 — 스냅샷 없음(복귀는 창 모드 폴백)
            }
            else if (!full && _viewMode == ShellViewMode.FullScreen)
            {
                _viewMode = ShellViewMode.Windowed; // 외부 해제 — 패널 복원 없이 창 모드로
                _fullScreenRestore = null;
            }
            ReevaluateBarAutoHide(); // A186: 전체화면 진입/해제 = 자동 숨김 재평가(표시 상태에서 재대기)
            UpdateShellChrome();
        };

        // 문서 편집 미저장 확인(A37): X 버튼/Alt+F4를 가로채 저장/버리기/취소를 묻는다
        AppWindow.Closing += OnAppWindowClosing;

        // 창별 트레이 미니 아이콘: 좌클릭=활성화, 우클릭=메뉴, 툴팁=창 제목
        _tray = new TrayIcon(File.Exists(IconPath) ? IconPath : null);
        _tray.ActivateRequested += BringToFront;
        _tray.CloseRequested += () => _ = ConfirmThenCloseAsync(); // 닫기도 미저장 가드 경유 (A37)
        _tray.MinimizeToTrayRequested += HideToTray; // A218: 트레이 숨김은 명시 호출 2곳뿐(이 메뉴 + 시작 메뉴)
        _tray.ExitAllRequested += _manager.CloseAll;
        Closed += (_, _) => _tray.Dispose();

        // A105 ①: 인스턴스 고유 AppUserModelID — 태스크바 그룹을 창(인스턴스)별로 분리한다.
        // 시퀀스는 A100 트레이 슬롯(창 생성 단조 증가·수명 불변)을 그대로 쓴다 — 표시 번호(A2)는
        // 중간 창이 닫히면 재배정되어 그룹이 재편되므로 부적격. 창 표시(Activate) 전인 여기서
        // 1회 지정하고, 실패는 TaskbarIdentity가 전부 조용히 무시한다(공유 AUMID로 후퇴).
        Integration.TaskbarIdentity.Apply(this, _tray.Slot);

        // A218(2026-08-24): 최소화 → 트레이 자동 숨김(A69 전 최소화 → A185 SC_MINIMIZE 한정 —
        // 이 주제 세 번째 변경)은 철회됐다. 최소화는 이제 OS 표준 그대로(작업표시줄에 남는다)이고,
        // 트레이 숨김은 명시 호출 2곳(트레이 우클릭 "Minimize to tray" + 시작 메뉴 최하단 항목)만
        // 들어간다 — 진입은 전부 HideToTray() 하나로 모인다. A185의 SC_MINIMIZE 관찰 기계
        // (WindowMinSize)도 소비자가 사라져 함께 제거됐다.
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
    /// 최종 형태(A103, v0.130.0 → A136, v0.162.0): "● 2-KOTU - sample.pdf"
    /// — ● = 미저장 변경(A37), 2 = 인스턴스 번호(창이 하나여도 표시, 하이픈으로 KOTU에 붙인다).
    /// 모듈명 단어(Document·Image…)는 A103에서 전부 뺐다: 모듈은 아이콘 색·테두리(A102)가 알려 주고,
    /// 제목은 "몇 번 창의 어떤 파일인가"만 남긴다. 파일이 없으면 "1-KOTU"뿐이다.
    /// 구분자는 ASCII 하이픈 — 옛 em-dash는 폭이 넓어 좁은 작업표시줄 버튼에서 손해였다.
    /// <paramref name="title"/>은 이 규칙으로 만든 문자열이어야 한다(조립 지점은 아래 SetTitle 호출부).
    /// </summary>
    private void SetTitle(string title)
    {
        _baseTitle = title;
        ApplyTitle();
    }

    /// <summary>파일 있는 제목의 단일 조립 지점(A103) — "KOTU - 파일명".</summary>
    private static string FileTitle(string path) => $"{Branding.AppName} - {Path.GetFileName(path)}";

    private string _baseTitle = Branding.AppName;
    private bool _titleDirtyMark; // 현재 뷰의 미저장 표시(A37 — ICloseGuard.UnsavedChanged)
    // 창 생성 순서 번호(1부터). A136(v0.162.0)부터 창이 하나뿐이어도 1이 들어온다 —
    // 0은 WindowManager가 번호를 주기 전(생성 직후) 잠깐뿐이고, 그때는 번호 없는 제목이다.
    private int _instanceNumber;

    private void ApplyTitle()
    {
        // 순서: 상태(●) → 인스턴스 번호 → 내용. 작업표시줄·Alt+Tab에서 잘려도
        // 앞쪽 두 표식이 남도록 상태와 번호를 앞에 둔다.
        // A103: 번호는 브래킷·공백 없이 앞에 붙는다.
        // A136(v0.162.0): 번호와 앱 이름 사이에 ASCII 하이픈을 넣어 "1-KOTU"가 된다 —
        // 파일이 열려 있으면 "1-KOTU - sample.txt"처럼 하이픈이 두 번 나오는데, 이는
        // 사용자가 인지하고 수용한 사양이다(부록 B 67). 구분자를 바꾸지 말 것.
        var title = _instanceNumber > 0 ? $"{_instanceNumber}-{_baseTitle}" : _baseTitle;
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
            var (size, pos, maximized) = BoundsForPersist();
            SaveBounds(size, pos, maximized);
        }
        catch
        {
            // 저장 실패가 종료를 막으면 안 된다.
        }
    }

    /// <summary>
    /// 남길 기하의 선택 규칙(A55) — 전체화면·최대화·최소화·접힘(A61)이면 직전 일반 값, 아니면 현재 값.
    /// A55 저장(SaveWindowBounds)과 A124 재시작 스냅샷(CaptureSessionSnapshot)이 같은 규칙을 쓴다
    /// (같은 항목·같은 보정 — 두 소비자가 어긋나지 않게 여기 한 곳만 고칠 것).
    /// </summary>
    private (Windows.Graphics.SizeInt32? Size, Windows.Graphics.PointInt32? Pos, bool Maximized)
        BoundsForPersist()
    {
        var presenter = AppWindow.Presenter;
        if (presenter.Kind == Microsoft.UI.Windowing.AppWindowPresenterKind.FullScreen)
            return (_lastNormalSize, _lastNormalPos, false);
        if (presenter is Microsoft.UI.Windowing.OverlappedPresenter p
            && p.State != Microsoft.UI.Windowing.OverlappedPresenterState.Restored)
        {
            return (_lastNormalSize, _lastNormalPos,
                p.State == Microsoft.UI.Windowing.OverlappedPresenterState.Maximized);
        }
        if (_barOnlyCollapsed)
            return (_lastNormalSize, _lastNormalPos, _preCollapseMaximized);
        return (AppWindow.Size, AppWindow.Position, false);
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

    // ---------- 관리자 재시작 창 세트 스냅샷/복원 (A124) ----------

    /// <summary>
    /// 관리자 재시작(A124)용 창 스냅샷. 모듈 ID가 없는 창(설정 화면·빈 셸·미지원 파일 안내)은
    /// null을 돌려 복원 대상에서 뺀다. 항목은 A55 저장분 준용(모듈·파일 + 위치·크기·최대화) —
    /// 휘발 상태(미저장 편집·재생 위치·오버레이)는 싣지 않는다. 기하는 A55와 같은 선택 규칙
    /// (BoundsForPersist)이라 최대화·전체화면·접힘 중이어도 직전 일반 값이 실린다.
    /// </summary>
    internal Integration.RestartSessionFile.WindowSnapshot? CaptureSessionSnapshot()
    {
        if (CurrentModuleId is not { } moduleId) return null;
        var (size, pos, maximized) = BoundsForPersist();
        var snapshot = new Integration.RestartSessionFile.WindowSnapshot
        {
            ModuleId = moduleId,
            FilePath = _currentFilePath,
            Maximized = maximized,
        };
        // A55 SaveBounds와 같은 유효성(320×240) — 못 미치면 기하 없이(0) 남겨 A55 승계로 연다.
        if (size is { Width: >= 320, Height: >= 240 } s && pos is { } p)
        {
            snapshot.X = p.X;
            snapshot.Y = p.Y;
            snapshot.Width = s.Width;
            snapshot.Height = s.Height;
        }
        return snapshot;
    }

    /// <summary>
    /// 관리자 재시작 복원(A124): 스냅샷의 위치·크기·최대화를 이 창에 적용한다.
    /// 생성자(RestoreWindowBounds)가 이미 적용해 둔 A55 승계 기하·최대화를 스냅샷 값으로
    /// 덮는 단계라, 창 표시(Activate) 전에 불러야 한다. 보정은 A55와 동일 —
    /// A40 최소 크기 클램프 + 화면 밖 보정(ClampToWorkArea). 실패는 조용히 —
    /// A55 승계 기하 그대로 열리는 것뿐이다.
    /// </summary>
    internal void ApplySessionBounds(int x, int y, int width, int height, bool maximized)
    {
        try
        {
            // 생성자가 A55의 window.maximized로 최대화해 뒀을 수 있다 — 최대화 중에는
            // Resize가 먹지 않으므로(A61과 같은 관찰) 먼저 일반 상태로 내린다.
            if (AppWindow.Presenter is Microsoft.UI.Windowing.OverlappedPresenter
                { State: Microsoft.UI.Windowing.OverlappedPresenterState.Maximized } max)
            {
                max.Restore();
            }

            var (minW, minH) = WindowMinSize.MinPhysical(
                WinRT.Interop.WindowNative.GetWindowHandle(this));
            var w = Math.Max(width, minW);
            var h = Math.Max(height, minH);
            AppWindow.MoveAndResize(ClampToWorkArea(new Windows.Graphics.RectInt32(x, y, w, h)));
            // A55 추적 기준값도 지금 적용한 값으로 — RestoreWindowBounds와 같은 순서
            // (최대화 직후 닫혀도 직전 일반 기하가 저장되게).
            _lastNormalPos = AppWindow.Position;
            _lastNormalSize = AppWindow.Size;
            if (maximized && AppWindow.Presenter is Microsoft.UI.Windowing.OverlappedPresenter op)
                op.Maximize();
        }
        catch
        {
            // 기하 적용 실패 — A55 승계 기하로라도 열리면 된다(A124 조용한 폴백).
        }
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
    /// 타이틀바는 남긴다 — 드래그 이동·닫기 수단이고 인스턴스 번호 접두 표기(A103)가 거기 있다.
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

    // A147(v0.163.0): **Alt+숫자(모듈 전환)·Alt+0(Settings) 폐지** — 셸에 남는 Alt 조합은 Alt+` 하나뿐.
    // 그에 따라 (모듈 Id, 키, 툴팁 힌트)를 묶던 ModuleShortcuts 배열과 SettingsShortcutHint 상수도
    // 함께 사라졌다: 소비처가 ① RegisterShortcuts 등록 루프 ② 시작 메뉴 툴팁 힌트 둘뿐이었고
    // 둘 다 소멸했기 때문이다(둘 다 지운 채 배열만 남기면 읽는 곳이 없어 CS0414로 빌드가 깨진다).
    // ⚠️ 시작 메뉴의 항목 순서는 이 배열이 아니라 BuildStartMenu가 직접 나열한다 — 배열 소멸과 무관.
    // 모듈 번호(메뉴 아래→위 1~7, A254/v0.242.0가 A96/v0.116.0 배열을 대체)는 표기·순서용
    // 개념으로만 남는다(BuildStartMenu 주석) — 재배열해도 키맵에는 아무 영향이 없다.
    // 정책 이력(같은 주제 세 번째 변경): 숫자 단독(A32/v0.66.0) → Alt+숫자(A107/v0.134.0) →
    // **폐지**(A147/v0.163.0). 숫자 키를 어떤 형태로도 되살리지 말 것.

    /// <summary>시작 메뉴 키 = Alt+`(숫자 1 왼쪽, VK_OEM_3 — A107). 툴팁 표기(A34)도 이 값으로 조립한다.</summary>
    private const string MenuShortcutHint = "Alt+`";

    /// <summary>
    /// 셸 액셀러레이터 등록. A147(v0.163.0) 이후 **Alt+`(1 왼쪽 키) = 시작 메뉴**와
    /// **Shift+N = 새 창**, 그리고 **Ctrl+P = 인쇄(A211 배치 1, v0.220.0)** 셋이다 —
    /// Alt+숫자(모듈 전환)·Alt+0(Settings)은 폐지됐고, 모듈 전환·설정
    /// 진입은 **시작 메뉴가 유일한 키보드 경로**다(A34 문자 핫키는 모듈 바 버튼 전용 — 전환과 무관).
    /// Shift+N = 새 창(A24 — A84에서 Ctrl+N을 Shift 계열로 전환, A107 무변경. 앱의 Ctrl 조합은
    /// 문서 Ctrl+S·A41 Ctrl+±(배율)·이 Ctrl+P뿐). 텍스트 입력 통과 예외(A32)는 Shift+N에만 적용된다 —
    /// Alt 조합은 문자를 만들지 않아 뺏을 입력이 없으므로 어디서나 동작한다(A107 전환의 목적).
    /// ⚠️ Alt+`도 <see cref="AddShortcut"/>의 `_altComboUsed` 부기를 그대로 탄다 — 그 분기를 지우면
    /// Alt 단독 up 조건 소비(OS 창 메뉴 모드 회피, OnRootKeyUp)가 깨진다.
    /// </summary>
    private void RegisterShortcuts()
    {
        // 액셀러레이터 키 이름 툴팁이 화면 중앙에 뜨는 WinUI 기본 동작 방지 (모듈 뷰들과 동일)
        RootLayout.KeyboardAcceleratorPlacementMode =
            Microsoft.UI.Xaml.Input.KeyboardAcceleratorPlacementMode.Hidden;

        AddShortcut((VirtualKey)192, () => StartFlyout.ShowAt(StartButton),
            Windows.System.VirtualKeyModifiers.Menu); // VK_OEM_3 = `(~) — A107: Alt+`
        // A34: 하단 바 메뉴 버튼도 툴팁에 키를 표기한다(문자열은 여기서만 만든다).
        ToolTipService.SetToolTip(StartButton, $"Menu ({MenuShortcutHint})");
        // A147(v0.163.0): 여기 있던 Alt+1~7(모듈 전환)·Alt+0(Settings) 등록을 제거했다.
        // 새 창 = 지금 보는 모듈의 빈 인스턴스(A24 사용자 확정). 설정 화면 등 모듈 없는 창은 기본 화면으로.
        AddShortcut(VirtualKey.N, () => _manager.OpenNewWindow(CurrentModuleId),
            Windows.System.VirtualKeyModifiers.Shift); // A84: Ctrl+N → Shift+N
        // A211 배치 1(v0.220.0): Ctrl+P = 인쇄. 조사 문서(A211-print-research.md §2) 확정 관용구 =
        // 액셀러레이터(DocumentView Ctrl+S와 같은 형 — 셸에서는 이 AddShortcut이 그 자리다).
        // Control 조합은 아래 A32 통과 예외(None/Shift)에 안 걸린다 — 문자를 만들지 않아 뺏을
        // 입력이 없고(A107 Alt 조합과 같은 논리), 문서 편집 중에도 인쇄 진입로가 있어야 한다
        // (배치 4~5 텍스트/마크다운의 주 사용처). 오토리피트·재진입은 PrintHost 세션 가드가
        // 무동작으로 거르고, 계약 미구현 뷰·S4는 RequestPrint가 거른다(양보 판단 3종은 그쪽 주석).
        AddShortcut(VirtualKey.P, RequestPrint, Windows.System.VirtualKeyModifiers.Control);
    }

    private void AddShortcut(VirtualKey key, Action action,
        Windows.System.VirtualKeyModifiers modifiers = Windows.System.VirtualKeyModifiers.None)
    {
        var accelerator = new KeyboardAccelerator { Key = key, Modifiers = modifiers };
        accelerator.Invoked += (_, e) =>
        {
            // A32 예외: 단독 키는 입력 컨트롤 타이핑을 뺏으면 안 된다.
            // A84: Shift 조합도 동일 — 에디터에서 Shift+글자는 대문자 입력이 우선(Shift+N 통과).
            // A107: Menu 조합은 이 예외에 안 걸린다(문자를 안 만든다) — 텍스트 입력 중에도 발화.
            // A211: Control 조합도 같은 이유로 예외 밖 — Ctrl+P는 문서 편집 중에도 인쇄여야 한다.
            if (modifiers is Windows.System.VirtualKeyModifiers.None
                    or Windows.System.VirtualKeyModifiers.Shift
                && IsTextInputFocused())
            {
                e.Handled = false; // 계속 흘려보내 컨트롤이 문자를 받게
                return;
            }
            // A107: Alt 조합 발화 기록 — Alt 단독 up의 조건 소비(OS 메뉴 모드 회피) 근거.
            // 액셀러레이터가 키 down을 소비하면 OS가 "Alt 중 다른 키가 눌렸다"를 못 보므로,
            // Alt up에서 우리가 대신 소비해야 창 메뉴 모드로 포커스를 뺏기지 않는다(OnRootKeyUp).
            if (modifiers.HasFlag(Windows.System.VirtualKeyModifiers.Menu)) _altComboUsed = true;
            e.Handled = true;
            action();
        };
        RootLayout.KeyboardAccelerators.Add(accelerator);
    }

    /// <summary>
    /// 포커스가 텍스트 입력 컨트롤(TextBox·PasswordBox·RichEditBox 계열)에 있는지.
    /// A34에서 판정 자체는 공용 헬퍼(HotkeySupport)로 옮겼다 — 모듈 버튼 핫키가 같은 규칙을 쓴다.
    /// 셸 키 중 이 판정에 걸리는 것은 A107부터 Shift+N뿐이다(숫자·`는 Alt 조합이 되어 예외 불필요).
    /// 파일 리스트 포커스에서는 계속 동작해야 하므로 리스트 통과까지 보는 ShouldPassThrough가 아니라
    /// 텍스트 입력만 보는 이 판정을 쓴다. 루트 KeyDown(Enter·홀드 취소 리셋)도 같은 판정을 공유한다.
    /// </summary>
    private bool IsTextInputFocused() => HotkeySupport.IsTextInputFocused(RootLayout);

    // ---------- 인쇄 (A211 배치 1, v0.220.0 — 사양 = docs/A211-print-research.md §3) ----------
    // CI가 인쇄 API(저장소 선례 0 — PrintHost.cs에 전부 격리)에서 깨질 때의 최소 복구:
    // PrintHost.cs 삭제 + 이 절 삭제 + RegisterShortcuts의 Ctrl+P 1줄 + ShowModule의
    // PrintRequested 블록 삭제(전부 "A211" 표식). Core 계약은 BCL 전용이라 남아도 안전.
    //
    // 하단 바 인쇄 버튼 규격(배치 2~5에서 모듈별 추가 — 이 배치는 키·기반만, 부록 B 78):
    // 모듈 자신의 하단 바 줄(IBottomBarProvider)에 버튼을 두고, 클릭 = 뷰가
    // IPrintPageProvider.PrintRequested를 발화하는 한 줄이 전부다(셸이 ShowModule에서 구독해
    // 아래 RequestPrint로 흘린다 — 모듈은 셸을 모른 채 끝난다). 툴팁 "Print (Ctrl+P)",
    // 활성 조건 = CanPrintNow(인쇄할 콘텐츠 없으면 비활성), A34 문자 핫키는 배정하지 않는다.

    /// <summary>창당 1개 인쇄 호스트 — 첫 요청 때 만든다(시작 경로에서 인쇄 API 무접촉 =
    /// 구형 OS에서도 요청 전까지는 아무 일도 없다). 해제는 자신이 창 Closed에서 전수 수행.</summary>
    private Printing.PrintHost? _printHost;

    /// <summary>현재 모듈 뷰의 인쇄 계약 — PlaybackView 등과 같은 캐스트 관용구. 설정·빈 셸은 자연히 null.</summary>
    private IPrintPageProvider? PrintProviderView => ModuleHost.Content as IPrintPageProvider;

    /// <summary>
    /// 인쇄 진입 단일 경로 — Ctrl+P 액셀러레이터와 모듈 하단 바 인쇄 버튼(PrintRequested, 배치 2~5)이
    /// 전부 여기로 온다. 양보 판단(A211 배치 1에서 확정): ① 오토리피트 = PrintHost 세션 가드가 1발로
    /// 접는다(대화상자 표시 중 재요청도 무동작) ② 텍스트 입력 = 통과 없이 발화(문자 비생성 —
    /// RegisterShortcuts 주석) ③ 탐색기 통과 표면(PassThroughTag) = 원 기능이 없어 양보 불요.
    /// 단 S4('오픈 파일' 탐색)는 무동작 — keymap S4 무동작 계열(중앙을 탐색기가 덮고 있어 "지금
    /// 보는 화면"이 인쇄 대상이 아니다). 계약 미구현 뷰·CanPrintNow false도 무동작 — 배치 1
    /// 시점엔 구현 모듈이 0이라 Ctrl+P는 항상 무동작이 정상이다(부록 B 78 확정 범위 = 3모듈).
    /// </summary>
    private void RequestPrint()
    {
        if (IsOpenFileBrowsing) return;
        if (PrintProviderView is not { CanPrintNow: true } provider) return;
        _printHost ??= new Printing.PrintHost(this);
        _ = _printHost.RequestPrintAsync(provider); // 실패 전부 내부 흡수(영어 안내 다이얼로그) — 예외 무전파
    }

    /// <summary>
    /// 단축키·센서 트레이(A18)로 모듈 전환. 이미 그 모듈이면 아무것도 하지 않는다(보던 파일 보호) —
    /// A109(v0.136.0)의 사이드바 기본도 이 no-op 가드 뒤에 있어, 같은 모듈 재선택은 화면을 건드리지
    /// 않는다(가드는 A109에서 손대지 않았다).
    /// </summary>
    internal void OpenModuleById(string id)
    {
        if (CurrentModuleId == id) return;
        var module = _router.Modules.FirstOrDefault(m => m.Id == id);
        if (module is not null) OpenModule(module);
    }

    // ---------- 시작 메뉴 (하단 바에서 위로 떠오르는 플라이아웃) ----------

    /// <summary>
    /// 시작 메뉴 구성. 패널은 **위→아래**로 채우는데 **번호는 아래에서 위로** 올라간다
    /// (1번이 메뉴 최하단) — 그래서 아래 AddModuleItem 호출은 7 → 1 역순으로 늘어놓는다.
    /// 번호(1=All Readable · 2=문서 · 3=이미지 · 4=오디오 · 5=영상 · 6=압축 · 7=하드웨어 —
    /// **A254/v0.242.0이 A96 배열을 대체**, A10 오디오 삽입 승계)는 A147(v0.163.0)이 Alt+숫자를
    /// 폐지한 뒤로 **표기·순서용 개념**일 뿐 어떤 키와도 연결되지 않는다(그래서 번호를 다시
    /// 매겨도 키맵은 영향이 없다 — A34 표는 폐지된 키의 이력일 뿐).
    /// A254(v0.242.0) 이후 배치(위→아래):
    /// 광고 · 구분선 · Settings(0) · **구분선** · 하드웨어(7) · 구분선 · 압축(6) · 구분선 ·
    /// 영상(5) · 오디오(4) · 이미지(3) · 문서(2) · **구분선** · All Readable(1) · 구분선 ·
    /// Minimize to tray(A218 — 최하단·번호 없음).
    /// 즉 화면에서 보이는 아래→위 순서는 Min to tray → All Readable → 문서 → 이미지 →
    /// 오디오 → 영상이다(A254 사용자 지시). 압축·하드웨어·Settings·상단부는 불변.
    /// 굵게 표시한 구분선 2개가 A96(v0.116.0) 신규다 — ① 1번과 2번 사이 ② 하드웨어와 Settings
    /// 사이(둘이 서로 붙어 보인다는 사용자 지적). A254는 구분선 위치를 건드리지 않았다.
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

        // 영상-오디오-이미지-문서 그룹 (A254/v0.242.0 재배열 — 아래부터 문서 → 위로 갈수록 영상).
        // 나열은 위→아래 채움 순서라 화면에서 읽히는 아래→위 순서와 정확히 반대다.
        AddModuleItem("video"); // 5 — A254 이전 3
        AddModuleItem("audio"); // 4 — 음악 재생 분리 (A10, v0.75.0), 번호 불변
        AddModuleItem("image"); // 3 — A254 이전 2
        AddModuleItem("document"); // 2 — v0.44.0 실제 모듈로 교체 (텍스트·마크다운 1단계), A254 이전 5
        StartMenuPanel.Children.Add(Divider()); // A96 신규 ①: 1번 ↔ 2번 분리

        AddModuleItem(KOTU.Module.AllReadable.AllReadableModule.ModuleId); // 1 — A96에서 최하단으로
        // 하단 바 우측 Info·Settings 아이콘은 제거(v0.28.2) — 시작 메뉴 항목으로 일원화.

        // A218: "Minimize to tray"는 All Readable보다 더 아래(진짜 최하단 — 사용자 지정 위치).
        // 모듈 항목이 아니라 창 동작이라 구분선으로 모듈 그룹과 나눈다. 같은 동작의 다른 진입은
        // 트레이 우클릭 메뉴(TrayIcon.MinimizeToTrayRequested) 하나뿐이다.
        StartMenuPanel.Children.Add(Divider());
        AddMinimizeToTrayItem();
    }

    /// <summary>A218: 시작 메뉴 최하단 "Min to tray" — HideToTray()의 시작 메뉴 진입로.</summary>
    private void AddMinimizeToTrayItem()
    {
        // A232: 라벨은 "Min to tray" — 폭 136 메뉴에서 "Minimize to tray"가 잘렸다.
        // 글리프는 E896 Download(아래 화살표+받침 — 트레이로 내려보낸다는 은유)로 교체. 종전 E921 = ChromeMinimize.
        var item = MakeMenuItem("\uE896", "Min to tray");
        item.Click += (_, _) =>
        {
            StartFlyout.Hide(); // 플라이아웃을 연 채 창만 사라지지 않게 먼저 닫는다
            HideToTray();
        };
        StartMenuPanel.Children.Add(item);
    }

    private void AddModuleItem(string moduleId)
    {
        var module = _router.Modules.FirstOrDefault(m => m.Id == moduleId);
        if (module is null) return;

        // A147(v0.163.0): Alt+숫자 폐지로 표시할 키가 없어 툴팁을 아예 붙이지 않는다
        // (MakeMenuItem은 hint가 null이면 SetToolTip을 건너뛴다).
        var item = MakeMenuItem(module.IconGlyph, module.DisplayName);
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
        var item = MakeMenuItem("\uE713", "Settings"); // A147: Alt+0 폐지 — 키 표기 없음
        item.Click += (_, _) =>
        {
            StartFlyout.Hide();
            OnSettingsClick(item, new RoutedEventArgs());
        };
        StartMenuPanel.Children.Add(item);
    }

    /// <summary>
    /// 시작 메뉴 항목. shortcutHint가 있으면 표준 툴팁으로 단다 —
    /// 다른 버튼들과 같은 지연(약 1초)·모양으로 표시된다(A1, v0.57.0 —
    /// v0.45.0의 즉시 인라인 힌트를 사용자 지시로 교체).
    /// ※ A147(v0.163.0)에서 전역 키가 Alt+`만 남아 **hint를 넘기는 호출부가 없어졌다**(모두 null) —
    /// 파라미터·null 분기는 존치한다(키가 다시 생기면 여기로 돌아온다).
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
            // ⚠️ 높이 이력: A31 44 → A96(v0.116.0) 40 → **A106(v0.132.0) 36**(둘 다 "항목 높이 −10%").
            //    A31의 44도 A96의 40도 이제 유효하지 않다 — 되돌리지 말 것.
            // ⚠️ **MinHeight와 상하 패딩은 반드시 같이 움직인다**(A96이 44→40과 12→10을 함께 바꾼 이유):
            //    아이콘·라벨의 실제 줄 높이는 FontSize(16/14)보다 크므로(약 21) 항목 높이를 정하는 쪽은
            //    MinHeight가 아니라 "내용 + 상하 패딩"이다. 패딩을 그대로 두고 MinHeight만 내리면
            //    화면에서는 아무것도 줄지 않는다. A106은 40→36에 맞춰 상하 패딩 10→8(합 −4).
            // 좌우는 10 유지: 메뉴 폭 136(A96 — v0.35.0의 124에서 +10%) 안에서 라벨 말줄임을 늘리지 않기 위해.
            // A50(v0.92.0): 좌측 히트 영역은 플라이아웃 프레젠터 패딩 0(XAML)으로 확대 —
            // Stretch인 버튼이 메뉴 좌우 가장자리까지 닿아, 포인터가 라벨보다 왼쪽이어도 눌린다.
            Padding = new Thickness(10, 8, 10, 8),
            MinHeight = 36,
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
    /// ※ A50 기록의 "항목 높이 44(A31)"는 A96(v0.116.0) **40** → A106(v0.132.0) **36**으로 조정됐다.
    /// 여백 3은 유지 — A96·A106이 바꾼 것은 항목 높이·메뉴 폭이지 구분선 여백이 아니다.
    /// </summary>
    private static Border Divider() => new()
    {
        Height = 1,
        Margin = new Thickness(4, 3, 4, 3),
        Background = (Brush)Application.Current.Resources["DividerStrokeColorDefaultBrush"],
    };

    // A103: 모듈만 연 상태의 제목은 모듈명 없이 "KOTU"뿐 — 모듈 구분은 아이콘 링 색(A102)이 한다.
    // A109(v0.136.0): 모듈 실행·전환은 좌·우 사이드바가 뜬 기본 상태로 시작한다(defaultSidebars) —
    // 파일을 여는 경로(OpenFile·OpenVerb → ShowModule 직접 호출)는 기본값 false라 종전 그대로다
    // (파일 인자 직접 열기 = 무사이드바, A81 유지).
    private void OpenModule(IModule module)
        => ShowModule(module, OpenContext.Empty, Branding.AppName, defaultSidebars: true);

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
        SetTitle(Branding.AppName); // A103: 설정 화면도 파일이 없으니 "KOTU"뿐 (구: "KOTU Settings")
        var settings = new SettingsView(_router);
        ModuleHost.Content = settings;
        // 설정도 하단 바 제공(광고 + ⛶, v0.50.0) — 모듈들과 같은 통합 방식
        ModuleBarHost.Content = settings.TakeBottomBar() as UIElement;
        ClearModulePanels(); // A119: 이전 모듈 패널(정보 모듈 그래프 등)이 설정 위에 남지 않게
        AttachDriveStrip(null); // 설정 바에는 드라이브 줄이 없다 — 이전 뷰 참조를 끊는다 (A22)
        CurrentModuleId = null;
        IsUntouched = false;
        UpdateModeIndicator(null, isSettings: true);
        // A205: 설정은 좌/우 사이드바 전면 배제 화면이다 — 아래 SetContentState가 부르는
        // ApplyOverlayStates에서 게이트(IsPanelFallbackView 제외)가 꺼져, 진입 직전에 떠 있던
        // 사이드바가 함께 내려간다. _listSide/_infoSide 상태 자체는 건드리지 않으므로
        // 설정을 나가면(모듈 복귀) 직전 구성이 그대로 복원된다.
        SetContentState(null, null);
    }

    // ---------- 파일 열기 ----------

    /// <summary>
    /// 내장 탐색기·좌 리스트 오버레이의 일반 더블클릭 열기(A24): 이 창에서 그대로 연다.
    /// A222(2026-08-24): "항상 새 창" 설정 분기(window.alwaysNewWindow) 폐지 — 명시적 새 창
    /// 조작(Shift+더블클릭·우클릭 메뉴)만 새 창을 만든다. 외부 진입(WindowManager가 창을 이미
    /// 골라 OpenFile을 부르는 경로)과 섞이지 않게 별도 메서드는 유지 — 배선 지점이 여럿이라
    /// (ExplorerPane·ThumbnailExplorer·오버레이) 시그니처를 흔들지 않는 쪽이 안전하다.
    /// </summary>
    private void OpenFileRouted(string path) => OpenFile(path);

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
                SetTitle(FileTitle(path)); // A103: 모듈명 없이 "KOTU - 파일명" 하나로 통일
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
            // A196: 안내를 포커스 가능한 ContentControl로 감싼다(구 TextBlock 단독은 Control이
            // 아니라 포커스를 못 받는다) — ① 로드 시 자기 포커스로 셸 키(F11/F12·Enter·Esc)가
            // 이 화면에서도 곧장 듣고(모듈 뷰들의 Loaded 자기 포커스 관용구 — SettingsView와 동일)
            // ② 패널 닫힘 뒤 포커스 고아 복구(ApplyOverlayStates 말미 `as Control` 재포커스)의
            // 대상이 된다. 콘텐츠 정렬은 TextBlock 자신의 Center + 스트레치 프레젠터로 유지한다.
            var unsupported = new ContentControl
            {
                IsTabStop = true,
                HorizontalContentAlignment = HorizontalAlignment.Stretch,
                VerticalContentAlignment = VerticalAlignment.Stretch,
                Content = new TextBlock
                {
                    Text = $"Unsupported file type: {Path.GetFileName(path)}",
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center,
                },
            };
            unsupported.Loaded += (_, _) => unsupported.Focus(FocusState.Programmatic);
            ModuleHost.Content = unsupported;
            ModuleBarHost.Content = null;
            ClearModulePanels(); // A119: 미지원 안내 화면에도 이전 모듈 패널이 남으면 안 된다
            AttachDriveStrip(null); // 미지원 파일 안내 화면 — 모듈 바와 함께 드라이브 줄도 내린다 (A22)
            CurrentModuleId = null;
            IsUntouched = false;
            UpdateModeIndicator(null);
            SetContentState(null, null);
            return;
        }
        ShowModule(module, OpenContext.ForFile(path), FileTitle(path));
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
            FileTitle(file));
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

        // A187(A93의 완결): 판정 기준은 **현재 모듈뿐**이다 — 콘텐츠가 열려 있는지는 보지 않는다.
        // 파일 모듈(빈 상태 S1 포함)에서 같은 종류(그 모듈 담당 확장자)면 그 자리에서 열고,
        // 다른 종류면 담당 모듈의 새 인스턴스를 만들어 거기서 연다(WindowManager의 "파일로 새 창"
        // 경로 재사용 — 담당 모듈이 없는 확장자도 그 경로에서 기존 "Unsupported file type" 안내로
        // 떨어진다). A93 시절의 `_currentFilePath is not null` 조건이 빠지면서, 빈 이미지 모듈에
        // 동영상을 떨어뜨리면 이 창을 갈아엎지 않고 새 인스턴스가 뜬다.
        // 파일 모듈이 아닌 화면(H/W·설정·미지원 안내·시작 직후 기본 화면)은 담당 확장자라는 개념이
        // 없으므로 IsFileModule 게이트로 걸러 **현행대로 이 창에서 연다** — 특히 시작 직후 화면은
        // 기본 모듈(H/W)이라, 여기서 새 창을 띄우면 빈 셸을 두고 창이 하나 더 생긴다.
        if (IsFileModule(_currentModule) && _currentModule is { } module
            && !ExplorerListing.MatchesExtension(path, module.SupportedExtensions))
        {
            _manager.OpenFileInNewWindow(path);
            return;
        }
        OpenFile(path);
    }

    /// <summary>
    /// 모듈 뷰 교체의 단일 종착점. defaultSidebars(A109, v0.136.0) = **모듈 실행·전환 경로**로 들어온
    /// 호출인지 — true면 뷰 교체를 마친 뒤 좌·우 사이드바(불투명 도크) 기본 상태를 다시 적용한다
    /// (A81이 창 생성 1회에만 주던 상태를 모듈 전환마다 준다 = A81의 "이후 사용자 상태 유지" 대체).
    /// 파일을 여는 경로(OpenFile·OpenVerb)는 false로 두어 A81의 "파일 인자 직접 열기 = 무사이드바"가
    /// 그대로 성립한다. 미저장 가드(A37)에서 취소되면 여기서 조기 반환하므로 사이드바도 손대지 않는다.
    /// </summary>
    private async void ShowModule(IModule module, OpenContext context, string title,
        bool defaultSidebars = false)
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
        // A119: 모듈 고유 패널 호스트를 비운다 — ModuleBarHost 교체와 같은 자리. 새 뷰가 패널
        // 제공자면 아래 SetContentState → ApplyOverlayStates가 새 콘텐츠를 다시 얹는다.
        ClearModulePanels();
        AttachDriveStrip(view as IDriveStripHost); // A22: 하단 바 드라이브 줄 주입(파일 없을 때만 표시)
        CurrentModuleId = module.Id;
        IsUntouched = false;
        UpdateModeIndicator(module);

        // 뷰 내부 열기(열기 버튼·◀/▶ 탐색·테스트 클립)도 셸과 동기화 (v0.25.0)
        if (view is IContentStateSource source)
            source.ContentOpened += path => DispatcherQueue.TryEnqueue(() => OnContentOpened(path));
        // A189: 무제 문서 진입(경로 없는 콘텐츠 — 문서 모듈 'New text file')도 셸과 동기화.
        // 뷰가 이미 교체됐으면 무시한다(아래 이벤트들과 같은 가드).
        if (view is IUntitledContentSource untitledSource)
            untitledSource.UntitledOpened += () => DispatcherQueue.TryEnqueue(() =>
            {
                if (!ReferenceEquals(ModuleHost.Content, view)) return;
                OnUntitledOpened();
            });
        // A186: 재생 상태 변화(재생/일시정지/정지) → 하단 바 자동 숨김 재평가.
        // 계약에 UI 스레드 보장이 없어 디스패치하고, 뷰가 이미 교체됐으면 무시한다(A37과 같은 가드).
        if (view is IPlaybackStateSource playback)
            playback.PlaybackStateChanged += () => DispatcherQueue.TryEnqueue(() =>
            {
                if (!ReferenceEquals(ModuleHost.Content, view)) return;
                OnPlaybackStateChanged();
            });
        // A223: 모듈 하단 바 Open 버튼(문서 모듈)의 열기 위임 — 셸 OpenFile 경로로 받는다.
        // 이 경로가 미저장 가드(A37)·제목 갱신을 전부 갖고 있다(뷰 직접 열기의 우회 방지 —
        // 계약 주석). 계약에 UI 스레드 보장이 없어 디스패치하고, 뷰가 교체됐으면 무시한다.
        if (view is IOpenFileRequestSource openRequest)
            openRequest.OpenFileRequested += path => DispatcherQueue.TryEnqueue(() =>
            {
                if (!ReferenceEquals(ModuleHost.Content, view)) return;
                OpenFile(path);
            });
        // A211 배치 1: 모듈 하단 바 인쇄 버튼(배치 2~5에서 추가)의 신호 → 셸 인쇄 단일 경로.
        // 계약에 UI 스레드 보장이 없어 디스패치하고, 뷰가 이미 교체됐으면 무시한다(위와 같은 가드).
        if (view is IPrintPageProvider printProvider)
            printProvider.PrintRequested += () => DispatcherQueue.TryEnqueue(() =>
            {
                if (!ReferenceEquals(ModuleHost.Content, view)) return;
                RequestPrint();
            });
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
                // A137: 저장 성공(A113 재기준화 — DocumentView.CommitSave의 1회 통지)의 용량 변화가
                // 이 경로로 창 32px 아이콘에 닿는다. 오디오의 1초 주기 발화 같은 잦은 호출은
                // _windowIconKey 선비교가 창 쪽 재합성을 걸러 낸다(트레이는 종전 ComposeKey 방어).
                RefreshShellIcons();
            });
        SetContentState(module, context.FilePath);
        // A109(v0.136.0): 모듈 전환의 기본 화면 = 좌·우 사이드바.
        // 반드시 SetContentState **뒤**다 — 그 안에서 S4('오픈 파일')가 먼저 자동 종료되고(A90),
        // 종료가 스냅샷(_s4Restore)을 버린 뒤에 사이드바 기본이 얹혀야 순서가 옳다.
        // (A151 모드 리셋도 SetContentState 안이라 이 사이드바 기본은 항상 모드1 위에 얹힌다.)
        if (defaultSidebars) SetDockedState(listDocked: true, infoDocked: true);
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
        // A151 ⑤: 모드 리셋 — 모듈 전환·파일 열기(설정 진입·미지원 안내 포함, 전부 이 경로)는
        // 창 모드로 돌아간다. 새 창은 인스턴스 필드 초기값이 이미 창 모드다. 복귀 스냅샷도 버린다.
        // 뷰 내부 탐색(◀/▶ 다음 파일 등 — OnContentOpened)은 같은 콘텐츠 세션의 연속으로 보고
        // 리셋하지 않는다(전체화면 슬라이드쇼가 화살표마다 풀리면 안 된다 — 구현 결정).
        // A186: 자동 숨김도 리셋 — 타이머 정지·바 복원(모듈 전환과의 경합 방지. 새 뷰의 재생이
        // 시작되면 PlaybackStateChanged가 다시 카운트를 연다).
        _fullScreenRestore = null;
        ResetBarAutoHide();
        if (_viewMode != ShellViewMode.Windowed) SetViewMode(ShellViewMode.Windowed);
        // A90: 콘텐츠·모듈이 바뀌면 S4('오픈 파일' 탐색)는 자동 종료 — 파일 열기(더블클릭·Enter·인포
        // 드랍)는 물론 숫자 키 모듈 전환·설정 진입도 같은 경로로 닫힌다. 새 콘텐츠가 화면을 차지하므로
        // 복귀 스냅샷은 버리고(restore:false), 좌/우는 지금 상태 그대로 A86 "상태는 콘텐츠를 넘어 유지"
        // 규칙을 탄다(자연 상태). 표시 갱신은 아래 ApplyOverlayStates가 하므로 여기서는 생략(refresh:false).
        ExitOpenFileBrowsing(restore: false, refresh: false);
        HideS1Flash(); // A90-b 강조가 콘텐츠 전환 뒤까지 남지 않게
        _currentModule = module;
        _currentFilePath = filePath;
        _untitledContent = false; // A189: 모듈 전환·실경로 열기·설정 진입은 무제 상태를 걷는다
        _selectedBrowse = null;   // A200: 열기·모듈 전환 = 선택 축 리셋 — 더블클릭 열기의 선택
                                  // 겹발화가 열린 콘텐츠 정보를 가리는 역전 방지(아래 Apply가 그린다)
        InfoOverlay.InvalidateCache();
        RememberLastFolder(); // 전역 마지막 폴더 저장 (v0.55.0 모듈별 → A174 전역 1벌)
        UpdateEmptyExplorer();
        UpdateDriveStrip(); // A22: 파일 유무가 바뀌면 드라이브 줄도 함께 켜고 끈다
        // 사이드바 상태는 유지한 채 새 콘텐츠(파일·모듈) 기준으로 다시 그린다 —
        // 기존 "상태는 콘텐츠를 넘어 유지" 규칙(A176: 홀드 판정 리셋은 상태 머신과 함께 소멸).
        ApplyOverlayStates();
        // A54: 모듈 전환·설정 전환·A59 안에서의 파일 교체까지 이 한 지점으로 모인다.
        // A137: 파일 열기/닫기가 창 아이콘(32px 확장자/용량)도 바꾸므로 트레이만이 아니라
        // 셸 아이콘 전체를 갱신한다 — 창 쪽은 _windowIconKey 선비교로 무변경이면 무동작.
        RefreshShellIcons();
    }

    /// <summary>
    /// 전역 마지막 폴더 설정 키 (A174, 부록 B 71 ① — v0.55.0 모듈별 "lastFolder.{id}"의 대체).
    /// 구 모듈별 키는 마이그레이션·청소 없이 설정 파일에 무해하게 잔존한다(코드 소비처만 제거).
    /// </summary>
    private const string LastFolderKey = "explorer.lastFolder";

    /// <summary>현재 파일의 폴더를 전역 마지막 폴더에 기억한다 (v0.55.0 모듈별 → A174 전역 1벌).</summary>
    private void RememberLastFolder()
    {
        if (_currentModule is null || _currentFilePath is null) return;
        if (Path.GetDirectoryName(_currentFilePath) is not { Length: > 0 } folder) return;
        RememberBrowsedFolder(folder);
    }

    /// <summary>
    /// 이 창이 마지막으로 전역 키에 알린 폴더 — 아래 RememberBrowsedFolder의 "실제 항해" 가드.
    /// 설정값 비교만으로는 부족하다: 설정은 창 간 공유라, 다른 창이 키를 새 폴더로 바꾼 뒤
    /// 이 창의 정렬 변경·감시 재스캔(폴더 불변 재발화)이 옛 폴더로 키를 되밟을 수 있다.
    /// </summary>
    private string _lastBrowsedFolder = "";

    /// <summary>
    /// 브라우징 항해도 전역 마지막 폴더에 기억한다 (A174 — 종전에는 파일을 열 때만 기억됐다).
    /// 배선은 생성자 ListOverlay.ViewChanged 한 곳 — 리스트 항해의 모든 경로(트리 선택·폴더
    /// 더블클릭·상위 이동·소실 폴더 상위 폴백)가 그리로 모인다. ViewChanged는 폴더 변경 통지가
    /// 아니라 "표시 목록 재작성" 통지라 정렬(A5)·필터(A7)·감시 재스캔(A94 5차)에도 같은 폴더로
    /// 반복 발화한다 — 이 창 기준으로 폴더가 실제로 바뀐 항해만 저장이 돌고(_lastBrowsedFolder),
    /// 같은 설정값 조기 반환(종전 RememberLastFolder 관용구)이 중복 Save를 한 번 더 거른다.
    /// </summary>
    private void RememberBrowsedFolder(string folder)
    {
        if (folder.Length == 0 || folder == _lastBrowsedFolder) return;
        _lastBrowsedFolder = folder;
        if (_settings.Get(LastFolderKey, string.Empty) == folder) return;
        _settings.Set(LastFolderKey, folder);
        _settings.Save();
    }

    /// <summary>모듈 뷰가 파일을 열었다는 알림(IContentStateSource) — 탐색기를 내리고 기준 경로 갱신.</summary>
    private void OnContentOpened(string path)
    {
        // A90: 뷰 내부 열기도 "새 콘텐츠가 화면을 차지"이므로 S4 자동 종료(SetContentState와 동일 규칙).
        ExitOpenFileBrowsing(restore: false, refresh: false);
        ResetBarAutoHide(); // A186: 콘텐츠 교체 = 타이머 정지·바 복원(새 재생이 다시 연다)
        var wasUntitled = _untitledContent; // A189: 무제 → 첫 저장(Save as)의 경로 확정 전이인지
        _untitledContent = false;
        _currentFilePath = path;
        _selectedBrowse = null; // A200: 뷰 내부 열기(◀/▶ 등)도 열기 — 선택 축 리셋(SetContentState와 동일 규칙)
        InfoOverlay.InvalidateCache();
        RememberLastFolder(); // v0.55.0
        UpdateEmptyExplorer();
        UpdateDriveStrip();   // A22: 뷰가 파일을 열었다 → 드라이브 줄을 숨긴다
        ApplyOverlayStates(); // 폴더·정보가 바뀌었을 수 있다 — 떠 있는 오버레이·도크 갱신
        // A189: 무제 → 저장 전이는 창 제목도 새 경로로("KOTU - Untitled" → "KOTU - 파일명").
        // 기존 파일 Save as의 제목 미갱신(A113 알려진 한계)은 그대로다 — 이번 수리는 무제 경로만.
        // ● 표시는 건드리지 않는다: 저장 성공의 더티 해제(UnsavedChanged)가 같은 디스패처 큐에서
        // 이 호출 직후 도착해 끈다(DocumentView.CommitSave의 통지 순서).
        if (wasUntitled) SetTitle(FileTitle(path));
        // A54: 유휴(3자) → 열림(2줄) 전환도 이 경로로 걸린다.
        // A137: 뷰 내부 열기(◀/▶ 등)도 창 32px의 확장자/용량을 바꾸므로 셸 아이콘 전체 갱신.
        RefreshShellIcons();
    }

    /// <summary>
    /// A189: 뷰가 무제 문서(경로 없는 콘텐츠)로 에디터에 진입했다는 알림(IUntitledContentSource) —
    /// <see cref="OnContentOpened"/>의 경로 없는 판본. 탐색기(S1)를 내리고 드라이브 줄을 숨기고
    /// 제목을 "KOTU - Untitled"(A103 연장 — FileTitle과 같은 하이픈 구분자, 표기는
    /// DocumentView.UntitledDisplayName과 동기)로 바꾼다.
    /// A196: 무제도 패널 컨텍스트다(HasPanelContext — 빈 파일 모듈과 동일 취급: F11/F12·경계
    /// 버튼 동작, 좌 리스트 = 전역 마지막 폴더(A174) + 문서 모듈 필터, 우 정보 = "No file open"
    /// 플레이스홀더). S4는 종전대로 무동작(파일 가드), 트레이·32px 아이콘 유휴(OpenFileIconInfo의
    /// File.Exists 가드), 마지막 폴더 무변경(RememberLastFolder의 null 가드)도 폴백 그대로다.
    /// 첫 저장이 경로를 확정하면 ContentOpened가 정상 콘텐츠로 승격시킨다.
    /// </summary>
    private void OnUntitledOpened()
    {
        // S1에서 S4는 성립하지 않지만 OnContentOpened와 같은 순서를 지킨다(방어 — 무해한 무동작).
        ExitOpenFileBrowsing(restore: false, refresh: false);
        ResetBarAutoHide();
        _currentFilePath = null;
        _untitledContent = true;
        _selectedBrowse = null; // A200: 무제 진입도 콘텐츠 전환 — 선택 축 리셋(방어 — S1 경유라 대개 이미 null)
        InfoOverlay.InvalidateCache();
        UpdateEmptyExplorer(); // IsEmptyFileModule=false → 중앙 썸네일 탐색기를 내린다(에디터가 중앙)
        UpdateDriveStrip();    // 무제도 "콘텐츠 열림" — 드라이브 줄 대신 파일명(Untitled) 칸
        ApplyOverlayStates();
        _titleDirtyMark = false; // 진입 직후는 무변경(더티 기준 = 빈 문자열 — DocumentView)
        SetTitle($"{Branding.AppName} - Untitled");
        RefreshShellIcons(); // 경로 없음 = 유휴 폴백으로 회귀(파일을 보다 무제로 오는 경로는 없지만 일관 갱신)
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
        // A189: 무제 문서도 "열려 있음" — 드라이브 줄 대신 파일명 칸(Untitled)이 보여야 한다.
        var show = _currentFilePath is null && !_untitledContent;
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
    /// A189: 무제 문서는 경로가 없어도 콘텐츠다 — S1이 아니다(탐색기를 다시 띄우면 에디터를 덮는다).
    /// </summary>
    private bool IsEmptyFileModule =>
        _currentFilePath is null && !_untitledContent && IsFileModule(_currentModule);

    // A176: 스왑체인 반투명 폴백 판별(A129 — IsSwapChainModule/IsSwapChainContent)은 반투명
    // 표시 축과 함께 철거 — 패널·S4 배경이 전부 불투명이라 아크릴 배경 샘플 문제 자체가 없다.

    /// <summary>
    /// 현재 뷰가 좌/우 패널 콘텐츠를 직접 내주는 뷰(ISidePanelProvider — A119, 지금은 정보 모듈뿐)면
    /// 그 계약, 아니면 null. 파일 오버레이 대신 SidePanelHost에 모듈 콘텐츠를 얹는 분기의 기준이다.
    /// </summary>
    private ISidePanelProvider? PanelProviderView => ModuleHost.Content as ISidePanelProvider;

    /// <summary>
    /// A205(v0.208.0): 지금 중앙이 설정 화면인지 — 설정은 좌/우 사이드바를 **전면 배제**한다
    /// (사용자 확정: "설정 모듈은 좌우 사이드바 안 떠야 해"). 판별을 모듈 축(_currentModule is null)이
    /// 아니라 **뷰 타입**으로 하는 이유: 모듈 축으로 거르면 같은 폴백 화면인 미지원 파일 안내까지
    /// 함께 빠져 A196 편입분이 무너진다(설정만 도로 제외가 사양).
    /// </summary>
    private bool IsSettingsView => ModuleHost.Content is SettingsView;

    /// <summary>
    /// A196: 모듈 축 밖의 폴백 패널 컨텍스트 화면(미지원 파일 안내)인지 — 모듈은 없지만
    /// 중앙에 뷰가 있는 상태다. 좌 리스트는 전역 마지막 폴더(A174)에 **전체 파일 필터**
    /// (ExplorerListing.AllFiles — 모듈 개념이 없어 담당 확장자도 없다), 우 정보는 "No file open"
    /// 플레이스홀더를 쓴다. 빈 셸(ModuleHost 비어 있음 — 창 생성 직후 잠깐)만 컨텍스트 밖으로 남는다.
    /// A205(v0.208.0 — A196 부분 반전): **설정 화면은 여기서 제외**된다. 이 속성이 게이트 산식
    /// 3곳(HasPanelContext · ApplyOverlayStates의 fallback 축 · RefreshInfoOverlayForSelection)의
    /// 유일한 폴백 입력이라, 여기 한 곳을 좁히면 세 산식이 함께 정합한다(F11/F12·경계 버튼·표시가
    /// 동시에 꺼진다 — "키는 죽었는데 패널은 뜨는" 모순 없음).
    /// </summary>
    private bool IsPanelFallbackView =>
        _currentModule is null && ModuleHost.Content is not null && !IsSettingsView;

    /// <summary>
    /// 좌/우 패널(오버레이/사이드바) 컨텍스트가 있는가 — 게이트 확장의 단일 판정(A119 통일):
    /// 파일 열림 · 빈 파일 모듈(A81) · 패널 제공 뷰(A119 — 정보 모듈) · **무제 문서(A189) ·
    /// 미지원 파일 안내(A196 — 위 폴백 화면)**. F11/F12 소비·상태 전이(ApplyOverlayStates의
    /// 폴백 축)·경계 버튼·CurrentShellState가 전부 이걸 탄다. 남은 예외 = 빈 셸(중앙에 아무 뷰도
    /// 없음)과 **설정 화면(A205 — 사이드바 전면 배제)**이고, S4 중 무동작 게이트
    /// (IsOpenFileBrowsing)는 별도 존치(사양).
    /// </summary>
    private bool HasPanelContext
        => _currentFilePath is not null || IsEmptyFileModule || PanelProviderView is not null
           || _untitledContent || IsPanelFallbackView;

    /// <summary>좌 패널이 화면에 떠 있는가 — 파일 컨텍스트는 ListOverlay, 패널 제공 뷰(A119)는
    /// LeftPanelHost가 표면이다(도크 폭·경계 버튼 x 계산 공용 판정).</summary>
    private bool LeftPanelIsOpen => PanelProviderView is not null ? LeftPanelHost.IsOpen : ListOverlay.IsOpen;

    /// <summary>우 패널 판정 — 위와 대칭(ContentInfoOverlay/RightPanelHost).</summary>
    private bool RightPanelIsOpen => PanelProviderView is not null ? RightPanelHost.IsOpen : InfoOverlay.IsOpen;

    /// <summary>
    /// 빈 상태의 시작 폴더 (A174 — v0.55.0 "그 모듈의 마지막 폴더" 규칙의 개정, 모듈 무관):
    /// ① 세션 안에서는 좌 리스트가 지금 보고 있는 폴더(현재 위치 유지 — 모듈 전환에도 리셋 없음),
    /// ② 리스트가 아직 없으면(앱 재시작·새 창) 전역 마지막 폴더, ③ 둘 다 없거나 사라졌으면 바탕화면.
    /// 세션 위치를 설정 키보다 먼저 보는 이유: 설정은 창 간 공유라, 다른 창의 브라우징이 이 창의
    /// 리스트를 끌고 가면 안 된다 — 전역 키는 새 리스트의 초기값으로만 쓴다(브라우징 갱신은
    /// RememberBrowsedFolder가 하므로 한 창에서는 두 값이 사실상 일치한다).
    /// 중앙 탐색기(A93)와 A81 빈 도크의 리스트 오버레이가 같은 규칙을 공유한다.
    /// </summary>
    private string ExplorerStartFolder()
    {
        if (ListOverlay.CurrentFolder is { } current && Directory.Exists(current)) return current;
        var saved = _settings.Get(LastFolderKey, string.Empty);
        if (saved.Length > 0 && Directory.Exists(saved)) return saved;
        return Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
    }

    /// <summary>
    /// 빈 상태(파일 없이 연 압축/이미지/동영상/오디오/문서 모듈)면 중앙에 썸네일 뷰를 띄운다
    /// (A93 — 구 ExplorerPane 중앙 탐색기 대체. A81의 "좌 도크 열림 시 숨김"도 대체 — 항상 표시).
    /// 시작 위치는 좌 리스트의 현재 위치(A174 — 없으면 전역 마지막 폴더/바탕화면), 파일은 담당 확장자만.
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
                _thumbnailExplorer.SelectionChanged += OnBrowseSelectionChanged; // A200 — 선택 우선 정보
                ExplorerHost.Children.Add(_thumbnailExplorer);
            }
            // A174: 빈 모듈 전환은 좌 리스트의 현재 위치를 리셋하지 않는다 — ExplorerStartFolder가
            // 세션 현재 폴더를 그대로 돌려주고, 필터만 새 모듈 것으로 바뀐다(A57 ③ 모듈 필터 유지).
            // 현재 폴더에 이 모듈의 확장자가 0건이면 빈 목록이 된다 — 사양상 허용(등재문 ⓑ).
            ListOverlay.NavigateList(ExplorerStartFolder(), module.SupportedExtensions);
            ExplorerHost.Visibility = Visibility.Visible;
        }
        else
        {
            ExplorerHost.Visibility = Visibility.Collapsed;
        }
    }

    // ---------- 좌/우 패널 키 입력 (A176 단타 토글 + A158 F11/F12) ----------

    /// <summary>
    /// 좌/우 패널 키 정본 — 되돌리기·재배정 지점을 이 두 상수 한 곳으로 모았다: 키를 바꾸려면
    /// 여기만 고친다(터널링 수신(A226)·S4 게이트가 전부 SideForKey 경유).
    /// 이력: A107(v0.134.0) Alt+Z/X → A118(v0.144.0) F1/F2 → **A158 F11/F12**(A151이 F11을
    /// 전체화면 매핑에서 뺀 뒤라야 성립하는 순서 의존이었다).
    /// 힌트 문구의 키 표기는 OverlayHints.ListKey/InfoKey — 함께 고칠 것.
    /// </summary>
    private const VirtualKey LeftPanelKey = VirtualKey.F11;  // 좌측 파일 리스트
    private const VirtualKey RightPanelKey = VirtualKey.F12; // 우측 정보

    /// <summary>
    /// 키 → 패널 사이드 매핑 (A118이 A107의 Alt+Z/X를 단독 F키로 대체하며 Alt 게이트 폐지,
    /// A158이 그 F키를 F11/F12로 재배정): F11 = 좌측 파일 리스트, F12 = 우측 정보. 그 밖의 키는
    /// null. 탐색기 표면의 F2 = 이름변경(A94)과의 "원 기능 우선" 공존은 **A158에서 충돌 자체가
    /// 소멸**했다(패널 키가 F12로 옮겨갔다) — 양보 경로(구 OnOverlaySideKey의 Handled 검사)도
    /// A226 터널링 승격에서 삭제됐다(실사례 0인 채 내장 소비까지 양보하던 게 불통의 축).
    /// Z·X는 미배정이다.
    /// </summary>
    private OverlaySide? SideForKey(VirtualKey key) => key switch
    {
        LeftPanelKey => _listSide,
        RightPanelKey => _infoSide,
        _ => null,
    };

    /// <summary>Alt(Menu) 계열 키인지 — A58의 SideForKey가 매칭하던 3종 그대로(좌우 구분 없이 받는다).</summary>
    private static bool IsAltKey(VirtualKey key)
        => key is VirtualKey.Menu or VirtualKey.LeftMenu or VirtualKey.RightMenu;

    /// <summary>
    /// Alt(Menu)가 지금 눌려 있는지 — Alt 단독 up 조건 소비 판정(MarkAltUseIfConsumed)에 쓴다.
    /// A107의 Z/X 게이트 용도는 A118(단독 F키 — A158에서 F11/F12로 재배정)로 소멸했지만 부기 자체는 Alt+`가
    /// 여전히 필요로 한다(A147/v0.163.0에서 Alt+숫자·Alt+0이 폐지돼 남은 조합은 그 하나다). 판정 API는 저장소 선례(ExplorerFileOps.IsCtrlDown/IsShiftDown —
    /// Ctrl+X 잘라내기와 같은 층)와 동일하다.
    /// </summary>
    private static bool IsAltDown() => Microsoft.UI.Input.InputKeyboardSource
        .GetKeyStateForCurrentThread(VirtualKey.Menu)
        .HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down);

    /// <summary>
    /// 이번 Alt 세션(누름~뗌)에서 우리 조합(Alt+` 액셀러레이터, Alt 홀드 중 셸이 소비한
    /// Esc/Enter/GoBack/F11/F12 포함)이 발화했는지 — Alt 단독 up의 조건 소비(OS 메뉴 모드 회피,
    /// A107이 A58 방식을 개작해 재도입 — A118 뒤에도 Alt 액셀러레이터용으로 존치)의 근거.
    /// A58은 Alt 자체가 오버레이 키라 "down을 본 Alt의 up"을 전부 소비했지만, A107 이후의 Alt는
    /// 수식키라 깨끗한 단독 탭(조합 미사용)은 통과시켜 OS 기본 동작을 보존한다.
    /// </summary>
    private bool _altComboUsed;

    /// <summary>
    /// Alt가 눌린 채 도착한 키를 셸(또는 그보다 앞선 소비자)이 Handled로 끝냈으면 조합 사용으로
    /// 기록한다(A107) — OS가 "Alt 중 눌린 키"를 못 봤으므로 Alt 단독 up을 우리가 소비해야 한다.
    /// 소비되지 않고 흘러간 키(Alt+Space의 Space 등)는 OS가 직접 봤으니 기록하지 않는다 —
    /// OS 자체 판정이 메뉴 모드를 막는다(시스템 조합 무간섭 원칙).
    /// </summary>
    private void MarkAltUseIfConsumed(KeyRoutedEventArgs e)
    {
        if (e.Handled && IsAltDown()) _altComboUsed = true;
    }

    private void OnRootKeyDown(object sender, KeyRoutedEventArgs e)
    {
        // A234 계측: 버블 KeyDown이 트리에 도달은 하는지(아무 키든 — F11/F12 한정 아님).
        // 어느 분기보다 먼저 센다(아래 return들과 무관하게 "도달" 자체가 신호다).
        // 오버레이가 꺼져 있으면 갱신하지 않는다(핫 패스 비용 0 — _diagOn 앞단 차단).
        if (_diagOn) _diagBubbleCount++;

        // A90: Esc는 텍스트 입력 판정보다 먼저 본다 — keymap 포커스 예외가 "텍스트 입력에서도
        // Esc만은 통과"라서다(필터 입력란에 포커스를 둔 채로도 S4 복귀가 성립해야 한다).
        // A202: 그래서 문서 에디터 포커스 중 Esc도 셸 체인(말단 = 콘텐츠 닫기)에 닿는다 —
        // 더티면 ShowModule의 미저장 가드가 묻는다. 이름변경 편집 상자는 자체 Esc 소비(취소)가
        // 먼저다(ExplorerRenameBox — e.Handled 존중). IME 조합 취소는 IME가 키를 먹어 여기 안 온다.
        if (e.Key == VirtualKey.Escape)
        {
            OnShellEscape(e);
            MarkAltUseIfConsumed(e); // A107: Alt 홀드 중 셸이 Esc를 소비(S4 복귀)한 경우도 Alt up 소비 대상
            return;
        }

        // A107(A176 뒤에도 존치): Alt(Menu)는 새 물리 누름마다 조합 사용 플래그만 초기화한다
        // (깨끗한 단독 탭 = OS 기본 통과). down은 소비하지 않는다 — OS 메뉴 모드는 Alt "up"에서
        // 발동하므로 소비는 OnRootKeyUp의 조건 소비 하나로 충분하고, down 시점에는 조합이 될지
        // 아직 몰라 무조건 소비하면 시스템 조합까지 건드리는 반대 함정이 있다.
        if (IsAltKey(e.Key))
        {
            if (!e.KeyStatus.WasKeyDown) _altComboUsed = false;
            return;
        }

        // A226: F11/F12(SideForKey) 분기는 여기 없다 — 터널링(OnRootPreviewKeyDown)으로 이관됐다
        // (계보: A118 단독 F키 → A158 F11/F12 재배정 → A226 터널링 승격. 버블 분기를 남기면
        // 터널링이 소비한 키가 handledEventsToo 구독 탓에 여기로도 와 죽은 이중 경로가 된다 —
        // 깔끔히 제거. 이 핸들러에 F11/F12가 도착해도 아래 분기 어디에도 안 걸려 무동작이다).

        // A151(A186 승계): Alt+Enter = 상황 무관 전체화면 토글. 텍스트 입력 판정보다 **앞**이다 —
        // 문서 편집 중(Enter = 줄바꿈이라 토글 불가)에도 전체화면 진입로가 있어야 한다.
        // Alt 조합 키가 이 핸들러로 오는 것은 A58 실증(위 구독 주석)·MarkAltUseIfConsumed의
        // 전제 그대로다. 소비하면 Alt 단독 up도 조건 소비된다(OS 메뉴 모드 회피, A107).
        if (e.Key == VirtualKey.Enter && IsAltDown())
        {
            OnShellAltEnter(e);
            MarkAltUseIfConsumed(e);
            return;
        }

        // A86 포커스 예외 ①(A32 통과 규칙 재사용): 텍스트 입력 컨트롤에 포커스가 있으면
        // Enter 등 입력이 우선이다(문서 에디터의 Enter 줄바꿈을 뺏으면 안 된다 — A151 ④ⓐ 재확인).
        // Esc는 위에서 따로 처리했다(전체화면·S4는 텍스트 입력 중에도 Esc가 통해야 한다).
        if (IsTextInputFocused()) return;

        // A41: Ctrl + '+'/'-'(OEM·넘패드) = UI 배율 한 단계 증/감, Ctrl + 넘패드 '*' = 리셋
        // (0 = 시스템 따름). 위 IsTextInputFocused 분기가 텍스트 입력 양보(3종 세트 ②)를 겸한다 —
        // 에디터의 Ctrl+±는 시스템/에디터 몫이다. Ctrl 판정은 저장소 관용구(ExplorerFileOps.IsCtrlDown).
        if (UiScaleStepForKey(e.Key) is { } scaleStep && ExplorerFileOps.IsCtrlDown())
        {
            OnUiScaleKey(scaleStep, e);
            return;
        }

        if (e.Key == VirtualKey.Enter)
        {
            OnShellEnter(e); // A186: Alt+Enter와 동일한 전체화면 토글 — 원 기능 우선 판정 포함
            // A107: Alt를 쥔 채 Enter를 셸이 소비하면 OS는 "Alt 중 눌린 키"를 못 본다
            // — Alt 액셀러레이터와 같은 이유로 이 Alt의 단독 up도 소비 대상이다.
            MarkAltUseIfConsumed(e);
            return;
        }

        if (e.Key == VirtualKey.GoBack)
        {
            OnShellBack(e); // A112: Browser Back 키 = 마우스 XButton1과 같은 '뒤로' 분배
            // 위 IsTextInputFocused 분기가 텍스트 입력 가드를 겸한다(타이핑 중 GoBack은 여기 안 온다).
            MarkAltUseIfConsumed(e); // Esc·Enter와 같은 규칙 — Alt를 쥔 채 소비했으면 Alt up도 소비
        }
        // 그 밖의 키는 셸 몫이 없다 — A176: 구 "다른 키 개입 = 홀드 취소" 안전장치는
        // 홀드 판정 기계와 함께 철거됐다(단타 토글에는 취소할 진행 상태가 없다).
    }

    /// <summary>
    /// A226: F11/F12 전용 터널링 수신 지점 — RootLayout.PreviewKeyDown(생성자 구독, DocumentView
    /// A121 선례 형태)이라 포커스 요소·WinUI 컨트롤 내장 KeyDown보다 먼저 온다. 계기: A212가
    /// 앱 코드 선소비 0건을 전수 증빙했는데도 "특정 영역 한 번 클릭 후 F11/F12 무반응"이 전
    /// 모듈에서 재보고됐다(2026-08-25) — 남는 축은 컨트롤 내장 처리의 Handled 선점이고, 그
    /// 축은 버블 수신으로는 원천 봉쇄가 안 된다. 여기서 조건 충족 시 소비하면(e.Handled)
    /// 내장 처리·버블 전 구간이 그 키를 못 본다(패널 토글 우선이 사양 — 플라이아웃·콤보 등
    /// 팝업 트리 포커스는 애초에 이 트리로 라우팅되지 않아 종전과 같이 못 받는 점도 무변경).
    /// MarkAltUseIfConsumed는 버블 시절(OnRootKeyDown의 구 분기)과 같은 규칙으로 함께 이관 —
    /// Alt를 쥔 채 소비했으면 Alt 단독 up도 소비 대상이다(A107).
    /// </summary>
    private void OnRootPreviewKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (SideForKey(e.Key) is not { } side) return; // 그 밖의 키는 전부 종전 경로(버블) 몫
        // A234 계측의 핵심 신호: 클릭 후 F11을 눌러도 이 카운트가 안 오르면 라우팅 키 이벤트
        // 자체가 미발화(포커스 null 또는 RootLayout 밖) = 갈래 ⓐ/ⓑ 확정이다. 값만 기록하고
        // 문자열 조립은 폴링(UpdateDiagStrip) 몫 — 오버레이 꺼짐 = 비용 0(_diagOn 앞단 차단).
        // 배치 3: 마지막 키(last=) 표기는 DET 자리를 내려고 제거했다 — 카운터가 신호의 본체다.
        if (_diagOn) _diagPreviewCount++;
        OnOverlaySideKey(side, e);
        MarkAltUseIfConsumed(e);
    }

    /// <summary>
    /// 좌/우 패널 키(A158: F11/F12) down 분배 — 호출부가 A226부터 터널링(OnRootPreviewKeyDown)
    /// 이라 텍스트 입력 판정은 물론 포커스 요소의 내장 처리보다도 앞이다.
    /// 순서: ① S4 무동작 소비 ② 첫 down만 토글 ③ 컨텍스트 소비.
    /// 구 게이트 "원 기능 우선"(e.Handled 양보 — A118 시절 탐색기 F2 = 이름변경(A94 2차)과
    /// 겹치던 자리. A158에서 패널 키가 F12로 옮겨가 실사례가 소멸한 일반 규칙)은 A226에서
    /// 삭제했다: 내장 Handled 선점을 무력화하는 것이 터널링 승격의 목적 그 자체이고, 터널링
    /// 시점엔 우리보다 앞선 소비자가 트리에 없어(RootLayout 위층 구독자 0) 검사할 값도 없다.
    /// A176: 홀드/2초/2연타 판정은 폐지 — **단타 = 그 쪽 토글** 하나다. 오토리피트를 거르는
    /// 이유도 "2연타 오염 방지"에서 "꾹 누르면 토글이 연사되는 것 방지"로 바뀌었다.
    /// </summary>
    private void OnOverlaySideKey(OverlaySide side, KeyRoutedEventArgs e)
    {
        // A90 keymap S4 행: 좌/우 키 = 무동작 (Q5 확정) — 토글에 태우지 않고 소비만 한다.
        if (IsOpenFileBrowsing)
        {
            e.Handled = true;
            return;
        }
        if (!e.KeyStatus.WasKeyDown) OnOverlaySideDown(side); // 오토리피트 제외 — 토글 연사 방지
        // 패널 컨텍스트가 있으면 소비한다(오토리피트 포함) — F11/F12는 시스템 조합이 아니고
        // 다른 수신자도 없다. A119: 패널 제공 뷰(정보 모듈)도 컨텍스트다(HasPanelContext).
        // A196: 설정·미지원 안내·무제 문서도 편입 — 컨텍스트 없음(빈 셸)만 무소비로 남는다.
        if (HasPanelContext) e.Handled = true;
    }

    /// <summary>
    /// 사이드 키(A158: F11/F12) 최초 down(오토리피트 제외) = **그 쪽 사이드바 토글**
    /// (A176 확정, 부록 B 72 — A58 계보의 홀드/2초 승격/2연타 전이 전면 폐지).
    /// 양쪽 사이드가 각자 독립 토글이라 F11+F12를 이어 눌러도 자연히 동시 호출이 성립한다.
    /// </summary>
    private void OnOverlaySideDown(OverlaySide side)
    {
        if (IsOpenFileBrowsing) return; // A90 keymap S4: 좌/우 키 = 무동작 — OnOverlaySideKey 가드의 이중 방어선(A226에서 호출부만 터널링으로 이동, 게이트 의미 불변)

        // 패널 컨텍스트가 없으면(A196부터 빈 셸뿐) 무동작. 파일 없이 연 파일 모듈(빈 모듈
        // 상태)은 A81부터, 패널 제공 뷰(정보 모듈)는 A119부터, 무제 문서·설정·미지원 안내는
        // A196부터 컨텍스트에 포함 — 기본 도크를 키로 닫고 다시 여는 입력이 성립해야 한다.
        if (!HasPanelContext) return;

        side.State = side.State == OverlayState.OpaqueDocked
            ? OverlayState.Closed
            : OverlayState.OpaqueDocked;
        ApplyOverlayStates();
    }

    private void OnRootKeyUp(object sender, KeyRoutedEventArgs e)
    {
        // A107: **우리 조합에 쓰인 Alt의 단독 up만 소비** — A86이 제거한 A58의 OS 메뉴 모드 회피를
        // 개작 재도입한 본체다. 조합 키 down을 우리가 소비하면 OS는 "Alt가 깨끗하게 눌렸다 떼졌다"로
        // 보고 up에서 창 메뉴 모드(SC_KEYMENU)에 들어가 포커스를 훔친다(Alt를 나중에 떼는 순서의 함정)
        // — 그 up을 여기서 대신 소비해 막는다. 반대로 조합 미사용(깨끗한 Alt 탭)은 통과 = OS 기본
        // 동작 유지. Alt+Tab(비활성화로 up이 우리에게 안 온다)·Alt+F4/Alt+Space(발동은 그 키의
        // down이고 우리는 그 down을 소비하지 않는다)는 이 조건에 걸리지 않아 무영향이다.
        // A176: F11/F12 up의 홀드 세션 종료 판정은 홀드 기계와 함께 철거 — up은 어느 키든
        // 소비하지 않는다(A86 이래의 "패널 키 up 무소비" 유지).
        if (IsAltKey(e.Key) && _altComboUsed)
        {
            _altComboUsed = false;
            e.Handled = true;
        }
    }

    // ---------- UI 배율 라이브 조절 (A41) ----------

    /// <summary>표면 판정의 조상 순회 상한 — HotkeySupport.MaxAncestorDepth와 같은 방어 값.</summary>
    private const int UiScaleAncestorDepth = 64;

    /// <summary>
    /// A41 배율 키 판별: +1 = 확대 / -1 = 축소 / 0 = 리셋(시스템 따름) / null = 배율 키 아님.
    /// '+'/'-' 문자 키는 VirtualKey에 이름이 없어 VK_OEM_PLUS(187)/VK_OEM_MINUS(189)를 int 캐스트로
    /// 쓴다 — Alt+`의 (VirtualKey)192(VK_OEM_3, RegisterShortcuts)와 같은 관용구.
    /// 리셋은 Ctrl+넘패드 '*'다 — Ctrl+0은 쓰지 않는다(설정 진입 충돌 회피, 사양 확정).
    /// </summary>
    private static int? UiScaleStepForKey(VirtualKey key) => key switch
    {
        VirtualKey.Add or (VirtualKey)187 => 1,       // 넘패드 + / '='(Shift로 +가 되는 그 키)
        VirtualKey.Subtract or (VirtualKey)189 => -1, // 넘패드 - / '-'
        VirtualKey.Multiply => 0,                     // 넘패드 * = 리셋
        _ => null,
    };

    /// <summary>
    /// A41 배율 키 실행부 — 호출부(OnRootKeyDown)가 Ctrl 판정·텍스트 입력 양보(3종 세트 ②)를
    /// 마친 뒤 부른다. ① 오토리피트는 **허용**한다(꾹 눌러 연속 조절이 사양 — A121 PDF 스크롤이
    /// 오토리피트를 살려 둔 것과 같은 취지. 토글 키들의 WasKeyDown 가드를 일부러 두지 않는다).
    /// ③ 탐색기 통과 표면(PassThroughTag)·텍스트 입력은 ShouldPassThrough로 양보한다.
    /// </summary>
    private void OnUiScaleKey(int step, KeyRoutedEventArgs e)
    {
        if (e.Handled) return; // 원 기능 우선 — 먼저 소비한 쪽에 양보(셸 공통 규칙)
        if (HotkeySupport.ShouldPassThrough(RootLayout)) return; // 3종 세트 ③ — 표면 양보
        e.Handled = true;
        if (step == 0) SetUiScaleSetting(0);
        else StepUiScale(step);
    }

    /// <summary>
    /// A41 Ctrl+휠 — 적용 표면은 **하단 바 위 + 빈 셸뿐**이다(사양 확정). 모듈 콘텐츠 위의
    /// Ctrl+휠은 기존 동작(사진 줌 A98·문서/PDF 줌·영상 볼륨)이 그대로 가져간다 — 구독이
    /// handledEventsToo 없음이라 소비된 휠은 애초에 안 오고, 안 소비된 휠도 아래 표면 판정이
    /// 거른다(판정이 애매한 표면은 기존 동작 우선 = 무동작).
    /// </summary>
    private void OnRootPointerWheel(object sender, PointerRoutedEventArgs e)
    {
        if (!e.KeyModifiers.HasFlag(Windows.System.VirtualKeyModifiers.Control)) return;
        if (!IsUiScaleWheelSurface(e.OriginalSource as DependencyObject)) return;
        var delta = e.GetCurrentPoint(RootLayout).Properties.MouseWheelDelta;
        if (delta == 0) return;
        e.Handled = true;
        StepUiScale(delta > 0 ? 1 : -1);
    }

    /// <summary>
    /// 휠이 배율 조절 표면 위에서 굴렀는가 — ① 히트 요소의 조상에 BottomBar가 있으면 하단 바 위,
    /// ② 패널 컨텍스트 없는 빈 셸(HasPanelContext false)이면 전면 허용. 단 설정 화면은 A205부터
    /// 컨텍스트 밖이어도 콤보 등 컨트롤 위 휠이 애매하므로 제외한다(기존 동작 우선).
    /// 그 밖(모듈 콘텐츠·패널·탐색기)은 전부 거짓 = 기존 동작 유지.
    /// </summary>
    private bool IsUiScaleWheelSurface(DependencyObject? source)
    {
        if (!HasPanelContext && !IsSettingsView) return true; // 빈 셸 — 충돌할 콘텐츠가 없다
        var node = source;
        for (var depth = 0; node is not null && depth < UiScaleAncestorDepth; depth++)
        {
            if (ReferenceEquals(node, BottomBar)) return true;
            if (ReferenceEquals(node, RootLayout)) return false; // 바를 안 거치고 루트 도달 — 콘텐츠 표면
            node = VisualTreeHelper.GetParent(node);
        }
        return false;
    }

    /// <summary>
    /// UiScale.Percents 목록 위를 한 칸 이동(A41). 현재 값이 0(시스템 따름)·목록 밖 값이면
    /// **현재 유효 배율**(ApplyUiScale의 percent 계산과 같은 축 — 0이면 유효 배율 = 시스템
    /// RasterizationScale×100)에 가장 가까운 목록 값을 기준 칸으로 삼는다. XamlRoot가 살아 있는
    /// 값을 주므로 모니터 이동 뒤에도 그 모니터 기준으로 성립한다. 목록 끝에서 더 가면 무동작.
    /// </summary>
    private void StepUiScale(int direction)
    {
        var percents = UiScale.Percents;
        var current = _settings.Get(UiScale.SettingKey, 0);
        var index = Array.IndexOf(percents, current);
        if (index < 0)
        {
            var effective = current > 0
                ? (double)current
                : (RootLayout.XamlRoot?.RasterizationScale ?? 1.0) * 100.0;
            index = 0;
            for (var i = 1; i < percents.Length; i++)
            {
                if (Math.Abs(percents[i] - effective) < Math.Abs(percents[index] - effective))
                    index = i;
            }
        }
        var next = index + direction;
        if (next < 0 || next >= percents.Length) return; // 목록 끝 — 무동작
        SetUiScaleSetting(percents[next]);
    }

    /// <summary>
    /// 배율 저장·전파의 단일 깔때기 — 설정 화면 콤보(SettingsView.BuildDisplaySection)와 같은
    /// 순서(Set → Save → NotifyChanged)로만 쓴다(A41 사양: 새 깔때기 금지). 같은 값이면 무동작 —
    /// 리셋 중복·목록 끝 무동작에서 저장 파일을 다시 쓰지 않는다.
    /// </summary>
    private void SetUiScaleSetting(int value)
    {
        if (value == _settings.Get(UiScale.SettingKey, 0)) return;
        _settings.Set(UiScale.SettingKey, value);
        _settings.Save();
        UiScale.NotifyChanged(); // 열린 모든 창의 ApplyUiScale + 설정 콤보 동기(A41)
    }

    // ---------- 셸 표시 모드 토글 (A151 3단 순환 → A186 전체화면 토글로 단순화) ----------

    /// <summary>
    /// Enter = **Alt+Enter와 동일한 전체화면 토글**(A186 ① — A151의 3단 순환 폐지).
    /// 원 기능 우선 예외(A151 ④ 그대로 — 3종 세트):
    /// ① 오토리피트 무시(꾹 누르면 왕복 연사되면 안 된다)
    /// ② 텍스트 입력(문서 에디터 줄바꿈)은 호출 전에 걸러진다(OnRootKeyDown의 IsTextInputFocused —
    ///    PDF·4MB 잘림 읽기 전용 문서는 텍스트 포커스가 아니라 토글 대상이다)
    /// ③ 탐색기 표면 포커스(중앙 썸네일·좌 트리·좌 리스트·S4 그리드) = 선택 열기 우선 —
    ///    표면이 선택을 직접 열어 소비하면 위 Handled에서 끝나고, 선택이 없어 안 삼킨 Enter도
    ///    통과 표면 판정(ShouldPassThrough — PassThroughTag)으로 양보한다.
    /// 영상 모듈도 이 경로다 — 영상 전용 Enter=전체화면 액셀러레이터는 A151에서 제거된 그대로다.
    /// </summary>
    private void OnShellEnter(KeyRoutedEventArgs e)
    {
        if (e.KeyStatus.WasKeyDown) return; // 오토리피트 — 토글이 연사되면 안 된다
        if (e.Handled) return; // 탐색기 그리드(Enter = 선택 항목 열기)가 이미 소비 — 원 기능 우선
        if (HotkeySupport.ShouldPassThrough(RootLayout)) return; // 탐색기 표면 포커스 — 선택 열기 우선
        e.Handled = true;
        ToggleFullScreen();
    }

    /// <summary>
    /// Alt+Enter(A151 ② — A186에서 Enter와 한 실행부로 수렴): 전체화면 토글 + 복귀 지점 기억.
    /// 텍스트 입력·탐색기 표면 양보 없이 동작한다(직행 단축키 — 호출부가 텍스트 입력 판정보다
    /// 앞에서 부른다). 오토리피트는 무시(연사로 왕복하지 않게).
    /// </summary>
    private void OnShellAltEnter(KeyRoutedEventArgs e)
    {
        if (e.KeyStatus.WasKeyDown || e.Handled) return;
        e.Handled = true;
        ToggleFullScreen();
    }

    /// <summary>
    /// 전체화면 토글의 단일 실행부(A186): Enter·Alt+Enter·하단 바 "Full screen" 버튼이 전부
    /// 이 한 함수로 수렴한다 — 창 모드면 진입(복귀 스냅샷 기억), 전체화면이면 스냅샷 복귀.
    /// </summary>
    private void ToggleFullScreen()
    {
        if (_viewMode == ShellViewMode.FullScreen) RestoreFromFullScreen();
        else EnterFullScreenRemembering();
    }

    /// <summary>
    /// 전체화면 진입 + 복귀 스냅샷(A151 ②③ — **A203 개정**): 스냅샷 = 진입 직전 좌/우 패널 상태
    /// (A176: 닫힘/도크 2상태 — A186: 모드 항목은 2단화로 소멸, 복귀는 항상 창 모드).
    /// A203: 진입하면서 **양쪽 패널을 닫는다**(콘텐츠 전용 화면 — 구 "패널은 건드리지 않는다"
    /// 폐지). 스냅샷 기록이 반드시 닫기보다 먼저다 — 뒤집으면 복귀가 항상 "닫힘"이 된다.
    /// 전체화면 중 F11/F12 패널 조작은 그대로 살아 있고(A153/A196 불변), 그렇게 바꾼 구성도
    /// 복귀(Esc/Enter/Alt+Enter/'뒤로'/버튼)가 이 스냅샷으로 되돌린다. 상태 적용은
    /// RestoreFromFullScreen과 같은 순서(State 대입 → SetViewMode → ApplyOverlayStates 단일 종착점).
    /// </summary>
    private void EnterFullScreenRemembering()
    {
        if (_viewMode == ShellViewMode.FullScreen) return; // 버튼 연타 등 재진입 방어
        _fullScreenRestore = (_listSide.State, _infoSide.State); // 닫기 전에 기록 — A203의 핵심 순서
        _listSide.State = OverlayState.Closed;
        _infoSide.State = OverlayState.Closed;
        SetViewMode(ShellViewMode.FullScreen);
        ApplyOverlayStates();
    }

    /// <summary>
    /// 전체화면에서 복귀(A151 — Esc·Enter/Alt+Enter 재누름·'뒤로'·버튼 공용): 창 모드로 돌아가며
    /// 스냅샷의 좌/우 패널 구성을 되돌린다. 스냅샷이 없으면(외부 경로로 전체화면에 들어온 경우)
    /// 패널 무변경으로 창 모드만. 패널 상태 적용은 반드시 ApplyOverlayStates(단일 종착점)를 거친다.
    /// </summary>
    private void RestoreFromFullScreen()
    {
        var restore = _fullScreenRestore;
        _fullScreenRestore = null;
        if (restore is { } r)
        {
            _listSide.State = r.List;
            _infoSide.State = r.Info;
            SetViewMode(ShellViewMode.Windowed);
            ApplyOverlayStates();
        }
        else
        {
            SetViewMode(ShellViewMode.Windowed);
        }
    }

    /// <summary>
    /// 모드 전이의 단일 실행점(A151): 프레젠터(전체화면 = FullScreen, 창 = Default)와 셸 크롬
    /// (하단 바·경계 버튼 여백)을 함께 맞춘다. 프레젠터는 실제로 다를 때만 만진다 — 최대화 등
    /// OverlappedPresenter 상태를 불필요하게 건드리지 않는다(기존 토글들과 같은 판단).
    /// 전체화면 진입 전에 A61 접힘을 먼저 펼친다 — 접힌 높이가 전체화면의 복원 크기로 굳지
    /// 않게(구 HardwareView.ToggleFullScreen의 "먼저 펼치기" 순서를 셸이 승계).
    /// A186: 모드가 바뀌면 영상 하단 바 자동 숨김도 재평가한다(표시 상태에서 카운트 재시작).
    /// </summary>
    private void SetViewMode(ShellViewMode mode)
    {
        _viewMode = mode;
        var wantFull = mode == ShellViewMode.FullScreen;
        var isFull = AppWindow.Presenter.Kind == Microsoft.UI.Windowing.AppWindowPresenterKind.FullScreen;
        if (wantFull && !isFull)
        {
            SetBarOnlyCollapsed(false); // A61: 접힌 채 전체화면 금지 — SetPresenter보다 먼저
            AppWindow.SetPresenter(Microsoft.UI.Windowing.AppWindowPresenterKind.FullScreen);
        }
        else if (!wantFull && isFull)
        {
            AppWindow.SetPresenter(Microsoft.UI.Windowing.AppWindowPresenterKind.Default);
        }
        ReevaluateBarAutoHide(); // A186: 전체화면 진입/해제 = 자동 숨김 상태 재평가
        UpdateShellChrome();
    }

    /// <summary>
    /// 하단 바 가시성의 단일 결정 지점(A151 — A186 개정): 입력 축 = (모드, 영상 자동 숨김).
    /// 실제 판정은 <see cref="BarVisible"/> 하나 — 영상 재생 표면이면 자동 숨김 축이,
    /// 아니면 모드 축(창 모드에서만 표시)이 정한다.
    /// A152: 바가 콘텐츠 위 오버레이가 되면서(행 분할 폐지) 종전의 행 높이 0 조작
    /// (BottomBarRow.Height)은 소멸했다 — 콘텐츠 영역(CenterArea)은 모드와 무관하게 창 전체라
    /// 바를 숨겨도 콘텐츠 중앙이 움직이지 않는다(A152의 목적). Visibility 토글 하나만 남는다.
    /// 경계 버튼 스택의 하단 여백도 바 실표시를 따라온다(UpdateEdgeButtons가
    /// <see cref="EdgeButtonsBottomInset"/>을 읽는다).
    /// </summary>
    private void UpdateShellChrome()
    {
        BottomBar.Visibility = BarVisible ? Visibility.Visible : Visibility.Collapsed;
        UpdateEdgeButtons();
        RecoverChromeFocusOrphan(); // A209: 바 붕괴 축 포커스 고아 방어 — 표시 반영 직후
    }

    /// <summary>
    /// A209: 크롬(하단 바) 붕괴 축의 포커스 고아 방어 — <see cref="UpdateShellChrome"/> 말미
    /// 후처리(+ A226부터 클릭 시점 판본 <see cref="OnRootPointerFocusGuard"/>도 같은 판정을
    /// 재사용한다). 포커스가 바 버튼 위인 채 바가 Collapsed되면(전체화면 진입·A186 자동 숨김) WinUI가
    /// 포커스를 null로 떨구거나 붕괴 요소에 남겨 두는데, 어느 쪽이든 이후 셸 KeyDown(RootLayout
    /// 버블 수신 — Esc·Enter·Alt+Enter·F11/F12 전부)이 미발화해 키가 전멸한다(실기기 확진
    /// 2026-08-21: 이미지 더블클릭 전체화면에서 키 3종 무반응·더블클릭만 생존 —
    /// docs/A135-audit.md §4-②가 예약해 둔 바로 그 갈래. ApplyOverlayStates 말미의 A135 2차
    /// 방어는 좌/우 패널 4표면만 커버하고 바 축은 무방비였다).
    /// 지점 선택: 전체화면 진입·해제의 두 경로(셸 SetViewMode · 외부 프레젠터 변화의
    /// AppWindow.Changed 동기 — 이미지 뷰 자체 토글 등. 후자는 SetViewMode를 거치지 않는다)와
    /// A186 자동 숨김(타이머 틱)까지, 바를 내리고 올리는 흐름 전부가 UpdateShellChrome 하나를
    /// 지난다(바 가시성의 단일 결정 지점). 호출 부위 전수(생성자 동기·SetViewMode·
    /// ResetBarAutoHide·NotifyBarAutoHideInput·자동 숨김 틱)가 모두 전이 시점뿐이라 포인터
    /// 이동마다 돌지 않는다 — 판정 비용(포커스 조회 1회 + 상한 있는 조상 순회)은 여기 얹어도 싸다.
    /// 조건(A234 배치 2에서 확장) = 고아일 때만: 고아 = 포커스 null ∨ 비가시(자신·조상 Collapsed)
    /// ∨ (RootLayout 자손 아님 ∧ 열린 팝업 안도 아님). 마지막 갈래가 배치 2의 확장분이다 —
    /// 실기기 계측(v0.239.0 스크린샷: inRoot=N·popup=N·vis=Y·PREVIEW 정지)으로 "클릭 후 포커스가
    /// 살아 있으면서 산 트리에서 분리된 템플릿 부품(이름 없는 ScrollViewer)에 남고, 그 조상
    /// 사슬이 RootLayout에 닿지 않아 셸 라우팅 키(터널링·버블 모두)가 아예 미발화"가 확정됐는데,
    /// 종전 판정(null·Collapsed 두 가지)은 이 상태를 생존 = 무개입으로 오판했다(GUARD=skip(alive)
    /// — A209·A212·A226 3연속 수리 실패의 진짜 이유). 살아 있는 정상 포커스(문서 에디터·이름변경
    /// 편집 상자·설정 콤보·열린 패널 리스트 = 전부 RootLayout 자손이며 가시)는 여전히 무개입이고,
    /// **열린 팝업 안(시작 메뉴 플라이아웃·콤보 드롭다운·대화상자)은 사유 불문 무개입**이 최우선
    /// 안전선이다 — 팝업 포커스를 뺏으면 열린 메뉴가 클릭 한 번에 닫히는 회귀가 난다(배치 1이
    /// 확장을 미룬 유일한 이유였고, popup 축이 실기기 계측으로 동작 실증돼 이제 안전하게 넣는다.
    /// 팝업 목록 취득 실패 시에도 InPopup=true 보수 처리 = 무개입 — GetShellFocusState 주석 참고).
    /// 판정 산출은 <see cref="GetShellFocusState"/> 하나를 계측(UpdateDiagStrip)과 공유한다 —
    /// 판정과 계측이 어긋나면 다음 회차 진단이 무의미해진다(배치 2 설계 핵심). 열린 패널 안
    /// 포커스 무개입 규칙은 A135 블록과 동일하고, 같은 전이에서 A135 블록과 겹쳐 돌아도 조건이
    /// 서로 배타적 표면이라 무해하다(둘 다 같은 대상 재포커스 관용구 — S4 종료
    /// ExitOpenFileBrowsing 말미와 동일).
    /// A234 배치 1의 2단 복구는 그대로다 — A209가 사양으로 뒀던 "실패(Content가 Control 아님·반환
    /// false) 무시" 한 줄 침묵 폴백 제거: 1단(모듈 뷰)이 없거나 Focus가 false면 2단
    /// (ShellFocusAnchor — RootLayout 안 포커스 전용 앵커, XAML 주석 참고)으로 폴백한다.
    /// 무한 복구 없음: 앵커가 포커스를 쥔 상태는 RootLayout 자손 + Visible이라 다음 판정이
    /// skip(alive)로 끝나고, 모듈 전환·전체화면 전이 중 "옛 뷰가 트리에서 빠지는 찰나"의 발동은
    /// ModuleHost.Content가 이미 새 뷰로 교체된 뒤라(ShowModule이 교체 후 SetContentState 순)
    /// 새 모듈 뷰 재포커스 = 의도된 동작이다(A201 Loaded 자기 포커스와 같은 방향 — Focus가
    /// false면 앵커로 안전). 호출 지점은 3곳 — UpdateShellChrome 말미(전이 시점) + 클릭 한 틱 뒤
    /// (OnRootPointerFocusGuard) + **500ms 주기 감시(_focusWatchTimer — A234 배치 3)**. 배치 3의
    /// 계기: v0.240.0 실기기에서 판정은 옳은데(RECOV=0·GUARD=skip(alive)·스트립 inRoot=N 병존)
    /// 두 호출 시점 모두 detach **이전**이라 발동 기회가 없었다 — 판정 로직은 무변경, 부르는
    /// 시점만 늘렸다(고아가 지속되면 매 틱 재시도 — 복구가 성공하면 다음 틱은 skip(alive)로
    /// 자연 종료). 실행 결과는 진단 오버레이의 GUARD 값(skip 2갈래 구분 +
    /// orphan(사유))으로, 복구 실행 누적은 RECOV로 남는다(오버레이 꺼짐 = 기록 생략 — 복구 동작
    /// 자체는 오버레이와 무관하게 항상 돈다). 직전 판정 결과(_focusWasOrphan — DET 에지 축)의
    /// 갱신도 이 함수 안에서만 한다: 세 호출 경로가 전부 여기를 지나므로 호출부마다 흩어
    /// 갱신하면 값이 뒤엉킨다(배치 3 규칙).
    /// </summary>
    private void RecoverChromeFocusOrphan()
    {
        if (RootLayout.XamlRoot is not { } xr)
        {
            if (_diagOn) _diagGuardLast = "no-xamlroot";
            return; // 로드 전 — 판정 불가면 무동작(_focusWasOrphan도 미갱신: 판정 자체가 없었다)
        }
        var state = GetShellFocusState(xr); // 판정 산출 = 계측(UpdateDiagStrip)과 공유하는 단일 헬퍼
        string reason; // 고아 사유 — GUARD의 orphan(사유) 표기(다음 회차 스크린샷 판독용)
        if (state.Focused is null)
        {
            reason = "null"; // 포커스 자체가 없다 — 라우팅 키 미발화 갈래 ⓐ
        }
        else if (state.InPopup)
        {
            // 최우선 안전선: 열린 팝업(시작 메뉴 플라이아웃·콤보 드롭다운·대화상자) 안 포커스는
            // 사유 불문(비가시·비RootLayout이어도) 무개입 — 뺏으면 열린 메뉴가 클릭 한 번에
            // 닫힌다. 팝업 목록 취득 실패의 보수적 InPopup=true도 여기로 온다(모르면 개입 금지).
            if (_diagOn) _diagGuardLast = "skip(popup)";
            _focusWasOrphan = false; // 판정 결과 갱신은 이 함수 안에서만(DET 에지 축 — 배치 3)
            return;
        }
        else if (!state.Visible)
        {
            reason = "hidden"; // 자신·조상 Collapsed — A209 원래 갈래(바 붕괴 축)
        }
        else if (!state.InRoot)
        {
            reason = "detach"; // 살아 있되 RootLayout 밖(산 트리에서 분리) — 배치 2가 잡는 그 갈래
        }
        else
        {
            if (_diagOn) _diagGuardLast = "skip(alive)";
            _focusWasOrphan = false; // 판정 결과 갱신은 이 함수 안에서만(DET 에지 축 — 배치 3)
            return; // 메인 트리 안 생존 포커스 — 무개입(정상 상태에서 포커스를 뺏지 않는다)
        }
        // A234 배치 3 계측(DET): "직전 판정 = 고아 아님 → 이번 판정 = 고아"의 상승 에지에서만,
        // 마지막 눌림(OnRootPointerFocusGuard 기록) 이후 경과 ms를 사유와 함께 남긴다 — 클릭 몇 ms
        // 뒤에 트리 분리가 일어나는지가 다음 회차("누가 트리를 갈아엎는가" 특정)의 단서다.
        // 진단 오버레이가 꺼져 있으면 계산·기록하지 않는다(핫 패스 비용 0 원칙 — 배치 1·2와 동일).
        // 단 **복구 자체는 오버레이와 무관하게 항상 돈다**(아래) — 게이트되는 건 계측(DET·RECOV·
        // GUARD 문자열)뿐이지 수리가 오버레이에 묶인다는 뜻이 아니다(혼동 주의 — 배치 3 결정).
        // 눌림 미관측(_diagLastPressUtc 초기값)이면 경과의 기준점이 없어 표시를 DET=- 그대로 둔다.
        if (_diagOn && !_focusWasOrphan && _diagLastPressUtc != DateTime.MinValue)
        {
            var ms = (long)(DateTime.UtcNow - _diagLastPressUtc).TotalMilliseconds;
            _diagDetachLast = "+" + ms + "ms(" + reason + ")";
        }
        _focusWasOrphan = true; // 에지 축 갱신도 이 함수 한 곳뿐(호출부 산개 금지 — 배치 3 규칙)
        // 복구 실행 누적(RECOV) — 성공 여부와 무관하게 "판정이 발동해 복구가 돌았다"가 신호다.
        if (_diagOn) _diagRecovCount++;
        // 1단: 종전과 같은 모듈 뷰(중앙 콘텐츠). 성공하면 여기서 끝.
        var host = ModuleHost.Content as Control;
        if (host is not null && host.Focus(FocusState.Programmatic))
        {
            if (_diagOn) _diagGuardLast = "orphan(" + reason + ")>host=" + host.GetType().Name + " ok";
            return;
        }
        // 2단: 포커스 전용 앵커 — 1단이 없거나(빈 셸 등) false를 돌려줘도 포커스를 메인 트리에
        // 남긴다. GUARD 문자열 조립은 진단 켜짐일 때만(복구는 전이 시점에만 돌아 비용 무해).
        // 1단 실패 갈래만 host= 접두어를 생략한다 — 사유가 붙으면서 40자 상한을 넘는 유일한
        // 조합이라서다(최장 뷰 타입 15자 기준 "orphan(detach)>VideoPlayerView fail>a:ok" = 40자).
        var anchored = ShellFocusAnchor.Focus(FocusState.Programmatic);
        if (!_diagOn) return;
        _diagGuardLast = host is null
            ? "orphan(" + reason + ")>anchor " + (anchored ? "ok" : "fail")
            : "orphan(" + reason + ")>" + host.GetType().Name + (anchored ? " fail>a:ok" : " fail>a:no");
    }

    /// <summary>셸 포커스 상태(A234 배치 2) — <see cref="GetShellFocusState"/>의 산출 묶음.
    /// Focused = 포커스 요소(null 가능), InRoot = RootLayout 자손인가, InPopup = 열린 팝업 안인가
    /// (목록 취득 실패 시 보수적 true), Visible = IsVisibleInTree, PopupUnknown = 팝업 목록 취득
    /// 실패(계측 표시 ? 전용 — InPopup의 보수적 true와 함께 켜진다).
    /// private readonly record struct = SettingsView.AssociationOutcome 선례 형태.</summary>
    private readonly record struct ShellFocusState(
        DependencyObject? Focused, bool InRoot, bool InPopup, bool Visible, bool PopupUnknown);

    /// <summary>
    /// 셸 포커스 상태의 단일 산출 지점(A234 배치 2) — 판정(RecoverChromeFocusOrphan)과 계측
    /// (UpdateDiagStrip)이 반드시 이 헬퍼 하나를 쓴다: 같은 조상 순회가 두 곳에 중복돼 서로
    /// 어긋나면 다음 회차 진단이 무의미해진다(배치 1이 UpdateDiagStrip 안에 짜 뒀던 순회를
    /// 그대로 들어낸 것). 한 번의 조상 순회(상한 UiScaleAncestorDepth)로 InRoot(RootLayout
    /// 도달)와 InPopup(열린 팝업의 Child 통과)을 함께 판정한다 — RootLayout에 닿았다면 메인
    /// 트리이므로 팝업 미검출이 맞다. Visible은 IsVisibleInTree(배치 1과 같은 산출 유지 —
    /// RootLayout 위 조상까지 보는 전체 순회라 여기 순회와 축이 달라 합치지 않는다).
    /// 포커스가 null이면 나머지 축은 전부 false다(호출부 규칙: null = 무조건 고아).
    /// GetOpenPopupsForXamlRoot 취득 실패(try/catch — 배치 1에서 컴파일·런타임 모두 실증)는
    /// **판정용 InPopup=true 보수 처리**다: 팝업 여부를 모를 땐 개입하지 않는 쪽이 안전하다
    /// (열린 시작 메뉴 포커스 탈취 사고 방지). 단 계측 표시는 종전대로 ?가 뜬다(PopupUnknown)
    /// — **판정(InPopup=true)과 표시(?)가 다른 유일한 지점이다**(A234 배치 2 구현 결정).
    /// </summary>
    private ShellFocusState GetShellFocusState(XamlRoot xr)
    {
        if (FocusManager.GetFocusedElement(xr) is not DependencyObject focused)
            return new ShellFocusState(null, false, false, false, false);
        // 열린 팝업 목록: 취득이 실패해도 나머지 축(inRoot·vis)은 산다 — InPopup만 보수적 true.
        IReadOnlyList<Microsoft.UI.Xaml.Controls.Primitives.Popup>? popups = null;
        var popupUnknown = false;
        try
        {
            popups = VisualTreeHelper.GetOpenPopupsForXamlRoot(xr);
        }
        catch
        {
            popupUnknown = true;
        }
        var inRoot = false;
        var inPopup = popupUnknown; // 취득 실패 = 팝업일지 모른다 → 무개입 쪽으로(절대 뒤집지 말 것)
        var node = (DependencyObject?)focused;
        for (var depth = 0; node is not null && depth < UiScaleAncestorDepth; depth++)
        {
            if (ReferenceEquals(node, RootLayout))
            {
                inRoot = true;
                break;
            }
            if (popups is not null)
            {
                foreach (var p in popups)
                {
                    if (p.Child is { } child && ReferenceEquals(node, child)) inPopup = true;
                }
            }
            node = VisualTreeHelper.GetParent(node);
        }
        return new ShellFocusState(focused, inRoot, inPopup, IsVisibleInTree(focused), popupUnknown);
    }

    /// <summary>요소가 화면에 있는가 — 자신·조상 어느 층도 Collapsed가 아니면 참(A209 고아 판정의
    /// 가시 축 — A234 배치 2부터 GetShellFocusState 경유로만 쓰인다).
    /// 순회 상한은 UiScaleAncestorDepth(HotkeySupport.MaxAncestorDepth와 같은 방어 값) 재사용.
    /// 팝업 트리(플라이아웃·대화상자)는 루트까지 전부 Visible이라 자연히 참이고,
    /// 텍스트 요소 등 UIElement가 아닌 층은 판정 없이 지나간다(IsWithin과 같은 순회 관용구).</summary>
    private static bool IsVisibleInTree(DependencyObject element)
    {
        var node = element;
        for (var depth = 0; node is not null && depth < UiScaleAncestorDepth; depth++)
        {
            if (node is UIElement { Visibility: Visibility.Collapsed }) return false;
            node = VisualTreeHelper.GetParent(node);
        }
        return true;
    }

    // ---------- 셸 포커스 주기 감시 (A234 배치 3) ----------
    // v0.240.0 실기기 회수: 판정 확장(배치 2)은 옳았는데 "보는 시점"이 전부 detach 이전이었다.
    // 클릭 한 틱 뒤(OnRootPointerFocusGuard)에는 포커스가 아직 멀쩡했고(GUARD=skip(alive)),
    // 그 뒤 어느 시점에 그 요소가 산 트리에서 떨어져 나갔는데(스트립 inRoot=N) 판정을 다시
    // 부르는 지점이 없어 영원히 복구되지 않았다(RECOV=0 — GUARD와 스트립의 어긋남은 모순이
    // 아니라 시점 차였다). 그래서 detach 순간을 특정해 쫓는 대신 주기 감시로 뒤늦게라도 반드시
    // 잡는다. detach는 F11/F12만이 아니라 셸 라우팅 키 전부(Esc·Enter·Alt+Enter·Alt+`·문자
    // 핫키)를 죽이므로, 키별 우회 수신이 아니라 포커스를 트리 안으로 되돌리는 것이 맞는 수리다.
    // 판정·복구는 기존 RecoverChromeFocusOrphan 그대로(무변경 — 부르는 시점만 늘린다).
    // **진단 오버레이와 무관하게 항상 돈다**: 오버레이는 기본 꺼짐이라 거기 묶으면 수리가 안
    // 된다 — _diagTimer(계측 표시용)와 존재 이유가 달라 별도 타이머다. 활성 창에서만 돈다
    // (생성자 Activated 배선 — 비활성 창의 포커스를 건드리면 사고. 정지 = 비활성 전환 + Closed).

    /// <summary>포커스 주기 감시 간격(ms) — 500 고정(A234 배치 3 구현 결정). 근거: 사용자가
    /// "클릭하고 다음 셸 키를 누르기"까지의 간격에 최소 한 틱이 들어가는 값(체감 = 클릭 후
    /// 늦어도 0.5초면 셸 키 복활)이면서, 틱 비용(포커스 조회 1 + 상한 있는 조상 순회(포함·가시
    /// 두 축) + 열린 팝업 목록 조회 1)이 2Hz로는 _barAutoHideTimer 등 기존 셸 타이머와 같은
    /// 층이라 부담이 없다.</summary>
    private const int FocusWatchPollMs = 500;

    /// <summary>포커스 주기 감시 타이머 — 활성 창에서 항상 돈다(진단 오버레이와 무관 — 수리용).
    /// 지연 생성(_diagTimer 관용구), 시작·정지는 생성자 Activated 배선 + Closed 배선이 전부다.</summary>
    private DispatcherTimer? _focusWatchTimer;

    /// <summary>직전 판정이 고아였는가 — DET(트리 분리 시점 계측)의 상승 에지 축. 갱신은
    /// RecoverChromeFocusOrphan 안에서만 한다: 전이·클릭·주기 세 호출 경로가 전부 그 함수를
    /// 지나므로, 호출부마다 흩어 갱신하면 값이 뒤엉킨다(A234 배치 3 규칙). 판정 불가 갈래
    /// (no-xamlroot)는 미갱신 — 이 필드는 "마지막으로 실제 내려진 판정"을 담는다.</summary>
    private bool _focusWasOrphan;

    private DispatcherTimer MakeFocusWatchTimer()
    {
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(FocusWatchPollMs) };
        // 반복 폴링 — Tick에서 멈추지 않는다(MakeDiagTimer와 같은 형태).
        // 정지 책임 = 생성자 Activated의 비활성 분기 + Closed 배선 두 곳뿐이다.
        // 재진입 없음: 복구의 Focus()는 이 틱을 다시 태우지 않는다(포인터·타이머 이벤트가
        // 아니고, GotFocus/GettingFocus 구독은 저장소 전체 0건 — grep 확인. 배치 3 함정 감사 ④).
        timer.Tick += (_, _) => RecoverChromeFocusOrphan();
        return timer;
    }

    // ---------- 셸 키 진단 오버레이 (A234 배치 1 계측 · 배치 2 판정 통합 · 배치 3 DET) ----------
    // F11/F12 불통 수리 3연속 실패(A209·A212·A226) 뒤의 계측 선행: 사용자에게 릴리스 1회 +
    // "클릭 후 F11 한 번 + 스크린샷 1장"만 요구해서 ⓐ 포커스 null(라우팅 미발화) /
    // ⓑ 포커스 생존이되 RootLayout 밖(팝업 트리 등) / ⓒ 키 도달하되 게이트 사망 중 어느
    // 갈래인지 실측으로 가른다. → 계측 회수(2026-08-27)로 ⓑ 확정 — 배치 2가 고아 판정을
    // 확장했고(RecoverChromeFocusOrphan 주석), 발동 여부는 RECOV 누적과 GUARD의 orphan(사유)로
    // 다음 스크린샷 한 장에서 읽힌다. → 배치 3: 수리 본체는 주기 감시(위 절 — 오버레이와 무관)로
    // 갔고, 여기는 detach가 클릭 후 몇 ms 뒤에 오는지의 계측(DET)을 얹는다. 카운터·GUARD·RECOV·
    // DET 입력은 각 핸들러가 값만 기록하고
    // (_diagOn 앞단 차단 = 꺼짐이면 비용 0), 문자열 조립·화면 갱신은 여기 폴링 한 곳이 전담한다.

    /// <summary>진단 스트립 폴링 주기(ms) — 포커스·게이트 값은 변경 이벤트가 없어 폴링이 유일한
    /// 취득법이다. 250 = 눈으로 따라 읽히면서 부담 없는 4Hz(A234 구현 결정, 고정값).</summary>
    private const int DiagPollMs = 250;

    /// <summary>진단 오버레이 켜짐(diag.shellKeyOverlay 설정) — 표시 여부이자 핫 패스(키·복구
    /// 핸들러)의 계측 기록을 앞단에서 차단하는 게이트다. 갱신은 ApplyShellDiagnostics 한 곳.</summary>
    private bool _diagOn;

    /// <summary>진단 폴링 타이머 — 오버레이가 켜져 있을 때만 돈다(끄면 Stop, 창 닫힘도 Stop —
    /// 생성자 Closed 배선). _barAutoHideTimer와 같은 지연 생성 관용구.</summary>
    private DispatcherTimer? _diagTimer;

    private int _diagPreviewCount;                            // OnRootPreviewKeyDown이 F11/F12로 발화한 누적
    private int _diagBubbleCount;                             // OnRootKeyDown이 아무 키로든 발화한 누적
    private string _diagGuardLast = "-";                      // RecoverChromeFocusOrphan 마지막 실행 결과
    private int _diagRecovCount;                              // 복구가 실제 실행된 누적(RECOV — 배치 2 발동 증거)
    private DateTime _diagLastPressUtc = DateTime.MinValue;   // 마지막 눌림 시각(DET 기준점 — MinValue = 미관측)
    private string _diagDetachLast = "-";                     // 마지막 고아 상승 에지의 +ms(사유) (DET — "-" = 미관측)

    /// <summary>
    /// 설정 토글 반영(생성자 배선: 초기 1회 + ShellDiagnostics.Changed) — 다른 창의 설정 화면
    /// (다른 UI 스레드)에서 발화할 수 있어 ApplyUiScale과 같은 자가 마샬링을 거친다.
    /// 켜면 스트립 표시 + 폴링 시작(첫 프레임은 즉시), 끄면 폴링 정지 + 스트립 숨김.
    /// </summary>
    private void ApplyShellDiagnostics()
    {
        if (DispatcherQueue is { } dq && !dq.HasThreadAccess)
        {
            dq.TryEnqueue(ApplyShellDiagnostics);
            return;
        }
        _diagOn = _settings.Get(ShellDiagnostics.SettingKey, false);
        if (_diagOn)
        {
            DiagStrip.Visibility = Visibility.Visible;
            _diagTimer ??= MakeDiagTimer();
            _diagTimer.Stop(); // DispatcherTimer 되감기 관용구(Stop 후 Start — ArmBarAutoHide와 동일)
            _diagTimer.Start();
            UpdateDiagStrip(); // 폴링 한 주기를 기다리지 않고 즉시 첫 프레임
        }
        else
        {
            _diagTimer?.Stop(); // 꺼짐 = 폴링 정지(누수 금지). 창 닫힘 정지는 생성자 Closed 배선.
            DiagStrip.Visibility = Visibility.Collapsed;
        }
    }

    private DispatcherTimer MakeDiagTimer()
    {
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(DiagPollMs) };
        // 반복 폴링 — Tick에서 멈추지 않는다(원샷 되감기인 MakeBarAutoHideTimer류와 다른 점).
        // 정지 책임 = ApplyShellDiagnostics의 끄기 분기 + 생성자 Closed 배선 두 곳뿐이다.
        timer.Tick += (_, _) => UpdateDiagStrip();
        return timer;
    }

    /// <summary>
    /// 진단 스트립 본문 갱신(폴링 틱 + 켜는 순간 1회). 1줄 = 포커스 상태: FOCUS(타입#이름 또는
    /// null), inRoot/popup/vis — 표기 형식은 배치 1 그대로(사용자가 읽는 법을 익혔다), 값 산출만
    /// A234 배치 2부터 GetShellFocusState 경유다(판정과 같은 눈 — 단일 헬퍼 공유. popup은 목록
    /// 취득 실패 시 ? 표시, 판정 쪽만 보수적 true — 헬퍼 주석의 "판정과 표시가 다른 유일한
    /// 지점"). 2줄 = PREVIEW(터널링 F11/F12 발화 누적 — 클릭 후 이 값이 멈추면 갈래 ⓐ/ⓑ),
    /// BUBBLE(버블 KeyDown 발화 누적 — 아무 키), RECOV(복구 실제 실행 누적 — 배치 2 판정이
    /// 발동했는가의 회귀 감시), DET(마지막 고아 상승 에지의 "마지막 눌림 이후 경과 ms(사유)" —
    /// 배치 3: detach가 클릭 몇 ms 뒤에 오는지가 다음 회차의 단서. 미관측 = -), GUARD(복구
    /// 마지막 결과), CTX(HasPanelContext), S4(IsOpenFileBrowsing). 배치 3에서 last=(마지막
    /// F11/F12 키 이름)는 DET 자리를 내려고 뺐다 — 1줄 형식은 배치 1 그대로 불변(사용자가
    /// 읽는 법을 익혔다). 문자열 조립은 여기(4Hz)에서만 한다.
    /// </summary>
    private void UpdateDiagStrip()
    {
        if (!_diagOn) return;
        string line1;
        if (RootLayout.XamlRoot is not { } xr)
        {
            line1 = "FOCUS <no-xamlroot>"; // 로드 전 잠깐 — 다음 폴링 틱이 곧 채운다
        }
        else
        {
            var state = GetShellFocusState(xr); // 계측 산출 = 판정과 공유(단일 헬퍼 — 배치 2)
            if (state.Focused is not { } focused)
            {
                line1 = "FOCUS <null>   inRoot=N  popup=N  vis=N"; // 갈래 ⓐ의 모양
            }
            else
            {
                var name = (focused as FrameworkElement)?.Name;
                if (string.IsNullOrEmpty(name)) name = "-";
                var inRoot = state.InRoot ? "Y" : "N";
                var popup = state.PopupUnknown ? "?" : state.InPopup ? "Y" : "N";
                var vis = state.Visible ? "Y" : "N";
                line1 = $"FOCUS {focused.GetType().Name}#{name}   inRoot={inRoot}  popup={popup}  vis={vis}";
            }
        }
        DiagText.Text = line1
            + $"\nPREVIEW={_diagPreviewCount}  BUBBLE={_diagBubbleCount}  RECOV={_diagRecovCount}"
            + $"  DET={_diagDetachLast}  GUARD={_diagGuardLast}  CTX={(HasPanelContext ? "Y" : "N")}  S4={(IsOpenFileBrowsing ? "Y" : "N")}";
    }

    /// <summary>하단 바 우측 "Full screen" 버튼 = Enter/Alt+Enter와 같은 전체화면 토글(A186 —
    /// 영상 자동 숨김 축에서는 전체화면에서도 바가 보일 수 있어 직행이 아니라 토글이어야 한다).</summary>
    private void OnFullScreenClick(object sender, RoutedEventArgs e) => ToggleFullScreen();

    // ---------- 영상 하단 바 자동 숨김 (A186 ②) ----------
    // 신호원 = IPlaybackStateSource(영상 뷰·All Readable 중계 — ShowModule 배선), 판단·타이머 =
    // 셸. 규칙: 재생 표면 + 재생 중 + 무입력 3초 = 숨김 / 재표시 = 포인터 이동·터치(클릭)·
    // 하단 가장자리 근접(틱 시점 유예)·일시정지·정지. 전체화면에서도 동일(플레이어 UX) —
    // 반대로 말하면 영상 재생 표면에서는 전체화면에서도 입력이 오면 바가 나타난다.
    // 커서 숨김은 하지 않는다(등재 후보 — A186 구현 시 결정).

    /// <summary>자동 숨김까지의 무입력 시간(ms) — 사양 상수 한 곳(A186, 제안값 3s 채택).</summary>
    private const int BarAutoHideIdleMs = 3000;

    private DispatcherTimer? _barAutoHideTimer;
    private bool _barAutoHidden; // 자동 숨김으로 바가 내려가 있는지 — BarVisible의 한 축

    /// <summary>마지막 포인터 y(CenterArea 기준) — 타이머 틱의 하단 근접 판정 입력.
    /// 이동이 없으면 틱 시점에 위치를 얻을 길이 없어 이동·눌림 핸들러가 기록해 둔다.</summary>
    private double _lastPointerY = double.NaN;

    /// <summary>현재 뷰의 재생 상태 계약 — 영상 뷰(직접)·All Readable(자식 중계)만 구현한다.</summary>
    private IPlaybackStateSource? PlaybackView => ModuleHost.Content as IPlaybackStateSource;

    /// <summary>영상 재생 표면이 전면인가(A186) — 자동 숨김 축이 바 가시성을 지배하는 조건.
    /// 파일 열림 + 재생 표면(All Readable은 영상 자식일 때만 참 — HasPlaybackSurface).</summary>
    private bool VideoBarContext => _currentFilePath is not null
        && PlaybackView is { HasPlaybackSurface: true };

    /// <summary>
    /// 하단 바 실표시(A186 — UpdateShellChrome·EdgeButtonsBottomInset 공용 판정):
    /// 영상 재생 표면이면 자동 숨김 축(재생 중 무입력이면 숨김, 일시정지·정지면 상시 표시 —
    /// 전체화면에서도 동일), 아니면 모드 축(창 모드에서만 표시 — A151 승계).
    /// </summary>
    private bool BarVisible => VideoBarContext ? !_barAutoHidden : _viewMode == ShellViewMode.Windowed;

    /// <summary>포인터가 하단 가장자리 띠(바 44 + 근접 여유 EdgeProximity) 안에 있는가 —
    /// 시크바를 노리고 내려온 손 밑에서 바가 꺼지지 않게 틱 시점에 유예한다(경계 버튼 근접
    /// 판정 관용구 재사용 — 같은 EdgeProximity 상수).</summary>
    private bool PointerNearBottomEdge => !double.IsNaN(_lastPointerY)
        && _lastPointerY >= CenterArea.ActualHeight - BottomBarHeight - EdgeProximity;

    /// <summary>
    /// 자동 숨김 리셋: 타이머 정지 + 숨겨져 있었으면 바 복원. 호출 = 콘텐츠·모듈 전환
    /// (SetContentState·OnContentOpened — 경합 방지)과 재생 상태 전이(OnPlaybackStateChanged).
    /// </summary>
    private void ResetBarAutoHide()
    {
        _barAutoHideTimer?.Stop();
        if (!_barAutoHidden) return;
        _barAutoHidden = false;
        UpdateShellChrome();
    }

    /// <summary>재생 중이면 무입력 카운트를 (재)시작한다 — 아니면 무동작(타이머는 정지 상태 유지).</summary>
    private void ArmBarAutoHide()
    {
        if (!VideoBarContext || PlaybackView is not { IsPlaying: true }) return;
        _barAutoHideTimer ??= MakeBarAutoHideTimer();
        _barAutoHideTimer.Stop(); // DispatcherTimer 되감기 관용구(Stop 후 Start)
        _barAutoHideTimer.Start();
    }

    /// <summary>모드 전환·외부 프레젠터 변화의 재평가: 표시 상태로 되돌리고 재생 중이면 재대기.
    /// UpdateShellChrome은 호출부가 곧 잇는다(중복 갱신 방지 — SetViewMode·AppWindow.Changed).</summary>
    private void ReevaluateBarAutoHide()
    {
        _barAutoHideTimer?.Stop();
        _barAutoHidden = false;
        ArmBarAutoHide();
    }

    /// <summary>재생 상태 전이(재생/일시정지/정지 — ShowModule 배선의 디스패치 종착점):
    /// 어느 쪽이든 일단 바를 되살리고, 재생 중이면 카운트를 다시 연다.</summary>
    private void OnPlaybackStateChanged()
    {
        ResetBarAutoHide();
        ArmBarAutoHide();
    }

    /// <summary>포인터 이동·눌림(터치 탭 포함) = "입력" — 숨겨져 있으면 재표시하고 카운트를 되감는다.
    /// 키보드는 재표시 트리거가 아니다(A186 확정 목록 밖 — F11/F12 패널 조작은 바와 무관하게
    /// 정상 동작한다, A153).</summary>
    private void NotifyBarAutoHideInput()
    {
        if (!VideoBarContext) return;
        if (_barAutoHidden)
        {
            _barAutoHidden = false;
            UpdateShellChrome();
        }
        ArmBarAutoHide();
    }

    private DispatcherTimer MakeBarAutoHideTimer()
    {
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(BarAutoHideIdleMs) };
        timer.Tick += (_, _) =>
        {
            timer.Stop(); // 반복 타이머 — Tick에서 반드시 멈춘다(MakeS1FlashTimer 관용구)
            if (!VideoBarContext || PlaybackView is not { IsPlaying: true }) return; // 그새 정지·전환
            if (PointerNearBottomEdge)
            {
                timer.Start(); // 바 근처에 손이 있다 — 숨기지 않고 재대기
                return;
            }
            _barAutoHidden = true;
            UpdateShellChrome();
        };
        return timer;
    }

    /// <summary>클릭·터치 탭의 자동 숨김 입력 훅(A186) — '뒤로'(OnRootPointerBack)와 분리된
    /// 독립 핸들러다(생성자 handledEventsToo 구독).</summary>
    private void OnRootPointerInput(object sender, PointerRoutedEventArgs e)
    {
        _lastPointerY = e.GetCurrentPoint(CenterArea).Position.Y;
        NotifyBarAutoHideInput();
    }

    /// <summary>
    /// A226: 클릭 시점 포커스 고아 방어 — A209(전이 시점)·A212(썸네일 표면)가 못 지키던
    /// "임의 표면 클릭이 포커스를 null/비XAML로 떨궈 이후 셸 키가 전멸"하는 갈래의 일반 방어.
    /// 순수 관찰: 이벤트를 소비하지 않고(handledEventsToo 구독이라 소비된 눌림도 관찰),
    /// 판정·복구는 A209 관용구(RecoverChromeFocusOrphan) 재사용이라 **메인 트리 안 생존 포커스
    /// (에디터·이름변경 상자·콤보·열린 패널)와 열린 팝업 트리는 절대 건드리지 않는다**(고아일
    /// 때만 모듈 뷰 재포커스 — 그 판정·무개입 규칙을 통째로 상속. A234 배치 2: 살아 있어도 산
    /// 트리에서 분리된(detach) 포커스는 이제 고아다 — 이 클릭발 갈래가 실측으로 확정된 F11/F12
    /// 불통의 진짜 원인이었다). 판정을 TryEnqueue로 한 틱 미루는
    /// 이유: 눌림 버블 시점엔 이 클릭이 일으킬 포커스 이동(획득이든 고아 낙하든)이 아직 끝나지
    /// 않았을 수 있다 — 입력 패스가 끝난 뒤의 최종 상태를 봐야 오판(정상 이동 중 가로채기·고아
    /// 미검출)이 없다(TryEnqueue는 셸의 기존 UI 마샬 관용구 — OnContentOpened 등과 동일).
    /// 비용 = 클릭당 enqueue 1 + 포커스 조회 1(조상 순회는 상한 있음 — A209 산정 그대로).
    /// 재진입 없음: 복구의 Focus 호출은 이 핸들러를 다시 태우지 않는다(포인터 이벤트가 아니다).
    /// A212의 썸네일 FocusGrid(눌림 동기 정착)와 같은 클릭에서 겹쳐도 무해: 그쪽이 먼저 정착에
    /// 성공하면 이 판정은 "포커스 생존 = 무개입"으로 끝난다 — 둘 다 관찰·정착 계열이라 순서 무관.
    /// A234 배치 3: 여기가 DET(트리 분리 시점 계측)의 기준점 관측 지점이기도 하다 — 마지막 눌림
    /// 시각만 기록하고(_diagOn 앞단 차단 = 꺼짐이면 비용 0, 배치 1 카운터와 같은 관용구.
    /// DateTime.UtcNow = ExplorerPane 더블클릭 판정 등 저장소 선례), 경과 계산은
    /// RecoverChromeFocusOrphan의 상승 에지에서, 표시는 UpdateDiagStrip에서 한다.
    /// </summary>
    private void OnRootPointerFocusGuard(object sender, PointerRoutedEventArgs e)
    {
        if (_diagOn) _diagLastPressUtc = DateTime.UtcNow; // DET 기준점 — 계측 전용(수리와 무관)
        DispatcherQueue.TryEnqueue(() => RecoverChromeFocusOrphan()); // 람다 = 셸 TryEnqueue 관용구 그대로
    }

    // ---------- Esc (A151 전체화면 복귀 → A90 S4 복귀 → A202 콘텐츠 닫기) ----------

    /// <summary>
    /// Esc 분배(A151 — "한 단계 되돌리기" 일관. A186: 모드2 분기는 모드2 소멸로 자동 소진):
    /// ① 전체화면 = 복귀 스냅샷으로(창 모드 + 좌/우 패널 구성) ② S4 = 진입 전 상태로 복귀
    /// ③ **콘텐츠 열림(무제 포함) = 닫기(A202)** — '뒤로' ③과 같은 실행부(TryCloseContent)를
    /// 쓰되 defaultSidebars=true: 닫은 뒤 A109 기본 사이드바가 얹혀, 파일 인자 시작(A81
    /// 무사이드바)에서 Esc 하나로 아이콘 실행 기본 화면(좌/우 사이드바 + 센터 썸네일)이 된다
    /// (사용자 문면). 검사 순서는 A112 '뒤로' 선례 그대로 — 전체화면 → S4 → 콘텐츠 한 층씩.
    /// 원 기능 우선(먼저 소비하는 쪽이 이긴다 — e.Handled 존중): 이름변경 편집 취소
    /// (ExplorerRenameBox), 잘라내기 표시 해제(A94 — A202부터 지운 게 있을 때만 표면이 소비),
    /// 대화상자·플라이아웃(팝업 트리 — 셸에 키가 오지 않는다). 문서 더티 닫기는 ShowModule의
    /// ConfirmDiscardAsync 가드 경유(취소 = 무변경)다. 그 외(콘텐츠 없는 창 모드)는 종전대로
    /// 건드리지 않는다(무간섭 원칙 — 설정·미지원 안내·빈 셸·S1에서 Esc는 무동작).
    /// </summary>
    private void OnShellEscape(KeyRoutedEventArgs e)
    {
        if (e.KeyStatus.WasKeyDown || e.Handled) return;
        if (_viewMode == ShellViewMode.FullScreen)
        {
            e.Handled = true;
            RestoreFromFullScreen();
            return;
        }
        if (IsOpenFileBrowsing)
        {
            e.Handled = true;
            ExitOpenFileBrowsing(restore: true);
            return;
        }
        // A202: 말단 층 — 콘텐츠가 열려 있으면 닫고 기본 사이드바를 적용한다(위 요약 ③).
        if (TryCloseContent(defaultSidebars: true)) e.Handled = true;
    }

    // ---------- '뒤로' 입력 (A112 — 마우스 XButton1 · 키보드 Browser Back) ----------

    /// <summary>
    /// Browser Back 키(GoBack) 분배(A112) — 마우스 XButton1과 같은 한 줄(TryNavigateBack)로 묶인다.
    /// 오토리피트는 무시(홀드 연사로 여러 층을 한 번에 내려가지 않게), 이미 소비된 키도 존중한다.
    /// 텍스트 입력 포커스 가드는 호출부(OnRootKeyDown의 IsTextInputFocused 분기)가 겸한다 —
    /// 문서 에디터에 커서가 있는 동안 키 쪽 '뒤로'는 무동작이고, 마우스 XButton1은 그대로 동작한다.
    /// </summary>
    private void OnShellBack(KeyRoutedEventArgs e)
    {
        if (e.KeyStatus.WasKeyDown || e.Handled) return;
        if (TryNavigateBack()) e.Handled = true;
    }

    /// <summary>
    /// 마우스 뒤로가기 판정(A112): PointerUpdateKind가 XButton1Pressed인 눌림 전이만 태운다 —
    /// 다른 버튼이 이미 눌린 채 겹쳐 온 눌림·뗌은 전이 종류가 달라 걸리지 않는다(press만, 구현 결정).
    /// handledEventsToo 구독이라 리스트·뷰가 소비한 눌림도 받는다(전역 '뒤로'는 셸 몫 —
    /// 어디를 가리키고 눌러도 같아야 한다). XButton2(앞으로 가기)는 이번 범위 밖 — 매핑 없음.
    /// 동작했을 때만 소비한다(무동작이면 흘린다 — None 상태 셸 키들과 같은 무간섭 원칙).
    /// </summary>
    private void OnRootPointerBack(object sender, PointerRoutedEventArgs e)
    {
        if (e.GetCurrentPoint(RootLayout).Properties.PointerUpdateKind
            != Microsoft.UI.Input.PointerUpdateKind.XButton1Pressed) return;
        if (TryNavigateBack()) e.Handled = true;
    }

    /// <summary>
    /// '뒤로' 분배(A112 — XButton1·GoBack 공용): 한 번에 한 층씩 걷어낸다. 반환값 = 소비 여부.
    /// ① 표시 모드(A151 — A186: 모드2 층은 소멸) = Esc와 같은 한 단계 — 전체화면이면 복귀
    ///    스냅샷으로(RestoreFromFullScreen). 전체화면 검사가 S4보다 앞인 순서는 A90/A112 확정
    ///    그대로다("첫 층 = 전체화면 해제, 다음 층 = S4 복귀"). 하단 바 복원은 UpdateShellChrome
    ///    (모드 전이의 단일 크롬 지점)이 처리한다.
    /// ② S4('오픈 파일' 탐색) = 진입 전 상태로 복귀만 — Esc와 동일(같은 '뒤로' 의미론).
    /// ③ 콘텐츠 열림(S2·S3 부류 = _currentFilePath 있음) = 콘텐츠 닫기 → 그 모듈의 빈 상태(S1).
    ///    새 해체 경로를 만들지 않고 모듈 전환과 같은 ShowModule(빈 컨텍스트) 재사용이다:
    ///    미저장 가드(A37 — 취소하면 아무것도 안 바뀐다)·재생 정지·파일 핸들 해제(뷰 Unloaded —
    ///    A59 검증 경로. All Readable은 호스트 Unloaded가 DetachChild로 자식까지 정리)·
    ///    제목 복귀(A103 "KOTU")·트레이 유휴 1줄(A54)·하단 바·드라이브 줄 교체·
    ///    S1 썸네일 탐색기(마지막 폴더 = 방금 닫은 파일의 폴더, v0.55.0)가 전부 그 경로 몫이다.
    ///    defaultSidebars는 기본 false — A109의 사이드바 기본 재적용을 타지 않아
    ///    좌/우 열림·닫힘 상태가 닫기 직전 그대로 보존된다(A112 요구 — **Esc의 콘텐츠 닫기
    ///    (A202)는 반대로 true**: 두 입력의 문면이 달라 의도된 차이다. TryCloseContent 주석 참고).
    /// ④ 콘텐츠 없음(S1·빈 셸·설정·정보 모듈·미지원 안내) = 무동작, 소비도 안 한다
    ///    (정보 모듈은 A119부터 패널 컨텍스트지만 닫을 콘텐츠(파일)가 없는 점은 그대로다).
    /// </summary>
    private bool TryNavigateBack()
    {
        if (_viewMode == ShellViewMode.FullScreen)
        {
            RestoreFromFullScreen(); // Esc와 동일 — A151 파생(A186: 모드2 층 소멸)
            return true;
        }
        if (IsOpenFileBrowsing)
        {
            ExitOpenFileBrowsing(restore: true);
            return true;
        }
        // A189: 무제 문서도 닫을 콘텐츠다 — 경로는 없지만 ③과 같은 "콘텐츠 닫기 → S1" 층.
        // 미저장 가드는 종전대로 ShowModule 안의 ConfirmDiscardAsync가 담당한다(취소 = 무변경).
        // A202: 닫기 실행부는 Esc 말단 층과 공용(TryCloseContent) — '뒤로'는 defaultSidebars=false
        // (좌/우 상태를 닫기 직전 그대로 보존 — A112 명시 요구라 Esc의 true와 다르다).
        return TryCloseContent(defaultSidebars: false);
    }

    /// <summary>
    /// 콘텐츠 닫기 층의 단일 실행부(A112 '뒤로' ③ — A202에서 Esc 말단 층과 공용으로 추출):
    /// 콘텐츠(파일 또는 무제 문서 A189)가 열려 있으면 그 모듈의 빈 상태(S1)로 돌아간다 —
    /// 새 해체 경로 없이 모듈 전환과 같은 ShowModule(빈 컨텍스트) 재사용이다(미저장 가드 A37 ·
    /// 재생 정지·파일 핸들 해제(뷰 Unloaded)·제목 복귀·트레이·하단 바·드라이브 줄 교체가 전부
    /// 그 경로 몫 — 상세는 TryNavigateBack ③ 주석). 반환값 = 닫을 콘텐츠가 있었는가.
    /// defaultSidebars: '뒤로'(A112) = false(좌/우 열림·닫힘 상태 보존 — 명시 요구) /
    /// Esc(A202) = true(A109 기본 사이드바 재적용 — 파일 인자 시작(A81 무사이드바)에서 닫아도
    /// 아이콘 실행 기본 화면과 동일해지는 것이 사용자 문면의 합격선).
    /// </summary>
    private bool TryCloseContent(bool defaultSidebars)
    {
        if ((_currentFilePath is null && !_untitledContent) || _currentModule is not { } module)
            return false;
        ShowModule(module, OpenContext.Empty, Branding.AppName, defaultSidebars);
        return true;
    }

    /// <summary>포커스 요소가 주어진 루트의 비주얼 트리 안에 있는지 (A90 — S4 그리드 포커스 판정).</summary>
    private bool IsFocusWithin(UIElement? root)
        => root is not null && RootLayout.XamlRoot is { } xr
           && FocusManager.GetFocusedElement(xr) is DependencyObject focused
           && IsWithin(focused, root);

    // A176: 구 Sticky(홀드 → 반투명 피닝 승격 정규화)·OnRootPointerIntervened(포인터 개입 =
    // 홀드 취소 안전장치)는 홀드 판정 기계와 함께 철거 — 단타 토글에는 취소할 진행 상태가 없다.

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

    // A176: 구 MakePinTimer(2초 홀드 → 반투명 고정 승격)·ResetOverlayInput(홀드 취소·2연타
    // 리셋)·CancelHoldCore(홀드 세션 종료 — peek 스냅샷 정리 포함)는 A58 상태 머신과 함께 철거.

    /// <summary>
    /// 외부에서 좌/우 사이드바(불투명 도크 — A108 용어) 상태를 지정한다 — 시작 경로별 기본 표시
    /// 상태(A81: 파일 인자 없이 모듈로 연 창은 양쪽 사이드바, 부록 B 30번)용 공개 API.
    /// 부르는 곳은 둘: WindowManager의 창 생성 진입(A81)과 **모듈 실행·전환**(A109, v0.136.0 —
    /// ShowModule의 defaultSidebars). A109가 A81의 "창 생성 뒤에는 사용자가 바꾼 상태를 그대로
    /// 유지(재적용 없음)"를 **모듈 전환에 한해 대체**한다 — 파일 열기는 여전히 재적용하지 않고,
    /// 세션 간 저장도 없다(A55 미포함).
    /// true = OpaqueDocked, false = 닫힘.
    /// ⚠️ 이미 요청과 같은 상태면 <b>다시 그리지 않는다</b>(A109에서 추가한 가드):
    /// <see cref="ApplyOverlayStates"/>는 좌 리스트를 매번 Show → 폴더 재스캔까지 시키므로,
    /// A109의 재적용이 모듈 전환마다 같은 폴더를 두 번 훑는 낭비를 막는다
    /// (SetContentState가 방금 같은 상태로 그려 놓은 직후에 불리는 자리라 결과는 동일하다).
    /// A176: 구 "홀드 세션 정리" 조건·CancelHoldCore 호출은 상태 머신과 함께 철거.
    /// </summary>
    public void SetDockedState(bool listDocked, bool infoDocked)
    {
        var list = listDocked ? OverlayState.OpaqueDocked : OverlayState.Closed;
        var info = infoDocked ? OverlayState.OpaqueDocked : OverlayState.Closed;
        if (_listSide.State == list && _infoSide.State == info) return;
        _listSide.State = list;
        _infoSide.State = info;
        ApplyOverlayStates();
    }

    /// <summary>
    /// A119: 모듈 고유 좌/우 패널 호스트를 비운다 — 모듈 뷰 교체 지점 3곳(ShowModule·설정 진입·
    /// 미지원 안내)에서 ModuleBarHost.Content 교체와 같은 자리에서 부른다. 비우지 않으면 이전
    /// 모듈의 패널 콘텐츠가 셸 호스트(트리)에 남아 다음 모듈 위에 뜨고(A59급 회귀), 이전 뷰의
    /// 요소 수명도 샌다. 같은 모듈 재선택은 OpenModuleById의 no-op 가드로 여기 오지 않는다.
    /// </summary>
    private void ClearModulePanels()
    {
        LeftPanelHost.ClearContent();
        RightPanelHost.ClearContent();
    }

    /// <summary>
    /// 사이드바(불투명 도크)가 차지하는 전폭 대비 % — 전 상태 공통 25 (양쪽이면 3구획 25:50:25).
    /// A116(v0.135.0): 종전에는 "S1 = 25 / 콘텐츠 상태 = 30(A57의 3:4:3 유산)"의 상태별 2값이라,
    /// 파일을 열거나 S4('오픈 파일')로 들어가면 같은 3구획 화면이 소리 없이 30:40:30이 됐다 —
    /// 사용자에게 "리사이즈에 비율 재계산 안 됨"으로 관측된 실원인(폭 자체는 전부 star라 리사이즈
    /// 추종은 원래 성립). 도크 컬럼·패널 내부 분할(SetPanelPercent)·S4 스페이서·경계 버튼 x가
    /// 전부 이 상수 하나를 쓴다 — 비율을 바꿀 일이 있으면 여기 한 곳만 고칠 것.
    /// </summary>
    private const double SidebarPercent = 25.0;

    /// <summary>
    /// 상태 → 화면 반영의 단일 종착점 (A58 계보 — A176에서 단순화: 상태 축 = 닫힘/사이드바).
    /// 표시 여부·안내 문구·도크 컬럼을 한 곳에서 일괄 갱신한다. 패널 컨텍스트가 없으면
    /// (A196부터 빈 셸뿐) 상태와 무관하게 숨긴다 — 상태 자체는 남아 있어 다음 파일을 열면
    /// 같은 구성으로 되살아난다(기존 유지 규칙).
    /// A196: 미지원 안내·무제 문서도 컨텍스트다 — 좌 리스트(전역 마지막 폴더)·우 정보
    /// 플레이스홀더가 아래 fallback 축으로 뜬다(게이트 완화 등재문).
    /// A205: 설정 화면은 fallback 축(IsPanelFallbackView)에서 빠졌다 — 설정 진입 경로
    /// (ShowSettingsAsync → SetContentState)가 이 메서드를 부르므로, 진입 전 열려 있던
    /// 사이드바는 listShow/infoShow가 false로 떨어져 Hide로 수렴한다(상태 필드는 보존 —
    /// 설정을 나가면 직전 구성이 그대로 다시 그려진다).
    /// 파일 없이 연 파일 모듈(빈 모듈 상태)도 컨텍스트다(A81): 좌측 리스트는 모듈 시작 폴더,
    /// 우측 정보는 "No file open" 플레이스홀더를 보여준다.
    /// A119: 패널 제공 뷰(ISidePanelProvider — 정보 모듈)도 컨텍스트다 — 같은 좌/우 상태를 파일
    /// 패널 대신 모듈 콘텐츠 호스트(SidePanelHost)로 표시한다. 두 표면이 같은 화면에 동시에
    /// 뜨는 조합은 없다(패널 제공 뷰 = 파일 없음 → 파일 패널 조건이 거짓).
    /// A176: 구 SetState 일괄 푸시(모드/pinned/overSwapChain — A129 스왑체인 폴백 포함)는
    /// 반투명 축과 함께 철거 — 배경은 불투명 고정이고 안내는 각 표면의 표시 메서드가 낸다.
    /// </summary>
    private void ApplyOverlayStates()
    {
        var hasFile = _currentFilePath is not null;
        var emptyModule = IsEmptyFileModule; // 파일 없이 연 파일 모듈 — A81부터 패널 컨텍스트
        var panelView = PanelProviderView;   // A119: 모듈 고유 패널(정보 모듈) — 아래 호스트 절이 소비
        // A196: 무제 문서(A189)·미지원 안내도 파일 패널 표면을 쓴다 — 좌 리스트는
        // ShowListOverlay(무제 = 모듈 필터 / 폴백 화면 = 전체 파일 필터), 우 정보는 파일이 없어
        // 플레이스홀더다. 패널 제공 뷰(정보 모듈)와는 상호 배타(모듈 유무로 갈린다).
        // A205: 설정 화면은 IsPanelFallbackView가 이미 걸러낸다(산식 3곳 공용 입력).
        var fallback = _untitledContent || IsPanelFallbackView;
        var listShow = (hasFile || emptyModule || fallback) && _listSide.State == OverlayState.OpaqueDocked;
        var infoShow = (hasFile || emptyModule || fallback) && _infoSide.State == OverlayState.OpaqueDocked;

        if (listShow) ShowListOverlay();
        else ListOverlay.Hide();

        ApplyInfoOverlayContent(infoShow, hasFile); // A200 — 선택 축 포함 정보 절(선택 변경 경로와 공용)

        // A119: 패널 제공 뷰(정보 모듈) — 같은 좌/우 상태를 모듈 콘텐츠 호스트로 표시한다.
        // 콘텐츠(그래프·스펙)는 뷰가 생성·소유하고(같은 인스턴스 반복 반환) 셸은 얹기만 한다.
        if (panelView is not null && _listSide.State == OverlayState.OpaqueDocked)
            LeftPanelHost.ShowContent(panelView.GetLeftPanel() as UIElement);
        else
            LeftPanelHost.Hide();
        if (panelView is not null && _infoSide.State == OverlayState.OpaqueDocked)
            RightPanelHost.ShowContent(panelView.GetRightPanel() as UIElement);
        else
            RightPanelHost.Hide();

        // 사이드바(OpaqueDocked)는 실제 공간을 차지한다: 도크 컬럼을 키워
        // 메인(ModuleHost/ExplorerHost)을 반대쪽으로 축소한다.
        // 도크 폭 = 전 상태 공통 SidebarPercent(A116, v0.135.0 — 종전 "S1 25 / 콘텐츠 30"의
        // 상태별 2값을 폐지: 같은 3구획 화면이 파일 열기·S4 진입에서 소리 없이 30:40:30으로
        // 바뀌던 것이 A116 관측의 원인이었다). 패널 내부 별 분할(SetPanelPercent)을
        // 같은 %로 맞춰야 사이드바에서 도크 컬럼과 픽셀 단위로 정렬된다.
        // A119: 모듈 패널 호스트도 같은 % — 열림 판정은 지금 표면(파일 패널/호스트)을 따라간다
        // (LeftPanelIsOpen·RightPanelIsOpen — A176부터 "열림 = 사이드바"라 표시가 곧 도크다).
        var dockPercent = SidebarPercent;
        ListOverlay.SetPanelPercent(dockPercent);
        InfoOverlay.SetPanelPercent(dockPercent);
        LeftPanelHost.SetPanelPercent(dockPercent);
        RightPanelHost.SetPanelPercent(dockPercent);
        var left = LeftPanelIsOpen ? dockPercent / 10 : 0;
        var right = RightPanelIsOpen ? dockPercent / 10 : 0;
        LeftDockColumn.Width = new GridLength(left, GridUnitType.Star);
        RightDockColumn.Width = new GridLength(right, GridUnitType.Star);
        CenterColumn.Width = new GridLength(10 - left - right, GridUnitType.Star);

        // A60 3차 → A119 개정: 공간을 차지 중인 도크 수(0/1/2)를 모듈 뷰에 민다(Core
        // ISidebarAwareView — 정보 모듈의 센터 타일 그리드가 열 수 4/3/2를 이 신호로 정한다.
        // 구 bool "양쪽 열림"의 4/8 매핑은 폐지). 오버레이(반투명)는 메인 폭을 안 줄이므로 세지
        // 않는다(도크만 — A93 썸네일과 같은 해석). 이 메서드가 사이드바 상태 변경의 단일
        // 종착점이라(F11/F12·2연타·모드 전이 복귀·경계 버튼·모듈 진입 기본 전부 경유) 재푸시 누락이 없고,
        // 미구현 뷰(다른 모듈·설정)는 캐스트 실패로 no-op이다.
        var dockCount = (left > 0 ? 1 : 0) + (right > 0 ? 1 : 0);
        (ModuleHost.Content as ISidebarAwareView)?.SetSidebarsState(dockCount);

        // A93: S1 중앙은 항상 썸네일 뷰다 — A81의 "좌 도크가 불투명이면 중앙 탐색기 숨김"
        // (중복 목록 제거)을 대체한다. 중앙이 리스트가 아니라 타일이라 중복으로 보이지 않는다.
        // A213: 열 수 = 8 − 2×(열린 도크 수) → 둘 다 4 · 하나 6 · 없음 8 (구 A93의 4/8 2단을
        // 3단으로 — A168 H/W 센터 열 산식과 동형. 타일 크기는 SizeChanged에서 floor(실폭/열수)로
        // 따라온다). dockCount는 위 A119 블록이 이미 셌다(도크만 — 오버레이 불산입 해석 공유).
        if (emptyModule)
            _thumbnailExplorer?.SetColumns(8 - 2 * dockCount);

        // A90 S4: 중앙 썸네일 영역을 좌/우 패널 폭만큼 비켜 세운다. S4 호스트는 ColumnSpan=3
        // 전폭이라 도크 컬럼과 별개로 같은 %의 스페이서를 스스로 잡아야 패널과 픽셀 정렬된다
        // (SetPanelPercent 산식). A176: S4가 추가하는 패널도 사이드바(도크)라 스페이서와 도크
        // 컬럼이 같은 25%로 일치한다. A213: 열 수는 A93 규칙 준용(8 − 2×열린 패널 수 = 4/6/8) —
        // S4는 양쪽을 항상 채우므로 통상 4, 폴더 소실로 한쪽이 못 뜬 경우(IsOpen=false)에만
        // 6 또는 8로 넓어진다.
        if (_openFileBrowsing)
        {
            var s4Left = ListOverlay.IsOpen ? dockPercent : 0;
            var s4Right = InfoOverlay.IsOpen ? dockPercent : 0;
            S4LeftSpacer.Width = new GridLength(s4Left, GridUnitType.Star);
            S4RightSpacer.Width = new GridLength(s4Right, GridUnitType.Star);
            S4CenterColumn.Width = new GridLength(100 - s4Left - s4Right, GridUnitType.Star);
            _s4Explorer?.SetColumns(8 - 2 * ((s4Left > 0 ? 1 : 0) + (s4Right > 0 ? 1 : 0)));
        }

        UpdateEdgeButtons(); // A86 경계 버튼 — 경계 x·글리프가 상태를 따라온다 (S4에서는 숨김 — A90)

        // A135 2차(방어 수리): 표시 반영이 끝난 뒤의 포커스 후처리. 포커스가 방금 화면에서 내려간
        // 좌/우 패널(파일 패널·모듈 패널 호스트 4표면) 안에 남아 있으면 모듈 뷰(중앙 콘텐츠)로
        // 되돌린다. 닫힘 경로 전부(F11/F12 단타 닫기·경계 핀 버튼·컨텍스트 소멸 — A176에서
        // 홀드 해제·2연타·모드2 갈래는 기계 철거로 소멸)가 이 메서드를 지나므로 여기 한 곳이면
        // 충분하다(상태 변경 단일 종착점).
        // 가설(포커스 고아 — collapse된 요소에 포커스가 남으면 셸 KeyDown이 아예 안 올 수 있다)은
        // 실기기 확정 전이다: 이 수리는 방어적이며, 실기기에서 증상이 남으면 가설 기각·재조사
        // (docs/A135-audit.md §4-①·말미 "2차 방어 수리" 참고). 열려 있는 패널 안 포커스는 건드리지
        // 않는다(리스트 타이핑 탐색 유지). 재포커스 관용구·대상은 S4 종료(ExitOpenFileBrowsing 끝)와
        // 동일 — 실패(반환값 false·Content가 Control 아님)는 조용히 무시한다(포커스만 표류).
        var focusOrphaned =
            (!ListOverlay.IsOpen && IsFocusWithin(ListOverlay)) ||
            (!InfoOverlay.IsOpen && IsFocusWithin(InfoOverlay)) ||
            (!LeftPanelHost.IsOpen && IsFocusWithin(LeftPanelHost)) ||
            (!RightPanelHost.IsOpen && IsFocusWithin(RightPanelHost));
        if (focusOrphaned)
            (ModuleHost.Content as Control)?.Focus(FocusState.Programmatic);
    }

    /// <summary>
    /// 우측 정보 패널의 내용 절 (A200 — ApplyOverlayStates의 정보 분기를 추출해 선택 변경 경로와
    /// 공용화): 선택 축(_selectedBrowse)이 있으면 **선택 파일** 우선(ShowForSelection — 셸 조회기),
    /// 없으면 종전 규칙 그대로 — 열린 콘텐츠(provider) → 플레이스홀더(A81) → 숨김.
    /// </summary>
    private void ApplyInfoOverlayContent(bool infoShow, bool hasFile)
    {
        if (infoShow && _selectedBrowse is { } selected)
            InfoOverlay.ShowForSelection(selected.Path, selected.IsPlaceholder);
        else if (infoShow && hasFile)
            InfoOverlay.ShowFor(_currentFilePath!, ModuleHost.Content as IContentInfoProvider);
        else if (infoShow)
            InfoOverlay.ShowPlaceholder(); // 빈 모듈 상태 — 보여줄 파일 정보가 없다 (A81)
        else
            InfoOverlay.Hide();
    }

    /// <summary>
    /// A200: 선택 축 변경 시 우측 정보 패널만 다시 판정한다 — 전체 ApplyOverlayStates를 부르지
    /// 않는 이유는 그 경로가 좌 리스트를 매번 Show → 폴더 재스캔까지 시키기 때문(SetDockedState의
    /// A109 가드와 같은 근거). 패널이 닫혀 있으면 무동작 — 다음 열림 때 ApplyOverlayStates 경로가
    /// 선택 축을 판정한다(구현 결정). infoShow 산식은 ApplyOverlayStates와 동일해야 한다
    /// (A205: 두 산식의 폴백 입력이 IsPanelFallbackView 하나라 설정 제외가 자동으로 함께 간다).
    /// </summary>
    private void RefreshInfoOverlayForSelection()
    {
        if (!InfoOverlay.IsOpen) return;
        var hasFile = _currentFilePath is not null;
        var fallback = _untitledContent || IsPanelFallbackView;
        var infoShow = (hasFile || IsEmptyFileModule || fallback)
                       && _infoSide.State == OverlayState.OpaqueDocked;
        ApplyInfoOverlayContent(infoShow, hasFile);
    }

    /// <summary>
    /// A200: 중앙 썸네일 그리드의 선택 변경 — 지금 화면을 차지한 표면(S4 중이면 _s4Explorer,
    /// 빈 모듈이면 _thumbnailExplorer)의 선택만 채택한다(내려간 표면의 잔존 이벤트 무시).
    /// 파일 선택 = 선택 축 갱신, 폴더 선택·해제 = null(구현 결정 — 파일만). 같은 값이면 무동작 —
    /// 목록 재작성이 만드는 중복 발화로 정보 패널을 다시 그리지 않는다.
    /// 더블클릭 열기와의 겹발화는 열기 경로(SetContentState·OnContentOpened)의 선택 축 리셋이
    /// 결박한다 — 선택 → 열기 순서라 열기가 항상 마지막에 축을 걷는다.
    /// </summary>
    private void OnBrowseSelectionChanged()
    {
        var active = _openFileBrowsing ? _s4Explorer
            : IsEmptyFileModule ? _thumbnailExplorer
            : null;
        if (active is null) return;
        var next = active.SelectedEntry is { IsFolder: false } entry
            ? ((string Path, bool IsPlaceholder)?)(entry.Path, entry.IsPlaceholder)
            : null;
        if (next is null && _selectedBrowse is null) return;
        if (next is { } n && _selectedBrowse is { } cur &&
            n.Path == cur.Path && n.IsPlaceholder == cur.IsPlaceholder) return;
        _selectedBrowse = next;
        RefreshInfoOverlayForSelection();
    }

    // ---------- 경계 버튼 (A86 keymap Q7) ----------

    /// <summary>경계 버튼이 경계선에서 메인 쪽으로 걸치는 깊이 — 버튼 폭 20의 절반(반씩 걸침).</summary>
    private const double EdgeButtonOverlap = 10;

    /// <summary>
    /// 근접 판정 반경(경계선 좌우 각각): 터치 타깃 관례 44px보다 약간 넓은 48 — 버튼(20px)을
    /// 노리고 다가가면 확실히 뜨되, 화면을 가로지르는 이동에 스치기만 해도 뜰 만큼 넓지는 않게.
    /// A154부터 세로 판정(스택 위쪽 여유)에도 같은 값을 쓴다 — OnRootPointerMoved 참고.
    /// </summary>
    private const double EdgeProximity = 48;

    /// <summary>
    /// 경계 버튼 스택의 하단 여백(A154, v0.170.0) = 하단 바 44(<see cref="BottomBarHeight"/>) + 여유 8.
    /// 스택이 VerticalAlignment=Bottom이라 이 값만큼 콘텐츠 영역 바닥에서 띄워야 하단 바와 겹치지 않는다.
    /// XAML에 두지 않는 이유: <see cref="UpdateEdgeButtons"/>가 Margin을 통째로 덮어쓴다.
    /// 실효 여백은 <see cref="EdgeButtonsBottomInset"/>이 바 실표시에 따라 정한다 —
    /// 이 상수는 "바가 보일 때" 값.
    /// </summary>
    private const double EdgeButtonBottomMargin = 52;

    /// <summary>
    /// 경계 버튼 스택 하단 여백의 실효값(A151 — A186 개정: 모드가 아니라 **바 실표시** 연동):
    /// 바가 보이면 52(바 44 + 여유 8), 숨으면(전체화면·영상 자동 숨김) 여유 8만 남겨 바닥에
    /// 내려 붙인다(52 − 44). 판정은 <see cref="BarVisible"/> — UpdateShellChrome과 같은 축이라
    /// 영상 자동 숨김으로 바가 내려가면 버튼도 함께 바닥으로 온다.
    /// A152: 콘텐츠 영역(CenterArea)이 창 전체가 되고 바가 그 위 오버레이라, "바닥" = 창 바닥이다.
    /// UpdateEdgeButtons(Margin)와 OnRootPointerMoved(근접 y 띠)가 같은 값을 읽는다 —
    /// 바 가시성 변화는 UpdateShellChrome이 UpdateEdgeButtons를 다시 불러 즉시 반영된다.
    /// </summary>
    private double EdgeButtonsBottomInset
        => BarVisible ? EdgeButtonBottomMargin : EdgeButtonBottomMargin - BottomBarHeight;

    /// <summary>
    /// 경계 버튼 스택의 높이 = XAML 핀 버튼 32 하나(A176 — A154의 66 = 32×2 + Spacing 2에서
    /// peek 버튼 삭제로 재계수). 근접 y 판정의 입력이라 XAML 값과 같아야 한다
    /// (BottomBarHeight가 XAML BottomBar Height와 짝인 것과 같은 규칙).
    /// </summary>
    private const double EdgeButtonsHeight = 32;

    private double _leftEdgeX;   // 좌 경계선 x (CenterArea 기준) — 닫힘이면 0(창 가장자리)
    private double _rightEdgeX;  // 우 경계선 x — 닫힘이면 실폭(창 가장자리)

    /// <summary>
    /// 경계 버튼 위치·글리프 갱신 (A86): 경계선 = 그 쪽 패널의 화면 폭.
    /// 사이드바가 열려 있으면 SidebarPercent%(전 상태 공통 25 — A116), 닫혀 있으면 0 = 창
    /// 가장자리(닫힌 상태에서도 같은 자리에서 꺼낼 수 있어야 한다).
    /// 버튼은 경계선에서 메인 쪽으로 절반(EdgeButtonOverlap) 걸친다 — "메인을 살짝 덮게"(A86 원문).
    /// A108: 사이드바 안내 문구(각 패널 컨트롤의 PinnedText)가 이 버튼의 화면 안쪽 옆
    /// (좌 = 버튼 오른쪽 / 우 = 버튼 왼쪽, 세로 중앙)에 뜬다 — 패널 내부 분할이 같은 %라
    /// 별도 좌표 계산 없이 정렬된다.
    /// A133(v0.155.0): 문구는 다크 반투명 판(PinnedPlate) 안에 들어갔다 — 자리 계산은 무변경
    /// (판이 그 자리를 차지하고, Margin 14는 판의 바깥 모서리 기준).
    /// 표시 여부는 근접 판정(OnRootPointerMoved)이 정하고, 여기서는 컨텍스트가 사라졌을 때만 감춘다.
    /// </summary>
    private void UpdateEdgeButtons()
    {
        var width = CenterArea.ActualWidth;
        if (width <= 0) return;
        // A119: 패널 제공 뷰(정보 모듈)도 컨텍스트(HasPanelContext) — 경계 버튼이 같은 규칙으로 뜬다.
        if (!HasPanelContext || IsOpenFileBrowsing) // S4에서는 표시 안 함(keymap) — A86 훅이 A90에서 살았다
        {
            HideEdgeButtons();
            return;
        }

        var dockPercent = SidebarPercent; // ApplyOverlayStates의 도크 폭과 동일 상수(A116 정합)
        _leftEdgeX = LeftPanelIsOpen ? width * dockPercent / 100 : 0;
        _rightEdgeX = RightPanelIsOpen ? width - width * dockPercent / 100 : width;
        // A154(A176에서 핀 1개로 축소): 자리를 잡는 대상은 스택 — 아래 여백은 여기서 함께 준다
        // (이 대입이 XAML Margin을 덮어쓴다). 여백은 바 실표시 연동 실효값(EdgeButtonsBottomInset —
        // 바가 숨으면 8)이다.
        var bottomInset = EdgeButtonsBottomInset;
        LeftEdgeButtons.Margin =
            new Thickness(Math.Max(0, _leftEdgeX - EdgeButtonOverlap), 0, 0, bottomInset);
        RightEdgeButtons.Margin =
            new Thickness(0, 0, Math.Max(0, width - _rightEdgeX - EdgeButtonOverlap), bottomInset);
        // 글리프 = 누르면 일어날 일의 방향: 사이드바가 아니면 "사이드바로 세우기"(안쪽), 사이드바면 닫기(바깥쪽).
        LeftEdgeGlyph.Glyph = _listSide.State == OverlayState.OpaqueDocked ? "\uE76B" : "\uE76C";
        RightEdgeGlyph.Glyph = _infoSide.State == OverlayState.OpaqueDocked ? "\uE76C" : "\uE76B";
    }

    /// <summary>
    /// 마우스 근접 시에만 경계 버튼 표시 (A86 원문: "마우스가 근처에 갔을 때만").
    /// 경계선 x에서 EdgeProximity 이내이고 **스택이 실제로 있는 세로 띠** 안일 때만 보인다.
    /// A154: 종전 세로 조건은 "콘텐츠 영역 안(0 ~ 실높이)" = 사실상 y 무시였는데, 버튼이 세로
    /// 중앙에서 하단으로 내려가면서 그대로 두면 화면 맨 위에서도 반응해 버린다. 새 띠 =
    /// [스택 위 모서리 - EdgeProximity, 콘텐츠 영역 바닥]. 스택 위 모서리 =
    /// 실높이 - EdgeButtonsBottomInset(바 실표시 연동) - EdgeButtonsHeight. 아래쪽은
    /// 하단 여백뿐이라 별도 여유 없이 바닥까지 열어 둔다(하단 바 위를 스치는 이동도 잡힌다).
    /// A176: 순간 표시(peek) 중 강제 표시 특례는 peek와 함께 소멸.
    /// A186: 같은 이동 이벤트가 영상 하단 바 자동 숨김의 "입력"이기도 하다 — 근접 판정의
    /// 컨텍스트 가드보다 먼저 기록·통지한다(S4·설정 화면 여부와 무관한 별개 축).
    /// </summary>
    private void OnRootPointerMoved(object sender, PointerRoutedEventArgs e)
    {
        var p = e.GetCurrentPoint(CenterArea).Position;
        _lastPointerY = p.Y;      // A186 — 타이머 틱의 하단 근접 판정 입력
        NotifyBarAutoHideInput(); // A186 — 포인터 이동 = 재표시·카운트 되감기
        if (!HasPanelContext || IsOpenFileBrowsing) // A119: 패널 제공 뷰 포함 — UpdateEdgeButtons와 동일 판정
        {
            HideEdgeButtons();
            return;
        }
        var stackTop = CenterArea.ActualHeight - EdgeButtonsBottomInset - EdgeButtonsHeight;
        var insideY = p.Y >= stackTop - EdgeProximity && p.Y <= CenterArea.ActualHeight;
        LeftEdgeButtons.Visibility =
            insideY && Math.Abs(p.X - _leftEdgeX) <= EdgeProximity
                ? Visibility.Visible : Visibility.Collapsed;
        RightEdgeButtons.Visibility =
            insideY && Math.Abs(p.X - _rightEdgeX) <= EdgeProximity
                ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>경계 버튼 감추기 — 대상은 버튼 개별이 아니라 스택 단위다(A154).</summary>
    private void HideEdgeButtons()
    {
        LeftEdgeButtons.Visibility = Visibility.Collapsed;
        RightEdgeButtons.Visibility = Visibility.Collapsed;
    }

    /// <summary>핀 버튼 동작 = 사이드바(불투명 도크) 토글 (A86 keymap Q7 확정 — 좌).
    /// A176: 경계 버튼은 이 핀 하나뿐이다 — A154의 위 버튼(순간 표시 peek)은 반투명 축과 함께
    /// 삭제됐다(기능 소멸·대체 없음, 부록 B 72). F11 단타와 같은 토글이라 키와 버튼이 늘 같은
    /// 결과를 낸다.</summary>
    private void OnLeftEdgeToggle(object sender, RoutedEventArgs e) => ToggleOpaqueDock(_listSide);

    /// <summary>핀 버튼 동작 = 사이드바(불투명 도크) 토글 (A86 keymap Q7 확정 — 우).</summary>
    private void OnRightEdgeToggle(object sender, RoutedEventArgs e) => ToggleOpaqueDock(_infoSide);

    // A176: 순간 표시(peek) 버튼 일습(A154 — BeginPeek/EndPeek·PeekRestore·Peek 버튼 핸들러 4종)은
    // 반투명 축과 함께 철거 — 경계 버튼 스택에는 핀 하나만 남는다.

    /// <summary>사이드바(불투명 도크) 토글 (Q7) — F11/F12 단타(OnOverlaySideDown)와 동일 전이.</summary>
    private void ToggleOpaqueDock(OverlaySide side)
    {
        side.State = side.State == OverlayState.OpaqueDocked
            ? OverlayState.Closed
            : OverlayState.OpaqueDocked;
        ApplyOverlayStates();
    }

    /// <summary>
    /// 좌측 리스트 오버레이 표시: 현재 파일이 있는 폴더 + 현재 모듈의 담당 확장자(A57 ③)를
    /// 주입한다 — A7 드롭다운은 리스트 안에서 그 목록을 추가로 좁힌다.
    /// 파일이 없으면(빈 모듈 상태 — A81) 탐색기 시작 폴더(A174 — 세션 현재 위치/전역 마지막
    /// 폴더/바탕화면)를 대신 쓴다 — 중앙 빈 상태 탐색기와 같은 규칙.
    /// 파일이 있으면 종전대로 그 파일의 폴더 — 파일을 여는 전환의 "파일 폴더로 항해"는
    /// A174에서도 유지다(부록 B 71 ② — 유지 대상은 빈 모듈 전환만).
    /// 폴더가 사라졌으면(이동식 드라이브 탈착 등) 띄우지 않는다 — 문구·도크는 IsOpen 기준으로 따라온다.
    /// </summary>
    private void ShowListOverlay()
    {
        // A196: 모듈이 없어도 폴백 화면(미지원 안내)이면 전체 파일 필터로 띄운다 —
        // 모듈 개념이 없어 담당 확장자가 없다(등재문 확정). 빈 셸(폴백도 아님)만 종전대로 숨김.
        // A205: 설정 화면은 폴백이 아니다 — 애초에 listShow가 false라 여기까지 오지 않지만,
        // 와도 extensions가 null이라 숨김으로 떨어진다(이중 안전).
        var extensions = _currentModule?.SupportedExtensions
            ?? (IsPanelFallbackView ? ExplorerListing.AllFiles : null);
        if (extensions is null)
        {
            ListOverlay.Hide();
            return;
        }
        var folder = _currentFilePath is not null
            ? Path.GetDirectoryName(_currentFilePath)
            : ExplorerStartFolder();
        if (folder is not { Length: > 0 } || !Directory.Exists(folder))
        {
            ListOverlay.Hide();
            return;
        }
        ListOverlay.Show(folder, extensions);
    }

    // ---------- '오픈 파일' 버튼 · S4 탐색 모드 (A90) ----------

    /// <summary>
    /// 하단 바 '오픈 파일' 버튼 (A90 — 시작 메뉴 버튼 바로 옆): 네이티브 파일 대화상자를 띄우지 않고
    /// 자체 탐색기를 쓴다. 분배는 keymap '오픈 파일' 행 그대로 — S1 = "이미 열려 있음" 강조만(A90-b,
    /// 복귀 개념 없음) / S2·S3* = S4 진입 / S4 = 진입 전 상태로 복귀(재누름).
    /// None(A196부터 빈 셸뿐)은 keymap 표 밖 — 띄울 탐색기 컨텍스트가 없어 무동작(구현 결정).
    /// A119: 정보 모듈은 S2/S3*로 분류되지만 파일 컨텍스트가 없어 EnterOpenFileBrowsing의
    /// 방어선이 걸러 준다 — 결과는 종전(None 시절)과 같은 무동작. A196: 설정·미지원 안내·
    /// 무제 문서도 같은 방어선이 걸러 준다(S4는 파일 콘텐츠 전용 — 사양 유지).
    /// 바가 숨은 동안(전체화면·영상 자동 숨김)은 이 버튼 자체를 누를 수 없고, 영상 자동 숨김
    /// 축에서 전체화면 중 바가 나타나 눌리면 S4가 전체화면 위에 뜬다 — Esc 순서는 종전 규칙
    /// 그대로(첫 Esc = 전체화면 해제, 다음 Esc = S4 복귀)라 특별 처리하지 않는다.
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
    /// S4 진입 (A90, keymap "S4 구성 규칙" — A176 개정): 이미 떠 있는 구획은 그대로 두고
    /// (다시 얹지 않음), 없는 구획만 **사이드바(OpaqueDocked)**로 추가하고(구 반투명 고정
    /// 추가는 반투명 축 폐지로 소멸 — 남은 유일한 표시 상태 재사용), 중앙 콘텐츠는 썸네일
    /// 탐색기(S4 전용 인스턴스 — A176부터 불투명)로 덮는다. 포커스는 썸네일 그리드로.
    /// 좌 리스트는 **현재 콘텐츠 파일의 폴더**로 항해한다(ApplyOverlayStates → ShowListOverlay가
    /// 파일 폴더 기준 — "보던 파일 근처에서 다음 파일을 고른다"가 자연스러워서다. S2·S3*에서만
    /// 진입하므로 파일은 항상 있고, 폴더가 사라진 경우만 모듈 시작 폴더로 폴백).
    /// 목록 원본은 S1과 같은 좌 리스트 하나 — 결과가 ViewChanged로 S4 그리드로 흐른다(생성자 배선).
    /// </summary>
    private void EnterOpenFileBrowsing()
    {
        if (_openFileBrowsing) return;
        // 파일 컨텍스트 전용 — A119부터 정보 모듈도 S2/S3*로 분류되므로 이 가드가 실동작한다
        // (파일이 없어 띄울 좌 리스트 폴더가 없다 — '오픈 파일'은 무동작).
        if (_currentFilePath is null || _currentModule is null) return;
        _s4Restore = (_listSide.State, _infoSide.State); // A176: 안정 상태 2종뿐이라 그대로 스냅샷
        if (_listSide.State == OverlayState.Closed) _listSide.State = OverlayState.OpaqueDocked;
        if (_infoSide.State == OverlayState.Closed) _infoSide.State = OverlayState.OpaqueDocked;
        _openFileBrowsing = true;
        EnsureS4Explorer();
        S4Host.Visibility = Visibility.Visible;
        ApplyOverlayStates(); // ShowListOverlay가 현재 파일의 폴더로 Show → ViewChanged → S4 그리드 채움
        if (!ListOverlay.IsOpen) // 파일 폴더 소실(드라이브 탈착 등) — 시작 폴더(A174)로라도 목록을 만든다
            ListOverlay.NavigateList(ExplorerStartFolder(), _currentModule.SupportedExtensions);
        _s4Explorer?.FocusGrid();
    }

    /// <summary>
    /// S4 종료 (A90): restore=true(Esc·재누름) = 진입 전 스냅샷으로 복귀 —
    /// 이번에 추가된 구획만 내려가고 원래 있던 구획은 원래 모습(불투명이면 불투명) 그대로.
    /// S4 중에는 F11/F12·경계 버튼이 전부 무동작이라 좌/우 상태가 변할 길이 없어, 스냅샷 전체 대입이
    /// 곧 "추가분만 되돌리기"와 같다. restore=false(콘텐츠 전환 = SetContentState/OnContentOpened) =
    /// 스냅샷을 버리고 좌/우는 지금 상태 그대로 A86 "상태는 콘텐츠를 넘어 유지" 규칙을 탄다.
    /// refresh=false는 호출부가 곧바로 ApplyOverlayStates를 부르는 경로(콘텐츠 전환)용.
    /// </summary>
    private void ExitOpenFileBrowsing(bool restore, bool refresh = true)
    {
        if (!_openFileBrowsing) return;
        _openFileBrowsing = false;
        _selectedBrowse = null; // A200: S4 그리드가 내려간다 — 그 선택이 우측 정보에 남지 않게
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
        // A176: 구 "중앙을 반투명으로 덮는다"(A90 원문)의 반투명 배경 교체(A129 폴백 포함)는
        // 반투명 축과 함께 철거 — S4도 S1과 같은 불투명 기본 배경으로 덮는다.
        _s4Explorer.FolderActivated += folder => ListOverlay.NavigateList(folder);
        _s4Explorer.FileActivated += OpenFileRouted; // 열리면 SetContentState가 S4를 자동 종료한다
        // 새 창 열기(Shift+더블클릭·우클릭)는 이 창의 콘텐츠가 안 바뀌므로 S4를 유지한다(구현 결정 —
        // 다른 창에 하나 열고 계속 고르는 흐름이 자연스럽다).
        _s4Explorer.FileActivatedNewWindow += _manager.OpenFileInNewWindow;
        _s4Explorer.SelectionChanged += OnBrowseSelectionChanged; // A200 — S1 쪽과 동일 배선(선택 우선 정보)
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
            timer.Stop(); // 반복 타이머 — Tick에서 반드시 멈춘다(반복 타이머 공통 관용구)
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

    /// <summary>현재 모듈 색 .ico 경로 — 링·표식 합성의 바탕(A102).</summary>
    private string? _moduleIconPath;

    /// <summary>
    /// 지금 그려진 아이콘의 모듈 색 — 중립 아이콘이면 null (A79 브랜드 표식 ①/② 판단).
    /// 모듈 ID가 아니라 <b>실제로 고른 .ico</b>를 기준으로 정한다: 모듈 .ico가 없어 중립으로
    /// 폴백했으면 중립 표식이 맞기 때문.
    /// </summary>
    private Windows.UI.Color? _moduleIconAccent;

    /// <summary>
    /// 지금 그려진 아이콘의 테두리 링 색 — 링이 없으면 null (A102, v0.130.0).
    /// 액센트와 마찬가지로 <b>실제로 고른 .ico</b> 기준이다: 모듈 .ico가 없어 중립으로
    /// 폴백했으면 구분할 모듈 색 자체가 없으므로 링도 없다.
    /// </summary>
    private Windows.UI.Color? _moduleIconRing;

    /// <summary>
    /// 창(태스크바) 아이콘의 모듈 3자 표기 (A105 ②, v0.143.0) — 없으면 null.
    /// A137부터 쓰임이 갈린다: 규칙 안 모듈의 유휴 32px에서는 전면 채움 타일의 글자
    /// (InstanceIcon.GetIdleTile — 트레이 유휴와 같은 모양), 규칙 밖(하드웨어)에서는 종전대로
    /// .ico 본체 하단 띠(GetComposed)다.
    /// 출처는 트레이 유휴 표기와 같은 <see cref="IdleTrayLabel"/> 단일 표다(중복 표 금지).
    /// 액센트·링과 달리 <b>모듈 ID 기준</b>(아이콘 파일 폴백과 무관): 모듈 .ico가 없어 중립
    /// 아이콘으로 폴백해도 "어느 모듈의 창인가"라는 사실은 그대로라, 모듈 ID로 그리는
    /// 트레이(A54 UpdateTrayIcon)와 같은 축을 유지한다.
    /// </summary>
    private string? _moduleIconLabel;

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
        _moduleIconRing = path == IconPath ? null : Branding.IconRing(moduleId);       // A102
        _moduleIconLabel = IdleTrayLabel(moduleId) is { Length: > 0 } code ? code : null; // A105 ②
        RefreshShellIcons();
    }

    /// <summary>
    /// 마지막으로 보낸 창 아이콘 합성 입력의 키(A137) — 같으면 WM_SETICON 왕복과 32px 캐시 밖
    /// 재합성을 통째로 건너뛴다. 트레이 <see cref="_trayIconKey"/>(A18 방식)와 같은 장치인데,
    /// 창 쪽 내용 축은 (경로·액센트·링·라벨·전면 채움 색·인스턴스 번호·열린 파일 확장자/용량)이다 —
    /// <b>새 정보 축을 더하면 반드시 이 키에도 넣을 것</b>(A169 교훈: 키 누락 = 값이 바뀌어도
    /// 아이콘이 갱신되지 않고, CI는 그것을 못 잡는다).
    /// </summary>
    private string _windowIconKey = string.Empty;

    /// <summary>
    /// 창·트레이 아이콘을 현재 상태로 다시 지정한다(A68 → A102 의미 개편 → A137 실시간 정보).
    /// A137: 창 아이콘 2종이 서로 다른 정보를 담는다 — 16px = 인스턴스 번호, 32px = 열린 파일의
    /// 확장자/용량(유휴면 3자 이니셜). 그래서 호출 지점도 모듈 전환(ApplyWindowIcon)만이 아니라
    /// 파일 열기/닫기(SetContentState·OnContentOpened)·저장 성공(TrayStatusChanged 경유)·
    /// 인스턴스 번호 변경(SetInstanceNumber)으로 늘었다 — 전부 이 한 지점으로 모이고,
    /// 재합성 여부는 _windowIconKey 선비교가 정한다(무변경 호출은 문자열 비교 비용뿐).
    /// 색 규칙(A140)의 판정 축은 트레이와 같은 Branding.IdleFill(모듈 ID 기준 — 하드웨어는
    /// moduleId == "hardware" 명시 조건으로 규칙 밖 = 전용 색·링 없음, 종전 .ico 합성 유지).
    /// AppWindow.SetIcon은 원본 경로 유지 — 실제 표시는 직후 WM_SETICON(WindowIcon)이 덮는다.
    /// ※ A54(v0.118.0): 트레이 아이콘은 값 텍스트를 그린다(<see cref="UpdateTrayIcon"/>).
    /// </summary>
    private void RefreshShellIcons()
    {
        if (_moduleIconPath is { } path && File.Exists(path))
        {
            var idleFill = Branding.IdleFill(CurrentModuleId);
            var (line1, line2) = OpenFileIconInfo(idleFill);
            var key = $"{path}|{_moduleIconAccent?.ToString() ?? "n"}|{_moduleIconRing?.ToString() ?? "n"}"
                + $"|{_moduleIconLabel ?? "n"}|{idleFill?.ToString() ?? "n"}|{_instanceNumber}"
                + $"|{line1 ?? "n"}|{line2 ?? "n"}";
            if (key != _windowIconKey)
            {
                AppWindow.SetIcon(path);
                // A105 ②/A137: 합성 실패(통째 폴백)면 키를 비워 다음 갱신 때 다시 시도한다
                // (트레이 UpdateTrayIcon의 실패 처리와 같은 규칙).
                var ok = WindowIcon.Apply(this, path, _moduleIconAccent, _moduleIconRing,
                    _moduleIconLabel, idleFill, _instanceNumber, line1, line2);
                _windowIconKey = ok ? key : string.Empty;
            }
        }
        UpdateTrayIcon();
    }

    /// <summary>
    /// 작업표시줄 32px 아이콘의 열림 2줄 값(A137 ② — 예: "TXT" / "40K"). 값은 트레이 계약이 아니라
    /// <b>셸이 현재 경로에서 직접 계산</b>한다 — 계약의 TrayStatus는 모듈마다 의미가 다르지만
    /// (문서=페이지(A138)·영상=해상도/비트레이트·오디오=시간/막대 — 부록 B 52) 확장자+용량은
    /// 전 모듈 공통이라 경로 계산이 계약 확장보다 싸다(구현 시 결정 — REQUIREMENTS A137 ②).
    /// 표기는 트레이와 같은 규격(TrayFormat.Extension·Size — 단일 소스).
    /// (null, null) = 열림 표기 없음 → 유휴(3자 이니셜)로 후퇴: 규칙 밖 화면(하드웨어·중립),
    /// 파일 없음, 실경로가 아닌 화면(압축 내부 항목처럼 디스크에 없는 경로).
    /// </summary>
    private (string? Line1, string? Line2) OpenFileIconInfo(Windows.UI.Color? idleFill)
    {
        if (idleFill is null) return (null, null);
        if (_currentFilePath is not { } file || !File.Exists(file)) return (null, null);
        long bytes = -1;
        try
        {
            bytes = new FileInfo(file).Length;
        }
        catch
        {
            // 크기를 못 읽으면 그 줄만 "—"가 된다(TrayFormat.Size(-1) — DocumentView의 종전 처리와 동일).
        }
        return (TrayFormat.Extension(file), TrayFormat.Size(bytes));
    }

    // ---------- 트레이 아이콘 내용 (A54, v0.118.0) ----------

    /// <summary>마지막으로 그린 트레이 아이콘의 키 — 같으면 GDI 재합성을 통째로 건너뛴다(A18 방식).</summary>
    private string _trayIconKey = string.Empty;

    /// <summary>
    /// 모듈이 내준 <see cref="TrayStatus"/>를 16px 아이콘으로 합성해 트레이에 올린다(A54).
    /// 값을 내주지 않는 화면(설정·미지원 파일 안내)은 모듈 ID → 3자 표기 표로 유휴 아이콘을 그리고,
    /// 그 표에도 없으면(설정·빈 셸) 중립 모듈 .ico로 폴백한다 — 인스턴스당 아이콘 1개는 언제나 유지된다.
    /// 호출 시점: 모듈 전환·설정 전환·파일 열기(IContentStateSource)·모듈의 TrayStatusChanged.
    /// 값이 그대로면 아무 일도 하지 않는다.
    /// ※ A102 테두리 링(모듈 색)의 출처가 두 갈래인 이유: .ico 폴백 경로는 그려질 바탕이
    ///   중립일 수 있어 <see cref="_moduleIconRing"/>(실제 고른 파일 기준)을 쓰고,
    ///   값 텍스트 경로는 바탕 없이 모듈 색 글자를 그리므로 모듈 ID에서 바로 구한다.
    /// </summary>
    private void UpdateTrayIcon()
    {
        var status = (ModuleHost.Content as ITrayStatusProvider)?.GetTrayStatus()
            ?? (IdleTrayLabel(CurrentModuleId) is { Length: > 0 } label ? TrayStatus.Idle(label) : null);

        var key = TrayStatusIcon.ComposeKey(status, CurrentModuleId);
        if (key == _trayIconKey) return;
        _trayIconKey = key;

        if (status is null)
        {
            _tray.SetIcon(_moduleIconPath, _moduleIconAccent, _moduleIconRing);
            return;
        }

        // A140(v0.164.0): 유휴 전면 채움 색은 링과 별도 축(Branding.IdleFill) — 하드웨어(규칙 밖)와
        // 중립 화면을 링만 보고는 구분할 수 없어(둘 다 null) 모듈 ID로 판단하는 메서드를 따로 둔다.
        var icon = TrayStatusIcon.Compose(status, Branding.ModuleAccent(CurrentModuleId),
            Branding.IconRing(CurrentModuleId), Branding.IdleFill(CurrentModuleId));
        if (icon == IntPtr.Zero)
        {
            _trayIconKey = string.Empty; // 합성 실패(GDI 고갈 등) — 다음 갱신 때 다시 시도
            return;
        }
        _tray.SetRenderedIcon(icon);
    }

    /// <summary>
    /// 콘텐츠를 안 열고 있을 때의 모듈 3자 표기(A54 — 사용자 확정: IMG/VID/AUD/DOC/ARC/ALL).
    /// 정보(하드웨어)의 INF는 A101(v0.137.0)부터 안전망이다 — HardwareView가 계약을 구현해
    /// 선택 0개면 같은 "INF" 유휴를 직접 돌려주므로 정상 경로에서 이 행에 닿지 않지만,
    /// 두 경로의 결과가 같아야 하니 표기를 바꾸면 뷰의 IdleLabel도 함께 바꿀 것
    /// (BrandName "KOTU-info"와 정합. 2자 "HW"는 3자 규칙에서 벗어나고 "HWM"은 조어라 채택 안 함).
    /// 표에 없는 화면(설정·미지원 파일 안내)은 빈 문자열 → 중립 모듈 아이콘 폴백.
    /// A105 ②(v0.143.0): 창(태스크바) 아이콘 하단 3자 표기(<see cref="_moduleIconLabel"/>)도
    /// 이 표를 단일 출처로 재사용한다 — 표기를 바꾸면 트레이·창 아이콘이 함께 바뀐다.
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

    // ---------- 트레이로 숨김 (A69 → A218: 자동 감지 폐지·명시 호출 전용) ----------

    /// <summary>
    /// 트레이로 숨긴 상태(A69) — 작업표시줄·Alt+Tab에 없고 창별 트레이 아이콘으로만 복귀한다.
    /// 숨김은 닫힘이 아니다: WindowManager의 창 목록 제거는 Closed에서만 일어나므로
    /// 마지막 창까지 숨겨도 열린 창으로 계산되어 프로세스가 유지된다(창 0개 = 종료 로직과 무충돌).
    /// </summary>
    private bool _hiddenInTray;

    /// <summary>
    /// A219: 트레이로 숨긴 상태인지의 조회 노출 — 창 재사용 판단(WindowManager.FindReusable)이
    /// 숨김 창을 후보에서 빼는 데 쓴다(A218 정합 — 숨김 창은 명시 복귀 경로로만 돌아온다).
    /// </summary>
    public bool IsHiddenInTray => _hiddenInTray;

    /// <summary>
    /// A218: 이 창을 트레이로 숨긴다 — 진입은 명시 호출 2곳뿐(트레이 우클릭 "Minimize to tray" +
    /// 시작 메뉴 최하단 항목). 최소화 버튼·Win+D 등 OS 최소화는 이제 전부 일반 최소화다
    /// (A69/A185의 자동 감지 폐지 — 세 번째 변경. 복원 참조 = A218 이전 git 이력의 OnMinimizeStateChanged).
    /// 숨김 동안만 WS_EX_TOOLWINDOW(부록 B 18번 사양 메모) — Hide가 주 동작이고 스타일은
    /// 숨김 창을 순환 목록에 남기는 셸 변형에 대한 보조 방어선이다. 복귀는 종전 그대로
    /// <see cref="BringToFront"/>(트레이 좌클릭·Activate window·파일 열기 재사용)로 모인다.
    /// </summary>
    public void HideToTray()
    {
        if (_hiddenInTray) return;
        _hiddenInTray = true;
        AltTabExclusion.Set(this, true);
        AppWindow.Hide(); // 작업표시줄 버튼 제거. 트레이 아이콘(_tray)은 창 수명 내내 남는다
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
        // A228: 백그라운드 프로세스는 Activate()만으로는 포그라운드를 못 뺏는다(OS 잠금).
        // 외부 진입이면 리다이렉터가 이양한 권한(Program.cs의 AllowSetForegroundWindow)으로
        // 실제 전면 전환이 성공하고, 권한이 없으면 작업표시줄 점멸로 후퇴한다. 트레이 클릭
        // 같은 자체 포그라운드 경로에서는 그냥 성공해 점멸 없이 무해하다(반환값 판정).
        Integration.ForegroundActivation.EnsureForeground(this);
    }

    // ---------- 인스턴스 번호 (A2, v0.58.0 → A141 배지 제거 → A137 아이콘 부분 부활) ----------
    // 소비처 2곳: 창 제목 접두 숫자(A103/A136) + 16px 타이틀바 아이콘 타일(A137 ①).
    // 하단 바 색상 배지와 그 9색 팔레트(구 InstanceIcon.ColorFor)는 A141에서 사라진 그대로다.

    /// <summary>
    /// 인스턴스 번호 설정. 값은 <see cref="WindowManager"/>가 생성 순서대로 준다 —
    /// A136(v0.162.0)부터 창이 하나뿐이어도 1이 들어온다(0은 더 이상 오지 않지만
    /// 방어적으로 0 이하를 0으로 접어 둔다 = 번호 없는 제목).
    /// 중간 창이 닫히면 WindowManager가 번호를 당겨서 다시 부른다.
    /// 표시 2곳: 창 제목 접두 숫자(A103/A136 "1-KOTU" — 개수 제한 없음) +
    /// 16px 타이틀바 아이콘의 번호 타일(A137 ① — A102가 없앤 번호 렌더의 부분 반전.
    /// 이력 A68→A102→A137은 InstanceIcon.GetNumberTile 주석 참고). 그래서 번호가 바뀌면
    /// 아이콘도 다시 지정한다 — 창 여닫이마다 전 창이 이 호출을 받지만 번호가 실제로 변한
    /// 창만 _windowIconKey 선비교를 통과해 재합성된다.
    /// </summary>
    public void SetInstanceNumber(int number)
    {
        _instanceNumber = number > 0 ? number : 0;
        ApplyTitle();
        RefreshShellIcons(); // A137 ①: 16px = 인스턴스 번호 — 번호가 다시 아이콘의 정보 축이 됐다
    }
}
