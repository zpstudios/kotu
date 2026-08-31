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
    /// A192 체감 조정 지점: 분할 조립 조각 크기(항목 수) — 첫 즉시 조각과 프레임당 append 조각이
    /// 같은 값을 쓴다(확정 수치 80 — DocumentView.RenderChunkBlocks의 상수 배치 관용구.
    /// 되돌리기·조정은 이 상수 하나만 고치면 된다).
    /// </summary>
    private const int FillChunkItems = 80;

    /// <summary>
    /// A192: 표면당 컨테이너 실체화 상한 — 초과분은 컨테이너를 만들지 않고 말미에 비상호작용
    /// 안내 1행만 붙인다(MakeOverflowNotice). 상한은 <b>컨테이너 실체화에만</b> 걸린다:
    /// _entries·_display·ViewChanged로 흐르는 Entry 목록과 체크 prune(A179)은 전체 그대로다.
    /// </summary>
    private const int MaterializeLimit = 2000;

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
    /// A241: 표시 목록 조립 완료(A192 FinishFill) 통지 — ViewChanged(조립 시작 시점)와 달리
    /// 컨테이너 실체화·상세 로더 기동이 끝난 뒤에 온다. 셸이 우측 정보 패널의 폴더 단위 EXIF
    /// 프리페치를 여기 걸어 뼈대 우선 원칙(A192)을 지킨다. 인자 = 표시 목록 전체(정렬·필터
    /// 반영 — 실체화 상한 밖 항목 포함, ViewChanged와 같은 집합).
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
    /// A192: 분할 조립 루프의 프레임 틱 핸들러(null = 루프 없음). CompositionTarget.Rendering은
    /// static 이벤트라 뷰 수명 안에서 반드시 해제한다 — 남기면 닫힌 페인이 통째로 누수된다
    /// (DocumentView._renderAppendHandler와 같은 사정). 해제의 단일 지점 = StopFillAppendLoop.
    /// 호출부 전수 = Unloaded·Fill 기동 직전 방어·NavigateToAsync 스캔 실패 경로·틱 내부
    /// (완료/seq 중단/예외).
    /// </summary>
    private EventHandler<object>? _fillAppendHandler;

    /// <summary>
    /// A192: 진행 중 분할 조립의 완료 신호 — 새 폴더/파일 생성 직후의 편집 진입
    /// (CreateFolderThenRenameAsync류)이 "컨테이너가 다 만들어진 뒤"를 기다리는 통로.
    /// 루프가 돌 때만 존재하고(소형 폴더 = 동기 완료 = null), 완료·예외·새 Fill로 대체될 때
    /// 반드시 TrySetResult로 풀어 준다(안 풀면 대기 흐름이 영원히 걸린다).
    /// 생성 형태는 ModuleWorker.Run의 RunContinuationsAsynchronously 관용구.
    /// </summary>
    private TaskCompletionSource<bool>? _fillDone;
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
    /// </summary>
    private ModuleWorkerPool FetchPool =>
        _fetchPool ??= new ModuleWorkerPool("KOTU explorer fetch", FetchConcurrency);

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
        IconGrid.SelectionChanged += (_, _) => SelectionChanged?.Invoke();
        ListPane.SelectionChanged += (_, _) => SelectionChanged?.Invoke();
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
            StopFillAppendLoop(); // A192 — CompositionTarget.Rendering은 static: 남기면 닫힌 페인 통째 누수
            _fillDone?.TrySetResult(true); // A192 — 조립 완료를 기다리던 흐름 해방(소화할 곳이 없다)
            _fillDone = null;
            TearDownWatch(); // 감시 이벤트 전부 해제 + Dispose + 디바운스 정지 — 창 통째 누수 방지
            _worker?.Dispose(); // 진행 중 작업은 워커가 마저 끝내고 스레드 종료
            _worker = null;
            _fetchPool?.Dispose(); // A194 — 풀 전파 Dispose. 닫힌 뒤의 Run은 취소 Task(계약)라
            _fetchPool = null;     // 발사 루프의 OperationCanceledException 처리로 조용히 끝난다.
        };
    }

    /// <summary>
    /// 잘라내기(Ctrl+X) 표시 반영 (A94 4차): 이미 그려 둔 항목의 콘텐츠 투명도를 경로 매칭으로
    /// 다시 맞춘다 — 재스캔이 아니라 제자리 갱신이라 선택·스크롤이 보존된다. 새로 그려지는
    /// 항목은 MakeGridItem·MakeListItem이 같은 규칙(ExplorerFileOps.ApplyCutMark)으로 처음부터 반영한다.
    /// </summary>
    private void ApplyCutMarks()
    {
        foreach (var item in IconGrid.Items) ExplorerFileOps.ApplyCutMark(item);
        foreach (var item in ListPane.Items) ExplorerFileOps.ApplyCutMark(item);
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
        // Name만 2* — 나머지 4칸은 1*. 좁으면 라벨이 잘리는 것을 허용한다(A276에서도 이 배분은
        // 불변 — 1* 넷 중 하나만 넓히면 나머지가 더 좁아져 잘림 총량이 줄지 않는다).
        ListHeader.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(2, GridUnitType.Star) });
        for (var i = 0; i < 4; i++)
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
        Fill(arranged, seq); // A192 — 첫 조각 즉시 + 나머지는 프레임 분할(완료 시 FinishFill)
        ViewChanged?.Invoke(_folder, arranged); // A93 — 중앙 썸네일 뷰가 같은 목록을 받아 그린다
        // A192: 소형 폴더(첫 조각 이하)는 조립이 위 Fill에서 동기로 끝났다 — 종전 순서 그대로
        // (Fill → ViewChanged → 로더) 마무리를 여기서 한다. 루프가 돌면 마지막 틱이 부른다.
        if (_fillAppendHandler is null) FinishFill(arranged, seq);
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

    /// <summary>가벼운 상세 텍스트(길이·모듈별 정보 — A6·A155)를 먼저 채우고,
    /// 무거운 썸네일을 이어서 채운다. A194: 각 단계 안에서는 fetch가 풀(워커 3)로 겹치지만
    /// 단계 간 순서(상세 전체 → 썸네일)는 종전대로 유지한다(await 직렬).</summary>
    private async Task LoadDetailsAsync(int seq)
    {
        await LoadDetailInfoAsync(seq);
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
    public void NavigateTo(string folder, IReadOnlyList<string> extensions) =>
        _ = NavigateToAsync(folder, extensions); // 발사 후 망각 — 본문이 예외를 스스로 처리(종전 async void와 동일 소비)

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

        var seq = ++_loadSeq;
        // A243: 폴더 실변경이면 스캔 완료를 기다리지 않고 즉시 옛 폴더 화면을 지우고 로딩 문구를
        // 띄운다(대형·OneDrive 폴더에서 수 초 무반응으로 보이던 체감 해소 — 스캔 완료 시 Fill이
        // 문구·목록을 덮고, 실패 경로도 "Cannot read..."가 덮는다). 같은 폴더 재스캔(감시 400ms
        // 디바운스·조작 후 갱신)은 이 갈래에 안 들어와 종전대로 무Clear(깜빡임 방지)다.
        // Clear ~ Fill 사이의 소비자는 전부 빈 목록에 안전하다: 선택 소멸 발화는 A240의 null 선택
        // 규칙(닫힌 도크는 FileListOverlay가 차단), ApplyCutMarks·CheckedPathsInView·FindItemByPath는
        // 빈 순회, 낡은 로더·조립 루프는 위 seq 증가와 아래 Stop이 접고, 편집 진입 대기
        // (WhenFillCompleteAsync)는 신호 해방 후 미매칭 폴백("그새 사라짐")으로 무해하게 끝난다.
        if (folderChanged)
        {
            StopFillAppendLoop(); // 직전 폴더의 조립 루프가 빈 판에 낡은 조각을 붙이지 않게(seq 대조와 이중)
            _fillDone?.TrySetResult(true); // 직전 조립을 기다리던 흐름 해방 — 낡은 목록이니 미매칭 폴백으로 끝난다
            _fillDone = null;
            IconGrid.Items.Clear();
            ListPane.Items.Clear();
            EmptyText.Text = "Loading...";
            EmptyText.Visibility = Visibility.Visible;
            NavigationStarted?.Invoke(folder); // 셸이 중앙 썸네일에도 같은 로딩 화면을 중계(A93 경로)
        }
        // A160: 표시 정책은 스캔 시작 시점에 스냅샷해 워커로 넘긴다 — 워커 스레드에서 UI 필드를
        // 읽지 않는다(스캔 도중 토글이 바뀌면 그 토글이 자기 재스캔을 다시 건다).
        var includeHidden = _showHidden;
        IReadOnlyList<ExplorerListing.Entry> entries;
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
            StopFillAppendLoop(); // A192 — 직전 폴더의 조립 루프가 빈 판에 낡은 조각을 붙이지 않게(seq 대조와 이중)
            _fillDone?.TrySetResult(true);
            _fillDone = null;
            IconGrid.Items.Clear();
            ListPane.Items.Clear();
            EmptyText.Text = "Cannot read this folder: " + ex.Message;
            EmptyText.Visibility = Visibility.Visible;
            ViewChanged?.Invoke(folder, []); // A93 — 썸네일 뷰도 옛 폴더 목록을 남기지 않는다
            return;
        }

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
    /// 표시 목록을 항목 컨테이너로 다시 만들어 채운다(ItemsSource·DataTemplate 없음 — 구조 규칙).
    /// A192: 종전 전량 동기 생성을 분할 조립으로 대체 — 첫 조각(FillChunkItems)만 즉시 만들고
    /// 나머지는 CompositionTarget.Rendering 틱당 한 조각씩 append한다(StartFillAppendLoop —
    /// DocumentView.StartRenderAppendLoop의 A193 구조 복제). 실체화 상한(MaterializeLimit)을
    /// 넘는 초과분은 만들지 않고 완료 시점(FinishFill)에 안내 1행만 붙는다. 재스캔·정렬·필터
    /// 재진입은 명시 해제(아래 Stop) + 틱 진입 seq 대조의 이중 방어 — 낡은 조각이 새 목록에
    /// 붙는 사고가 없다. 상세·썸네일 로더 기동도 FinishFill로 옮겼다(조각이 덜 붙은 스냅샷을
    /// 로더가 잡으면 나중 항목이 이번 회차에서 영영 빠지기 때문 — 근거는 FinishFill 주석).
    /// A179 유의: 체크(작업 집합)는 경로 키 집합(_checkedPaths)이 진실이라 이 재생성(폴더 감시
    /// 400ms 재스캔 포함)이 돌아도 MakeListItem이 집합에서 복원한다 — 종전 A157의 "재스캔 후
    /// 체크 소실" 낙수는 이것으로 해소. **선택**(하이라이트)은 여전히 재생성과 함께 사라진다 —
    /// 선택 복원은 별도 설계가 필요해 범위 밖(등재 후보 유지).
    /// </summary>
    private void Fill(IReadOnlyList<ExplorerListing.Entry> entries, int seq)
    {
        StopFillAppendLoop(); // 방어: 직전 조립 루프가 남아 있으면 먼저 해제(A193 관용구)
        _fillDone?.TrySetResult(true); // 직전 조립을 기다리던 흐름 해방 — 낡은 목록이니 미매칭 폴백으로 끝난다
        _fillDone = null;

        IconGrid.Items.Clear();
        ListPane.Items.Clear();

        var cap = Math.Min(entries.Count, MaterializeLimit);
        var first = Math.Min(FillChunkItems, cap);
        AppendFillRange(entries, 0, first);

        EmptyText.Text = "No matching files here";
        EmptyText.Visibility = entries.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

        if (first < cap)
        {
            _fillDone = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            StartFillAppendLoop(seq, entries, first, cap);
        }
        // 소형 폴더(첫 조각 이하)는 여기서 조립이 끝났다 — 마무리(FinishFill)는 호출자
        // (RefreshView)가 종전 순서(Fill → ViewChanged → 로더)를 지키려고 자기 자리에서 부른다.
    }

    /// <summary>조각 하나를 두 표면에 붙인다 (A192) — 종전 Fill 본문의 항목 생성 그대로.</summary>
    private void AppendFillRange(IReadOnlyList<ExplorerListing.Entry> entries, int start, int count)
    {
        var makeGrid = IconGrid.Visibility == Visibility.Visible; // 리스트 전용 모드(현행 유일 사용처)면 그리드 생략
        for (var i = start; i < start + count; i++)
        {
            var entry = entries[i];
            if (makeGrid) IconGrid.Items.Add(MakeGridItem(entry));
            ListPane.Items.Add(MakeListItem(entry));
        }
    }

    /// <summary>
    /// A192: 첫 조각 이후의 나머지 항목을 CompositionTarget.Rendering 틱마다 한 조각
    /// (FillChunkItems)씩 append한다 — UI 스레드 점유 상한 = 조각 1개 생성
    /// (DocumentView.StartRenderAppendLoop과 같은 프레임 틱 관용구·같은 해제 의무).
    /// 중단 판정 = 매 틱 append 직전의 seq 대조(한 틱 = 한 조각이라 틱 진입 시 1회로 충분):
    /// 재스캔(감시 디바운스 포함)·정렬·필터·폴더 전환이 _loadSeq를 올리는 현행 구조 그대로다.
    /// 틱 핸들러는 본문 전체가 try/catch다(static 이벤트라 예외가 새면 앱 전역 크래시) —
    /// 조각 생성 예외 = 루프 중단(부분 목록 잔존은 다음 재스캔이 덮는다) + 완료 신호 해방.
    /// </summary>
    private void StartFillAppendLoop(int seq, IReadOnlyList<ExplorerListing.Entry> entries, int start, int cap)
    {
        StopFillAppendLoop(); // 방어: 기동 직전 잔존 루프 해제(A193 관용구)

        var next = start;
        void OnTick(object? sender, object? e)
        {
            try
            {
                if (seq != _loadSeq)
                {
                    StopFillAppendLoop(); // 그새 재스캔·정렬·폴더 전환 — 낡은 조각을 붙이지 않는다
                    return;               // _fillDone은 건드리지 않는다 — 이미 새 Fill 것으로 대체됐다
                }
                var count = Math.Min(FillChunkItems, cap - next);
                AppendFillRange(entries, next, count);
                next += count;
                if (next >= cap)
                {
                    StopFillAppendLoop(); // 완료 — 더 깨울 이유가 없다
                    FinishFill(entries, seq);
                }
            }
            catch (Exception)
            {
                StopFillAppendLoop();
                _fillDone?.TrySetResult(true); // seq 일치 확인 뒤의 예외라 이 신호는 이번 조립 것이다
                _fillDone = null;
            }
        }
        _fillAppendHandler = OnTick;
        CompositionTarget.Rendering += OnTick;
    }

    /// <summary>A192: 분할 조립 루프 해제의 단일 지점 — 구독 해제 + 표지 소거(루프 없으면 무동작).
    /// 기동은 StartFillAppendLoop 한 곳뿐이라 구독 중 핸들러 = 이 필드 하나가 불변식이다.</summary>
    private void StopFillAppendLoop()
    {
        if (_fillAppendHandler is { } handler)
        {
            CompositionTarget.Rendering -= handler;
            _fillAppendHandler = null;
        }
    }

    /// <summary>
    /// A192: 조립 완료의 단일 마무리 — ① 상한 초과분 안내 1행 부착, ② 완료 신호 해방,
    /// ③ 상세·썸네일 로더 기동. 로더를 "루프 완료 후"로 옮긴 근거: LoadDetailInfoAsync·
    /// LoadThumbnailsAsync는 기동 시점에 Items를 스냅샷해 순회하므로, 조각이 덜 붙은 시점에
    /// 기동하면 뒤 조각의 항목이 이번 회차에서 영영 빠진다(부재 "내성"으로 해결 불가 —
    /// 다시 찾지 않는 구조). 대형 폴더의 상세·썸네일이 조립 완료까지(2000항목 기준 수백 ms)
    /// 늦는 것은 수용(사양 명기). 소형 폴더는 RefreshView가 동기로 불러 종전 시점과 같다.
    /// 낡은 완료(폐기된 루프의 마지막 틱)는 seq 대조로 걸러진다.
    /// </summary>
    private void FinishFill(IReadOnlyList<ExplorerListing.Entry> entries, int seq)
    {
        if (seq != _loadSeq) return; // 방어 — 낡은 완료가 로더를 기동하지 않게
        if (entries.Count > MaterializeLimit)
        {
            var hidden = entries.Count - MaterializeLimit;
            if (IconGrid.Visibility == Visibility.Visible)
                IconGrid.Items.Add(MakeOverflowNotice(hidden, grid: true));
            ListPane.Items.Add(MakeOverflowNotice(hidden, grid: false));
        }
        _fillDone?.TrySetResult(true);
        _fillDone = null;
        _ = LoadDetailsAsync(seq);
        // A241: 조립 완료 훅 — 셸이 우측 정보 패널의 EXIF 프리페치를 여기서 기동한다(뼈대 우선:
        // 목록 조립·상세 로더 기동이 끝난 뒤에만 부가 스캔이 붙는다). 감시 재스캔의 재통지는
        // 소비 쪽 캐시(경로+수정시각)가 흡수한다 — 여기서 거르지 않는다(ViewChanged와 같은 방침).
        FillCompleted?.Invoke(entries);
    }

    /// <summary>
    /// A192: 실체화 상한 초과 안내 — 비상호작용 1행/1타일. Tag 없음(항목 조회·조작 루틴은 전부
    /// Tag의 Entry 패턴 매칭이라 자연 제외된다: FindItemByPath·CheckedPathsInView·SelectedPathsOf·
    /// ApplyCutMark·LoadDetailInfoAsync 전수 확인), 계약 훅(메뉴·드래그·체크·더블클릭) 미부착,
    /// IsEnabled=false로 포커스·클릭 대상에서도 뺀다. 문구는 사양 확정(UI 문자열 영어).
    /// </summary>
    private static SelectorItem MakeOverflowNotice(int hidden, bool grid)
    {
        var text = new TextBlock
        {
            Text = $"{hidden} more items are not shown. Refine the filter to see them.",
            FontSize = 11,
            Opacity = 0.6,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(4),
        };
        return grid
            ? new GridViewItem { Content = text, IsEnabled = false }
            : (SelectorItem)new ListViewItem { Content = text, IsEnabled = false };
    }

    /// <summary>
    /// A192: 진행 중 분할 조립의 완료 대기(없으면 즉시) — 새 폴더/파일 생성 직후의 편집 진입이
    /// FindItemByPath 전에 부른다(그 항목의 컨테이너가 뒤 조각에 있을 수 있다). 대기 중 새
    /// 재스캔이 끼어들면 낡은 신호가 즉시 풀리고, 뒤이은 FindItemByPath 미매칭이 기존
    /// "그새 사라짐" 폴백으로 무해하게 끝난다. 상한 밖(2000 초과분) 항목도 같은 폴백이다.
    /// </summary>
    private Task WhenFillCompleteAsync() => _fillDone?.Task ?? Task.CompletedTask;

    /// <summary>
    /// 항목 우클릭 메뉴 (A94 2차 신설 → 6차 확장). 순서는 탐색기 관례 근사:
    /// 파일 = "Open in new instance"(A24) → 구분선 → Cut·Copy → 구분선 → Rename·Delete,
    /// 폴더 = Cut·Copy·**Paste(대상 = 그 폴더)** → 구분선 → Rename·Delete.
    /// Delete·Cut·Copy 대상은 드래그와 같은 규칙 — 그 항목이 작업 집합(A179: 체크 우선,
    /// 체크 0개면 선택)에 포함돼 있으면 집합 전부, 아니면 그 항목 하나(PathsForDrag 재사용).
    /// Rename은 플라이아웃이 닫히며 포커스를 되돌린 '뒤'에 진입해야 편집 상자가 곧장 LostFocus
    /// 커밋으로 닫혀 버리지 않는다 — 디스패처로 한 박자 미룬다(BeginRenameOf).
    /// </summary>
    private void AttachContextMenu(SelectorItem item, ExplorerListing.Entry entry, ListViewBase owner)
    {
        var flyout = new MenuFlyout();
        if (!entry.IsFolder) AddOpenInNewInstance(flyout, entry);
        AddClipboardItems(flyout, entry, owner); // A94 6차 — Cut·Copy·(폴더면 Paste) + 구분선
        var rename = new MenuFlyoutItem
        {
            Text = "Rename",
            Icon = new FontIcon { Glyph = "\uE8AC" }, // Rename
        };
        rename.Click += (_, _) => DispatcherQueue.TryEnqueue(() => BeginRenameOf(item));
        flyout.Items.Add(rename);
        var delete = new MenuFlyoutItem
        {
            Text = "Delete",
            Icon = new FontIcon { Glyph = "\uE74D" }, // Delete
        };
        delete.Click += async (_, _) => await DeleteWithNoticeAsync(PathsForDrag(owner, entry));
        flyout.Items.Add(delete);
        item.ContextFlyout = flyout;
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
            flyout.Items.Add(pasteItem);
            flyout.Opening += (_, _) => pasteItem.IsEnabled = ExplorerFileOps.CanPasteFromClipboard();
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
    private GridViewItem MakeGridItem(ExplorerListing.Entry entry)
    {
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

        var item = new GridViewItem { Content = panel, Tag = entry };
        ExplorerFileOps.ApplyCutMark(item); // A94 4차 — 잘라내기 중인 경로면 처음부터 반투명
        AttachContextMenu(item, entry, IconGrid); // A24 + A94 2차(Rename·Delete)
        AttachDragDrop(item, entry, IconGrid); // A94 — 드래그 아웃 + 폴더 항목 드랍
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

    /// <summary>이름 TextBlock의 조회 키 (A156). MakeListItem·MakeGridItem이 같은 이름을 붙인다.</summary>
    private const string ItemNameBlockName = "ExplorerItemName";

    /// <summary>2줄째 상세 TextBlock의 조회 키 (A156) — 크기·속성·날짜를 한 줄로 합쳐 담는다.</summary>
    private const string ItemDetailBlockName = "ExplorerItemDetail";

    /// <summary>체크박스의 조회 키 (A157 → A179) — Space 체크 토글의 시각 동기가 이 이름으로 찾는다.</summary>
    private const string ItemCheckBoxName = "ExplorerItemCheck";

    /// <summary>
    /// 항목 콘텐츠 패널에서 이름으로 TextBlock을 찾는다 (A156).
    /// 항목 루트는 평평한 패널 하나(중첩 없음)라 한 레벨 탐색으로 충분하다 — 시각 트리 상향
    /// 탐색(ItemFromSource)과 달리 여기는 우리가 만든 구조만 본다.
    /// </summary>
    private static TextBlock? FindItemBlock(object item, string name) =>
        item is ContentControl { Content: Panel panel }
            ? panel.Children.OfType<TextBlock>().FirstOrDefault(t => t.Name == name)
            : null;

    /// <summary>항목 콘텐츠 패널의 작업 집합 체크박스 (A157 → A179) — FindItemBlock과 같은 이름 기반 규칙.</summary>
    private static CheckBox? FindItemCheckBox(object item) =>
        item is ContentControl { Content: Panel panel }
            ? panel.Children.OfType<CheckBox>().FirstOrDefault(c => c.Name == ItemCheckBoxName)
            : null;

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
    /// 상세 줄과 툴팁을 한 벌로 (다시) 채운다 (A156) — 생성 시점(MakeListItem)과 지연 로드
    /// 도착 시점(LoadDetailInfoAsync)이 같은 조립을 쓰게 하는 단일 깔때기.
    /// </summary>
    private static void ApplyDetail(ListViewItem item, ExplorerListing.Entry entry, DetailInfo details)
    {
        if (FindItemBlock(item, ItemDetailBlockName) is { } detail)
            detail.Text = BuildDetailText(entry, details);
        if (item.Content is UIElement row)
            ToolTipService.SetToolTip(row, BuildTooltipText(entry, details));
    }

    /// <summary>
    /// 리스트 행 (A156 — 2줄): 1줄 = 아이콘 + 이름, 2줄 = 크기·[속성]·Created·Modified 한 줄,
    /// 우측 끝 = 작업 집합 체크박스(A157 → A179). 속성 조각은 지연 로드(A6 → A155)라 처음에는
    /// 빠진 채 조립되고, 도착하면 상세 줄을 통째로 다시 만든다.
    /// 루트는 **평평한 Grid 하나**다(중첩 패널 금지) — 이름변경(ExplorerRenameBox.Begin)이
    /// host 패널에 편집 상자를 끼우고 Grid.SetRow/SetColumn으로 이름 자리에 앉히기 때문.
    /// A198 행 높이 압축: 위 트리(FileListOverlay의 FolderTree — 스타일 무지정 = WinUI 기본
    /// TreeViewItemMinHeight 32px)와 동급을 목표로, 숨은 하한 세 개를 전부 명시로 누른다:
    /// ① ListViewItem.MinHeight = 0 (기본 ListViewItemMinHeight 40이 진짜 하한이었다 — A156이
    ///    예고한 "커지면 MinHeight 한 줄" 복구 지점의 반대 방향 적용),
    /// ② ListViewItem.Padding = 12,1,12,1 (세로 1px — 기본 세로 패딩 승계 차단, 가로는 기본 근사),
    /// ③ 체크박스 MinHeight = 0 (기본 32가 RowSpan=2로 두 줄 전체를 32px 이상으로 버텼다).
    /// 글꼴은 이름 12 유지·상세 10(11에서 축소)·아이콘 13 유지 → 콘텐츠 약 29px + 패딩 2px ≈ 31px.
    /// 실기기 육안이 최종 판정 — 되돌리기 지점은 아래 item 초기화의 MinHeight·Padding 두 줄과
    /// 상세 FontSize 한 줄이다(전부 이 메서드 안).
    /// (LineHeight/LineStackingStrategy는 쓰지 않는다 — 저장소 선례 0건이고 기본 전략상 무효).
    /// </summary>
    private ListViewItem MakeListItem(ExplorerListing.Entry entry)
    {
        var row = new Grid { ColumnSpacing = 8, RowSpacing = 0 };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });                      // 0 아이콘
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) }); // 1 이름·상세
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });                      // 2 체크박스
        row.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });                           // 0 이름
        row.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });                           // 1 상세

        var icon = new FontIcon
        {
            Glyph = entry.IsFolder ? "\uE8B7" : "\uE7C3",
            FontSize = 13,
            VerticalAlignment = VerticalAlignment.Center,
        };
        Grid.SetRowSpan(icon, 2); // 두 줄 높이 가운데 정렬

        var name = new TextBlock
        {
            Name = ItemNameBlockName,
            Text = entry.Name,
            FontSize = 12,
            VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis,
        };
        Grid.SetColumn(name, 1);

        var detail = new TextBlock
        {
            Name = ItemDetailBlockName,
            FontSize = 10, // A198: 11 → 10 — 행 높이 압축의 일부(되돌리기 지점)
            Opacity = 0.6,
            VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis,
        };
        Grid.SetColumn(detail, 1);
        Grid.SetRow(detail, 1);

        // A179(종전 A157 반전): 체크 = 선택과 분리된 **작업 집합**의 시각화 — 진실은 경로 키 집합
        // _checkedPaths다. 행 클릭 선택(하이라이트)은 체크에 손대지 않고, 체크는 이 체크박스
        // 클릭(또는 Space)으로만 토글된다. SelectionMode는 Extended 그대로다 — Multiple로 바꾸면
        // A94의 Ctrl/Shift 관례와 PathsForDrag 계약이 그 위에 서 있어 깨진다.
        // 콘텐츠 '안'에 두는 이유 = 잘라내기 흐림(ExplorerFileOps.ApplyCutMark)이 SelectorItem.Content
        // 루트의 Opacity를 만지므로, 밖에 두면 잘라낸 항목에서 체크만 또렷하게 남는다. 잘라내기 중
        // 체크도 함께 0.5로 흐려지는 것은 수용한다(항목 전체가 흐려지는 탐색기 모양).
        // 기본 치수(MinWidth·MinHeight·Padding·Margin)를 0으로 눌러야 2줄 행 높이를 체크박스가
        // 먹지 않는다(A198: 기본 MinHeight 32가 A156 경고대로 숨은 하한이었다 — 명시 0 추가).
        var check = new CheckBox
        {
            Name = ItemCheckBoxName,
            Content = null,
            // A179: 경로 키 집합에서 복원 — Fill 전량 재생성(재스캔)을 건너 체크가 생존한다.
            IsChecked = _checkedPaths.Contains(entry.Path),
            MinWidth = 0,
            MinHeight = 0, // A198 — 행 압축의 필수 조건(이게 남으면 다른 압축이 전부 무효)
            Padding = new Thickness(0),
            Margin = new Thickness(0),
            VerticalAlignment = VerticalAlignment.Center,
        };
        Grid.SetColumn(check, 2);
        Grid.SetRowSpan(check, 2);
        // A179 시각 규칙 안내(하이라이트 = 선택 / 체크 = 작업 집합)를 툴팁으로 — UI 문자열 영어.
        ToolTipService.SetToolTip(check,
            "Check to target file operations (copy, cut, delete, drag, open). No checks = selection.");
        check.Click += OnItemCheckClick; // Checked/Unchecked는 구독 금지 — 근거는 OnItemCheckClick 주석

        row.Children.Add(icon);
        row.Children.Add(name);
        row.Children.Add(detail);
        row.Children.Add(check);

        var item = new ListViewItem
        {
            Content = row,
            Tag = entry,
            // A198 행 높이 압축(트리 행 32px 동급 목표) — 되돌리기 지점: 아래 두 줄을 지우면
            // WinUI 기본(ListViewItemMinHeight 40·테마 패딩)으로 복귀한다.
            MinHeight = 0,
            Padding = new Thickness(12, 1, 12, 1),
        };
        ApplyDetail(item, entry, DetailInfo.Empty); // 상세 줄 + 툴팁 초판(속성 조각은 아직 도착 전)
        ExplorerFileOps.ApplyCutMark(item); // A94 4차 — 잘라내기 중인 경로면 처음부터 반투명
        AttachContextMenu(item, entry, ListPane); // A24 + A94 2차(Rename·Delete)
        AttachDragDrop(item, entry, ListPane); // A94 — 드래그 아웃 + 폴더 항목 드랍
        item.IsDoubleTapEnabled = true; // A85 — 압축 모듈 내부 리스트(ArchiveView)와 같은 명시
        item.DoubleTapped += OnItemDoubleTapped; // A85 — 더블클릭 열기의 기본 경로
        return item;
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
    /// 파일별 상세 조각(재생시간·해상도·페이지 수·압축률·인코딩 — A6 → A155 → A199)을 리스트 행
    /// 2줄째 상세 줄에 합쳐 넣는다. 취득은 전부 워커에서(폴더 진입 체감 불변 — 동기 열거에 안 싣는 A156 결정),
    /// UI는 텍스트 반영만. 재진입은 _loadSeq 가드(썸네일 루프와 같은 관용구).
    /// 정렬·필터 재그리기는 캐시가 흡수한다(수정시각 일치 시 재조회 없음).
    /// <para>
    /// A194: 항목별 fetch는 서로 독립이라 풀(FetchPool — 워커 3)로 겹치게 돌린다. 발사는
    /// UI 스레드의 SemaphoreSlim 게이트로 동시 3건까지만 — 폴더 전환(seq 변화)·풀 닫힘이 오면
    /// 더 발사하지 않으므로 낡은 폴더의 fetch가 큐에 쌓이지 않는다. 결과 도착 순서는 무관(사양) —
    /// 항목별 반영이라 순서가 필요 없다. <b>_infoCache와 ApplyDetail은 종전대로 UI 스레드에서만
    /// 만진다</b>(발사 루프와 fetch 후속부는 전부 UI 문맥에서 돌고, 워커 람다는 순수 fetch뿐).
    /// </para>
    /// </summary>
    private async Task LoadDetailInfoAsync(int seq)
    {
        var items = ListPane.Items.ToList(); // 스냅샷 — await 중 컬렉션 변경 대비
        using var gate = new SemaphoreSlim(FetchConcurrency); // 동시 발사 상한 (A194)
        var running = new List<Task>();
        var stop = false; // 풀이 닫힘(취소 Task) — 남은 발사 중단. UI 스레드에서만 읽고 쓴다.

        // 항목 하나의 fetch + UI 반영. UI 스레드에서 시작하므로 await 후속부도 UI 스레드다.
        async Task FetchIntoAsync(ListViewItem item, ExplorerListing.Entry entry, InfoKind kind)
        {
            try
            {
                DetailInfo details;
                try
                {
                    details = await FetchPool.Run(_ => FetchDetailInfo(kind, entry.Path));
                }
                catch (OperationCanceledException)
                {
                    stop = true; // 페인이 내려가며 풀이 닫힘 — 발사 루프도 멈춘다
                    return;
                }
                catch
                {
                    return; // 속성·헤더를 못 읽는 파일은 빈 칸 유지
                }
                if (seq != _loadSeq) return; // 폴더 전환 — 낡은 결과 폐기
                if (_infoCache.Count > 4000) _infoCache.Clear(); // 장시간 세션 폭주 방지
                _infoCache[entry.Path] = (entry.Modified, details);
                if (details.Info.Length == 0) return;
                // A156: 대입이 아니라 그 항목의 상세 줄과 툴팁을 통째로 다시 조립한다
                // (조각 순서는 BuildDetailText가 쥔다).
                ApplyDetail(item, entry, details);
            }
            finally
            {
                gate.Release(); // 예외·취소 경로 포함 — 누락되면 3건 뒤 조용히 멈춘다
            }
        }

        foreach (var obj in items)
        {
            if (stop || seq != _loadSeq) break;
            if (obj is not ListViewItem { Tag: ExplorerListing.Entry { IsFolder: false } entry } item) continue;
            var kind = InfoKindOf(entry.Name);
            if (kind == InfoKind.None) continue;
            // A175: 클라우드 전용(placeholder) 파일은 상세 조각 취득 자체가 하이드레이션이다 —
            // 재생시간·이미지 해상도(속성 핸들러가 파일을 연다 — A180의 이미지 축 포함)·PDF 페이지 수
            // (전체 로드)·zip 압축률(아카이브 열기)·텍스트 인코딩(A199 — 앞부분이라도 파일을 읽는다)
            // 전부 생략하고 상세 줄은 초판(크기·날짜)대로 둔다. 캐시에도 넣지 않는다 —
            // 사용자가 열어 로컬화되면 다음 재스캔에서 정상 조회된다.
            if (entry.IsPlaceholder) continue;

            if (_infoCache.TryGetValue(entry.Path, out var hit) && hit.Modified == entry.Modified)
            {
                if (hit.Details.Info.Length > 0) ApplyDetail(item, entry, hit.Details);
                continue; // 캐시 히트는 워커 없이 즉시 반영 (종전 동작)
            }

            await gate.WaitAsync(); // UI 문맥 await — 후속부는 UI 스레드로 복귀
            if (stop || seq != _loadSeq)
            {
                gate.Release(); // 획득만 하고 발사하지 않는 경로 — 누수 방지
                break;
            }
            running.Add(FetchIntoAsync(item, entry, kind));
        }
        // 발사분 완주 대기 — using gate의 Dispose가 대기 중 Release보다 앞서지 않게 한다.
        await Task.WhenAll(running);
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
    /// LoadDetailInfoAsync 주석 참고)로 동시 3건까지 겹친다. loaded 카운터는 발사 전 검사와
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
        async Task FetchIntoAsync(GridViewItem item, ExplorerListing.Entry entry)
        {
            try
            {
                var png = await FetchPool.Run(_ => FetchThumbnail(entry.Path, entry.IsPlaceholder));
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
            if (obj is not GridViewItem { Tag: ExplorerListing.Entry { IsFolder: false } entry } item) continue;

            await gate.WaitAsync(); // UI 문맥 await — 후속부는 UI 스레드로 복귀
            if (stop || seq != _loadSeq || loaded >= ThumbnailLimit)
            {
                gate.Release(); // 획득만 하고 발사하지 않는 경로 — 누수 방지
                break;
            }
            running.Add(FetchIntoAsync(item, entry));
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
        item is FrameworkElement { Tag: ExplorerListing.Entry { IsFolder: false } entry } ? entry.Path : null;

    /// <summary>
    /// 선택된 항목(파일·폴더 불문) — 없으면 null (A240: 셸 선택 축 질의. ThumbnailExplorer.
    /// SelectedEntry와 같은 계약 — 폴더/무선택의 해석(= 선택 축 null)은 셸 몫이다).
    /// 상한 초과 안내 행(MakeOverflowNotice)은 Tag가 없어 패턴 매칭에서 자연 제외된다.
    /// </summary>
    internal ExplorerListing.Entry? SelectedEntry =>
        EntryOfSelection(IconGrid.SelectedItem) ?? EntryOfSelection(ListPane.SelectedItem);

    private static ExplorerListing.Entry? EntryOfSelection(object? item) =>
        item is FrameworkElement { Tag: ExplorerListing.Entry entry } ? entry : null;

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
            .OfType<FrameworkElement>()
            .Select(i => i.Tag)
            .OfType<ExplorerListing.Entry>()
            .Where(entry => !entry.IsFolder)
            .Select(entry => entry.Path)
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
    private void AttachDragDrop(SelectorItem item, ExplorerListing.Entry entry, ListViewBase owner)
    {
        item.CanDrag = true;
        item.DragStarting += async (_, args) =>
        {
            var deferral = args.GetDeferral();
            try
            {
                if (!await ExplorerFileOps.FillDragDataAsync(args.Data, PathsForDrag(owner, entry)))
                    args.Cancel = true; // 실을 항목이 없다(그새 삭제 등) — 빈 드래그는 시작하지 않는다
            }
            finally
            {
                deferral.Complete();
            }
        };

        if (!entry.IsFolder) return;
        item.AllowDrop = true;
        item.DragOver += (_, e) => ExplorerFileOps.HandleTargetDragOver(e, entry.Path);
        item.Drop += (_, e) => HandleDrop(e, entry.Path);
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

    /// <summary>표면의 선택 항목 경로 전부(폴더 포함) — 항목 = 컨테이너 직접 추가라 Tag에서 꺼낸다.</summary>
    private static IReadOnlyList<string> SelectedPathsOf(ListViewBase owner) =>
        owner.SelectedItems
            .OfType<FrameworkElement>()
            .Select(i => i.Tag)
            .OfType<ExplorerListing.Entry>()
            .Select(e => e.Path)
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
                if (owner.SelectedItem is not SelectorItem { Tag: ExplorerListing.Entry entry }) return;
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
                if (entry.IsFolder) NavigateTo(entry.Path, _extensions);
                else FileActivated?.Invoke(entry.Path);
                return;
            // A158: 셸 패널 키가 F11/F12로 옮겨가 F2 충돌 소멸 — 이름변경은 F2 유지(사용자 확정),
            // "선택이 있을 때만 Handled"라는 기존 소비 규칙도 무변경.
            case Windows.System.VirtualKey.F2: // 이름변경 — 다중 선택이어도 첫 항목(SelectedItem)만
                if (owner.SelectedItem is not SelectorItem selected) return;
                e.Handled = true;
                BeginRenameOf(selected);
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
        if (item.Tag is not ExplorerListing.Entry entry) return;
        if (item.Content is not Panel panel) return; // 편집 상자를 끼울 host
        if (FindItemBlock(item, ItemNameBlockName) is not { } nameBlock) return;
        ExplorerRenameBox.Begin(panel, nameBlock, entry.Path, MakeOpUi(), RefreshAfterFileOp);
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
        await WhenFillCompleteAsync(); // A192 — 분할 조립 완료 뒤에 찾는다(새 항목이 뒤 조각일 수 있다)
        if (FindItemByPath(owner, created) is not { } item) return; // 그새 사라짐 등 — 생성만으로 끝
        owner.SelectedItem = item;
        owner.ScrollIntoView(item);
        owner.UpdateLayout(); // 컨테이너 실체화 — 편집 상자 삽입·포커스가 성립하게
        BeginRenameOf(item);
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
        await WhenFillCompleteAsync(); // A192 — 분할 조립 완료 뒤에 찾는다(새 항목이 뒤 조각일 수 있다)
        if (FindItemByPath(owner, created) is not { } item) return; // 필터 밖·그새 사라짐 — 생성만으로 끝
        owner.SelectedItem = item;
        owner.ScrollIntoView(item);
        owner.UpdateLayout(); // 컨테이너 실체화 — 편집 상자 삽입·포커스가 성립하게
        BeginRenameOf(item);
    }

    /// <summary>경로로 항목 컨테이너 찾기 — 항목 = 컨테이너 직접 추가(Tag = Entry) 구조 전제.</summary>
    private static SelectorItem? FindItemByPath(ListViewBase owner, string path) =>
        owner.Items.OfType<SelectorItem>().FirstOrDefault(i =>
            i.Tag is ExplorerListing.Entry entry &&
            string.Equals(entry.Path, path, StringComparison.OrdinalIgnoreCase));

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
        if (ItemFromSource(e.OriginalSource) is not { Tag: ExplorerListing.Entry entry })
        {
            _lastPress = null; // 빈 영역·스크롤바 — 항목 밖 눌림은 쌍을 끊는다
            return;
        }
        var now = DateTime.UtcNow;
        var isPair = _lastPress is { } last && last.Path == entry.Path &&
                     (now - last.At).TotalMilliseconds < DoubleClickMs;
        _lastPress = isPair ? null : (entry.Path, now);
        if (isPair) Activate(entry, owner);
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
    /// 경로 키인 이유 = Fill 전량 재생성(폴더 감시 400ms 재스캔 포함)을 건너 생존해야 해서
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
        if (ItemFromSource(box) is not { Tag: ExplorerListing.Entry entry }) return;
        if (box.IsChecked == true) _checkedPaths.Add(entry.Path);
        else _checkedPaths.Remove(entry.Path);
    }

    /// <summary>
    /// 포커스 항목의 체크 토글 (A179 — Space 경로. 종전 A157은 IsSelected 토글이었다).
    /// 집합을 먼저 뒤집고 체크박스 시각을 맞춘다 — 그리드 타일(MakeGridItem)에는 체크박스가
    /// 없어 시각 동기가 조용히 생략되지만, 그 경로는 휴면이다(리스트 전용 모드 — 클래스 주석.
    /// 전체 페인 사용처가 되살아나면 타일에도 체크박스를 다는 것이 복구 지점이다).
    /// </summary>
    private void ToggleCheckOf(SelectorItem item)
    {
        if (item.Tag is not ExplorerListing.Entry entry) return;
        var nowChecked = !_checkedPaths.Contains(entry.Path);
        if (nowChecked) _checkedPaths.Add(entry.Path);
        else _checkedPaths.Remove(entry.Path);
        if (FindItemCheckBox(item) is { } box) box.IsChecked = nowChecked;
    }

    /// <summary>
    /// 체크 집합 중 **현재 화면(리스트)에 있는** 경로만 (A179 — 작업 집합의 소비형).
    /// 집합이 아니라 리스트 항목을 기준으로 거르는 이유 = WYSIWYG: 세션 필터(A7)로 가려진
    /// 체크가 조작 대상에 섞이면 화면 밖 항목이 지워지는 사고가 된다. 항목 순서 = 표시 순서라
    /// 일괄 열기(OpenFiles)의 상한 절단도 화면 순서를 따른다.
    /// 리스트(ListPane)만 보는 근거 = 체크박스가 리스트 행에만 있고, 리스트는 그리드와 같은
    /// 폴더·같은 목록을 항상 담는다(Fill이 두 표면을 한 번에 채운다).
    /// </summary>
    private IReadOnlyList<string> CheckedPathsInView(bool filesOnly = false) =>
        ListPane.Items
            .OfType<SelectorItem>()
            .Select(i => i.Tag)
            .OfType<ExplorerListing.Entry>()
            .Where(entry => (!filesOnly || !entry.IsFolder) && _checkedPaths.Contains(entry.Path))
            .Select(entry => entry.Path)
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
        if (e.ClickedItem is not FrameworkElement { Tag: ExplorerListing.Entry entry }) return;

        var now = DateTime.UtcNow;
        var isDouble = _lastClick is { } last && last.Path == entry.Path &&
                       (now - last.At).TotalMilliseconds < DoubleClickMs;
        _lastClick = (entry.Path, now);
        if (!isDouble) return;

        _lastClick = null;
        Activate(entry, sender as ListViewBase);
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
        if (sender is not SelectorItem { Tag: ExplorerListing.Entry entry } item) return;
        e.Handled = true;
        _lastClick = null; // 이 제스처를 이룬 클릭 기록이 다음 클릭 쌍 판정에 섞이지 않게
        // 소속 표면 = 컨테이너 타입으로 결정된다(MakeGridItem = GridViewItem/IconGrid,
        // MakeListItem = ListViewItem/ListPane) — 일괄 열기(A94 6차)가 그 표면의 선택을 본다.
        Activate(entry, item is GridViewItem ? IconGrid : ListPane);
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
    /// 현재 폴더 감시 시작·재대상 (A94 5차): 폴더가 바뀔 때마다 기존 감시자를 통째로 dispose하고
    /// 새로 만든다 — Path 교체 재사용보다 실패 상태가 단순하다(예외가 나도 반쯤 살아 있는 감시자가
    /// 남지 않고, 감시자 생성은 값싸다). NotifyFilter는 최소 구성(FileName·DirectoryName·
    /// LastWrite·Size) — LastAccess류를 빼 Changed 폭주와 "재스캔이 이벤트를 되먹이는" 순환을 피한다.
    /// 생성·시작 실패(네트워크 드라이브·접근 불가·제거된 드라이브·사라진 폴더)는 조용히 무감시 =
    /// 종전과 동일하게 명시 재스캔만 남는다(사양). UI 스레드 전용(NavigateToAsync·Loaded·오류 재시작).
    /// </summary>
    private void EnsureWatch(string folder)
    {
        TearDownWatch(); // 재대상 = 기존 감시·보류 상태 폐기(새 폴더는 곧 전체 스캔으로 시작한다)
        if (!_surfaceLive || folder.Length == 0) return;

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
            _watchQueue = DispatcherQueue; // 풀 스레드에서 컨트롤 프로퍼티 접근 금지 — 여기(UI)서 캡처(OpUi 관용구)
            watcher.EnableRaisingEvents = true; // 접근 불가·소실 경로는 대개 여기서 던진다
            _watcher = watcher;
        }
        catch
        {
            watcher?.Dispose(); // 반쯤 만든 감시자 정리 — 이 폴더는 감시 없이 동작(명시 재스캔만)
        }
    }

    /// <summary>
    /// 감시 해제 (A94 5차): 이벤트 전부 해제 후 Dispose — FileSystemWatcher는 OS 콜백에 뿌리를
    /// 두므로 Dispose 없이는 닫힌 창의 뷰 델리게이트째 살아남는다(정적 이벤트 구독과 같은 부류의
    /// 누수). 디바운스 타이머도 멈춘다 — Unloaded 뒤 Tick이 죽은 뷰를 만지지 않게(핸들러 안
    /// 상태 검사와 이중 방어). 보류 플래그도 버린다 — 재대상이면 곧 전체 스캔이 오고, Unloaded면
    /// 소화할 곳이 없다(편집 커밋 직후의 재스캔이 보류분을 대신 덮는 근거이기도 하다).
    /// </summary>
    private void TearDownWatch()
    {
        if (_watcher is { } watcher)
        {
            _watcher = null;
            watcher.Created -= OnWatcherEvent;
            watcher.Deleted -= OnWatcherEvent;
            watcher.Renamed -= OnWatcherEvent;
            watcher.Changed -= OnWatcherEvent;
            watcher.Error -= OnWatcherError;
            watcher.Dispose();
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
