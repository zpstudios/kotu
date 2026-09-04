using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.ApplicationModel.DataTransfer;
using Windows.Storage;
using Windows.Storage.FileProperties;
using KOTU.Core.Routing;
using KOTU.Core.Settings;
using KOTU.Core.Threading;
using KOTU.Input;

namespace KOTU.App;

/// <summary>
/// 내장 탐색기 컨트롤 (v0.25.0, docs/explorer-plan.md).
/// 좌 70% 썸네일 그리드 + 우 30% 리스트로 같은 폴더를 두 방식으로 보여준다.
/// 폴더는 전부, 파일은 주입된 담당 확장자만(사용자 확정). 더블클릭: 폴더=진입, 파일=FileActivated.
/// 좌 리스트 오버레이(FileListOverlay)용으로는 ConfigureListOnly()로 리스트만 남겨 재사용한다.
/// ※ A93(v0.120.0)부터 살아 있는 사용처는 좌 오버레이(리스트 전용)뿐이다 — S1 중앙은
/// ThumbnailExplorer가 대체했고, 이 페인의 표시 목록(ViewChanged)이 그 뷰의 데이터 원본이다.
/// 썸네일 그리드 경로(MakeGridItem·LoadThumbnailsAsync)는 전체 페인 사용처가 다시 생길 때를
/// 위해 남겨 뒀다(리스트 전용 모드에서는 그리드가 숨겨져 실행되지 않는다).
/// <b>A345 배치 2 — 두 표면의 구조가 갈라졌다</b>: 좌 리스트(ListPane)는 ItemsSource +
/// DataTemplate(x:Bind) + ContainerContentChanging <b>가상화</b>라 보이는 행만 실체화되고,
/// 항목의 표시 상태는 뷰모델(ExplorerEntryVm)이 들고 있다. 좌 그리드(IconGrid)는 휴면 표면이라
/// 종전 컨테이너 직접 조립(Tag = 뷰모델)이 그대로다. 항목 객체의 해석은 그래서 표면마다
/// 다르며, 그 차이는 <c>VmOf</c> 한 곳이 흡수한다 — 새 코드도 반드시 그것을 지나게 할 것.
/// 폴더 스캔은 페인 전용 워커(A42)에서 직렬로, 항목별로 독립인 썸네일·상세 조각 fetch는
/// 페인 전용 풀(A194 — ModuleWorkerPool, 워커 3)에서 상한 있는 병렬로 돌고,
/// UI 스레드는 결과 반영만 한다(카운터·캐시·컨테이너 접근은 전부 UI 스레드 단독).
/// 외부 변경(다른 앱·OS 탐색기)은 폴더 감시(A94 5차 — FileSystemWatcher + 디바운스)가
/// 같은 재스캔 경로로 반영한다(아래 "폴더 감시" 절).
/// </summary>
public sealed partial class ExplorerPane : UserControl
{
    private const int ThumbnailLimit = 300;   // 썸네일 로드 상한 (초대형 폴더 보호)
    private const int FetchConcurrency = 3;   // 썸네일·상세 조각 fetch 동시성 상한 (A194 — 풀 워커 수와 동일)
    private const int DoubleClickMs = 500;

    /// <summary>
    /// A192: 컨테이너 실체화 상한 — <b>A345 배치 2부터 IconGrid(휴면 그리드) 전용</b>이다.
    /// 좌 리스트(ListPane)는 ItemsSource + DataTemplate 가상화라 보이는 행만 실체화되고,
    /// 따라서 상한 자체가 필요 없다(안내 행 MakeOverflowNotice도 함께 폐기 — ItemsSource
    /// 상태에서 Items.Add는 즉시 예외다). 그리드는 종전 컨테이너 조립이라 상한을 남긴다.
    /// _entries·_display·ViewChanged로 흐르는 Entry 목록과 체크 prune(A179)은 전체 그대로다.
    /// </summary>
    private const int MaterializeLimit = 500;

    /// <summary>파일 더블클릭 시 전체 경로와 함께 발생. 셸이 라우팅한다(재사용 규칙 적용, A24).</summary>
    public event Action<string>? FileActivated;

    /// <summary>
    /// 파일을 명시적으로 새 창에서 열라는 요청(A24: Shift+더블클릭 또는 우클릭 메뉴).
    /// 셸이 재사용 규칙과 무관하게 항상 새 창으로 연다.
    /// </summary>
    public event Action<string>? FileActivatedNewWindow;

    /// <summary>
    /// 표시 목록이 다시 그려질 때(폴더 이동·정렬 A5·필터 A7) 폴더 경로·정렬·필터 적용 결과와 함께
    /// 발생 (A93 — 폴더 인자는 A94: 썸네일 뷰가 드랍·붙여넣기 대상 폴더를 알아야 한다).
    /// 중앙 썸네일 뷰(ThumbnailExplorer)가 좌 리스트와 같은 폴더 상태를 공유하는 통로 —
    /// 셸이 구독해 같은 항목을 타일로 다시 그린다. 폴더를 못 읽으면 빈 목록으로 알린다.
    /// </summary>
    public event Action<string, IReadOnlyList<ExplorerListing.Entry>>? ViewChanged;

    /// <summary>
    /// A240: 리스트 선택 변경 — 셸이 우측 정보 패널의 "선택 우선" 표시(A200)에 쓴다. 인자 없음:
    /// 셸이 <see cref="SelectedEntry"/>를 질의한다(ThumbnailExplorer의 A200 관용구와 동일 —
    /// 선택 상태의 원본은 표면 컨트롤 하나). 목록 재작성(Fill의 Items.Clear)·다중 선택 조작에서도
    /// 표면이 알아서 발화한다. A179가 철거한 "선택 → 체크 동기"(A157 거울)와 무관하다 —
    /// 이것은 순수 관찰(선택 축)이고 체크 집합은 건드리지 않는다.
    /// </summary>
    public event Action? SelectionChanged;

    /// <summary>
    /// A241: 표시 목록 조립 완료(FinishFill) 통지 — ViewChanged(조립 시작 시점)와 달리
    /// 목록 반영·로더 기동이 끝난 뒤에 온다(A345 배치 2부터 리스트 컨테이너 실체화는 화면 분량뿐). 셸이 우측 정보 패널의 폴더 단위 EXIF
    /// 프리페치를 여기 걸어 뼈대 우선 원칙(A192)을 지킨다. 인자 = 표시 목록 전체(정렬·필터
    /// 반영 — 화면 밖 항목 포함, ViewChanged와 같은 집합).
    /// </summary>
    public event Action<IReadOnlyList<ExplorerListing.Entry>>? FillCompleted;

    /// <summary>
    /// A243: 폴더 실변경 항해의 시작 통지 — ViewChanged(스캔 완료 후 목록)보다 앞서, 스캔 시작
    /// 시점에 새 폴더 경로와 함께 발생한다. 셸이 중앙 썸네일(ThumbnailExplorer.ShowLoading)에
    /// 같은 즉시 화면 전환을 중계하는 통로다. 같은 폴더 재스캔(감시 400ms 디바운스·조작 후
    /// 갱신)·정렬·필터 재작성은 발화하지 않는다 — 실변경 판정은 NavigateToAsync 한 곳(단일 지점).
    /// </summary>
    public event Action<string>? NavigationStarted;

    /// <summary>
    /// 파일 조작(드랍 이동/복사·붙여넣기 — A94) 실패 안내 문구. 이 페인에는 상태 표시 줄이 없어
    /// 호스트(FileListOverlay)가 받아 A92류 일시 문구로 띄운다. 성공은 조용(뷰 갱신이 피드백).
    /// </summary>
    internal event Action<string>? Notice;

    /// <summary>
    /// 숨김·시스템 표시(A160, v0.169.0)가 토글됐을 때. 호스트(FileListOverlay)의 폴더 트리는
    /// 이 페인의 리스트와 <b>별개로</b> 폴더를 열거하므로, 같은 설정을 다시 읽어 트리를 새로
    /// 만들라는 신호다. 리스트 쪽 재열거는 이 페인이 스스로 한다(EnsureFilterFlyout의 토글 참고).
    /// </summary>
    internal event Action? ShowHiddenChanged;

    /// <summary>현재 폴더 경로 (A94 — 호스트의 패널 드랍·붙여넣기 대상). 항해 전이면 빈 문자열.</summary>
    internal string CurrentFolder => _folder;

    /// <summary>
    /// A323: 지금 표시 중인 목록(정렬 A5·필터 A7 반영 = 마지막 ViewChanged와 같은 집합).
    /// 셸이 <b>재스캔 없이</b> 새 표면(S4 그리드)을 시드할 때 읽는다 — Show가 같은 폴더 재항해를
    /// 건너뛰면 ViewChanged가 오지 않기 때문이다(MainWindow.EnterOpenFileBrowsing 주석).
    /// </summary>
    internal IReadOnlyList<ExplorerListing.Entry> DisplayEntries => _display;

    /// <summary>
    /// A323: 위 <see cref="DisplayEntries"/>가 속한 폴더 — <see cref="CurrentFolder"/>(항해 시작
    /// 즉시 새 폴더로 바뀐다)와 달리 **마지막으로 실제 표시까지 간** 폴더다. 둘이 다르면
    /// 스캔이 도는 중(목록은 아직 옛 폴더 것)이라는 뜻이라, 셸의 시드는 두 값이 같을 때만 한다 —
    /// 그렇지 않으면 로딩 화면(A243)을 옛 폴더 타일로 덮어 버린다.
    /// </summary>
    internal string DisplayFolder => _displayFolder;

    /// <summary>A323: <see cref="DisplayFolder"/>의 실값 — 갱신은 RefreshView 한 곳(_display와 같은 자리).</summary>
    private string _displayFolder = string.Empty;

    // "name"/"size"/"modified"/"created"(A117)/"type"(A155) — SortKey.ToString().ToLowerInvariant()와
    // 수동 동기. 모르는 값(구 버전·손편집)은 이름순으로 폴백한다(아래 switch의 _ 분기).
    private const string SortSettingKey = "explorer.sort";

    // 정렬 방향 (A155, 부록 B 69 ①) — true = 내림차순. explorer.sort와 같은 층·같은 왕복(전역 1벌).
    // 키가 없으면(구 버전 설정) 현재 정렬 키의 종전 고정 방향(DefaultDescending)으로 폴백해
    // 업데이트 전과 같은 화면을 유지한다 — 마이그레이션 없음.
    private const string SortDescSettingKey = "explorer.sortDesc";

    /// <summary>
    /// 숨김·시스템 파일 표시 여부 (A160, v0.169.0) — explorer.sort와 같은 층의 탐색기 설정이라
    /// 같은 자리·같은 모양(const 문자열 키 + 즉시 Set/Save)으로 둔다. 값은 전역 1벌(A110 결론).
    /// private이 아니라 internal인 이유: 좌 패널 폴더 트리(FileListOverlay)가 같은 값을 읽어야
    /// 리스트와 트리가 같은 집합을 보인다 — 키 문자열을 복제하지 않으려고 여기를 단일 출처로 쓴다.
    /// </summary>
    internal const string ShowHiddenSettingKey = "explorer.showHidden";

    private IReadOnlyList<string> _extensions = [];
    private string _folder = string.Empty;
    private int _loadSeq;                     // 빠른 연속 탐색 시 늦은 결과 폐기

    /// <summary>
    /// A345 배치 2: 상세 조각 fetch의 동시 발사 상한 — 페인 수명 1벌이다(종전 LoadDetailInfoAsync가
    /// 회차마다 만들던 지역 게이트를 대체). 발사 주체가 "목록 전체를 도는 루프"에서
    /// "보이는 행마다 도착하는 CCC"로 바뀌어 회차 개념이 사라졌기 때문이다.
    /// <b>Dispose하지 않는다</b> — 대기 중인 획득이 남은 채 닫으면 finally의 Release가
    /// ObjectDisposedException을 던진다(페인이 내려가면 seq 대조와 풀 취소가 이미 흐름을 끊는다).
    /// </summary>
    private readonly SemaphoreSlim _detailGate = new(FetchConcurrency);
    private (string Path, DateTime At)? _lastClick;
    private (string Path, DateTime At)? _lastActivation; // A85: ItemClick 쌍·DoubleTapped 겹침을 1회로 억제
    private (string Path, DateTime At)? _lastPress;      // A131: 원시 눌림 쌍 — 항목 재구축을 건너 살아남는 최후 폴백
    private ModuleWorker? _worker;            // 폴더 스캔 전용(순서 의존) — 페인별 분리(A42 정책)
    private ModuleWorkerPool? _fetchPool;     // 썸네일·상세 조각 fetch 전용(A194 — 항목별 독립 작업만)
    private IReadOnlyList<ExplorerListing.Entry> _entries = []; // 마지막 스캔 결과 — 재스캔 없는 재배치의 원본(A5)
    // A204: 마지막 Arrange 결과(현재 표시 순서). 정렬 키·방향 변경(헤더 클릭)은 이것을 입력으로
    // stable 재정렬해 직전 기준이 동률의 2차 순서로 살아남는다. 재스캔·필터 변경은 _entries
    // (스캔 결과 = 이름순)를 입력으로 — 안정성은 세션 내 정렬 조작 간에만 성립, 재스캔은 리셋.
    private IReadOnlyList<ExplorerListing.Entry> _display = [];
    // A345 배치 1: _display와 같은 순서·같은 개수의 뷰모델 목록(데이터 축). 조립(Fill)은 이제
    // 이 목록을 소비하고 컨테이너 Tag에 뷰모델을 건다 — 공개 API가 돌려주는 Entry 목록은 무변경.
    private IReadOnlyList<ExplorerEntryVm> _displayVms = [];
    private ExplorerListing.SortKey _sortKey = ExplorerListing.SortKey.Name;
    private bool _sortDesc; // A155 — 초기값 false = Name의 기본 방향(DefaultDescending(Name))과 일치
    private bool _showHidden; // A160 — explorer.showHidden(기본 false = 숨김·시스템 감춤)
    private ISettingsService? _settings;

    /// <summary>
    /// 정렬 키(A5)·숨김 표시(A160) 저장용. 셸(MainWindow)이 페인 생성 직후 주입한다 —
    /// 없어도 동작한다(기본 이름순 · 숨김 감춤).
    /// </summary>
    public ISettingsService? Settings
    {
        get => _settings;
        set
        {
            _settings = value;
            _sortKey = value?.Get(SortSettingKey, "name") switch
            {
                "size" => ExplorerListing.SortKey.Size,
                "modified" => ExplorerListing.SortKey.Modified,
                "created" => ExplorerListing.SortKey.Created, // A117
                "type" => ExplorerListing.SortKey.Type, // A155
                _ => ExplorerListing.SortKey.Name,
            };
            // A155: 방향은 키 다음에 읽는다 — 저장값이 없으면 그 키의 종전 고정 방향으로 폴백.
            _sortDesc = value?.Get(SortDescSettingKey, DefaultDescending(_sortKey))
                ?? DefaultDescending(_sortKey);
            _showHidden = value?.Get(ShowHiddenSettingKey, false) ?? false; // A160 — 정렬과 같은 왕복
            SyncSortHeaders();
            SyncShowHiddenCheck(); // A160 — 필터 메뉴가 이미 만들어져 있으면 체크도 새 값에 맞춘다
        }
    }

    /// <summary>지연 생성: Unloaded로 정리된 뒤 다시 로드돼도(좌 리스트 오버레이 재오픈) 되살아난다.</summary>
    private ModuleWorker Worker => _worker ??= new ModuleWorker("KOTU explorer worker");

    /// <summary>
    /// 썸네일·상세 조각 fetch 전용 풀 (A194 — 워커 3, Worker와 같은 지연 생성·Unloaded 정리 규칙).
    /// 항목별로 독립인 fetch만 여기로 — 순서 의존 작업(폴더 스캔)은 단일 Worker에 남는다
    /// (풀은 배정 간 순서를 보장하지 않는다 — ModuleWorkerPool 계약).
    /// <para>
    /// A333: 우선순위는 BelowNormal — 상세 조각·썸네일은 <b>이미 그려진 목록에 뒤늦게 얹히는</b>
    /// 배경성 작업이라, 사용자가 결과를 기다리는 폴더 스캔(<see cref="Worker"/> = Normal 유지)이나
    /// UI 스레드와 CPU를 다투면 안 된다(ModuleWorker의 priority 계약 · 선례 App.xaml.cs
    /// 셸 등록 정비 · DriveStrip 워커 · PollingWorker 기본값). 순수 스레드 우선순위 변경이라
    /// 결과 반영 경로(UI 문맥 await 후속부 = 디스패처 큐 = UI 스레드)는 전혀 달라지지 않는다.
    /// </para>
    /// </summary>
    private ModuleWorkerPool FetchPool =>
        _fetchPool ??= new ModuleWorkerPool(
            "KOTU explorer fetch", FetchConcurrency, ThreadPriority.BelowNormal);

    public ExplorerPane()
    {
        InitializeComponent();
        BuildListHeader(); // A155 — 컬럼 헤더(정렬 버튼) 조립 + 초기 정렬 지표 동기
        // A34: 파일 리스트에 포커스가 있는 동안에는 모듈 버튼 핫키를 삼키지 않는다 —
        // 리스트의 타이핑 탐색(첫 글자 점프)이 우선(빈 모듈 탐색기·좌측 오버레이 공통).
        IconGrid.Tag = HotkeySupport.PassThroughTag;
        ListPane.Tag = HotkeySupport.PassThroughTag;
        // A94: 클립보드 키(Ctrl+C/X/V/A)는 이 표면에 포커스가 있을 때만 받는다 — KeyDown은
        // 포커스 요소에서만 버블링하므로 문서 에디터 등 텍스트 표면으로 샐 일이 없고,
        // A34 통과 규칙(단독 문자 키만 다루는 HotkeySupport)과도 겹치지 않는다(수정자 키 조합).
        // 컨트롤이 먼저 Handled를 걸어도 받게 handledEventsToo (ThumbnailExplorer Enter와 같은 관용구).
        IconGrid.AddHandler(UIElement.KeyDownEvent, new KeyEventHandler(OnSurfaceKeyDown), true);
        ListPane.AddHandler(UIElement.KeyDownEvent, new KeyEventHandler(OnSurfaceKeyDown), true);
        // A131: 원시 눌림 쌍 폴백 — 두 더블클릭 판정(ItemClick 쌍·DoubleTapped)은 둘 다 항목
        // 컨테이너 수명에 묶여 있어, 두 클릭 사이·클릭 도중에 목록 재구축(A94 5차 폴더 감시
        // 재스캔 등 — Fill이 항목을 전부 새로 만든다)이 끼면 눌림·뗌이 다른 요소가 되어 클릭이
        // 성립하지 않고(ItemClick 침묵) 새 컨테이너에는 제스처 상태가 없어 DoubleTapped도 뜨지
        // 않는다 — 열기 요청이 셸에 도달하지 못한 채 완전 침묵(압축 모듈 zip 무반응으로 관측).
        // 눌림은 요소 교체와 무관하게 매번 도착하므로 경로 키 판정이 재구축을 건너 살아남는다.
        // handledEventsToo = 순수 관찰(Handled 불변 — 선택·드래그·제스처 무간섭. ThumbnailExplorer와 동일 한 벌).
        IconGrid.AddHandler(UIElement.PointerPressedEvent,
            new PointerEventHandler(OnSurfacePointerPressed), handledEventsToo: true);
        ListPane.AddHandler(UIElement.PointerPressedEvent,
            new PointerEventHandler(OnSurfacePointerPressed), handledEventsToo: true);
        // A179: 체크는 선택과 분리된 작업 집합(_checkedPaths)이 단일 원본이다 — 종전 A157의
        // "선택 → 체크 동기"(SelectionChanged 거울)는 철거했다. 행 클릭 = 선택(하이라이트)만,
        // 체크 토글 = 체크박스 클릭(OnItemCheckClick) 또는 Space(OnSurfaceKeyDown)뿐이다.
        // 시각 규칙: 하이라이트 = 선택 / 체크 = 파일 조작 작업 집합(체크 0개면 선택이 대신한다).
        // A94 6차: 빈 영역(항목이 아닌 곳) 우클릭 메뉴 — New folder / Paste / Refresh.
        // 항목 메뉴와의 이중 발화는 ContextFlyout 규칙이 원천 차단한다: 컨텍스트 요청은 원본
        // 요소에서 위로 버블링하며 **가장 안쪽의 ContextFlyout 하나만** 떠서 요청을 소비하므로,
        // 항목 위 우클릭은 항목 컨테이너(AttachContextMenu)가 받고 여기까지 오지 않는다.
        // 빈 영역이 히트 테스트되도록 XAML에서 Background=Transparent를 준 것과 한 쌍이다.
        IconGrid.ContextFlyout = MakeSurfaceMenu(IconGrid);
        ListPane.ContextFlyout = MakeSurfaceMenu(ListPane);
        // A240: 선택 변경을 셸로 중계 — ThumbnailExplorer :206과 같은 얇은 래핑(선택 판정·해석은
        // 셸 몫 — SelectedEntry 질의). 두 표면 어느 쪽 발화든 한 이벤트로 모은다(현행 유일
        // 사용처는 리스트 전용 모드라 IconGrid는 접혀 항목이 안 만들어진다 — 실발화는 ListPane뿐).
        // A323: 열린 콘텐츠 표시용 프로그램 선택(SetCurrentFile)은 중계하지 않는다 — 그 선택은
        // **열림 축**의 표시일 뿐이라 셸의 **선택 축**(A200 _selectedBrowse)을 세우면 안 된다.
        IconGrid.SelectionChanged += (_, _) => { if (!_syncingCurrent) SelectionChanged?.Invoke(); };
        ListPane.SelectionChanged += (_, _) => { if (!_syncingCurrent) SelectionChanged?.Invoke(); };
        // A94 4차: 잘라내기 표시(전역 1벌)가 바뀌면 이미 그려 둔 항목의 흐림만 다시 맞춘다.
        // 구독을 Loaded/Unloaded로 묶는 이유 = 정적 이벤트가 닫힌 창의 컨트롤을 붙들지 않게
        // (Unloaded로 워커를 정리하는 아래 관용구와 같은 수명 규칙). 중복 구독은 -= 선행으로 막는다.
        // A94 5차: 폴더 감시(FileSystemWatcher)와 편집 종료 알림(EditEnded)도 같은 수명 규칙 —
        // Loaded에서 (재)시작·구독, Unloaded에서 해제. 좌 도크 "닫힘"은 Visibility 변경이라
        // Unloaded가 아니다 — 도크가 닫혀 있어도 감시는 살아 중앙 썸네일(ViewChanged)이 계속 갱신된다.
        Loaded += (_, _) =>
        {
            ExplorerFileOps.CutMarksChanged -= ApplyCutMarks;
            ExplorerFileOps.CutMarksChanged += ApplyCutMarks;
            ExplorerRenameBox.EditEnded -= OnRenameEditEnded;
            ExplorerRenameBox.EditEnded += OnRenameEditEnded;
            _surfaceLive = true;
            EnsureWatch(_folder); // 언로드 전에 항해한 폴더가 있으면(재오픈) 거기부터 다시 감시
        };
        Unloaded += (_, _) =>
        {
            ExplorerFileOps.CutMarksChanged -= ApplyCutMarks;
            ExplorerRenameBox.EditEnded -= OnRenameEditEnded;
            _surfaceLive = false;
            // A345 배치 2: 분할 조립 루프(CompositionTarget.Rendering)와 완료 신호가 사라져
            // 여기서 풀어 줄 것이 없다 — 리스트는 ItemsSource 대입 한 줄로 동기 완결이다.
            TearDownWatch(); // 감시 이벤트 전부 해제 + Dispose + 디바운스 정지 — 창 통째 누수 방지
            _worker?.Dispose(); // 진행 중 작업은 워커가 마저 끝내고 스레드 종료
            _worker = null;
            _fetchPool?.Dispose(); // A194 — 풀 전파 Dispose. 닫힌 뒤의 Run은 취소 Task(계약)라
            _fetchPool = null;     // 발사 루프의 OperationCanceledException 처리로 조용히 끝난다.
        };
    }

    /// <summary>
    /// 잘라내기(Ctrl+X) 표시 반영 (A94 4차): 표시 중인 항목의 콘텐츠 투명도를 경로 매칭으로
    /// 다시 맞춘다 — 재스캔이 아니라 제자리 갱신이라 선택·스크롤이 보존된다.
    /// <para>
    /// A345 배치 2: 리스트는 <b>뷰모델 목록</b>을 순회한다 — 화면 밖 항목도 값이 맞아 있어야
    /// 나중에 실체화될 때 옳게 그려진다(x:Bind OneWay가 ContentOpacity를 읽는다).
    /// IconGrid(휴면)는 종전대로 컨테이너 순회다 — 그쪽은 컨테이너가 값을 들고 있다.
    /// </para>
    /// </summary>
    private void ApplyCutMarks()
    {
        foreach (var item in IconGrid.Items) ExplorerFileOps.ApplyCutMark(item);
        foreach (var vm in _displayVms) ExplorerFileOps.ApplyCutMark(vm);
    }

    // ---------- 정렬 (A5 → A155 컬럼 헤더) ----------
    // A155: 종전 SortButton 드롭다운(4옵션·방향 토글 없음)을 리스트 위 컬럼 헤더로 대체.
    // 헤더 클릭 = 그 속성으로 정렬, 같은 헤더 재클릭 = 방향 토글(부록 B 69 ①). 필터(A7)는 존치.
    // A199: A155 때 함께 있던 표시 전용 Info 헤더(6번째 칸)는 제거됐다.
    // 헤더는 정렬 5종(Name·Type·Size·Created·Modified)만 남는다 — 정렬 키 자체는 불변.

    /// <summary>정렬 방향 지표 글리프 — 오름차순(ChevronUp). 활성 헤더에만 보인다.</summary>
    private const string SortAscGlyph = "\uE70E";

    /// <summary>정렬 방향 지표 글리프 — 내림차순(ChevronDown).</summary>
    private const string SortDescGlyph = "\uE70D";

    /// <summary>키별 방향 지표 아이콘 — SyncSortHeaders가 활성 헤더 하나만 켠다.</summary>
    private readonly Dictionary<ExplorerListing.SortKey, FontIcon> _sortHeaderArrows = new();

    /// <summary>
    /// 키의 기본(첫 클릭) 정렬 방향 (A155) — Arrange가 방향 인자화되기 전의 종류별 고정 방향과
    /// 같은 값이다(Size = 큰 것부터, Modified/Created = 최신부터, Name/Type = 오름차순).
    /// 저장값 없는 구 설정의 폴백(Settings 세터)과 헤더 전환 첫 클릭이 같은 표를 쓴다.
    /// </summary>
    private static bool DefaultDescending(ExplorerListing.SortKey key) =>
        key is ExplorerListing.SortKey.Size
            or ExplorerListing.SortKey.Modified
            or ExplorerListing.SortKey.Created;

    /// <summary>
    /// 리스트 컬럼 헤더 조립 (A155 → A199) — XAML의 빈 ListHeader 그리드에 정렬 5칸을 채운다
    /// (생성자에서 1회). 헤더는 정렬 버튼이 본체다: 값 표시는 A156의 2줄 행이 담당하므로
    /// 열 정렬(칸 맞춤)은 없고, 25% 사이드바 폭에 맞춰 라벨 + 잘림 허용(TextTrimming)으로 간다.
    /// A276(v0.273.0): 버튼 모양이 MainWindow.MakeMenuItem의 투명 평면 관용구(배경 투명·테두리 0)에서
    /// 하단 바 버튼 관용구(BottomBarButtonStyle — 기본 배경·테두리 1·CornerRadius 4)로 바뀌었다.
    /// 시각만 바뀌고 정렬 동작(A155 클릭·방향 토글·화살표 표시)은 전부 그대로다.
    /// A199: 표시 전용 Info 헤더(A155 — 부록 B 69 ④, 정렬 비대상·흐림 표시)는 제거됐다.
    /// 모듈별 속성 조각은 상세 줄(BuildDetailText)이 유일한 표시 자리다.
    /// </summary>
    private void BuildListHeader()
    {
        // A297: 5칸 전부 1* — 헤더 버튼 상자를 같은 크기로 맞춘다. 종전엔 Name만 2*라 Name 버튼
        // 하나만 폭이 2배였고, 그것이 "헤더 버튼 크기가 제각각"으로 보인 유일한 차이였다
        // (높이·상하 패딩·수직 정렬·글꼴은 AddSortHeader 한 곳이 만들어 A276 때부터 5칸이 이미 동일하다).
        // 이 칸 폭은 데이터 열과 맞물리지 않는다 — 리스트 행은 A156의 2줄이고 상세는 TextBlock 1개
        // (열 분할 없음)이라 헤더 칸은 정렬 버튼이 앉는 자리일 뿐이다. 균등화해도 정렬 표시·동작은
        // 그대로다. 부수 효과로 협폭 잘림(A276 ②)이 완화된다 — 가장 긴 라벨(Created·Modified)이
        // 종전의 절반 칸에서 균등 칸으로 넓어지고, 짧은 Name은 균등 칸으로도 넉넉하다.
        for (var i = 0; i < 5; i++)
            ListHeader.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        AddSortHeader(0, "Name", "Sort by name", ExplorerListing.SortKey.Name);
        AddSortHeader(1, "Type", "Sort by type", ExplorerListing.SortKey.Type);
        AddSortHeader(2, "Size", "Sort by size", ExplorerListing.SortKey.Size);
        AddSortHeader(3, "Created", "Sort by date created", ExplorerListing.SortKey.Created);
        AddSortHeader(4, "Modified", "Sort by date modified", ExplorerListing.SortKey.Modified);

        SyncSortHeaders();
    }

    /// <summary>
    /// 정렬 헤더 버튼 1개 — 라벨 + (활성일 때만) 방향 지표 아이콘.
    /// <b>A276</b>: 하단 바 버튼처럼 보이게 한다. 다만 BottomBarButtonStyle을 Style로 통째로 걸지
    /// 않고 <b>시각 속성만 개별 이식</b>한다 — 그 스타일은 Width 32·Height 32를 못박아 32×32
    /// 정사각을 강제하므로, 폭이 컬럼(2*:1*×4)을 따라야 하고 높이가 헤더 한 줄이어야 하는 여기서는
    /// 쓸 수 없다(스타일을 걸면 헤더 행이 32px로 부풀고 라벨 칸이 32px로 잘린다).
    /// 이식 대상 = 배경(기본 버튼 배경 — 종전의 투명 덮어쓰기를 걷어내 기본 스타일 값이 살아난다)·
    /// BorderThickness 1(테두리 색도 기본 스타일의 ButtonBorderBrush가 그린다 — 코드에서
    /// ThemeResource를 인덱서로 뒤지면 키 부재 시 던지므로 조회 자체를 만들지 않는다)·
    /// CornerRadius 4. 이식하지 않는 것 = Width·Height·Padding·HorizontalContentAlignment
    /// (좌정렬·Stretch·MinHeight 억제는 헤더 고유 규격이라 유지).
    /// 높이 영향 = 테두리 상하 1+1 = 2px 증가뿐이다(행은 Auto 높이라 그만큼만 늘어난다).
    /// 협폭 잘림 대응(A276 ②): 라벨은 <b>고정 전체 표기 유지</b>가 결론이다 — "Cr."/"Mod." 류
    /// 축약형은 폭을 몇 px 벌자고 뜻을 잃어 기각했고, 대신 화살표(8px)까지 포함해 좁아지면
    /// CharacterEllipsis로 잘리는 현행 + 아래 툴팁으로 전체 뜻이 늘 확인된다.
    /// <b>A297</b>: 헤더 버튼 5개의 크기 속성은 <b>전부 이 한 곳에서만</b> 정한다 — 버튼별로
    /// 값을 따로 주면 다시 제각각이 된다(호출부 AddSortHeader 5줄은 열 번호·라벨·툴팁·키만 다르다).
    /// 세로는 VerticalAlignment=Stretch로 못박아 헤더 행(자식 중 가장 큰 높이)을 5개가 똑같이
    /// 채우게 한다 — 정렬 화살표는 활성 칸에만 보이지만(SyncSortHeaders) 라벨(11px)보다 작은
    /// 8px이고, 설령 더 컸더라도 행 높이는 5개가 공유하므로 <b>화살표 유무로 높이가 갈리지 않는다</b>.
    /// </summary>
    private void AddSortHeader(int column, string label, string tooltip, ExplorerListing.SortKey key)
    {
        var content = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 2 };
        content.Children.Add(new TextBlock
        {
            Text = label,
            FontSize = 11,
            VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis,
        });
        var arrow = new FontIcon
        {
            Glyph = SortAscGlyph,
            FontSize = 8,
            VerticalAlignment = VerticalAlignment.Center,
            Visibility = Visibility.Collapsed,
        };
        content.Children.Add(arrow);
        _sortHeaderArrows[key] = arrow;

        var button = new Button
        {
            Content = content,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Stretch, // A297 — 5개가 헤더 행을 똑같이 채운다(기본값 명시)
            HorizontalContentAlignment = HorizontalAlignment.Left, // 좌정렬은 A155부터 5칸 모두 성립 — 무변경
            // A276: 배경은 종전의 투명 덮어쓰기를 지워 기본 버튼 스타일 값이 그대로 나오게 한다.
            BorderThickness = new Thickness(1), // A276 — BottomBarButtonStyle과 같은 두께
            CornerRadius = new CornerRadius(4), // A276 — BottomBarButtonStyle과 같은 반경
            Padding = new Thickness(4, 2, 4, 2),
            MinWidth = 0,
            MinHeight = 0, // 기본 MinHeight(32)가 헤더 한 줄 높이를 먹지 않게(A157 체크박스와 같은 이유)
        };
        // 라벨이 잘려도 전체 뜻 확인 가능(PathText 관용구). A276 ②: 축약 라벨을 쓰지 않는 대신
        // 이 툴팁이 전체 라벨을 포함한 문장("Sort by date created" 등)으로 잘림을 보완한다.
        ToolTipService.SetToolTip(button, tooltip);
        button.Click += (_, _) => OnSortHeaderClick(key);
        Grid.SetColumn(button, column);
        ListHeader.Children.Add(button);
    }

    /// <summary>헤더의 정렬 지표를 _sortKey·_sortDesc에 맞춘다 (종전 SyncSortChecks의 자리).</summary>
    private void SyncSortHeaders()
    {
        foreach (var (key, arrow) in _sortHeaderArrows)
        {
            var active = key == _sortKey;
            arrow.Visibility = active ? Visibility.Visible : Visibility.Collapsed;
            if (active) arrow.Glyph = _sortDesc ? SortDescGlyph : SortAscGlyph;
        }
    }

    /// <summary>
    /// 헤더 클릭 (A155): 같은 헤더 = 방향 토글, 다른 헤더 = 그 키로 전환(방향은 키의 기본값으로
    /// 리셋 — 탐색기 관례). 저장은 종전 OnSortChanged와 같은 즉시 Set + Save(전역 1벌) —
    /// A204에서도 저장은 <b>최종 기준 1개</b>(explorer.sort/sortDesc)뿐이고 정렬 이력은 저장하지
    /// 않는다(안정성은 세션 내 표시 순서 승계로만 성립 — 재시작·재스캔이면 리셋).
    /// 재그리기는 RefreshView(재스캔 없음) — ViewChanged로 중앙 썸네일까지 같은 순서가 흐르고,
    /// 트리 동기(A134 SyncTreeToFolder)는 같은 폴더 조기 반환으로 걸러진다(재통지 무해).
    /// A204: 입력은 _display(직전 Arrange 결과 = 현재 표시 순서) — Arrange가 stable이라
    /// "이름 오름 → 크기" 전환 시 같은 크기끼리 이름 오름 순서가 유지된다. 같은 키 재클릭
    /// (방향 토글)도 같은 경로 — stable이라 동률 내 직전 순서가 그대로다(별도 처리 없음).
    /// </summary>
    private void OnSortHeaderClick(ExplorerListing.SortKey key)
    {
        if (key == _sortKey)
        {
            _sortDesc = !_sortDesc;
        }
        else
        {
            _sortKey = key;
            _sortDesc = DefaultDescending(key);
            _settings?.Set(SortSettingKey, key.ToString().ToLowerInvariant());
        }
        _settings?.Set(SortDescSettingKey, _sortDesc);
        _settings?.Save();
        SyncSortHeaders();
        RefreshView(_display);
    }

    /// <summary>
    /// 입력 목록을 현재 정렬·필터로 재배치해 다시 그린다. 재스캔 없음.
    /// 항목이 새로 만들어지므로 썸네일도 다시 채운다(셸 썸네일 캐시라 재추출은 싸다).
    /// A204 — 입력 선택이 안정성의 전부다:
    /// · 정렬 키·방향 변경(OnSortHeaderClick) = _display(직전 표시 순서) → 직전 기준이 동률의
    ///   2차 순서로 승계(stable sort).
    /// · 재스캔(NavigateToAsync — 감시 디바운스 포함)·필터 토글(A7) = _entries(스캔 결과 = 이름순)
    ///   → 최종 키 1개만 적용 = 종전과 같은 화면. 감시 재통지마다 순서가 흔들리지 않도록
    ///   재스캔 경로는 절대 _display를 입력으로 쓰지 않는다(이전 화면 순서의 재정렬 금지).
    ///   필터 해제로 돌아오는 항목은 _display에 없으므로 필터 경로도 _entries가 맞다.
    /// </summary>
    private void RefreshView(IReadOnlyList<ExplorerListing.Entry> input)
    {
        var seq = ++_loadSeq; // 돌고 있던 길이·썸네일 루프 중단
        var arranged = ExplorerListing.Arrange(input, _sortKey, _sortDesc, _hiddenExts);
        _display = arranged; // A204 — 다음 정렬 변경의 입력(현재 표시 순서)
        // A345 배치 1: 데이터 축을 여기서 한 번만 만든다 — 항목 컨테이너는 전부 이 뷰모델을 Tag로 든다.
        _displayVms = arranged.Select(e => new ExplorerEntryVm(e)).ToList();
        _displayFolder = _folder; // A323 — 이 목록이 속한 폴더(셸 시드의 정합 판정용)
        Fill(arranged); // A345 배치 2 — 리스트는 ItemsSource 대입 한 줄(동기 완결)
        ViewChanged?.Invoke(_folder, arranged); // A93 — 중앙 썸네일 뷰가 같은 목록을 받아 그린다
        // A345 배치 2: 조립이 항상 동기로 끝나므로 마무리도 항상 여기서 한다(종전 분할 조립
        // 루프의 "마지막 틱이 부른다" 분기는 사라졌다). 순서는 종전 그대로 Fill → ViewChanged → 로더.
        FinishFill(arranged, seq);
    }

    // ---------- 파일 종류 필터 (A7) ----------

    private readonly HashSet<string> _hiddenExts = new(StringComparer.OrdinalIgnoreCase);
    private IReadOnlyList<string> _filterBuiltFor = []; // 마지막으로 플라이아웃을 만든 확장자 목록
    private Brush? _filterDefaultBrush;                 // 아이콘 원래 색 (활성 표시 해제용)
    private ToggleMenuFlyoutItem? _showHiddenItem;      // A160 — 같은 메뉴 안의 숨김 표시 토글(체크 동기용)

    /// <summary>
    /// 담당 확장자 목록으로 필터 플라이아웃을 만든다. 목록이 그대로면 재사용(체크 상태 유지),
    /// 모듈 전환 등으로 바뀌면 새로 만들고 필터를 초기화한다. 필터는 저장하지 않는다(세션 한정).
    /// A160(v0.169.0): 같은 메뉴 아래쪽에 구분선 + "숨김·시스템 표시" 토글이 붙는다. 그쪽은
    /// 세션 한정이 아니라 저장되는 설정(explorer.showHidden)이라 여기서 초기화하지 않고
    /// 현재 값으로 체크만 맞춘다. 메뉴 조립 지점은 이 함수 하나뿐이라(확장자 목록이 폴더·모듈마다
    /// 달라 재생성돼도 여기로만 온다) 구분선·토글이 빠진 채 다시 만들어질 경로가 없다.
    /// </summary>
    private void EnsureFilterFlyout()
    {
        if (_extensions.SequenceEqual(_filterBuiltFor)) return;
        _filterBuiltFor = _extensions;
        _hiddenExts.Clear();

        var flyout = new MenuFlyout { Placement = FlyoutPlacementMode.BottomEdgeAlignedRight };
        // "Show all"이 되돌릴 대상 목록 — A160의 숨김 토글은 여기 담지 않는다(아래 주석).
        var extensionToggles = new List<ToggleMenuFlyoutItem>();
        foreach (var ext in _extensions)
        {
            // A196: 전체 파일 필터(ExplorerListing.AllFiles — 설정·미지원 안내 화면)의 "*"는
            // 토글로 만들지 않는다 — 좁힐 확장자 목록 자체가 없고, 숨김 판정(Arrange의
            // hiddenExtensions)은 실제 확장자 문자열이라 "*" 토글은 아무 일도 못 하는 거짓 UI다.
            if (ext == "*") continue;
            var toggle = new ToggleMenuFlyoutItem { Text = ext, IsChecked = true };
            toggle.Click += (_, _) =>
            {
                if (toggle.IsChecked) _hiddenExts.Remove(ext);
                else _hiddenExts.Add(ext);
                UpdateFilterVisual();
                RefreshView(_entries); // A204 — 필터 변경은 스캔 결과 입력(안정성 리셋, 계약대로)
            };
            extensionToggles.Add(toggle);
            flyout.Items.Add(toggle);
        }
        flyout.Items.Add(new MenuFlyoutSeparator());
        var showAll = new MenuFlyoutItem { Text = "Show all" };
        showAll.Click += (_, _) =>
        {
            if (_hiddenExts.Count == 0) return;
            _hiddenExts.Clear();
            // A160: 확장자 토글만 되돌린다. 종전처럼 flyout.Items를 타입(ToggleMenuFlyoutItem)으로
            // 훑으면 아래 숨김 표시 토글까지 체크돼, 설정은 그대로인 채 메뉴만 거짓말을 한다.
            foreach (var t in extensionToggles) t.IsChecked = true;
            UpdateFilterVisual();
            RefreshView(_entries); // A204 — 위 확장자 토글과 같은 입력 선택(스캔 결과)
        };
        flyout.Items.Add(showAll);

        // A160: 확장자 필터(세션 한정)와 표시 정책(저장·전역 1벌)은 다른 층이라 구분선으로 나눈다.
        flyout.Items.Add(new MenuFlyoutSeparator());
        var showHidden = new ToggleMenuFlyoutItem
        {
            Text = "Show hidden and system files",
            // Settings 주입이 첫 NavigateTo(=이 메뉴의 첫 조립)보다 앞서므로 여기서 이미 실값이다.
            // 주입이 뒤로 밀리는 경로가 생겨도 Settings 세터의 SyncShowHiddenCheck가 다시 맞춘다.
            IsChecked = _showHidden,
        };
        showHidden.Click += (_, _) =>
        {
            // ToggleMenuFlyoutItem은 Click 시점에 IsChecked가 이미 뒤집혀 있다(위 확장자 토글이
            // 기대는 것과 같은 성질). 저장은 A5 정렬과 같은 관용구 — 즉시 Set + Save.
            _showHidden = showHidden.IsChecked;
            _settings?.Set(ShowHiddenSettingKey, _showHidden);
            _settings?.Save();
            // 좌 패널 폴더 트리는 자기 열거를 따로 한다 — 같은 설정으로 다시 만들라고 알린다.
            ShowHiddenChanged?.Invoke();
            // ⚠️ RefreshView(_entries)가 아니라 **재열거**여야 한다: 그건 마지막 스캔 결과를
            // 다시 배열할 뿐인데 숨김 항목은 애초에 그 목록에 없다(거르기가 열거 시점에 일어난다).
            // RefreshAfterFileOp = 현재 폴더 NavigateTo(재스캔) — 빈 영역 메뉴의 Refresh와 같은 경로다.
            RefreshAfterFileOp();
        };
        flyout.Items.Add(showHidden);
        _showHiddenItem = showHidden;

        FilterButton.Flyout = flyout;
        UpdateFilterVisual();
    }

    /// <summary>
    /// 필터 메뉴의 숨김 표시 토글 체크를 _showHidden에 맞춘다 (A160 — SyncSortHeaders와 같은 역할).
    /// 정렬 헤더는 생성자(BuildListHeader)에서 만들어져 늘 존재하지만 이 항목은 첫 항해
    /// (EnsureFilterFlyout) 때 만들어지므로,
    /// 그전에는 맞출 대상이 없다(null이면 조용히 넘어간다 — 만들어질 때 현재 값으로 초기화된다).
    /// </summary>
    private void SyncShowHiddenCheck()
    {
        if (_showHiddenItem is { } item) item.IsChecked = _showHidden;
    }

    /// <summary>필터가 걸려 있으면 아이콘을 강조색으로 — 걸린 걸 잊지 않게.</summary>
    private void UpdateFilterVisual()
    {
        _filterDefaultBrush ??= FilterIcon.Foreground;
        FilterIcon.Foreground = _hiddenExts.Count > 0 &&
            Application.Current.Resources["AccentTextFillColorPrimaryBrush"] is Brush accent
            ? accent
            : _filterDefaultBrush;
        ToolTipService.SetToolTip(FilterButton, _hiddenExts.Count > 0
            ? $"Filter file types ({_hiddenExts.Count} hidden)"
            : "Filter file types");
    }

    /// <summary>
    /// 조립 완료 뒤의 지연 로더 — A345 배치 2부터 <b>IconGrid 썸네일뿐</b>이다.
    /// 리스트 상세 조각(A6·A155)은 목록 전체를 도는 루프가 아니라 보이는 행마다 도는
    /// ContainerContentChanging(RequestDetail)이 요청한다 — 상한 없는 목록에서 fetch가
    /// 개수에 비례해 폭주하지 않게 하는 것이 가상화의 핵심 조건이다.
    /// </summary>
    private async Task LoadDetailsAsync(int seq)
    {
        await LoadThumbnailsAsync(seq);
    }

    /// <summary>좌 리스트 오버레이용: 썸네일 그리드를 숨기고 리스트만 남긴다.</summary>
    public void ConfigureListOnly()
    {
        GridColumn.Width = new GridLength(0);
        IconGrid.Visibility = Visibility.Collapsed;
        ListPane.BorderThickness = new Thickness(0);
    }

    /// <summary>
    /// 경로 바(위로 이동 + 경로 + 필터 + 정렬) 한 줄을 이 페인에서 떼어 돌려준다 (A91, v0.115.0).
    /// 오버레이(A91)가 이 줄을 자기 최상단으로 옮겨 붙이려고 떼어 간다 — 트리와 리스트 사이에
    /// 끼어 있던 줄을 패널 맨 위로 올리는 것이 요구다. 뗀 뒤 Grid.Row 0(Auto)은 자식이 없어
    /// 높이 0으로 접힌다 — RowDefinition은 그대로 둔다(전체 페인 사용처가 다시 생기면
    /// 이 줄이 제자리에 붙은 채 쓰여야 한다 — A93 이후 현재 사용처는 좌 오버레이뿐).
    /// 이미 떼어 간 뒤면 null을 돌려준다(멱등) — 같은 UIElement는 부모를 둘 가질 수 없으므로
    /// 호출이 반복돼도 두 번 붙지 않게 컬렉션 멤버십으로 직접 판정한다(FrameworkElement.Parent는
    /// 라이브 트리 부착 전에 null이라 가드로 못 쓴다 — HardwareView.EnsureCards 주석 참고).
    /// x:Name 필드(UpButton·PathText·FilterButton…)는 부모에서 떼어도 그대로
    /// 살아 있어 NavigateTo·EnsureFilterFlyout 등 기존 코드는 손댈 필요가 없다
    /// (ImageViewerView.TakeBottomBar와 같은 관용구).
    /// </summary>
    public UIElement? DetachPathBar()
    {
        if (!PaneRoot.Children.Contains(PathBar)) return null;
        PaneRoot.Children.Remove(PathBar);
        Grid.SetRow(PathBar, 0); // 새 부모의 0행 — 옛 부모 기준 행 번호가 따라가지 않게 명시
        return PathBar;
    }

    /// <summary>폴더로 이동해 내용을 채운다. 목록 스캔은 백그라운드, UI 채우기는 이어서.</summary>
    public void NavigateTo(string folder, IReadOnlyList<string> extensions)
    {
        // 항해 계측(diag.navTiming, 기본 꺼짐)의 합류점 — 세 진입 경로(트리 선택·리스트 활성화·
        // 중앙 썸네일 더블클릭)가 전부 이 메서드로 모인다. 출처만 각 진입점이 미리 적어 둔다
        // (NavDiagnostics.NoteSource). 꺼져 있으면 Begin이 앞단 게이트에서 즉시 반환한다.
        NavDiagnostics.Begin();
        _ = NavigateToAsync(folder, extensions); // 발사 후 망각 — 본문이 예외를 스스로 처리(종전 async void와 동일 소비)
    }

    /// <summary>
    /// NavigateTo의 대기 가능형 (A94 2차) — 새 폴더 생성 직후 "재스캔 '완료 후' 그 항목으로
    /// 이름변경 편집 진입"(CreateFolderThenRenameAsync)이 스캔 완료 시점을 알아야 해서 분리했다.
    /// </summary>
    private async Task NavigateToAsync(string folder, IReadOnlyList<string> extensions)
    {
        _extensions = extensions;
        EnsureFilterFlyout(); // A7 — 확장자 목록이 바뀌었으면 필터 재구성
        // A179: 폴더가 바뀌면 체크 집합을 비운다 — 다른 폴더의 체크가 보이지 않는 채 작업 집합에
        // 남으면 삭제·복사 대상이 화면 밖 항목이 된다(위험). 같은 폴더 재스캔(폴더 감시·조작 후
        // 갱신)은 여기 걸리지 않아 체크가 생존한다.
        var folderChanged = !string.Equals(folder, _folder, StringComparison.OrdinalIgnoreCase);
        if (folderChanged) _checkedPaths.Clear();
        _folder = folder;
        PathText.Text = folder;
        ToolTipService.SetToolTip(PathText, folder); // 잘려도 전체 경로 확인 가능(A8)
        UpButton.IsEnabled = Directory.GetParent(folder) is not null;
        EnsureWatch(folder); // A94 5차 — 폴더 전환 즉시 재대상(스캔 완료 전의 변경도 디바운스로 잡힌다)
        // 계측 pre: 여기까지가 "스캔 전 준비" 전부다(필터 재구성·부모 계산·감시자 재대상).
        // nav>pre가 크면 정지는 스캔이 아니라 이 준비 구간에 있다는 뜻이다.
        NavDiagnostics.Mark("pre");

        var seq = ++_loadSeq;
        // A243: 폴더 실변경이면 스캔 완료를 기다리지 않고 즉시 옛 폴더 화면을 지우고 로딩 문구를
        // 띄운다(대형·OneDrive 폴더에서 수 초 무반응으로 보이던 체감 해소 — 스캔 완료 시 Fill이
        // 문구·목록을 덮고, 실패 경로도 "Cannot read..."가 덮는다). 같은 폴더 재스캔(감시 400ms
        // 디바운스·조작 후 갱신)은 이 갈래에 안 들어와 종전대로 무Clear(깜빡임 방지)다.
        // Clear ~ Fill 사이의 소비자는 전부 빈 목록에 안전하다: 선택 소멸 발화는 A240의 null 선택
        // 규칙(닫힌 도크는 FileListOverlay가 차단), ApplyCutMarks·CheckedPathsInView·FindVmByPath는
        // 빈 순회(A345 배치 2부터 순회 대상이 _displayVms다), 낡은 로더는 위 seq 증가가 접는다.
        // 편집 진입 대기(A192 WhenFillCompleteAsync)는 조립이 동기가 되며 함께 사라졌다.
        if (folderChanged)
        {
            IconGrid.Items.Clear();
            ListPane.ItemsSource = null; // A345 배치 2 — ItemsSource 상태에서 Items.Clear는 즉시 예외다
            EmptyText.Text = "Loading...";
            EmptyText.Visibility = Visibility.Visible;
            // 계측 load: "Loading..." 대입 직후 = 사용자가 보게 될 화면 상태가 정해진 시점.
            // 중앙 썸네일 중계(아래 NavigationStarted)보다 앞에 둬야 좌/중앙 비용이 분리된다.
            NavDiagnostics.Mark("load");
            NavigationStarted?.Invoke(folder); // 셸이 중앙 썸네일에도 같은 로딩 화면을 중계(A93 경로)
        }
        else
        {
            // 계측 same: 같은 폴더 재스캔(감시 디바운스·조작 후 갱신) — Clear도 로딩 문구도 없다.
            // load 자리에 이 마크가 보이면 "화면을 지우지 않은 갱신"이라는 뜻이다(오해 방지).
            NavDiagnostics.Mark("same");
        }
        // A160: 표시 정책은 스캔 시작 시점에 스냅샷해 워커로 넘긴다 — 워커 스레드에서 UI 필드를
        // 읽지 않는다(스캔 도중 토글이 바뀌면 그 토글이 자기 재스캔을 다시 건다).
        var includeHidden = _showHidden;
        IReadOnlyList<ExplorerListing.Entry> entries;
        // 계측 yield: UI 스레드가 **메시지 루프로 실제로 돌아온** 시점을 잰다. await 뒤에서는
        // 잴 수 없다(그건 결과 도착이다) — await 직전에 큐에 넣어 두면 UI 스레드가 풀리는
        // 즉시 이 콜백이 먼저 돌고(같은 우선순위 FIFO, await 재개보다 앞서 큐에 든다),
        // load>yield가 크면 "await로 넘겼는데도 UI 스레드가 풀리지 않았다"가 확정된다.
        if (NavDiagnostics.Enabled)
        {
            var navSession = NavDiagnostics.Session; // 그새 다른 항해가 시작되면 이 마크는 버려진다
            DispatcherQueue.TryEnqueue(() => NavDiagnostics.MarkFor(navSession, "yield"));
        }
        try
        {
            entries = await Worker.Run(_ =>
                ExplorerListing.List(folder, extensions, includeHidden: includeHidden));
        }
        catch (OperationCanceledException)
        {
            return; // 페인이 내려가며 워커가 닫힘 — 그릴 곳도 없다
        }
        catch (Exception ex)
        {
            if (seq != _loadSeq) return;
            IconGrid.Items.Clear();
            ListPane.ItemsSource = null; // A345 배치 2 — 위 folderChanged 경로와 같은 규칙
            EmptyText.Text = "Cannot read this folder: " + ex.Message;
            EmptyText.Visibility = Visibility.Visible;
            ViewChanged?.Invoke(folder, []); // A93 — 썸네일 뷰도 옛 폴더 목록을 남기지 않는다
            // 계측 fail: 실패로 끝난 항해도 반드시 한 번은 조립돼 화면에 남는다
            // (렌더 프레임 마크까지 가지 못하는 유일한 정상 경로다).
            NavDiagnostics.Mark("fail");
            NavDiagnostics.ArmPaint("paint");
            return;
        }

        // 계측 scan: 폴더 열거 결과 도착(= 워커 왕복 완료). yield>scan이 크면 정지의 원인은
        // 워커 쪽 열거 시간이고, load>yield가 크면 UI 스레드가 애초에 풀리지 않은 것이다.
        NavDiagnostics.Mark("scan");

        if (seq != _loadSeq) return; // 그새 다른 폴더로 이동함

        _entries = entries;
        // A179: 체크 집합 정리 — 재스캔 결과에 없는 경로(삭제·이동·이름변경·숨김 전환으로 소실)는
        // 걷어낸다. 세션 한정 확장자 필터(A7)는 열거가 아니라 표시 단계(Arrange)라 여기 안 걸린다 —
        // 필터로 가려진 체크는 집합에 남고, 소비 시점의 "화면에 있는 것만" 교집합(CheckedPathsInView)이
        // 조작 대상에서 뺀다(WYSIWYG — 필터를 풀면 체크가 복원돼 보인다).
        if (_checkedPaths.Count > 0)
        {
            var alive = new HashSet<string>(entries.Select(e => e.Path), StringComparer.OrdinalIgnoreCase);
            _checkedPaths.RemoveWhere(p => !alive.Contains(p));
        }
        // A204: 재스캔(폴더 감시 디바운스 포함)은 스캔 결과(이름순)에 최종 키 1개만 적용 —
        // 감시 재통지마다 화면이 흔들리지 않는다. 직전 표시 순서(_display)는 정렬 클릭 전용.
        RefreshView(_entries);
    }

    /// <summary>
    /// 표시 목록을 채운다 — A345 배치 2부터 두 표면의 방식이 다르다.
    /// 좌 리스트(ListPane)는 <b>뷰모델 목록을 ItemsSource로 대입</b>하고, 화면에 보이는 행만
    /// 컨테이너가 만들어진다(DataTemplate + ContainerContentChanging 가상화 — 실체화 상한 없음).
    /// 좌 그리드(IconGrid)는 휴면 표면이라 종전대로 컨테이너를 직접 만들어 붙인다
    /// (상한 MaterializeLimit 유지 · 리스트 전용 모드에서는 접혀 있어 한 개도 만들지 않는다).
    /// <para>
    /// 사라진 것: 분할 조립 루프(A192 — CompositionTarget.Rendering 틱당 한 조각)·완료 신호
    /// (_fillDone)·상한 초과 안내 행(MakeOverflowNotice). ItemsSource 대입은 동기 1회라
    /// 기다릴 것이 없고, 안내 행은 ItemsSource 상태에서 Items.Add가 즉시 예외라 성립하지 않는다.
    /// </para>
    /// A179 유의: 체크(작업 집합)는 경로 키 집합(_checkedPaths)이 진실이라 이 재작성(폴더 감시
    /// 400ms 재스캔 포함)이 돌아도 생존한다 — 시각 복원은 뷰모델을 만드는 RefreshView가 하고,
    /// x:Bind가 그것을 읽는다. **선택**(하이라이트)은 여전히 재작성과 함께 사라진다
    /// (선택 복원은 별도 설계가 필요해 범위 밖 — 등재 후보 유지).
    /// </summary>
    private void Fill(IReadOnlyList<ExplorerListing.Entry> entries)
    {
        IconGrid.Items.Clear();
        ListPane.ItemsSource = null; // 옛 목록 해제(같은 참조 재대입이 무시되는 일도 함께 막는다)
        // 계측 clr: 옛 목록 해제 비용을 새 목록 대입과 분리해 본다(둘 다 UI 스레드 동기다).
        NavDiagnostics.Mark("clr");

        // RefreshView가 방금 만든 뷰모델 목록 — entries와 같은 순서·같은 개수다.
        ListPane.ItemsSource = _displayVms;
        // 계측 fill0: 리스트 대입 완료. 실제 컨테이너 생성은 레이아웃이 "보이는 행만" 하므로
        // 여기부터 fillN까지는 종전처럼 개수에 비례하지 않는다(가상화의 판별식).
        NavDiagnostics.Mark("fill0");

        if (IconGrid.Visibility == Visibility.Visible) // 휴면 경로 — 종전 컨테이너 조립 그대로
        {
            var cap = Math.Min(entries.Count, MaterializeLimit);
            for (var i = 0; i < cap; i++) IconGrid.Items.Add(MakeGridItem(_displayVms[i]));
        }

        EmptyText.Text = "No matching files here";
        EmptyText.Visibility = entries.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>
    /// 조립 완료의 단일 마무리: ① 열린 콘텐츠 표시(A323) 재적용, ② 썸네일 로더 기동,
    /// ③ 조립 완료 통지(A241 — 셸의 EXIF 프리페치 훅).
    /// <para>
    /// A345 배치 2에서 빠진 것 = 상한 초과 안내 행 부착과 완료 신호 해방. 로더를 "조립 뒤"에
    /// 두는 근거도 이제 <b>IconGrid 썸네일 한정</b>이다: LoadThumbnailsAsync가 기동 시점에
    /// IconGrid.Items를 스냅샷해 순회하므로 타일이 다 붙은 뒤여야 한다. 리스트 상세 조각은
    /// 스냅샷 순회를 그만두고 보이는 행마다(RequestDetail) 도는 구조라 이 순서와 무관하다.
    /// </para>
    /// 낡은 완료(폐기된 회차)는 seq 대조로 걸러진다.
    /// </summary>
    private void FinishFill(IReadOnlyList<ExplorerListing.Entry> entries, int seq)
    {
        if (seq != _loadSeq) return; // 방어 — 낡은 완료가 로더를 기동하지 않게
        // 계측 fillN: 목록 반영이 끝난 시점(로더 기동 직전). 뒤이어 무장하는 paint는
        // 이 목록이 실제 화면 프레임에 올라간 시점을 잰다.
        NavDiagnostics.Mark("fillN");
        // A323: 목록 재작성은 선택을 지운다 — 열린 콘텐츠 표시를 여기서 다시 건다.
        // 셸의 선택 축은 바로 앞 ViewChanged가 이미 null로 리셋했으므로(MainWindow 생성자 배선)
        // 이 재적용이 사용자 선택을 덮는 일이 없다 — 두 축의 순서 계약(A200)이 그대로 성립한다.
        ApplyCurrentFileSelection();
        // 계측 paint: 조립 완료 후 첫 렌더 프레임 — 로더 기동보다 앞에 무장해 둔다(무장 자체는
        // 이벤트 구독 한 줄이라 순서가 비용에 영향을 주지 않는다).
        NavDiagnostics.ArmPaint("paint");
        _ = LoadDetailsAsync(seq);
        // A241: 조립 완료 훅 — 셸이 우측 정보 패널의 EXIF 프리페치를 여기서 기동한다(뼈대 우선).
        // 감시 재스캔의 재통지는 소비 쪽 캐시(경로+수정시각)가 흡수한다 — 여기서 거르지 않는다.
        FillCompleted?.Invoke(entries);
    }

    // ---------- 항목 해석의 단일 지점 (A345 배치 2) ----------

    /// <summary>
    /// 어떤 "항목 객체"에서든 뷰모델을 꺼낸다 — <b>이 배치의 최대 함정을 막는 단일 깔때기</b>다.
    /// 두 표면의 항목 표현이 서로 다르기 때문이다:
    /// ListPane은 ItemsSource라 SelectedItem·ClickedItem·SelectedItems가 <b>뷰모델 자체</b>이고
    /// 컨테이너(ListViewItem)의 Content도 뷰모델이다. IconGrid(휴면)는 종전대로 컨테이너를 직접
    /// 담으므로 <b>Tag</b>가 뷰모델이다. 한 곳이라도 옛 Tag 패턴으로 남으면 Enter·F2·Del·
    /// Ctrl+C/X·드래그·체크·정보 패널이 <b>예외 없이</b> 죽는다(컴파일도 통과한다) — 그래서
    /// 항목 해석은 전부 이 한 곳을 지난다.
    /// </summary>
    private static ExplorerEntryVm? VmOf(object? o) => o switch
    {
        ExplorerEntryVm vm => vm,
        SelectorItem { Content: ExplorerEntryVm vm } => vm,
        FrameworkElement { Tag: ExplorerEntryVm vm } => vm,
        _ => null,
    };

    /// <summary>
    /// 리스트 컨테이너 준비 (A345 배치 2 — ListPane 전용). 가상화의 계약이 여기 다 모여 있다:
    /// <list type="bullet">
    /// <item>재활용 큐로 들어가는 컨테이너는 <b>편집 상자를 강제 커밋</b>하고 드랍을 끈다 —
    /// 편집 중 스크롤로 상자가 다른 파일 행으로 옮겨 가는 것이 이 배치의 데이터 사고다.</item>
    /// <item>훅(컨텍스트 메뉴·드래그·더블탭)은 컨테이너당 <b>1회만</b> 붙이고, 핸들러 안에서는
    /// 항목을 캡처하지 않고 <see cref="VmOf"/>로 <b>그때그때 다시 푼다</b> — 캡처하면 재활용된
    /// 컨테이너가 옛 파일을 조작한다.</item>
    /// <item>AllowDrop은 <b>매번</b> 다시 정한다 — 폴더였던 컨테이너가 파일 행으로 재활용되면
    /// 잔존한 AllowDrop이 파일을 드랍 대상으로 만든다.</item>
    /// </list>
    /// 표시값(이름·상세·툴팁·체크·잘라내기 흐림)은 x:Bind가 새 항목 값으로 다시 평가하므로
    /// 여기서 손대지 않는다(그것이 뷰모델 축을 만든 이유다 — 배치 1).
    /// </summary>
    private void OnListContainerContentChanging(
        ListViewBase sender, ContainerContentChangingEventArgs args)
    {
        if (args.ItemContainer is not ListViewItem item) return;
        if (args.InRecycleQueue)
        {
            // 인라인 이름변경 상자가 이 컨테이너에 남아 있으면 커밋으로 끝낸다(보수안 ⓐ의 짝).
            if (item.ContentTemplateRoot is Panel host) ExplorerRenameBox.ForceFinish(host);
            item.AllowDrop = false; // 잔존 방지 — 다음 항목이 파일이어도 드랍을 받지 않게
            return;
        }
        if (args.Item is not ExplorerEntryVm vm) return;
        EnsureListItemHooks(item);    // 컨테이너당 1회
        item.AllowDrop = vm.IsFolder; // 매 재사용마다 재설정(폴더만 드랍 대상 — A94)
        if (args.Phase == 0) RequestDetail(vm, _loadSeq); // 보이는 행만 상세 조각 요청
    }

    /// <summary>
    /// 리스트 컨테이너에 계약 훅을 1회만 건다 (A345 배치 2). "이미 붙였는가"의 표지는
    /// <see cref="UIElement.ContextFlyout"/> 유무다 — 아래에서 반드시 하나를 걸기 때문에
    /// 별도 플래그(첨부 속성·사전)를 만들지 않아도 되는 가장 값싼 판정이다.
    /// 훅은 전부 <b>지연 해석</b>이다(핸들러 안에서 VmOf로 다시 푼다) — 컨테이너는 재활용돼도
    /// 훅은 남기 때문에, 여기서 vm이나 entry를 캡처하면 옛 파일을 조작하게 된다.
    /// </summary>
    private void EnsureListItemHooks(ListViewItem item)
    {
        if (item.ContextFlyout is not null) return; // 이미 부착됨
        AttachContextMenu(item, ListPane); // A24 + A94 2차(Rename·Delete) — A335 Opening 재구성
        AttachDragDrop(item, ListPane);    // A94 — 드래그 아웃 + 폴더 항목 드랍
        item.IsDoubleTapEnabled = true;    // A85 — 압축 모듈 내부 리스트(ArchiveView)와 같은 명시
        item.DoubleTapped += OnItemDoubleTapped; // A85 — 더블클릭 열기의 기본 경로
    }

    /// <summary>
    /// 항목 우클릭 메뉴 (A94 2차 신설 → 6차 확장). 순서는 탐색기 관례 근사:
    /// 파일 = "Open in new instance"(A24) → 구분선 → Cut·Copy → 구분선 → Rename·Delete,
    /// 폴더 = Cut·Copy·**Paste(대상 = 그 폴더)** → 구분선 → Rename·Delete.
    /// Delete·Cut·Copy 대상은 드래그와 같은 규칙 — 그 항목이 작업 집합(A179: 체크 우선,
    /// 체크 0개면 선택)에 포함돼 있으면 집합 전부, 아니면 그 항목 하나(PathsForDrag 재사용).
    /// Rename은 플라이아웃이 닫히며 포커스를 되돌린 '뒤'에 진입해야 편집 상자가 곧장 LostFocus
    /// 커밋으로 닫혀 버리지 않는다 — 디스패처로 한 박자 미룬다(BeginRenameOf).
    /// </summary>
    /// <remarks>
    /// A335: 항목이 만들어질 때는 <b>빈 MenuFlyout 하나만</b> 달고, 내용은 <b>열릴 때</b> 채운다
    /// (Opening 재구성 — 오디오 하단 바 플라이아웃 3종·이 파일의 표면 메뉴와 같은 관용구).
    /// 종전에는 항목마다 메뉴를 통째로 조립했다: 항목 1개당 MenuFlyoutItem 4~5개 + FontIcon
    /// 같은 수 + 구분선까지 열 개 남짓의 XAML 객체다. 파일 10,000개 폴더에서는 그것이 10만 개가
    /// 되고, A334 계측판이 그 구간을 <c>clay&gt;fillN 8,539ms</c>로 찍었다(항목당 비용이 개수에
    /// 그대로 비례하는 유일한 큰 항). 실제로 열리는 메뉴는 사람이 우클릭한 하나뿐이다.
    /// 첫 우클릭에서 메뉴를 놓치지 않는 근거: ContextFlyout은 <b>Opening을 먼저 발화시키고</b>
    /// 그 결과를 띄우므로, 그 시점에 채우면 같은 우클릭에서 그대로 보인다(위 3종 선례와 동일).
    /// </remarks>
    /// <remarks>
    /// A345 배치 2: 대상 항목을 인자로 받지 않는다 — <b>열리는 순간</b>에 VmOf로 다시 푼다.
    /// 가상화 뒤에는 이 컨테이너가 다른 파일로 재활용되므로, 부착 시점의 entry를 캡처하면
    /// 옛 파일이 Cut·Delete 대상이 된다(재활용 잔존 사고 중 가장 위험한 갈래).
    /// 그새 항목이 풀리지 않으면(재활용 중 등) 빈 메뉴로 연다 — 조작 대상이 없는 것이 옳다.
    /// </remarks>
    private void AttachContextMenu(SelectorItem item, ListViewBase owner)
    {
        var flyout = new MenuFlyout();
        flyout.Opening += (_, _) =>
        {
            if (VmOf(item) is { } vm) BuildItemContextMenu(flyout, item, vm, owner);
            else flyout.Items.Clear();
        };
        item.ContextFlyout = flyout;
    }

    /// <summary>
    /// A335: 항목 메뉴의 실제 내용 — 열릴 때마다 새로 채운다(항목 구성·순서·활성 조건·다중 선택
    /// 대상 규칙은 종전 그대로. 옮긴 것은 <b>시점</b>뿐이다). 매번 비우고 다시 만들므로 두 번째
    /// 우클릭에 항목이 겹쳐 쌓이지 않는다.
    /// </summary>
    /// <remarks>
    /// A345 배치 2: 대상은 <b>열린 순간에 푼 뷰모델</b>이다(vm). Rename만은 디스패처로 한 박자
    /// 미루므로 그 사이 컨테이너가 재활용될 수 있어, 실행 직전에 같은 뷰모델인지 다시 대조한다.
    /// </remarks>
    private void BuildItemContextMenu(
        MenuFlyout flyout, SelectorItem item, ExplorerEntryVm vm, ListViewBase owner)
    {
        var entry = vm.Entry;
        flyout.Items.Clear();
        if (!entry.IsFolder) AddOpenInNewInstance(flyout, entry);
        AddClipboardItems(flyout, entry, owner); // A94 6차 — Cut·Copy·(폴더면 Paste) + 구분선
        var rename = new MenuFlyoutItem
        {
            Text = "Rename",
            Icon = new FontIcon { Glyph = "\uE8AC" }, // Rename
        };
        rename.Click += (_, _) => DispatcherQueue.TryEnqueue(() =>
        {
            // 지연 사이에 컨테이너가 재활용됐으면 다른 파일의 이름을 고치게 된다 — 무동작이 옳다.
            if (ReferenceEquals(VmOf(item), vm)) BeginRenameOf(item);
        });
        flyout.Items.Add(rename);
        var delete = new MenuFlyoutItem
        {
            Text = "Delete",
            Icon = new FontIcon { Glyph = "\uE74D" }, // Delete
        };
        delete.Click += async (_, _) => await DeleteWithNoticeAsync(PathsForDrag(owner, entry));
        flyout.Items.Add(delete);
    }

    /// <summary>파일 전용 "Open in new instance"(A24)를 메뉴 맨 위에 + 구분선 (A94 2차 재배치).</summary>
    private void AddOpenInNewInstance(MenuFlyout flyout, ExplorerListing.Entry entry)
    {
        var open = new MenuFlyoutItem
        {
            Text = "Open in new instance", // A53 문구
            Icon = new FontIcon { Glyph = "\uE8A7" }, // OpenInNewWindow
        };
        open.Click += (_, _) => FileActivatedNewWindow?.Invoke(entry.Path);
        flyout.Items.Add(open);
        flyout.Items.Add(new MenuFlyoutSeparator());
    }

    /// <summary>
    /// 항목 메뉴의 클립보드 묶음 (A94 6차): Cut · Copy · (폴더면) Paste + 뒤따르는 구분선.
    /// 조작 자체는 Ctrl+C/X/V와 **완전히 같은 경로**다(CopyWithNoticeAsync·PasteIntoAsync) —
    /// 폴더 Paste만 대상이 현재 폴더가 아니라 그 폴더다(PasteFromClipboardAsync가 이미 대상
    /// 폴더를 인자로 받으므로 넓힐 것이 없었다). Paste 활성 판정은 메뉴가 열릴 때 한다.
    /// </summary>
    private void AddClipboardItems(MenuFlyout flyout, ExplorerListing.Entry entry, ListViewBase owner)
    {
        var cutItem = new MenuFlyoutItem
        {
            Text = "Cut",
            Icon = new FontIcon { Glyph = "\uE8C6" }, // Cut
        };
        cutItem.Click += async (_, _) => await CopyWithNoticeAsync(PathsForDrag(owner, entry), cut: true);
        flyout.Items.Add(cutItem);

        var copyItem = new MenuFlyoutItem
        {
            Text = "Copy",
            Icon = new FontIcon { Glyph = "\uE8C8" }, // Copy
        };
        copyItem.Click += async (_, _) => await CopyWithNoticeAsync(PathsForDrag(owner, entry), cut: false);
        flyout.Items.Add(copyItem);

        if (entry.IsFolder)
        {
            var pasteItem = new MenuFlyoutItem
            {
                Text = "Paste",
                Icon = new FontIcon { Glyph = "\uE77F" }, // Paste
            };
            pasteItem.Click += async (_, _) => await PasteIntoAsync(entry.Path);
            // A335: 종전에는 여기서 flyout.Opening을 하나 더 구독해 활성을 정했다. 이제 이 조립
            // 자체가 Opening 안에서 돌므로 지금 판정이 곧 "열리는 순간의 판정"이고, 구독을 남기면
            // 열 때마다 핸들러가 쌓인다(이미 버려진 항목을 만지는 죽은 구독이 된다).
            pasteItem.IsEnabled = ExplorerFileOps.CanPasteFromClipboard();
            flyout.Items.Add(pasteItem);
        }
        flyout.Items.Add(new MenuFlyoutSeparator());
    }

    /// <summary>
    /// 빈 영역 우클릭 메뉴 (A94 6차 → A189에서 New file 추가): New folder / New file / Paste /
    /// Refresh — 전부 기존 경로 재사용이다(Ctrl+Shift+N의 CreateFolderThenRenameAsync와 그 파일
    /// 판본 CreateFileThenRenameAsync = 생성 후 이름 편집 진입까지 · 현재 폴더
    /// 붙여넣기 · 조작 후 재스캔 RefreshAfterFileOp). 표면(그리드·리스트)마다 한 벌씩 만든다 —
    /// 새 폴더 편집 진입이 자기 owner의 컨테이너를 찾아야 하기 때문.
    /// 활성 판정은 메뉴가 열릴 때: 아직 항해 전이면(폴더 미정) 전부 비활성, Paste는 클립보드에
    /// 파일 항목이 있을 때만(판정 실패 시 활성 — CanPasteFromClipboard 주석).
    /// </summary>
    private MenuFlyout MakeSurfaceMenu(ListViewBase owner)
    {
        var newFolder = new MenuFlyoutItem
        {
            Text = "New folder",
            Icon = new FontIcon { Glyph = "\uE8F4" }, // NewFolder
        };
        // 편집 진입 전에 재스캔 await가 한 번 끼므로 플라이아웃은 그때 이미 닫혀 있다
        // (Rename처럼 디스패처로 미룰 필요가 없다 — 순서는 CreateFolderThenRenameAsync가 보장).
        newFolder.Click += async (_, _) => await CreateFolderThenRenameAsync(owner);

        // A189: New file — New folder 옆, 같은 흐름(생성 후 이름변경 편집 진입)의 파일 판본.
        var newFile = new MenuFlyoutItem
        {
            Text = "New file",
            Icon = new FontIcon { Glyph = "\uE7C3" }, // 문서(파일) — 항목 타일과 같은 글리프
        };
        newFile.Click += async (_, _) => await CreateFileThenRenameAsync(owner);

        var paste = new MenuFlyoutItem
        {
            Text = "Paste",
            Icon = new FontIcon { Glyph = "\uE77F" }, // Paste
        };
        paste.Click += async (_, _) => await PasteIntoAsync(_folder);

        var refresh = new MenuFlyoutItem
        {
            Text = "Refresh",
            Icon = new FontIcon { Glyph = "\uE72C" }, // Refresh
        };
        refresh.Click += (_, _) => RefreshAfterFileOp();

        var flyout = new MenuFlyout();
        flyout.Items.Add(newFolder);
        flyout.Items.Add(paste);
        flyout.Items.Add(refresh);
        flyout.Opening += (_, _) =>
        {
            var ready = _folder.Length > 0;
            newFolder.IsEnabled = ready;
            paste.IsEnabled = ready && ExplorerFileOps.CanPasteFromClipboard();
            refresh.IsEnabled = ready;
        };
        return flyout;
    }

    /// <summary>그리드 타일: 썸네일 자리(우선 글리프, 이후 비동기 교체) + 이름 2줄.</summary>
    private GridViewItem MakeGridItem(ExplorerEntryVm vm) // A345 배치 1 — 입력이 뷰모델
    {
        var entry = vm.Entry;
        var icon = new FontIcon
        {
            Glyph = entry.IsFolder ? "\uE8B7" : "\uE7C3", // 폴더 / 문서
            FontSize = 40,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        };
        var thumbHost = new Grid { Width = 72, Height = 72 };
        thumbHost.Children.Add(icon);

        var name = new TextBlock
        {
            // A156: 이름변경 진입(BeginRenameOf)은 두 표면 공용이라 조회 키도 공용이다 —
            // 리스트 행 구조가 바뀌며 인덱스 계약을 버렸으므로 타일 쪽도 같은 이름을 붙여 둔다.
            // (타일 레이아웃 자체는 이 배치의 범위 밖 — 이름 부여 한 줄만 더한다.)
            Name = ItemNameBlockName,
            Text = entry.Name,
            FontSize = 12,
            TextWrapping = TextWrapping.Wrap,
            MaxLines = 2,
            TextTrimming = TextTrimming.CharacterEllipsis,
            TextAlignment = TextAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Center,
        };

        var panel = new StackPanel { Width = 96, Spacing = 4, Padding = new Thickness(4) };
        panel.Children.Add(thumbHost);
        panel.Children.Add(name);
        ToolTipService.SetToolTip(panel, entry.Name);

        var item = new GridViewItem { Content = panel, Tag = vm }; // A345 배치 1 — Tag = 뷰모델
        ExplorerFileOps.ApplyCutMark(item); // A94 4차 — 잘라내기 중인 경로면 처음부터 반투명
        // A345 배치 2: 두 훅은 대상을 인자로 받지 않고 VmOf로 그때그때 푼다 — 여기(그리드)에서는
        // 컨테이너 Tag가, 리스트에서는 컨테이너 Content가 뷰모델이라 같은 함수가 양쪽을 덮는다.
        AttachContextMenu(item, IconGrid); // A24 + A94 2차(Rename·Delete)
        AttachDragDrop(item, IconGrid); // A94 — 드래그 아웃 + 폴더 항목 드랍
        item.IsDoubleTapEnabled = true; // A85 — 압축 모듈 내부 리스트(ArchiveView)와 같은 명시
        item.DoubleTapped += OnItemDoubleTapped; // A85 — 더블클릭 열기의 기본 경로
        return item;
    }

    // ---------- 항목 자식 조회: 이름 기반 (A156, v0.168.0) ----------
    // 종전에는 콘텐츠 패널의 Children[n] 인덱스가 계약이었다(이름 = [1], 길이 = [2]). A156이
    // 리스트 행을 1행 4열에서 2행 3열로 바꾸면서 그 인덱스가 전부 밀렸고, 인덱스 계약은 어긋나도
    // 예외 없이 조용한 return이라(길이 미표시·F2 무반응) 컴파일에도 정적 검사에도 걸리지 않았다.
    // 그래서 조회 키를 코드에서 지정한 Name으로 옮긴다 — 조회 지점이 아래 두 헬퍼로 모여 있어
    // 항목 구조를 또 바꿔도 고칠 곳이 한 군데다.

    /// <summary>이름 TextBlock의 조회 키 (A156) — 이름변경 진입(BeginRenameOf)이 이것으로 찾는다.
    /// 리스트는 XAML DataTemplate의 x:Name, 그리드(휴면)는 MakeGridItem이 같은 이름을 붙인다
    /// (A345 배치 2 — 값이 어긋나면 F2·Rename이 예외 없이 무반응이 된다).</summary>
    private const string ItemNameBlockName = "ExplorerItemName";

    /// <summary>2줄째 상세 TextBlock의 이름 (A156) — 값은 이제 x:Bind가 채운다(A345 배치 2).
    /// 상수는 XAML의 x:Name과 짝을 이루는 정본 표기로 남긴다.</summary>
    private const string ItemDetailBlockName = "ExplorerItemDetail";

    /// <summary>체크박스의 이름 (A157 → A179) — 체크 시각도 x:Bind가 맡는다(A345 배치 2).
    /// 상수는 XAML의 x:Name과 짝을 이루는 정본 표기로 남긴다.</summary>
    private const string ItemCheckBoxName = "ExplorerItemCheck";

    /// <summary>
    /// 항목 콘텐츠 패널에서 이름으로 TextBlock을 찾는다 (A156).
    /// 항목 루트는 평평한 패널 하나(중첩 없음)라 한 레벨 탐색으로 충분하다 — 시각 트리 상향
    /// 탐색(ItemFromSource)과 달리 여기는 우리가 만든 구조만 본다.
    /// <para>
    /// A345 배치 2: 콘텐츠 패널을 얻는 길이 표면마다 다르다 — 리스트는 DataTemplate이 만든
    /// <see cref="ContentControl.ContentTemplateRoot"/>이고, 그리드(휴면)는 코드가 직접 넣은
    /// Content다. 앞쪽 갈래를 빠뜨리면 이름변경(F2·Rename)이 <b>예외 없이</b> 무반응이 된다.
    /// </para>
    /// </summary>
    private static TextBlock? FindItemBlock(object item, string name) =>
        ContentPanelOf(item)?.Children.OfType<TextBlock>().FirstOrDefault(t => t.Name == name);

    /// <summary>항목의 콘텐츠 패널 (A345 배치 2) — 리스트 = 템플릿 루트, 그리드 = 직접 넣은 Content.</summary>
    private static Panel? ContentPanelOf(object item) => item switch
    {
        ContentControl { ContentTemplateRoot: Panel templated } => templated,
        ContentControl { Content: Panel direct } => direct,
        _ => null,
    };

    /// <summary>
    /// 지연 로드로 채우는 상세 조각 한 벌 (A6 길이 → A155 확장 → A199 모듈별 1조각으로 정리).
    /// Info = 상세 줄의 속성 조각 — 종류마다 정확히 하나다(영상·오디오 재생시간 "1:23:45" ·
    /// 이미지 해상도 "1920x1080" · PDF 페이지 "12 pages" · 텍스트 인코딩 "UTF-8" · zip 압축률 "42%").
    /// A199에서 영상 해상도가 탈락하며 종전 Duration 필드(재생 길이 전용)가 Info로 합쳐졌다.
    /// InfoTip = 툴팁용 라벨 포함 한 줄("Length: …"·"Resolution: …" 등 — 라벨 선택이 종류마다
    /// 달라 조립 시점이 아니라 취득 시점(FetchDetailInfo)에 확정한다).
    /// Entry에 싣지 않는 이유(A156 결정 승계): 폴더 스캔이 동기 열거라 진입이 느려진다.
    /// </summary>
    private sealed record DetailInfo(string Info, string InfoTip)
    {
        public static readonly DetailInfo Empty = new(string.Empty, string.Empty);
    }

    /// <summary>
    /// 리스트 행 2줄째 텍스트 (A156 → A199). 순서 확정: 크기 · [속성(모듈별 1조각)] · Created · Modified.
    /// 구분자는 저장소 관용구 "  ·  "(ImageViewerView.BuildMetaText와 같은 조립)이고,
    /// 빈 조각은 건너뛴다 = 구분자만 남는 "  ·    ·  " 모양이 생기지 않는다.
    /// 폴더는 크기 조각을 넣지 않는다(종전 리스트 행의 규칙 승계).
    /// 날짜는 둘 다 yy-MM-dd 절대 표기 (A199 — A180이 Modified에 썼던 순수 상대 표기("3d"류
    /// 전용 헬퍼 포함)를 원복·삭제: 상대 표기는 시간이 지나면 낡는데 갱신 트리거가 재스캔·지연
    /// 로드 도착뿐이라 오차가 쌓였다. 절대 표기는 낡지 않아 그 문제 자체가 소멸).
    /// 정확한 날짜·시각은 툴팁(BuildTooltipText) 몫.
    /// 문화권 인자 없이 쓰는 것은 저장소 표시 관용구 그대로다(ImageViewerView·ArchiveView 동일).
    /// 크기·날짜는 빈 문자열이 될 수 없어(FormatSize는 최소 "0 B") 조각 가드가 필요 없고,
    /// 속성 조각만 비어 올 수 있어 그것만 가드한다.
    /// </summary>
    private static string BuildDetailText(ExplorerListing.Entry entry, DetailInfo details)
    {
        var parts = new List<string>();
        if (!entry.IsFolder) parts.Add(ExplorerListing.FormatSize(entry.Size));
        if (details.Info.Length > 0) parts.Add(details.Info); // A155 모듈별 속성 — A199 1조각 확정
        parts.Add(entry.Created.ToString("yy-MM-dd"));
        parts.Add(entry.Modified.ToString("yy-MM-dd"));
        return string.Join("  ·  ", parts);
    }

    /// <summary>
    /// 리스트 행 툴팁 (A156): 파일명 + 라벨 붙은 상세를 줄 단위로 쌓는다.
    /// 2줄 레이아웃의 상세 줄에는 라벨이 없어 Created와 Modified를 눈으로 구분할 수 없고,
    /// 상세 줄 날짜는 yy-MM-dd로 축약된다 — 정확한 날짜·시각의 정본이 이 툴팁이다
    /// (yyyy-MM-dd HH:mm — 인포 오버레이·플레이어 정보 행과 같은 저장소 관용구.
    /// 조각 선택 규칙은 BuildDetailText와 같다 — A199에서 영상 해상도가 상세 줄에서 빠지며
    /// 이 툴팁에서도 함께 빠졌다).
    /// 모듈별 속성(A155)의 라벨은 InfoTip이 이미 담고 있어 그대로 한 줄을 얹는다.
    /// </summary>
    private static string BuildTooltipText(ExplorerListing.Entry entry, DetailInfo details)
    {
        var lines = new List<string> { entry.Name };
        if (!entry.IsFolder) lines.Add("Size: " + ExplorerListing.FormatSize(entry.Size));
        if (details.InfoTip.Length > 0) lines.Add(details.InfoTip); // A155 → A199 1조각
        lines.Add("Created: " + entry.Created.ToString("yyyy-MM-dd HH:mm"));
        lines.Add("Modified: " + entry.Modified.ToString("yyyy-MM-dd HH:mm"));
        return string.Join("\n", lines);
    }

    /// <summary>
    /// 상세 줄과 툴팁을 한 벌로 (다시) 채운다 (A156) — 초판(크기·날짜)과 지연 로드 도착분이
    /// 같은 조립을 쓰게 하는 단일 깔때기. 호출부는 전부 RequestDetail 안에 있다(A345 배치 2).
    /// </summary>
    /// <remarks>
    /// A345 배치 2: 대입 대상은 <b>뷰모델뿐</b>이다 — 컨테이너 직접 대입은 사라졌다.
    /// 화면 반영은 DataTemplate의 x:Bind(DetailText·TooltipText, Mode=OneWay)가 맡으므로
    /// 화면 밖 항목에 적용해도 값이 보존되고, 나중에 실체화될 때 옳게 그려진다.
    /// </remarks>
    private static void ApplyDetail(ExplorerEntryVm vm, DetailInfo details)
    {
        vm.DetailText = BuildDetailText(vm.Entry, details);
        vm.TooltipText = BuildTooltipText(vm.Entry, details);
    }

    /// <summary>상세 조각 캐시(A6 → A155 확장): 경로→(수정시각, 조각 한 벌). 수정시각이 다르면 무효.</summary>
    private readonly Dictionary<string, (DateTime Modified, DetailInfo Details)> _infoCache =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>지연 로드할 상세 조각의 종류 (A155) — 확장자로 결정한다(InfoKindOf).</summary>
    private enum InfoKind
    {
        None,
        Video, // 재생시간만 (A199 — 종전 "길이 + 해상도"에서 해상도 탈락)
        Audio, // 재생시간 (A6 종전 동작)
        Image, // 해상도 (A180 — A155 확정 때 누락된 원 서술 "해상도(이미지 기준)"의 수리)
        Pdf,   // 페이지 수 — 문서 모듈 확장자 중 PDF만(인코딩 개념이 없어 A199에서도 유지 확정)
        Zip,   // 압축률 — 압축 모듈 확장자 중 zip만(그 외 포맷은 헤더 읽기가 값싸지 않아 생략)
        Text,  // 인코딩 (A199 신규) — 문서 모듈의 비PDF 텍스트. ReadTextSmart 판정 규칙 재사용
    }

    /// <summary>
    /// 어떤 상세 조각을 물어볼 파일인지 (A155 — 종전 IsMediaFile의 확장).
    /// 미디어 판정은 비디오·오디오 모듈 담당 확장자 기준(A6, A10 분리 반영).
    /// </summary>
    private static InfoKind InfoKindOf(string name) =>
        ExplorerListing.MatchesExtension(name, KOTU.Module.Video.VideoModule.Extensions) ? InfoKind.Video
        : ExplorerListing.MatchesExtension(name, KOTU.Module.Audio.AudioModule.Extensions) ? InfoKind.Audio
        // A180: 이미지 판정도 담당 모듈 목록 재사용(ThumbnailExplorer의 A93 관용구와 동일 소스).
        : ExplorerListing.MatchesExtension(
              name, KOTU.Module.Image.ImageFolderNavigator.SupportedExtensions) ? InfoKind.Image
        : string.Equals(System.IO.Path.GetExtension(name), ".pdf", StringComparison.OrdinalIgnoreCase)
            ? InfoKind.Pdf
        : string.Equals(System.IO.Path.GetExtension(name), ".zip", StringComparison.OrdinalIgnoreCase)
            ? InfoKind.Zip
        // A199: 문서 모듈 담당 목록 재사용 — .pdf도 이 목록에 있지만 위 Pdf 갈래가 먼저 잡으므로
        // 여기 도달하는 것은 비PDF 텍스트(txt·md·markdown·log·ini·html·htm — A224 추가분 자동 추종)뿐이다.
        : ExplorerListing.MatchesExtension(name, KOTU.Module.Document.DocumentModule.Extensions)
            ? InfoKind.Text
        : InfoKind.None;

    /// <summary>
    /// 행 하나의 상세 조각(재생시간·해상도·페이지 수·압축률·인코딩 — A6 → A155 → A199) 요청.
    /// A345 배치 2: 호출부는 <b>보이는 행마다 도는 ContainerContentChanging</b> 하나뿐이다 —
    /// 종전 LoadDetailInfoAsync는 목록 전체를 스냅샷해 돌았고, 실체화 상한이 사라진 지금
    /// 그 구조를 두면 10,000개 폴더에서 fetch가 개수에 비례해 폭주한다(가상화의 필수 짝).
    /// <list type="number">
    /// <item>초판(크기·날짜)을 먼저 채운다 — 상세 줄이 빈 채로 실체화되면 행 높이가 나중에
    /// 늘어 스크롤이 튄다(종전 MakeListItem의 초판 적용과 같은 역할).</item>
    /// <item>폴더·클라우드 전용(A175)·대상 아닌 확장자는 여기서 끝. 이미 요청한 행도 끝
    /// (재활용 때마다 같은 행이 다시 들어오므로 DetailRequested 표지가 중복을 막는다).</item>
    /// <item>캐시 히트(경로 + 수정시각 일치)는 워커 없이 즉시 반영 — 같은 폴더 재진입이 값싸다.
    /// 종전의 조각내기(A342 배치 3의 DetailHitChunk·YieldToUiAsync)는 필요가 없어졌다:
    /// 수천 건이 한 덩어리로 도는 일 자체가 사라졌다(보이는 행만 온다).</item>
    /// <item>그 밖은 풀(A194 — 워커 3)로 fetch. 동시 발사 상한은 페인 수명 1벌의 게이트다.</item>
    /// </list>
    /// <b>async void인 이유</b>: 이벤트(CCC)에서 직접 부르는 발사 후 망각이라 기다릴 주체가
    /// 없다 — 대신 본문 전체를 try/catch로 감싸 예외가 UI 스레드로 새지 않게 한다.
    /// _infoCache와 뷰모델 대입은 종전대로 UI 스레드 단독이다(워커 람다는 순수 fetch뿐).
    /// </summary>
    private async void RequestDetail(ExplorerEntryVm vm, int seq)
    {
        try
        {
            if (vm.DetailText.Length == 0) ApplyDetail(vm, DetailInfo.Empty); // 초판 — 행 높이 확보
            if (vm.DetailRequested || vm.IsFolder || vm.IsPlaceholder) return; // A175 — 하이드레이션 유발 금지
            var kind = InfoKindOf(vm.Name);
            if (kind == InfoKind.None) return;
            vm.DetailRequested = true;

            if (_infoCache.TryGetValue(vm.Path, out var hit) && hit.Modified == vm.Entry.Modified)
            {
                if (hit.Details.Info.Length > 0) ApplyDetail(vm, hit.Details);
                return; // 캐시 히트는 워커 없이 즉시 반영(종전 동작)
            }

            await _detailGate.WaitAsync(); // UI 문맥 await — 후속부는 UI 스레드로 복귀
            try
            {
                if (seq != _loadSeq) return; // 폴더 전환 — 낡은 요청 폐기
                DetailInfo details;
                try
                {
                    details = await FetchPool.Run(_ => FetchDetailInfo(kind, vm.Path));
                }
                catch (OperationCanceledException)
                {
                    return; // 페인이 내려가며 풀이 닫힘
                }
                catch
                {
                    return; // 속성·헤더를 못 읽는 파일은 빈 칸 유지
                }
                if (seq != _loadSeq) return; // 폴더 전환 — 낡은 결과 폐기
                if (_infoCache.Count > 4000) _infoCache.Clear(); // 장시간 세션 폭주 방지
                _infoCache[vm.Path] = (vm.Entry.Modified, details); // 캐시 키는 종전대로 경로
                if (details.Info.Length > 0) ApplyDetail(vm, details);
            }
            finally
            {
                _detailGate.Release(); // 예외·취소 경로 포함 — 누락되면 3건 뒤 조용히 멈춘다
            }
        }
        catch
        {
            // 발사 후 망각이라 삼킬 곳이 여기뿐이다 — 한 행의 상세 실패가 목록을 깨면 안 된다.
        }
    }

    /// <summary>
    /// 워커 스레드: 종류별 상세 조각 취득 (A155). 표시 문자열까지 여기서 확정한다 —
    /// 캐시가 완성형을 담아 재그리기에서 재조립이 없다. 실패는 호출부 catch가 빈 칸으로 삼킨다.
    /// </summary>
    private static DetailInfo FetchDetailInfo(InfoKind kind, string path)
    {
        switch (kind)
        {
            // A199: 영상도 재생시간만 — 해상도 조각은 탈락했다(오디오와 같은 갈래로 합류.
            // 상세 줄·툴팁이 조각 선택 규칙을 공유하므로 양쪽에서 함께 빠진다 — BuildTooltipText 계약).
            case InfoKind.Video:
            case InfoKind.Audio:
            {
                var ticks = FetchDurationTicks(path);
                var duration = ticks > 0
                    ? ExplorerListing.FormatDuration(TimeSpan.FromTicks(ticks))
                    : string.Empty;
                // 속성 없음(손상·비표준 컨테이너)이나 1초 미만(FormatDuration이 빈 문자열)은
                // 조각 생략 — InfoTip까지 비워야 툴팁에 빈 "Length:" 줄이 남지 않는다.
                if (duration.Length == 0) return DetailInfo.Empty;
                return new DetailInfo(duration, "Length: " + duration);
            }
            case InfoKind.Image:
            {
                // A180: 이미지 해상도 — 표기는 "1920x1080".
                // 속성이 없는 파일(손상·비지원 코덱)은 조각 없이 빈 벌로 남는다.
                var (width, height) = FetchImageSize(path);
                if (width <= 0 || height <= 0) return DetailInfo.Empty;
                var res = $"{width}x{height}";
                return new DetailInfo(res, "Resolution: " + res);
            }
            case InfoKind.Pdf:
            {
                // 문서를 실제로 여는 비용(암호 PDF는 예외 → 빈 칸)이지만 워커 + 캐시라 수용 —
                // PdfPane.LoadDocumentAsync와 같은 API를 동기 대기(FetchThumbnail 관용구)로 쓴다.
                var file = StorageFile.GetFileFromPathAsync(path).AsTask().GetAwaiter().GetResult();
                var doc = Windows.Data.Pdf.PdfDocument.LoadFromFileAsync(file)
                    .AsTask().GetAwaiter().GetResult();
                if (doc.PageCount == 0) return DetailInfo.Empty;
                var pages = doc.PageCount == 1 ? "1 page" : $"{doc.PageCount} pages";
                return new DetailInfo(pages, "Pages: " + doc.PageCount);
            }
            case InfoKind.Zip:
            {
                // 압축률 = 파일 크기 ÷ 원본 합(중앙 디렉터리만 읽는다 — 해제 없음). zip 한정.
                var percent = KOTU.Module.Archive.ArchiveQuickInfo.TryGetZipCompressionPercent(path);
                if (percent < 0) return DetailInfo.Empty;
                return new DetailInfo(percent + "%", "Compression ratio: " + percent + "%");
            }
            case InfoKind.Text:
            {
                // A199: 텍스트 인코딩 — ReadTextSmart(DocumentView)의 판정 규칙을 앞부분 상한
                // 읽기로 재사용한다(DocumentQuickInfo — 전체 읽기를 워커 직렬 큐에 싣지 않는다).
                // 판정 불가·실패(잠김·빈 파일 등)는 null = 조각 생략.
                var encoding = KOTU.Module.Document.DocumentQuickInfo.TryGetEncodingName(path);
                if (encoding is null) return DetailInfo.Empty;
                return new DetailInfo(encoding, "Encoding: " + encoding);
            }
            default:
                return DetailInfo.Empty;
        }
    }

    /// <summary>워커 스레드: 셸 미디어 속성 — 재생 길이(100ns 단위 = TimeSpan 틱)를 읽는다.
    /// 없으면 0. (A6 원형 → A155에서 비디오 해상도를 얹어 FetchMediaProperties로 확장했다가
    /// A199에서 해상도 조각이 탈락하며 길이 단일 조회로 복귀.)</summary>
    private static long FetchDurationTicks(string path)
    {
        var file = StorageFile.GetFileFromPathAsync(path).AsTask().GetAwaiter().GetResult();
        var props = file.Properties.RetrievePropertiesAsync(["System.Media.Duration"])
            .AsTask().GetAwaiter().GetResult();
        return props.TryGetValue("System.Media.Duration", out var d) && d is ulong u ? (long)u : 0L;
    }

    /// <summary>워커 스레드: 이미지 픽셀 치수 (A180 — FetchDurationTicks와 같은 셸 속성 관용구,
    /// 키만 이미지용 System.Image.*다. ImageViewerView의 System.Image.BitDepth 조회와 같은 계열). 없으면 0.</summary>
    private static (int Width, int Height) FetchImageSize(string path)
    {
        var file = StorageFile.GetFileFromPathAsync(path).AsTask().GetAwaiter().GetResult();
        var props = file.Properties.RetrievePropertiesAsync(
                ["System.Image.HorizontalSize", "System.Image.VerticalSize"])
            .AsTask().GetAwaiter().GetResult();
        var width = props.TryGetValue("System.Image.HorizontalSize", out var w) && w is uint uw ? (int)uw : 0;
        var height = props.TryGetValue("System.Image.VerticalSize", out var h) && h is uint uh ? (int)uh : 0;
        return (width, height);
    }

    /// <summary>
    /// 파일 썸네일을 채운다(그리드 타일의 글리프를 이미지로 교체).
    /// 추출(셸 API 호출·스트림 읽기)은 워커에서 하고 UI 스레드는 비트맵 표시만 한다(A42).
    /// 항목마다 느릴 수 있으므로 상한을 두고, 폴더 이동 시 중단한다.
    /// A194: 추출은 상세 조각과 같은 풀(FetchPool)·같은 발사 구조(SemaphoreSlim 게이트,
    /// RequestDetail 주석 참고)로 동시 3건까지 겹친다. loaded 카운터는 발사 전 검사와
    /// 반영 직전 검사 양쪽에서 보므로 상한(300)을 넘겨 반영되지 않고, 증감·검사 전부
    /// UI 스레드에서만 일어난다(경쟁 없음 — 워커 람다는 순수 fetch뿐).
    /// </summary>
    private async Task LoadThumbnailsAsync(int seq)
    {
        var loaded = 0; // 성공 수 — UI 스레드에서만 읽고 쓴다
        // 스냅샷 순회: await 중 NavigateTo가 Items를 비우면 라이브 컬렉션 순회는 깨진다.
        var items = IconGrid.Items.ToList();
        using var gate = new SemaphoreSlim(FetchConcurrency); // 동시 발사 상한 (A194)
        var running = new List<Task>();
        var stop = false; // 풀이 닫힘(취소 Task) — 남은 발사 중단. UI 스레드에서만 읽고 쓴다.

        // 타일 하나의 추출 + 교체. UI 스레드에서 시작하므로 await 후속부도 UI 스레드다.
        async Task FetchIntoAsync(GridViewItem item, ExplorerEntryVm vm)
        {
            try
            {
                var png = await FetchPool.Run(_ => FetchThumbnail(vm.Path, vm.IsPlaceholder));
                if (seq != _loadSeq || loaded >= ThumbnailLimit) return; // 낡은 결과·상한 도달 폐기
                if (png is null) return;

                var bitmap = new BitmapImage();
                using (var stream = new MemoryStream(png))
                    await bitmap.SetSourceAsync(stream.AsRandomAccessStream());
                if (seq != _loadSeq || loaded >= ThumbnailLimit) return;

                var host = (Grid)((StackPanel)item.Content).Children[0];
                host.Children.Clear();
                host.Children.Add(new Image
                {
                    Source = bitmap,
                    Stretch = Microsoft.UI.Xaml.Media.Stretch.Uniform,
                });
                loaded++;
            }
            catch (OperationCanceledException)
            {
                stop = true; // 페인이 내려가며 풀이 닫힘 — 발사 루프도 멈춘다
            }
            catch
            {
                // 썸네일 실패는 글리프 유지로 충분하다.
            }
            finally
            {
                gate.Release(); // 예외·취소 경로 포함 — 누락되면 3건 뒤 조용히 멈춘다
            }
        }

        foreach (var obj in items)
        {
            if (stop || seq != _loadSeq || loaded >= ThumbnailLimit) break;
            if (obj is not GridViewItem { Tag: ExplorerEntryVm { IsFolder: false } vm } item) continue;

            await gate.WaitAsync(); // UI 문맥 await — 후속부는 UI 스레드로 복귀
            if (stop || seq != _loadSeq || loaded >= ThumbnailLimit)
            {
                gate.Release(); // 획득만 하고 발사하지 않는 경로 — 누수 방지
                break;
            }
            running.Add(FetchIntoAsync(item, vm));
        }
        // 발사분 완주 대기 — using gate의 Dispose가 대기 중 Release보다 앞서지 않게 한다.
        await Task.WhenAll(running);
    }

    /// <summary>
    /// 워커 스레드: 셸 썸네일을 PNG/JPG 바이트로 추출한다. 없으면 null.
    /// StorageFile API는 agile이라 워커에서 불러도 되고, WinRT 비동기는 여기서 동기 대기한다
    /// (전용 스레드라 UI 교착 없음).
    /// cachedOnly(A175) = 클라우드 전용(placeholder) 파일 — 캐시·클라우드 제공 썸네일만 요청
    /// (ReturnOnlyIfCached). 옵션 없는 호출은 캐시가 비면 시스템이 원본을 열어 생성하므로
    /// placeholder에서는 하이드레이션(전체 다운로드)이 된다. 캐시에 없으면 null → 글리프 유지.
    /// 일반 파일은 종전과 같은 2인자 호출을 유지한다(회귀 방지).
    /// </summary>
    private static byte[]? FetchThumbnail(string path, bool cachedOnly)
    {
        var file = StorageFile.GetFileFromPathAsync(path).AsTask().GetAwaiter().GetResult();
        using var thumb = (cachedOnly
                ? file.GetThumbnailAsync(ThumbnailMode.SingleItem, 96, ThumbnailOptions.ReturnOnlyIfCached)
                : file.GetThumbnailAsync(ThumbnailMode.SingleItem, 96))
            .AsTask().GetAwaiter().GetResult();
        if (thumb is null || thumb.Size == 0) return null;

        using var stream = thumb.AsStreamForRead();
        using var buffer = new MemoryStream((int)thumb.Size);
        stream.CopyTo(buffer);
        return buffer.ToArray();
    }

    // ---------- 입력 ----------

    /// <summary>
    /// 선택된 **파일** 항목의 경로 — 폴더·무선택이면 null (A86: 셸 Enter "선택 파일 있으면 열기" 판정).
    /// 그리드·리스트 중 선택이 있는 쪽을 쓴다. A94(Extended)부터 다중 선택이 가능하지만
    /// 열기·인포류 단일 대상 동작은 종전대로 첫 선택 항목(SelectedItem) 기준을 유지한다.
    /// A179: 단일 대상 판정은 체크와 무관하게 **선택** 기준 그대로다 — 체크(작업 집합)는
    /// 다중 조작(드래그·복사/잘라내기/삭제·다중 열기)에만 관여한다(확정 규칙의 적용 범위).
    /// </summary>
    internal string? SelectedFilePath =>
        PathOfSelection(IconGrid.SelectedItem) ?? PathOfSelection(ListPane.SelectedItem);

    private static string? PathOfSelection(object? item) =>
        VmOf(item) is { IsFolder: false } vm ? vm.Path : null; // A345 배치 2 — 표면별 표현 차이는 VmOf가 흡수

    /// <summary>
    /// 선택된 항목(파일·폴더 불문) — 없으면 null (A240: 셸 선택 축 질의. ThumbnailExplorer.
    /// SelectedEntry와 같은 계약 — 폴더/무선택의 해석(= 선택 축 null)은 셸 몫이다).
    /// A345 배치 2: ListPane의 SelectedItem은 이제 <b>뷰모델 객체 자체</b>이고 IconGrid(휴면)는
    /// 종전대로 컨테이너다 — 둘의 차이는 VmOf가 흡수한다. 이 API의 반환은 종전대로 Entry다.
    /// </summary>
    internal ExplorerListing.Entry? SelectedEntry =>
        EntryOfSelection(IconGrid.SelectedItem) ?? EntryOfSelection(ListPane.SelectedItem);

    private static ExplorerListing.Entry? EntryOfSelection(object? item) => VmOf(item)?.Entry;

    // ---------- 열린 콘텐츠 표시 (A323) ----------

    /// <summary>
    /// A323: 셸이 알려 준 "지금 열려 있는 콘텐츠 파일" — 목록 재작성(Fill이 선택을 지운다) 후
    /// 다시 걸어야 하므로 필드로 기억한다. null = 표시할 열린 콘텐츠 없음.
    /// </summary>
    private string? _currentFile;

    /// <summary>
    /// A323: 위 표시용 선택 대입이 도는 동안 <see cref="SelectionChanged"/> 중계를 억제하는 표지.
    /// 배선 지점 = 생성자의 두 SelectionChanged 람다(그 주석에 근거).
    /// </summary>
    private bool _syncingCurrent;

    /// <summary>
    /// A323: 열린 콘텐츠 파일을 리스트에 **선택 표시**로 보여준다(사용자 요구 — 클릭했을 때와
    /// 같은 테두리. 새 시각 요소를 만들지 않고 기존 선택 표시 기구를 그대로 쓴다).
    /// 목록 밖(다른 폴더·A7 필터 밖·실체화 상한 초과)이면 지금 선택을 그대로 둔다 —
    /// 지우면 사용자가 방금 고른 항목의 표시가 사라진다(FindItemByPath 미매칭 = 무동작 관례).
    /// 선택 축과의 분리: 이 대입은 SelectionChanged를 발화시키지 않으므로(_syncingCurrent)
    /// 셸의 선택 축(A200 _selectedBrowse — 우측 정보 패널의 "선택 우선")은 서지 않는다.
    /// 우측 정보는 종전대로 열린 콘텐츠(provider) 기준을 유지한다.
    /// </summary>
    internal void SetCurrentFile(string? path)
    {
        _currentFile = path;
        ApplyCurrentFileSelection();
    }

    /// <summary>
    /// A336: 선택 표시를 <b>열린 콘텐츠 표시(A323) 상태로 되돌린다</b> — 셸이 다른 표면(중앙
    /// 썸네일·S4 그리드)에서 선택이 일어났을 때 부른다. 선택 축은 하나뿐이라(A200) 표시도 한
    /// 표면에만 남아야 한다(사용자 확정).
    /// <para>
    /// 중앙 표면의 <c>ClearSelection</c>과 달리 <b>그냥 비울 수 없다</b>: 이 목록의 선택 표시는
    /// A323에서 "지금 열려 있는 파일"을 가리키는 표시로도 쓰인다(새 시각 요소를 만들지 않고 선택
    /// 기구를 재사용한 것이 그 사양). 그래서 일단 비우고, 열린 콘텐츠가 있으면 그 항목을 다시
    /// 건다 — 결과는 "사용자 선택은 사라지고 열린 파일 표시는 남는다"다.
    /// </para>
    /// 되먹임 차단은 두 겹이다: 여기서 <see cref="_syncingCurrent"/>로 중계를 막고(A323의 표지를
    /// 그대로 재사용 — 목적이 같다: 표시용 대입은 선택 축을 건드리지 않는다), 셸도 자기 표지로
    /// 같은 구간을 막는다(MainWindow._syncingBrowseSelection).
    /// </summary>
    internal void RevertSelectionToCurrentFile()
    {
        _syncingCurrent = true;
        try
        {
            IconGrid.SelectedItems.Clear();
            ListPane.SelectedItems.Clear();
        }
        finally
        {
            _syncingCurrent = false; // 예외가 나도 표지가 남으면 이후 사용자 선택이 통째로 침묵한다
        }
        ApplyCurrentFileSelection(); // 열린 콘텐츠가 있으면 그 표시만 되살린다(없으면 무동작 = 빈 선택)
    }

    /// <summary>
    /// A323: 기억해 둔 열린 콘텐츠 경로를 지금 목록에 반영한다. 호출부 = <see cref="SetCurrentFile"/>
    /// (셸 통지 시점) + <see cref="FinishFill"/>(목록 재작성 후 — 폴더가 바뀐 경우에도 새 목록에서
    /// 표시가 맞게). 이미 그 항목이 선택돼 있으면 무동작이라 연속 항해(키 반복)에서도
    /// 스크롤이 되풀이되지 않는다.
    /// </summary>
    private void ApplyCurrentFileSelection()
    {
        if (_currentFile is not { Length: > 0 } path) return;
        _syncingCurrent = true;
        try
        {
            SelectCurrentIn(IconGrid, path);
            SelectCurrentIn(ListPane, path);
        }
        finally
        {
            _syncingCurrent = false; // 예외가 나도 표지가 남으면 이후 사용자 선택이 통째로 침묵한다
        }
    }

    /// <summary>
    /// A323: 한 표면에서 경로에 해당하는 항목을 선택하고 보이게 스크롤한다 —
    /// 대입·스크롤 관용구는 CreateFolderThenRenameAsync와 같은 한 벌(SelectedItem + ScrollIntoView).
    /// A345 배치 2: 리스트는 <b>뷰모델</b>을 선택 대상으로 대입한다(컨테이너를 찾지 않는다 —
    /// 화면 밖 항목이면 컨테이너가 아예 없다. ScrollIntoView가 실체화까지 맡는다).
    /// 그리드(휴면)는 종전 컨테이너 경로 그대로다. 리스트 전용 모드에서는 IconGrid에 항목이
    /// 없어 그쪽이 자연 무동작이다.
    /// </summary>
    private void SelectCurrentIn(ListViewBase owner, string path)
    {
        object? target = ReferenceEquals(owner, ListPane)
            ? FindVmByPath(path)
            : FindItemByPath(owner, path);
        if (target is null) return;
        if (ReferenceEquals(owner.SelectedItem, target)) return; // 이미 그 항목 — 스크롤도 되풀이하지 않는다
        owner.SelectedItem = target;
        owner.ScrollIntoView(target);
    }

    // ---------- 다중 선택 일괄 열기 (A94 6차, v0.153.0) ----------

    /// <summary>
    /// 셸(MainWindow)의 Enter 분배가 부르는 일괄 열기 (A94 6차) — 종전 "SelectedFilePath 하나를
    /// OpenFileRouted"를 대체한다. 표면 자체 Enter·더블클릭과 **같은 규칙**(아래 OpenFiles):
    /// 선택된 파일만(폴더 제외), 첫 파일은 재사용 규칙(A24) 경로, 나머지는 새 인스턴스.
    /// 반환 = 하나라도 열었는지(false면 셸이 종전 폴백 — 오버레이 토글 등으로 간다).
    /// A179: 체크가 있으면 체크된 파일이 우선(작업 집합 규칙 — 전부 폴더면 빈 목록 = false 폴백).
    /// 체크가 없으면 종전대로 그리드·리스트 중 파일 선택이 있는 쪽(SelectedFilePath와 같은 우선순위).
    /// </summary>
    internal bool OpenSelectedFiles()
    {
        if (CheckedPathsInView().Count > 0) return OpenFiles(CheckedPathsInView(filesOnly: true));
        var files = SelectedFilePathsOf(IconGrid);
        if (files.Count == 0) files = SelectedFilePathsOf(ListPane);
        return OpenFiles(files);
    }

    /// <summary>
    /// 일괄 열기 실행 (A94 6차): 상한(10) 적용 뒤 **첫 파일 = 기존 단일 열기 경로**
    /// (newWindowFirst면 Shift+더블클릭과 같은 새 인스턴스, 아니면 재사용 규칙 A24를 셸이 적용),
    /// **나머지 = 전부 새 인스턴스**. 창 생성은 셸의 기존 이벤트(FileActivated·
    /// FileActivatedNewWindow)로만 나가므로 이 컨트롤은 창 규칙을 알지 않는다.
    /// 루프를 동기로 도는 근거: 창 생성·파일 열기는 단일 UI 스레드에서 동기 완결이다
    /// (A124 복원 루프가 같은 형태 — WindowManager.TryRestoreSession). 중간에 대화상자가 떠도
    /// 그건 그 창의 XamlRoot 몫이라 창당 동시 1개 규칙(A113)을 깨지 않는다.
    /// 반환 = 하나라도 열었는지.
    /// </summary>
    private bool OpenFiles(IReadOnlyList<string> files, bool newWindowFirst = false)
    {
        if (files.Count == 0) return false;
        var batch = ExplorerFileOps.TakeBatchOpen(files, text => Notice?.Invoke(text));
        if (newWindowFirst) FileActivatedNewWindow?.Invoke(batch[0]);
        else FileActivated?.Invoke(batch[0]);
        for (var i = 1; i < batch.Count; i++) FileActivatedNewWindow?.Invoke(batch[i]);
        return true;
    }

    /// <summary>선택 항목 중 **파일**만의 경로 (A94 6차 — 일괄 열기 대상. 폴더는 제외한다).</summary>
    private static IReadOnlyList<string> SelectedFilePathsOf(ListViewBase owner) =>
        owner.SelectedItems
            .Select(VmOf) // A345 배치 2 — 리스트는 뷰모델, 그리드는 컨테이너 Tag
            .OfType<ExplorerEntryVm>()
            .Where(vm => !vm.IsFolder)
            .Select(vm => vm.Path)
            .ToList();

    // ---------- 파일 조작: 드래그 아웃 · 드랍 이동/복사 · 클립보드 (A94 1차, v0.124.0) ----------

    /// <summary>
    /// 항목 컨테이너에 드래그 아웃(전 항목)과 드랍 대상(폴더 항목만)을 건다.
    /// 드래그 데이터는 DragStarting에서 채운다 — CanDragItems의 DragItemsStarting은 await가
    /// 안 되는데 StorageItems 수집이 비동기라, 데퍼럴이 있는 컨테이너 CanDrag 경로를 쓴다
    /// (ExplorerFileOps.FillDragDataAsync 주석 참고). 폴더 항목 드랍은 그 폴더가 대상 —
    /// 빈 영역·파일 항목 드랍은 호스트(FileListOverlay 패널)가 현재 폴더 대상으로 받는다.
    /// 항목 핸들러가 Handled를 걸므로 패널 핸들러와 이중 처리되지 않는다.
    /// </summary>
    /// <remarks>
    /// A345 배치 2: 여기서도 항목을 캡처하지 않는다 — 세 핸들러 전부 발화 시점에 VmOf로 다시
    /// 푼다(재활용된 컨테이너가 옛 파일을 끌거나 옛 폴더로 드랍받는 사고의 방지선).
    /// 드랍 갈래는 <b>부착 시점에 폴더/파일을 가르지 않고</b> 항상 걸어 두고, 핸들러 안에서
    /// "지금 이 컨테이너가 폴더인가"로 가른다. 실제 수용 여부는 AllowDrop이 정하며 그 값은
    /// 리스트에서는 매 ContainerContentChanging이, 그리드(휴면)에서는 아래 한 줄이 정한다.
    /// </remarks>
    private void AttachDragDrop(SelectorItem item, ListViewBase owner)
    {
        item.CanDrag = true;
        item.DragStarting += async (_, args) =>
        {
            var deferral = args.GetDeferral();
            try
            {
                if (VmOf(item) is not { } vm ||
                    !await ExplorerFileOps.FillDragDataAsync(args.Data, PathsForDrag(owner, vm.Entry)))
                    args.Cancel = true; // 실을 항목이 없다(그새 삭제·재활용 등) — 빈 드래그는 시작하지 않는다
            }
            finally
            {
                deferral.Complete();
            }
        };

        item.AllowDrop = VmOf(item) is { IsFolder: true }; // 그리드용 초기값(리스트는 CCC가 매번 다시 정한다)
        item.DragOver += (_, e) =>
        {
            if (VmOf(item) is { IsFolder: true } vm) ExplorerFileOps.HandleTargetDragOver(e, vm.Path);
        };
        item.Drop += (_, e) =>
        {
            if (VmOf(item) is { IsFolder: true } vm) HandleDrop(e, vm.Path);
        };
    }

    /// <summary>
    /// 드래그·우클릭 조작에 실을 경로들 (A179): 잡은 항목이 작업 집합(체크 우선 — WorkingPathsOf)에
    /// 포함돼 있으면 집합 전부(다중 드래그 — 윈도우 관례), 아니면 그 항목 하나만.
    /// 체크 집합을 읽어야 해 static이 아니다(종전 선택 전용 시절과 다른 점).
    /// </summary>
    private IReadOnlyList<string> PathsForDrag(ListViewBase owner, ExplorerListing.Entry entry)
    {
        var working = WorkingPathsOf(owner);
        return working.Contains(entry.Path, StringComparer.OrdinalIgnoreCase) ? working : [entry.Path];
    }

    /// <summary>표면의 선택 항목 경로 전부(폴더 포함) — 항목 해석은 VmOf 하나를 지난다(A345 배치 2).</summary>
    private static IReadOnlyList<string> SelectedPathsOf(ListViewBase owner) =>
        owner.SelectedItems
            .Select(VmOf)
            .OfType<ExplorerEntryVm>()
            .Select(vm => vm.Path)
            .ToList();

    /// <summary>
    /// 드랍 실행: 동작 결정(같은 볼륨 이동/다른 볼륨 복사·Ctrl/Shift 강제)은 DragOver와 같은
    /// 판정을 다시 쓴다. 조작은 워커에서 비동기, 완료 후 현재 폴더 재스캔 — 이 페인이 폴더
    /// 상태의 단일 원본이라 ViewChanged로 중앙 썸네일까지 함께 갱신된다(A93 경로).
    /// </summary>
    private async void HandleDrop(DragEventArgs e, string targetFolder)
    {
        e.Handled = true; // 창 수준 라우팅과의 이중 처리 방지 (await 전에 동기로 지정해야 유효)
        var operation = ExplorerFileOps.DecideOperation(e, targetFolder);
        if (operation == DataPackageOperation.None ||
            !e.DataView.Contains(StandardDataFormats.StorageItems))
            return;
        e.AcceptedOperation = operation; // 소스(OS 탐색기 등)에 확정 동작을 알린다

        // A94 3차 — 충돌 대화상자·진행 문구용 UI 문맥(조작 시작 시점 캡처). 4차 — 접근 거부 안내도 같은 문맥.
        var ui = MakeOpUi();
        var move = operation == DataPackageOperation.Move;
        var result = await ExplorerFileOps.TransferDroppedAsync(e.DataView, targetFolder, move, ui);
        RefreshAfterFileOp();
        await ExplorerFileOps.ReportAsync(result.Notice(move), result.Denied, ui);
    }

    /// <summary>
    /// 파일 조작 뒤 현재 폴더 재스캔. A94 5차부터 폴더 감시가 같은 변경을 또 볼 수 있지만 명시
    /// 재스캔은 유지한다 — 겹치면 디바운스가 흡수하고, 최악이 중복 재스캔 1회(무해)라 억제
    /// 플래그를 두지 않는다(단순 우선 — 사양 명기).
    /// </summary>
    private void RefreshAfterFileOp()
    {
        if (_folder.Length > 0) NavigateTo(_folder, _extensions);
    }

    /// <summary>
    /// 파일 조작용 UI 문맥 (A94 3차) — 이 표면 창의 DispatcherQueue·XamlRoot(충돌 대화상자용)와
    /// Notice 채널(진행 문구 라이브 갱신용)을 조작 시작 시점에 캡처한다. 4차부터는 영구 삭제 확인·
    /// 접근 거부 안내(관리자 재시작 제안)와 이름변경·새 폴더 실패 보고까지 같은 문맥을 쓴다.
    /// </summary>
    private ExplorerFileOps.OpUi MakeOpUi() =>
        new(DispatcherQueue, XamlRoot, notice => Notice?.Invoke(notice));

    /// <summary>
    /// 표면 키 (A94): Ctrl+C = 복사, Ctrl+X = 잘라내기(RequestedOperation=Move로 구분),
    /// Ctrl+V = 현재 폴더에 붙여넣기, Ctrl+A = 전체 선택. 2차(v0.125.0)가 얹은 것 —
    /// F2 = 이름변경(첫 선택 항목 1개), Del = 휴지통 삭제(선택 전부), Ctrl+Shift+N = 새 폴더.
    /// A85가 얹은 것 — Enter = 선택 항목 열기(폴더 = 진입. ThumbnailExplorer와 동일).
    /// 4차(v0.151.0)가 얹은 것 — Shift+Del = 영구 삭제(확인 대화상자 뒤), Esc = 잘라내기 표시 해제.
    /// 6차(v0.153.0)가 얹은 것 — Enter가 **다중 선택이면 선택된 파일 전부**를 연다(폴더 제외).
    /// A157(v0.168.0)이 얹은 것 — Space = 포커스 항목 토글 → A179부터 **체크 토글**(체크박스
    /// 클릭과 같은 동작 — 선택은 건드리지 않는다). A179가 바꾼 것 — Del·Ctrl+C/X·Enter 다중
    /// 열기의 대상이 선택 집합에서 작업 집합(체크 우선)으로.
    /// 이 표면(그리드/리스트)에 포커스가 있을 때만 온다 — 생성자 AddHandler 주석 참고.
    /// </summary>
    private async void OnSurfaceKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.KeyStatus.WasKeyDown || sender is not ListViewBase owner) return;
        // A94 2차: 이름변경 편집 상자(TextBox) 안의 키는 전부 편집 몫 — handledEventsToo 구독이라
        // 편집 상자가 Handled를 걸어도 여기까지 오므로 원본 요소로 걸러낸다
        // (Del·Ctrl+A/V가 파일 조작으로, F2가 재진입으로 새면 안 된다).
        if (e.OriginalSource is TextBox) return;

        switch (e.Key)
        {
            case Windows.System.VirtualKey.Enter: // A85 — 선택 항목 열기(원 기능 우선: 셸 OnShellEnter가
                // "탐색기 리스트 포커스 = 선택 항목 열기 우선"으로 양보하는 표면 쪽 구현.
                // ThumbnailExplorer.OnGridKeyDown의 Enter와 같은 구성): 폴더 = 진입, 파일 = 열기.
                // 선택이 없으면 삼키지 않는다 — 셸도 이 표면엔 양보(ShouldPassThrough)라 무동작(A151·A274 유지).
                if (VmOf(owner.SelectedItem) is not { } vm) return; // A345 배치 2 — VmOf 단일 해석
                var entry = vm.Entry;
                e.Handled = true;
                _lastClick = null; // 같은 Enter가 만든 ItemClick 기록이 더블클릭 판정에 섞이지 않게
                // A179: 체크가 있으면 체크된 파일 전부가 우선(작업 집합 규칙 — 전부 폴더면 빈
                // 목록 = false라 아래 첫 항목 동작으로 떨어진다. 체크 없이 선택이 없으면 애초에
                // 위 SelectedItem 가드에서 셸로 양보 — 셸 경로(OpenSelectedFiles)도 체크 우선이다).
                if (CheckedPathsInView().Count > 0 &&
                    OpenFiles(CheckedPathsInView(filesOnly: true))) return;
                // A94 6차: 다중 선택이면 선택된 '파일' 전부를 연다(폴더는 일괄 열기에서 제외).
                // 선택에 파일이 하나도 없으면(폴더만 다중) 아래 현행 첫 항목 동작으로 떨어진다.
                if (owner.SelectedItems.Count > 1 && OpenFiles(SelectedFilePathsOf(owner))) return;
                if (entry.IsFolder)
                {
                    NavDiagnostics.NoteSource("list"); // 계측 출처 — 리스트 Enter 활성화
                    NavigateTo(entry.Path, _extensions);
                }
                else FileActivated?.Invoke(entry.Path);
                return;
            // A158: 셸 패널 키가 F11/F12로 옮겨가 F2 충돌 소멸 — 이름변경은 F2 유지(사용자 확정),
            // "선택이 있을 때만 Handled"라는 기존 소비 규칙도 무변경.
            case Windows.System.VirtualKey.F2: // 이름변경 — 다중 선택이어도 첫 항목(SelectedItem)만
                // A345 배치 2: 리스트의 SelectedItem은 뷰모델이라 편집 상자를 끼울 컨테이너를
                // 먼저 실체화해야 한다(보수안 ⓐ). 그리드(휴면)는 SelectedItem이 곧 컨테이너다.
                if (VmOf(owner.SelectedItem) is not { } target) return;
                var container = ReferenceEquals(owner, ListPane)
                    ? RealizeListContainer(target)
                    : owner.SelectedItem as SelectorItem;
                if (container is null) return; // 그새 사라짐 — 무동작(종전 미매칭 폴백과 같은 자리)
                e.Handled = true;
                BeginRenameOf(container);
                return;
            case Windows.System.VirtualKey.Delete: // Del = 휴지통 / Shift+Del = 영구 삭제(A94 4차)
                if (ExplorerFileOps.IsCtrlDown()) return; // Ctrl+Del은 우리 조합이 아니다 — 종전대로 비켜 준다
                var targets = WorkingPathsOf(owner); // A179 — 체크 우선, 체크 0개면 선택
                if (targets.Count == 0) return;
                e.Handled = true;
                if (ExplorerFileOps.IsShiftDown()) await PermanentDeleteWithConfirmAsync(targets);
                else await DeleteWithNoticeAsync(targets);
                return;
            case Windows.System.VirtualKey.Space: // A157 → A179 — 포커스 항목의 **체크** 토글(체크박스 클릭과 같은 동작)
                // 포커스 항목 = 키 이벤트의 원천(포커스된 항목 컨테이너)에서 상향 탐색으로 찾는다.
                // 못 찾으면 무동작·무소비 — 표면 자체(빈 영역)에 포커스가 있을 때 Space를 삼키면
                // 스크롤 등 기본 동작을 잃는다. 편집 중에는 위의 TextBox 가드가 이미 막았다.
                if (ItemFromSource(e.OriginalSource) is not { } focused) return;
                e.Handled = true;
                // 두 표면 모두 IsItemClickEnabled=True라 키보드 조작이 ItemClick을 낳을 수 있다 —
                // Space 연타가 클릭 쌍(OnItemClick)으로 읽혀 파일이 열리는 것을 막는다.
                // 위 Enter 분기가 같은 이유로 두는 한 줄과 같은 방어(A85 관례).
                _lastClick = null;
                ToggleCheckOf(focused); // A179 — 선택(IsSelected)은 건드리지 않는다
                return;
            case Windows.System.VirtualKey.Escape: // A94 4차 — 잘라내기 표시 해제(탐색기 동등)
                // A202 개정: **실제로 지운 표시가 있을 때만 소비**한다 — 셸 Esc 체인에 콘텐츠
                // 닫기 층이 생겨, 무조건 흘리면 "표시 해제 + 콘텐츠 닫힘/S4 복귀"가 한 번에
                // 일어난다(한 층씩 규칙 위반). 지울 게 없으면 종전대로 흘려 셸 체인(전체화면 →
                // S4 → 콘텐츠 닫기)이 받는다. 클립보드 자체는 건드리지 않는다(Ctrl+V 재사용 가능).
                if (ExplorerFileOps.ClearCutMarks()) e.Handled = true;
                return;
        }

        if (!ExplorerFileOps.IsCtrlDown()) return;

        switch (e.Key)
        {
            case Windows.System.VirtualKey.N: // Ctrl+Shift+N = 새 폴더 (Shift 없는 Ctrl+N 아님 —
                // 앱 전역 Shift+N 새 창(A84)과도 다른 조합. 판정 = Ctrl(위) && Shift && N)
                if (!ExplorerFileOps.IsShiftDown() || _folder.Length == 0) return;
                e.Handled = true;
                await CreateFolderThenRenameAsync(owner);
                break;
            case Windows.System.VirtualKey.A:
                e.Handled = true;
                owner.SelectAll(); // Extended 모드 전제 — Single이면 던진다
                break;
            case Windows.System.VirtualKey.C:
            case Windows.System.VirtualKey.X:
                var paths = WorkingPathsOf(owner); // A179 — 체크 우선, 체크 0개면 선택
                if (paths.Count == 0) return;
                e.Handled = true;
                await CopyWithNoticeAsync(paths, cut: e.Key == Windows.System.VirtualKey.X);
                break;
            case Windows.System.VirtualKey.V:
                if (_folder.Length == 0) return;
                e.Handled = true;
                await PasteIntoAsync(_folder);
                break;
        }
    }

    /// <summary>
    /// 클립보드 적재 공용 (A94 6차 — Ctrl+C/X와 우클릭 메뉴 Cut/Copy가 같은 경로).
    /// 잘라내기 반투명 표시(4차)는 ExplorerFileOps가 적재 성공 시에만 갱신한다.
    /// </summary>
    private async Task CopyWithNoticeAsync(IReadOnlyList<string> paths, bool cut)
    {
        if (paths.Count == 0) return;
        if (await ExplorerFileOps.CopyToClipboardAsync(paths, cut) is { } notice) Notice?.Invoke(notice);
    }

    /// <summary>
    /// 붙여넣기 공용 (A94 6차 — Ctrl+V·빈 영역 메뉴는 현재 폴더, 폴더 항목 메뉴는 그 폴더).
    /// 대상이 현재 폴더가 아니어도 갱신은 현재 폴더 재스캔 하나면 된다(하위 폴더 내용 변화는
    /// 목록에 보이지 않지만, 대상이 현재 폴더인 경우를 같은 줄이 덮는다).
    /// </summary>
    private async Task PasteIntoAsync(string targetFolder)
    {
        if (targetFolder.Length == 0) return;
        var ui = MakeOpUi(); // A94 3차 — 충돌 대화상자·진행 문구, 4차 — 접근 거부 안내
        var (didWork, result, notice) = await ExplorerFileOps.PasteFromClipboardAsync(targetFolder, ui);
        if (didWork) RefreshAfterFileOp();
        await ExplorerFileOps.ReportAsync(notice, result.Denied, ui);
    }

    // ---------- 이름변경 · 새 폴더 · 휴지통 삭제 (A94 2차, v0.125.0) ----------

    /// <summary>
    /// F2·우클릭 Rename 진입: 항목의 이름 TextBlock을 찾아 인라인 편집(ExplorerRenameBox)으로 바꾼다.
    /// 이름 TextBlock 조회 = 이름 기반(FindItemBlock, A156) — 종전의 "콘텐츠 패널 둘째 자식"
    /// 인덱스 계약은 A156의 2줄 레이아웃에서 자리가 밀려 폐기했다(어긋나도 예외 없이 조용한
    /// return이라 F2가 무반응으로만 보였다). 두 표면 공용이라 MakeGridItem 타일의 이름
    /// TextBlock에도 같은 이름을 붙여 뒀다.
    /// 편집 상자는 ExplorerRenameBox가 이름 TextBlock과 같은 행·같은 칸에 앉힌다 — 새 리스트
    /// 구조에서는 row 0 · column 1(이름 자리)이고, 상세 줄(row 1)은 편집 중에도 그대로 보인다.
    /// 커밋 성공 갱신 = RefreshAfterFileOp(편집이 끝난 뒤에만 — 편집 중 재스캔은 편집 UI를 지운다).
    /// </summary>
    private void BeginRenameOf(SelectorItem item)
    {
        if (VmOf(item) is not { } vm) return; // A345 배치 2 — VmOf 단일 해석
        if (ContentPanelOf(item) is not { } panel) return; // 편집 상자를 끼울 host(리스트 = 템플릿 루트)
        if (FindItemBlock(item, ItemNameBlockName) is not { } nameBlock) return;
        ExplorerRenameBox.Begin(panel, nameBlock, vm.Path, MakeOpUi(), RefreshAfterFileOp);
    }

    /// <summary>
    /// Del·우클릭 Delete: 휴지통 경유 삭제(StorageDeleteOption.Default — ExplorerFileOps 주석).
    /// 확인 대화상자 없음(윈도우 탐색기도 휴지통행은 기본 무확인) — 실패만 안내 문구,
    /// 권한 부족은 관리자 재시작 제안(A94 4차 — ReportAsync).
    /// </summary>
    private async Task DeleteWithNoticeAsync(IReadOnlyList<string> paths)
    {
        var ui = MakeOpUi();
        var result = await ExplorerFileOps.DeleteToRecycleAsync(paths);
        RefreshAfterFileOp();
        await ExplorerFileOps.ReportAsync(result.Notice("deleted"), result.Denied, ui);
    }

    /// <summary>
    /// Shift+Del = 영구 삭제 (A94 4차): 탐색기 동등으로 **영구 삭제만** 확인창을 띄우고(기본 버튼 =
    /// Cancel), 확인하면 휴지통을 거치지 않고 지운다. 대상 선택 규칙·재스캔·실패 안내는 Del과
    /// 같은 경로다. 취소하면 아무것도 하지 않는다(재스캔도 없음).
    /// </summary>
    private async Task PermanentDeleteWithConfirmAsync(IReadOnlyList<string> paths)
    {
        var ui = MakeOpUi();
        if (!await ExplorerDialogs.ConfirmPermanentDeleteAsync(ui.Dispatcher, ui.Root, paths)) return;
        var result = await ExplorerFileOps.DeletePermanentlyAsync(paths);
        RefreshAfterFileOp();
        await ExplorerFileOps.ReportAsync(result.Notice("deleted"), result.Denied, ui);
    }

    /// <summary>
    /// Ctrl+Shift+N: 현재 폴더에 "New folder" 생성(충돌 = "New folder (2)") → 재스캔 '완료'를
    /// 기다렸다가(NavigateToAsync) 새 폴더 항목을 선택하고 곧바로 이름변경 편집 진입(탐색기 관례).
    /// 편집 진입이 재스캔보다 먼저면 스캔이 편집 상자를 지워 버린다 — 순서가 규칙이다.
    /// </summary>
    private async Task CreateFolderThenRenameAsync(ListViewBase owner)
    {
        var (created, notice, denied) = ExplorerFileOps.CreateFolder(_folder);
        if (notice is not null) await ExplorerFileOps.ReportAsync(notice, denied ? 1 : 0, MakeOpUi());
        if (created is null) return;
        await NavigateToAsync(_folder, _extensions);
        // A345 배치 2: 조립이 동기라 기다릴 것이 없다(WhenFillCompleteAsync 폐기). 리스트는
        // 뷰모델을 선택하고 그 행의 컨테이너를 실체화해 편집에 들어간다(보수안 ⓐ).
        BeginRenameByPath(owner, created);
    }

    /// <summary>
    /// 빈 영역 메뉴 New file (A189 — 위 CreateFolderThenRenameAsync의 파일 판본, 흐름 동일):
    /// "New file.txt" 생성(충돌 = "New file (2).txt") 후 재스캔 완료를 기다려 그 항목으로
    /// 이름변경 편집에 진입한다. 감시(A94 5차) 재스캔·편집 중 보류(EditEnded)는 New folder와
    /// 같은 경로를 그대로 탄다. 현재 목록이 모듈 확장자로 필터돼 .txt가 안 보이는 모듈에서는
    /// 파일만 만들어지고 편집 진입은 조용히 생략된다(FindItemByPath 미매칭 — 위 "그새 사라짐"
    /// 폴백과 같은 무해 경로).
    /// </summary>
    private async Task CreateFileThenRenameAsync(ListViewBase owner)
    {
        var (created, notice, denied) = ExplorerFileOps.CreateFile(_folder);
        if (notice is not null) await ExplorerFileOps.ReportAsync(notice, denied ? 1 : 0, MakeOpUi());
        if (created is null) return;
        await NavigateToAsync(_folder, _extensions);
        // A345 배치 2: New folder와 같은 경로. 필터 밖·그새 사라짐이면 조용히 생성만으로 끝난다.
        BeginRenameByPath(owner, created);
    }

    /// <summary>
    /// 방금 만든 항목(새 폴더·새 파일)을 골라 이름변경 편집에 들여보낸다 (A345 배치 2 —
    /// CreateFolderThenRenameAsync·CreateFileThenRenameAsync 공용).
    /// 리스트는 뷰모델을 선택한 뒤 그 행의 컨테이너를 실체화하고(RealizeListContainer),
    /// 그리드(휴면)는 종전대로 컨테이너를 찾아 선택·스크롤한다.
    /// 반환은 "편집에 들어갔는가" — 못 찾으면(필터 밖·그새 사라짐) 조용히 false다.
    /// </summary>
    private bool BeginRenameByPath(ListViewBase owner, string path)
    {
        SelectorItem? container;
        if (ReferenceEquals(owner, ListPane))
        {
            if (FindVmByPath(path) is not { } vm) return false;
            owner.SelectedItem = vm;
            container = RealizeListContainer(vm);
        }
        else
        {
            if (FindItemByPath(owner, path) is not { } item) return false;
            owner.SelectedItem = item;
            owner.ScrollIntoView(item);
            owner.UpdateLayout(); // 컨테이너 실체화 — 편집 상자 삽입·포커스가 성립하게
            container = item;
        }
        if (container is null) return false;
        BeginRenameOf(container);
        return true;
    }

    /// <summary>
    /// 경로로 표시 목록의 뷰모델 찾기 (A345 배치 2) — 가상화 뒤의 정본 조회다.
    /// 컨테이너 검색과 달리 <b>화면 밖 항목도 찾는다</b>(그것이 종전 상한·조각 대기 문제의 해소).
    /// </summary>
    private ExplorerEntryVm? FindVmByPath(string path) =>
        _displayVms.FirstOrDefault(vm =>
            string.Equals(vm.Path, path, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// 뷰모델 하나의 리스트 컨테이너를 실체화해 돌려준다 (A345 배치 2 — 이름변경 보수안 ⓐ).
    /// 인라인 편집은 컨테이너 안의 이름 TextBlock 자리에 상자를 끼우는 구조라, 가상화 뒤에는
    /// "보이게 스크롤 → 레이아웃 강제 → 컨테이너 조회"의 세 단계를 거쳐야 상자를 끼울 대상이
    /// 생긴다. 그래도 못 얻으면(목록 밖 등) null — 호출부는 무동작으로 끝낸다.
    /// </summary>
    private ListViewItem? RealizeListContainer(ExplorerEntryVm vm)
    {
        ListPane.ScrollIntoView(vm);
        ListPane.UpdateLayout();
        return ListPane.ContainerFromItem(vm) as ListViewItem;
    }

    /// <summary>경로로 항목 컨테이너 찾기 (IconGrid 전용 — 컨테이너 직접 추가 구조, Tag = 뷰모델).
    /// 리스트는 컨테이너가 화면 분량뿐이라 이 방식이 성립하지 않는다(FindVmByPath를 쓴다).</summary>
    private static SelectorItem? FindItemByPath(ListViewBase owner, string path) =>
        owner.Items.OfType<SelectorItem>().FirstOrDefault(i =>
            i.Tag is ExplorerEntryVm vm &&
            string.Equals(vm.Path, path, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// 원시 눌림(PointerPressed) 쌍 = 더블클릭 최후 폴백 (A131 — 배선 근거는 생성자 주석).
    /// 왼쪽 눌림 "전이"만 태운다(A112 XButton1 판정과 같은 관용구). Ctrl 눌림은 다중 선택 토글
    /// 제스처라 쌍에서 제외한다(Shift는 제외하지 않는다 — Shift+더블클릭 = 새 창(A24)은
    /// Activate가 해석한다). 쌍 상태는 페인 1벌 — _lastClick과 같은 스코프(그리드·리스트 공유).
    /// 정상 환경에서는 기존 두 판정과 같은 제스처에서 겹쳐 발화하지만 Activate의 _lastActivation
    /// 억제(A85)가 1회로 누른다 — 두 번째 눌림 시점 발화는 탐색기 관례(WM_LBUTTONDBLCLK)와 같다.
    /// </summary>
    private void OnSurfacePointerPressed(object sender, PointerRoutedEventArgs e)
    {
        if (sender is not ListViewBase owner) return;
        if (e.GetCurrentPoint(owner).Properties.PointerUpdateKind
            != Microsoft.UI.Input.PointerUpdateKind.LeftButtonPressed) return;
        if (e.OriginalSource is TextBox) return; // 이름변경 편집 상자(A94 2차) — 더블클릭은 텍스트 선택 몫
        if (IsInCheckBox(e.OriginalSource))
        {
            _lastPress = null; // A157 → A179: 체크박스 눌림은 체크 토글 몫 — 쌍 판정에서 빼고 끊는다
            return;            // (이 핸들러는 handledEventsToo라 체크박스가 Handled를 걸어도 도착한다)
        }
        if (ExplorerFileOps.IsCtrlDown())
        {
            _lastPress = null; // Ctrl 토글 선택 — 진행 중이던 쌍 판정을 끊는다
            return;
        }
        if (VmOf(ItemFromSource(e.OriginalSource)) is not { } vm)
        {
            _lastPress = null; // 빈 영역·스크롤바 — 항목 밖 눌림은 쌍을 끊는다
            return;
        }
        var now = DateTime.UtcNow;
        var isPair = _lastPress is { } last && last.Path == vm.Path &&
                     (now - last.At).TotalMilliseconds < DoubleClickMs;
        _lastPress = isPair ? null : (vm.Path, now);
        if (isPair) Activate(vm.Entry, owner);
    }

    /// <summary>눌림의 원본 요소에서 항목 컨테이너(SelectorItem)를 찾는다 — 조상 상향 탐색
    /// (깊이 상한 64 = HotkeySupport.MaxAncestorDepth와 같은 방어).</summary>
    private static SelectorItem? ItemFromSource(object source)
    {
        var node = source as DependencyObject;
        for (var depth = 0; node is not null && depth < 64; depth++)
        {
            if (node is SelectorItem item) return item;
            node = VisualTreeHelper.GetParent(node);
        }
        return null;
    }

    // ---------- 작업 집합 체크박스 (A157, v0.168.0 → A179 반전) ----------

    /// <summary>
    /// 체크 집합 (A179) — 파일 조작 작업 집합의 **단일 원본**. 체크박스 클릭(OnItemCheckClick)과
    /// Space(OnSurfaceKeyDown)로만 늘고 준다 — 행 클릭 선택은 여기 손대지 않는다.
    /// 경로 키인 이유 = 목록 전량 재작성(폴더 감시 400ms 재스캔 포함)을 건너 생존해야 해서
    /// (ExplorerFileOps.ApplyCutMark의 경로 집합 관용구). 폴더가 바뀌면 비우고, 재스캔 결과에
    /// 없는 경로는 걷어낸다 — 둘 다 NavigateToAsync가 한다.
    /// </summary>
    private readonly HashSet<string> _checkedPaths = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// 체크박스 클릭 = 그 항목의 체크 토글 (A179 — 종전 A157의 "선택 토글"에서 반전).
    /// 컨트롤의 IsChecked는 Click 시점에 이미 뒤집혀 있으므로(EnsureFilterFlyout의
    /// ToggleMenuFlyoutItem과 같은 성질) 그 값을 집합에 반영만 한다 — 선택은 건드리지 않는다.
    /// <para>
    /// **Checked/Unchecked를 구독하지 않는 이유(A179 재평가)**: 종전 근거였던 "선택 → 체크 → 선택"
    /// 되먹임 루프는 거울 철거로 사라졌지만, 그 둘은 프로그램적 IsChecked 대입(MakeListItem의
    /// 재스캔 복원·Space 토글의 시각 동기)에도 발화해 집합 갱신이 복원 경로에서 또 돈다 —
    /// 사용자 입력에서만 발화하는 Click 하나가 여전히 맞다(배선 불변).
    /// </para>
    /// <para>
    /// ButtonBase.Click의 인자(RoutedEventArgs)에는 Handled가 없다(WinUI — 저장소의 e.Handled
    /// 사용처는 전부 Pointer/Key/Tapped 파생 인자다). 클릭이 더블클릭 열기로 새는 것은
    /// 체크박스가 포인터 이벤트를 스스로 소비하는 것 + IsInCheckBox 가드 두 벌이 막는다.
    /// </para>
    /// </summary>
    private void OnItemCheckClick(object sender, RoutedEventArgs e)
    {
        if (sender is not CheckBox box) return;
        if (VmOf(ItemFromSource(box)) is not { } vm) return; // A345 배치 2 — VmOf 단일 해석
        var nowChecked = box.IsChecked == true;
        if (nowChecked) _checkedPaths.Add(vm.Path);
        else _checkedPaths.Remove(vm.Path);
        vm.IsChecked = nowChecked; // 시각 반영은 x:Bind(OneWay)가 맡는다 — 컨테이너 직접 대입 없음
    }

    /// <summary>
    /// 포커스 항목의 체크 토글 (A179 — Space 경로. 종전 A157은 IsSelected 토글이었다).
    /// 집합을 뒤집고 뷰모델에 반영하면 끝이다 — A345 배치 2부터 체크박스 시각은 x:Bind(OneWay)가
    /// 따라온다(종전의 컨테이너 체크박스 직접 대입은 사라졌다). 그리드 타일(MakeGridItem)에는
    /// 체크박스가 없어 시각이 보이지 않지만 그 경로는 휴면이다(리스트 전용 모드 — 클래스 주석.
    /// 전체 페인 사용처가 되살아나면 타일에도 체크박스를 다는 것이 복구 지점이다).
    /// </summary>
    private void ToggleCheckOf(SelectorItem item)
    {
        if (VmOf(item) is not { } vm) return; // A345 배치 2 — VmOf 단일 해석
        var nowChecked = !_checkedPaths.Contains(vm.Path);
        if (nowChecked) _checkedPaths.Add(vm.Path);
        else _checkedPaths.Remove(vm.Path);
        vm.IsChecked = nowChecked;
    }

    /// <summary>
    /// 체크 집합 중 **현재 화면(리스트)에 있는** 경로만 (A179 — 작업 집합의 소비형).
    /// 집합이 아니라 리스트 항목을 기준으로 거르는 이유 = WYSIWYG: 세션 필터(A7)로 가려진
    /// 체크가 조작 대상에 섞이면 화면 밖 항목이 지워지는 사고가 된다. 항목 순서 = 표시 순서라
    /// 일괄 열기(OpenFiles)의 상한 절단도 화면 순서를 따른다.
    /// 리스트(ListPane)만 보는 근거 = 체크박스가 리스트 행에만 있고, 리스트는 그리드와 같은
    /// 폴더·같은 목록을 항상 담는다(Fill이 두 표면을 한 번에 채운다).
    /// <para>
    /// A345 배치 2: 순회 대상이 컨테이너에서 <b>표시 목록(_displayVms)</b>으로 바뀌었다 —
    /// 가상화 뒤에는 컨테이너가 화면 분량뿐이라 컨테이너를 세면 "스크롤 위치에 따라 작업 집합이
    /// 달라지는" 사고가 된다. _displayVms는 정렬·필터가 적용된 표시 목록이라 WYSIWYG 계약
    /// (필터로 가려진 체크는 제외)과 순서(일괄 열기의 상한 절단)가 종전과 완전히 같다.
    /// </para>
    /// </summary>
    private IReadOnlyList<string> CheckedPathsInView(bool filesOnly = false) =>
        _displayVms
            .Where(vm => (!filesOnly || !vm.IsFolder) && _checkedPaths.Contains(vm.Path))
            .Select(vm => vm.Path)
            .ToList();

    /// <summary>
    /// 작업 집합 (A179 확정 규칙): 화면에 보이는 체크가 1개 이상이면 체크 집합, 0개면 선택 집합
    /// (탐색기류 다중 선택 도구의 관례). 드래그·복사/잘라내기/삭제·다중 열기가 이 하나를 쓴다.
    /// </summary>
    private IReadOnlyList<string> WorkingPathsOf(ListViewBase owner)
    {
        var check = CheckedPathsInView();
        return check.Count > 0 ? check : SelectedPathsOf(owner);
    }

    /// <summary>작업 집합의 파일 한정형 (A179) — 다중 열기(A94 6차)용. 규칙은 WorkingPathsOf와
    /// 같다: 체크가 1개 이상이면 **체크 집합이 관할**이라, 체크가 전부 폴더면 선택으로 넘어가지
    /// 않고 빈 목록이 되어 호출부(OpenFiles)가 false → 단일 항목 폴백으로 떨어진다.</summary>
    private IReadOnlyList<string> WorkingFilePathsOf(ListViewBase owner) =>
        CheckedPathsInView().Count > 0
            ? CheckedPathsInView(filesOnly: true)
            : SelectedFilePathsOf(owner);

    /// <summary>
    /// 이벤트 원본이 항목 체크박스(A157) 안에서 왔는지 — 조상 상향 탐색(깊이 상한 64 =
    /// ItemFromSource와 같은 방어). CheckBox는 자기 템플릿 내부 요소를 OriginalSource로 실어
    /// 보내므로 `is CheckBox` 한 줄로는 걸러지지 않는다. 항목 컨테이너(SelectorItem)까지
    /// 올라오면 체크박스 밖이 확정이라 거기서 멈춘다 — 바깥쪽 무관한 CheckBox 오탐 방지.
    /// 필요한 이유: A85/A131 더블클릭 판정이 PointerPressed를 handledEventsToo로 관찰해
    /// 체크박스가 Handled를 걸어도 그대로 받는다 — 막지 않으면 체크박스 빠른 2연타가 파일 열기가 된다.
    /// </summary>
    private static bool IsInCheckBox(object source)
    {
        var node = source as DependencyObject;
        for (var depth = 0; node is not null && depth < 64; depth++)
        {
            if (node is CheckBox) return true;
            if (node is SelectorItem) return false; // 항목 컨테이너까지 왔다 = 체크박스 밖
            node = VisualTreeHelper.GetParent(node);
        }
        return false;
    }

    /// <summary>
    /// 클릭 2회(500ms 내 같은 항목) = 더블클릭: 폴더 진입 또는 파일 열기.
    /// Shift를 누른 채 더블클릭하면 파일을 새 창으로(A24) — 폴더에는 효과 없음.
    /// ※ A85: 실기기 입력 스택은 더블클릭의 두 번째 클릭을 더블탭 제스처로 소비해 두 번째
    /// ItemClick이 안 올 수 있다 — 그 경우는 OnItemDoubleTapped가 받는다. 이 판정은
    /// ItemClick이 2회 오는 환경(키보드 Enter 연타 포함)의 보조 경로로 유지한다.
    /// </summary>
    private void OnItemClick(object sender, ItemClickEventArgs e)
    {
        if (VmOf(e.ClickedItem) is not { } vm) return; // A345 배치 2 — 리스트의 ClickedItem은 뷰모델이다

        var now = DateTime.UtcNow;
        var isDouble = _lastClick is { } last && last.Path == vm.Path &&
                       (now - last.At).TotalMilliseconds < DoubleClickMs;
        _lastClick = (vm.Path, now);
        if (!isDouble) return;

        _lastClick = null;
        Activate(vm.Entry, sender as ListViewBase);
    }

    /// <summary>
    /// 항목 컨테이너 DoubleTapped = 더블클릭 열기 (A85). 실기기에서는 두 번째 클릭이 더블탭
    /// 제스처로 소비되어 두 번째 ItemClick이 오지 않아, 클릭 쌍 판정(OnItemClick)만으로는
    /// 열기가 조용히 무시됐다(압축 모듈 내부 리스트는 처음부터 DoubleTapped라 이 증상이 없었다).
    /// 그리드·리스트 양쪽 컨테이너(SelectorItem) 공용 — ThumbnailExplorer와 같은 구성.
    /// </summary>
    private void OnItemDoubleTapped(object sender, DoubleTappedRoutedEventArgs e)
    {
        if (e.OriginalSource is TextBox) return; // 이름변경 편집 상자(A94 2차) — 더블클릭은 텍스트 선택 몫
        if (IsInCheckBox(e.OriginalSource)) return; // A157 → A179: 체크박스 2연타 = 체크 토글 두 번(열기 아님)
        if (sender is not SelectorItem item || VmOf(item) is not { } vm) return; // A345 배치 2
        e.Handled = true;
        _lastClick = null; // 이 제스처를 이룬 클릭 기록이 다음 클릭 쌍 판정에 섞이지 않게
        // 소속 표면 = 컨테이너 타입으로 결정된다(MakeGridItem = GridViewItem/IconGrid,
        // 리스트 템플릿 = ListViewItem/ListPane) — 일괄 열기(A94 6차)가 그 표면의 선택을 본다.
        Activate(vm.Entry, item is GridViewItem ? IconGrid : ListPane);
    }

    /// <summary>
    /// 더블클릭 열기 공통 종착점 (A85): 폴더 = 진입(NavigateTo), 파일 = 열기(Shift = 새 창, A24).
    /// ItemClick 쌍과 DoubleTapped가 같은 제스처에서 둘 다 발화하는 환경이 있어, 같은 경로의
    /// 연속 발화를 판정 창(DoubleClickMs) 안에서 1회로 누른다 — A24 "항상 새 창" 설정에서
    /// 창이 두 개 뜨는 이중 열기 방지.
    /// A94 6차 → A179: 활성화한 항목이 **작업 집합(체크 우선, 체크 0개면 선택)에 포함돼 있으면**
    /// 집합의 파일 전부를 연다(폴더 제외 — 집합에 파일이 하나도 없으면 종전대로 그 항목 하나.
    /// Enter 규칙과 같다).
    /// </summary>
    private void Activate(ExplorerListing.Entry entry, ListViewBase? owner)
    {
        var now = DateTime.UtcNow;
        if (_lastActivation is { } last && last.Path == entry.Path &&
            (now - last.At).TotalMilliseconds < DoubleClickMs)
            return;
        _lastActivation = (entry.Path, now);

        var shift = Microsoft.UI.Input.InputKeyboardSource
            .GetKeyStateForCurrentThread(Windows.System.VirtualKey.Shift)
            .HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down);

        // A94 6차 → A179 — 작업 집합(체크 우선) 일괄 열기. 잡은 항목이 집합 밖이면 그 항목만
        // (드래그·삭제와 같은 규칙 — PathsForDrag의 멤버십 판정과 동일).
        if (owner is not null && WorkingPathsOf(owner) is { Count: > 1 } working &&
            working.Contains(entry.Path, StringComparer.OrdinalIgnoreCase) &&
            OpenFiles(WorkingFilePathsOf(owner), shift))
            return;

        if (entry.IsFolder)
        {
            NavDiagnostics.NoteSource("list"); // 계측 출처 — 리스트 항목 더블클릭
            NavigateTo(entry.Path, _extensions);
            return;
        }

        if (shift) FileActivatedNewWindow?.Invoke(entry.Path);
        else FileActivated?.Invoke(entry.Path);
    }

    private void OnUpClicked(object sender, RoutedEventArgs e)
    {
        if (Directory.GetParent(_folder) is { } parent)
            NavigateTo(parent.FullName, _extensions);
    }

    /// <summary>
    /// 홈으로 이동 (A282) — 홈 = 사용자 프로필 폴더(%UserProfile%, 설정 키 없음).
    /// 이동은 위로 가기(OnUpClicked)와 같은 내부 경로(NavigateTo + 현재 담당 확장자)를 그대로 쓴다.
    /// 이미 홈이거나 홈 경로를 못 얻으면 무동작 — 버튼을 비활성화하지는 않는다(사용자 확정:
    /// 깜빡이며 켜졌다 꺼지는 것보다 눌러도 아무 일이 없는 편이 낫다). 폴더 비교는 NavigateToAsync의
    /// 폴더 변경 판정과 같은 OrdinalIgnoreCase(윈도우 경로 관례).
    /// </summary>
    private void OnHomeClicked(object sender, RoutedEventArgs e)
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (home.Length == 0) return; // 프로필 경로를 못 얻는 환경 — 조용히 무동작
        if (string.Equals(home, _folder, StringComparison.OrdinalIgnoreCase)) return;
        NavigateTo(home, _extensions);
    }

    // ---------- 폴더 감시 (A94 5차, v0.152.0) — 외부 변경 자동 갱신 ----------

    /// <summary>감시 디바운스(ms) — 마지막 이벤트 기준 병합, 만료 시 전체 재스캔 1회(사양 확정 수치).</summary>
    private const int WatchDebounceMs = 400;

    private FileSystemWatcher? _watcher;     // 현재 폴더 전용 — 창(페인)별 1개(전역 공유 금지), 전환·오류 시 dispose+재생성
    private DispatcherTimer? _watchDebounce; // UI 스레드 디바운스(DocumentView DirtyTimer와 같은 방식) — 지연 생성
    private DispatcherQueue? _watchQueue;    // 풀 스레드 이벤트 → UI 마셜용. 감시 시작 시점(UI 스레드)에 캡처
    private bool _watchPending;              // 이름변경 편집 중 만료된 재스캔 보류 — bool 1개(큐 아님, 소화도 1회)
    private bool _surfaceLive;               // Loaded~Unloaded 사이인가 — 죽은 뷰 접근 방어(감시 경로 공용)

    /// <summary>
    /// A333: 감시 세대 번호 — 워커에서 만들어 온 감시자를 채택해도 되는지의 판정 하나뿐이다.
    /// 올리는 곳은 <see cref="TearDownWatch"/> 한 곳(= "지금 감시를 버린다"의 단일 지점: 재대상·
    /// 언로드·오류 재시작이 전부 그리로 모인다). UI 스레드에서만 읽고 쓴다 — 증가도 채택 판정도
    /// 전부 UI 문맥이고, 워커는 이 값을 <b>복사본으로만</b> 들고 간다(_loadSeq와 같은 관용구).
    /// </summary>
    private int _watchSeq;

    /// <summary>
    /// 현재 폴더 감시 시작·재대상 (A94 5차): 폴더가 바뀔 때마다 기존 감시자를 통째로 dispose하고
    /// 새로 만든다 — Path 교체 재사용보다 실패 상태가 단순하다(예외가 나도 반쯤 살아 있는 감시자가
    /// 남지 않고, 감시자 생성은 값싸다). NotifyFilter는 최소 구성(FileName·DirectoryName·
    /// LastWrite·Size) — LastAccess류를 빼 Changed 폭주와 "재스캔이 이벤트를 되먹이는" 순환을 피한다.
    /// 생성·시작 실패(네트워크 드라이브·접근 불가·제거된 드라이브·사라진 폴더)는 조용히 무감시 =
    /// 종전과 동일하게 명시 재스캔만 남는다(사양). 호출은 UI 스레드 전용(NavigateToAsync·Loaded·
    /// 오류 재시작).
    /// <para>
    /// <b>A333 — 생성·해제를 워커로 뺐다(CLAUDE.md 1.8)</b>. <c>new FileSystemWatcher(folder)</c>는
    /// 경로 유효성(디렉터리 존재 확인)을, <c>EnableRaisingEvents = true</c>는 디렉터리 핸들 열기와
    /// ReadDirectoryChangesW 등록을 <b>동기 커널 I/O로</b> 한다. 이 메서드는 폴더가 바뀔 때마다
    /// <see cref="NavigateToAsync"/>가 <b>"Loading..." 표시(A243)보다 앞서</b> 부르므로, 잠들어 있던
    /// 디스크·느린 볼륨에서는 그 I/O가 곧 <b>첫 프레임이 그려지기 전의 UI 정지</b>가 된다
    /// (A243 로딩 문구가 있는데도 안 보인다는 사용자 보고의 유일한 구조적 설명 — 이 지점 말고는
    /// 항해 시작~로딩 표시 사이에 UI 스레드 I/O가 없다). 그래서 만들기는 워커에서 하고,
    /// UI 스레드는 다 만들어진 감시자를 <see cref="AdoptWatch"/>로 <b>채택만</b> 한다.
    /// 채택 전에 폴더가 또 바뀌었으면(<see cref="_watchSeq"/> 불일치) 그 감시자는 버린다 —
    /// 필드(_watcher·_watchPending·타이머)는 여전히 UI 스레드에서만 만진다(경쟁 없음).
    /// 감시가 붙는 시점이 몇 ms 늦어지지만 사양은 그대로다: 그 창의 변경은 어차피 곧 도착하는
    /// 전체 스캔 결과가 담고 있다(재대상 직후 = "곧 전체 스캔으로 시작한다"는 종전 근거 그대로).
    /// </para>
    /// </summary>
    private void EnsureWatch(string folder)
    {
        TearDownWatch(); // 재대상 = 기존 감시·보류 상태 폐기(새 폴더는 곧 전체 스캔으로 시작한다)
        if (!_surfaceLive || folder.Length == 0) return;

        var seq = _watchSeq;         // 방금 TearDownWatch가 올린 세대 — 이후 재대상·언로드면 어긋난다
        var queue = DispatcherQueue; // 풀 스레드에서 컨트롤 프로퍼티 접근 금지 — 여기(UI)서 캡처(OpUi 관용구)
        _watchQueue = queue;
        _ = Task.Run(() => // 워커 — FileListOverlay.LoadChildrenAsync와 같은 Task.Run 관용구
        {
            FileSystemWatcher? watcher = null;
            try
            {
                watcher = new FileSystemWatcher(folder)
                {
                    NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName |
                                   NotifyFilters.LastWrite | NotifyFilters.Size,
                    IncludeSubdirectories = false, // 감시 대상 = 현재 폴더 한 단계뿐(사양)
                };
                watcher.Created += OnWatcherEvent;
                watcher.Deleted += OnWatcherEvent;
                watcher.Renamed += OnWatcherEvent; // RenamedEventArgs는 FileSystemEventArgs 파생 — 같은 핸들러로 받는다
                watcher.Changed += OnWatcherEvent;
                watcher.Error += OnWatcherError;
                watcher.EnableRaisingEvents = true; // 접근 불가·소실 경로는 대개 여기서 던진다
            }
            catch
            {
                DiscardWatcher(watcher); // 반쯤 만든 감시자 정리 — 이 폴더는 감시 없이 동작(명시 재스캔만)
                return;
            }
            if (watcher is not { } made) return; // 도달 불가(위 catch가 실패를 다 걷는다) — 널 흐름 분석용 명시
            // 채택은 UI 스레드에서. 큐가 이미 닫혔으면(창 종료) 여기서 버린다 — 누수 방지.
            if (!queue.TryEnqueue(() => AdoptWatch(made, seq))) DiscardWatcher(made);
        });
    }

    /// <summary>
    /// A333 — UI 스레드: 워커가 다 만들어 온 감시자를 채택한다. 그새 폴더가 또 바뀌었거나
    /// (세대 불일치) 뷰가 내려갔으면 채택하지 않고 버린다. 필드 대입은 여기 한 곳뿐이라
    /// _watcher가 UI 스레드 단독 소유라는 종전 불변식이 그대로 유지된다.
    /// </summary>
    private void AdoptWatch(FileSystemWatcher watcher, int seq)
    {
        if (seq != _watchSeq || !_surfaceLive)
        {
            DiscardWatcher(watcher);
            return;
        }
        _watcher = watcher;
    }

    /// <summary>
    /// A333 — 감시자 1개 폐기의 단일 지점: 이벤트 전부 해제 후 <b>워커에서</b> Dispose.
    /// Dispose를 워커로 미루는 이유는 생성과 같다 — 디렉터리 핸들을 닫고 진행 중 OS 콜백이
    /// 빠져나가기를 기다리므로 UI 스레드에서 하면 그만큼 멈춘다. 해제 뒤에 버리므로 그 사이
    /// 도착하는 잔여 콜백은 없고, 실패는 삼킨다(정리 실패는 이미 버린 객체의 문제).
    /// 호출부 = TearDownWatch(살아 있던 것) · EnsureWatch의 생성 실패 · AdoptWatch의 채택 거부.
    /// </summary>
    private void DiscardWatcher(FileSystemWatcher? watcher)
    {
        if (watcher is null) return;
        watcher.Created -= OnWatcherEvent;
        watcher.Deleted -= OnWatcherEvent;
        watcher.Renamed -= OnWatcherEvent;
        watcher.Changed -= OnWatcherEvent;
        watcher.Error -= OnWatcherError;
        _ = Task.Run(() =>
        {
            try { watcher.Dispose(); }
            catch { /* 뒷정리 실패는 무시 — ModuleWorker.Post와 같은 방침 */ }
        });
    }

    /// <summary>
    /// 감시 해제 (A94 5차): 이벤트 전부 해제 후 Dispose — FileSystemWatcher는 OS 콜백에 뿌리를
    /// 두므로 Dispose 없이는 닫힌 창의 뷰 델리게이트째 살아남는다(정적 이벤트 구독과 같은 부류의
    /// 누수). 디바운스 타이머도 멈춘다 — Unloaded 뒤 Tick이 죽은 뷰를 만지지 않게(핸들러 안
    /// 상태 검사와 이중 방어). 보류 플래그도 버린다 — 재대상이면 곧 전체 스캔이 오고, Unloaded면
    /// 소화할 곳이 없다(편집 커밋 직후의 재스캔이 보류분을 대신 덮는 근거이기도 하다).
    /// A333: 세대 번호를 올려 <b>만들어지는 중이던</b> 감시자의 채택까지 함께 취소한다 —
    /// 실물 폐기·Dispose는 DiscardWatcher 한 곳이 맡는다.
    /// </summary>
    private void TearDownWatch()
    {
        _watchSeq++; // A333 — 보류 중인 채택 무효화(만들어 오던 감시자는 AdoptWatch가 버린다)
        if (_watcher is { } watcher)
        {
            _watcher = null;
            DiscardWatcher(watcher);
        }
        _watchDebounce?.Stop();
        _watchPending = false;
    }

    /// <summary>
    /// 감시 이벤트(Created/Deleted/Renamed/Changed 공용) — FileSystemWatcher 콜백은 스레드풀로
    /// 온다. 여기서는 UI 상태를 일절 만지지 않고 TryEnqueue 마셜만 한다(WinUI에는
    /// SynchronizingObject 대상이 없다). 우리 조작(이동/복사/삭제)의 명시 재스캔과 겹칠 수 있지만
    /// 억제 플래그는 두지 않는다 — 디바운스가 흡수하고, 최악이 중복 재스캔 1회(무해)라 단순 우선(사양).
    /// </summary>
    private void OnWatcherEvent(object sender, FileSystemEventArgs e) =>
        _watchQueue?.TryEnqueue(RestartWatchDebounce);

    /// <summary>
    /// 감시 오류(InternalBufferOverflowException = 이벤트 폭주로 개별 통지 유실 포함) =
    /// 감시 재시작 시도 + 전체 재스캔 1회(사양). 재스캔은 디바운스 경유 — 편집 중 보류 규칙이
    /// 같은 길을 타고, 폴더 자체가 사라진 경우도 만료 시 상위 이동으로 수렴한다.
    /// 재시작 실패(폴더 소실 등)는 생성 실패와 같은 조용한 무감시다.
    /// </summary>
    private void OnWatcherError(object sender, ErrorEventArgs e) =>
        _watchQueue?.TryEnqueue(() =>
        {
            if (!_surfaceLive || !ReferenceEquals(sender, _watcher)) return; // 재대상 뒤 옛 감시자의 잔여 오류 무시
            EnsureWatch(_folder);
            RestartWatchDebounce();
        });

    /// <summary>UI 스레드: 디바운스 타이머 되감기 — 마지막 이벤트 기준 400ms 병합(연속 이벤트 1회로).</summary>
    private void RestartWatchDebounce()
    {
        if (!_surfaceLive || _folder.Length == 0) return; // Unloaded 뒤 도착한 잔여 마셜 — 죽은 뷰 방어
        var timer = _watchDebounce ??= CreateWatchDebounce();
        timer.Stop(); // 반복 타이머 — Stop 후 Start로 확실히 되감는다(전 모듈 관용구)
        timer.Start();
    }

    private DispatcherTimer CreateWatchDebounce()
    {
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(WatchDebounceMs) };
        timer.Tick += (_, _) =>
        {
            timer.Stop(); // 반복 타이머 — 1회 판정용이라 즉시 멈춘다(DocumentView DirtyTimer 관용구)
            OnWatchDebounceExpired();
        };
        return timer;
    }

    /// <summary>
    /// 디바운스 만료 = 전체 재스캔 1회. 항목별 증분 반영을 하지 않는 이유: 정렬(A5)·필터(A7)·
    /// 썸네일 채우기·중앙 썸네일 동기(A93 ViewChanged)가 전부 기존 재스캔 경로(NavigateTo)에 이미
    /// 있다 — 그 경로 재사용이 규칙이다. 재스캔 재진입은 NavigateToAsync의 _loadSeq(늦은 결과
    /// 폐기)가 이미 막으므로 별도 방어를 더하지 않는다.
    /// 이름변경 편집 중(ExplorerRenameBox)이면 재스캔이 편집 상자를 지우므로 보류 플래그만 세우고,
    /// 편집 종료 알림(OnRenameEditEnded)에서 1회 소화한다.
    /// 현재 폴더가 사라졌으면 가장 가까운 존재하는 상위로 이동한다(탐색기 동등 — 기존 NavigateTo
    /// 경로 재사용). 루트까지 없으면 현재 폴더 재스캔이 기존 실패 경로("Cannot read this folder" +
    /// 빈 목록 통지)로 떨어지고, 감시자도 재대상 실패로 꺼진다 = 감시 중지 + 빈 목록(사양).
    /// </summary>
    private void OnWatchDebounceExpired()
    {
        if (!_surfaceLive || _folder.Length == 0) return; // Unloaded 직후 잔여 Tick 방어(타이머 Stop과 이중)
        if (ExplorerRenameBox.IsEditing)
        {
            _watchPending = true; // 몇 번 만료돼도 소화는 1회 — bool 하나가 전부(큐 금지, 사양)
            return;
        }
        _watchPending = false;

        // UI 스레드 Directory.Exists는 ExplorerRenameBox.Begin과 같은 수준의 가벼운 조회 —
        // 끊긴 네트워크 경로면 느릴 수 있지만, 그 경우는 감시 생성부터 실패해 여기 올 일이 드물다.
        var target = _folder;
        while (target.Length > 0 && !Directory.Exists(target))
            target = Directory.GetParent(target)?.FullName ?? string.Empty;
        NavigateTo(target.Length > 0 ? target : _folder, _extensions);
    }

    /// <summary>
    /// 이름변경 편집 종료(커밋·취소·검증 실패 공통) 알림 수신 (A94 5차) — 편집 중 보류한 감시
    /// 재스캔을 이제 소화한다. 만료 처리를 재사용하므로 다른 창의 편집이 아직 남아 있으면 도로
    /// 보류되고, 커밋 성공 직후면 onRenamed의 재스캔(NavigateTo)이 방금 보류 플래그를 걷어 가
    /// (TearDownWatch) 이중 스캔 없이 끝난다. 정적 이벤트 구독 — Loaded/Unloaded 수명 규칙.
    /// </summary>
    private void OnRenameEditEnded()
    {
        if (_watchPending) OnWatchDebounceExpired();
    }
}
