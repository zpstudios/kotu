using System.Text;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.ApplicationModel.DataTransfer;
using Windows.Storage;
using Windows.Storage.FileProperties;
using Windows.System;
using KOTU.Core.Routing;
using KOTU.Core.Threading;
using KOTU.Input;

namespace KOTU.App.Controls;

/// <summary>
/// 중앙 썸네일 탐색기 뷰 (A93) — S1(콘텐츠 없음·모듈만 실행)의 중앙 구획.
/// A81(v0.101.0)의 "좌 도크 열림 시 중앙 탐색기 숨김"을 대체한다 — S1 중앙은 항상 이 뷰다.
/// 목록은 좌 도크 리스트(ExplorerPane)가 원본: 정렬(A5)·필터(A7)가 적용된 표시 목록을
/// 셸이 ShowEntries로 밀어 넣는다(ExplorerPane.ViewChanged 경유) — 어느 쪽에서 폴더를 바꿔도
/// 둘 다 같은 목록을 그린다. 더블클릭 열기·새 인스턴스 이벤트도 ExplorerPane과 같은 배선이라
/// 셸의 기존 라우팅(OpenFileRouted·A24)을 그대로 쓴다.
/// 열 수 = 8 − 2×(열린 도크 수) → 둘 다 열림 4 · 하나 6 · 없음 8(A213 — 구 A93의 4/8 2단 개정.
/// A63 대체 계보: 크기 고정·열 수 가변이던 종전 규칙을 열 수 고정·크기 가변으로 뒤집은 위에
/// 3단화). 타일 한 변 = floor(실폭/열수).
/// </summary>
public sealed partial class ThumbnailExplorer : UserControl
{
    /// <summary>
    /// 미리보기 요청 폭 상한(물리 px) — 원본 크기 디코드로 메모리가 폭주하지 않게.
    /// 이 파일의 미리보기 3경로가 공유하는 유일한 수치다: ① 이미지 실디코드
    /// (MakeImagePreview의 BitmapImage.DecodePixelWidth) ② placeholder 캐시 전용 셸 썸네일
    /// (FillCachedThumbnailAsync) ③ 워커 지연 교체 셸 썸네일(FetchTilePreview) — 셋이 같은 값을
    /// 써야 같은 파일이 경로에 따라 다른 선명도로 뜨는 일이 없다.
    /// <b>A275(v0.272.0): 256 → 768.</b> 타일 한 변 = floor(중앙 실폭 ÷ 열수)라 큰 창·고DPI에서
    /// 256은 업스케일이었다(예: 2560px 폭·4열이면 타일 640 → 256을 2.5배 늘려 그린다).
    /// 768은 셸 썸네일 캐시가 실제로 굽는 버킷 상단(1024 아래 최대 상용 버킷)이라 요청이
    /// 버킷에 맞아떨어지고, 3열~4열 배치의 타일 실크기를 대부분 덮는다. 메모리는 픽셀 수 기준
    /// 대략 9배(768² ÷ 256²)로 늘지만 타일은 화면에 보이는 수십 장 규모라 수용한다.
    /// 화면 폭·RasterizationScale로 매번 계산하는 동적 산식은 기각했다 — 요청 폭이 폴더·창마다
    /// 흔들리면 셸 썸네일 캐시가 버킷마다 새로 구워져 첫 표시가 느려지고, 캐시 적중률이 떨어진다.
    /// ※ 앱 .ico 자산(16~256 7종)과는 무관하다 — 이 상수는 파일 미리보기 전용이다.
    /// ※ 창 아이콘 2프레임(16/32)의 DPI 추종은 이 항목 범위 밖(A275 ② 별건 후보).
    /// </summary>
    private const int PreviewDecodeWidth = 768;

    /// <summary>A233: 텍스트 프리뷰가 파일 앞에서 읽는 상한(바이트) — 타일에 이 이상 안 보이므로
    /// 전체 읽기는 낭비다(대형 파일 보호 — File.ReadAllText 금지, FileStream 부분 읽기).</summary>
    private const int TextPreviewMaxBytes = 4096;

    /// <summary>A233: 텍스트 프리뷰 표시 줄 수 상한 — 읽기 상한과 같은 근거(타일 크기).</summary>
    private const int TextPreviewMaxLines = 12;

    /// <summary>A233: 텍스트 읽기 동시 상한 — ExplorerPane.FetchConcurrency(A194)와 같은 값·
    /// 같은 근거(수백 파일 폴더에서 IO 폭주 방지). 풀 워커 수와 게이트 초기값이 함께 쓴다.</summary>
    private const int TextPreviewConcurrency = 3;

    /// <summary>A242: 셸 썸네일 추출 동시 상한 — A233과 같은 A194 구조(풀 워커 수 = 게이트
    /// 초기값). 텍스트 풀과 분리한 이유: 셸 썸네일 추출(영상 프레임 디코드 등)은 건당 수백 ms까지
    /// 느릴 수 있어, 같은 풀에 섞으면 가벼운 텍스트 앞부분 읽기가 그 뒤에 줄을 선다.</summary>
    private const int ThumbFetchConcurrency = 3;

    /// <summary>더블클릭 판정 창 — ExplorerPane.DoubleClickMs와 같은 값(같은 감각).</summary>
    private const int DoubleClickMs = 500;

    /// <summary>
    /// A192 체감 조정 지점: 분할 조립 조각 크기(타일 수) — 첫 즉시 조각과 프레임당 append 조각이
    /// 같은 값을 쓴다(확정 수치 60 — DocumentView.RenderChunkBlocks의 상수 배치 관용구.
    /// 되돌리기·조정은 이 상수 하나만 고치면 된다).
    /// </summary>
    private const int TileChunkItems = 60;

    /// <summary>
    /// A339: 미리보기를 <b>즉시</b> 만드는 앞쪽 타일 수 — 첫 조각(<see cref="TileChunkItems"/>)과
    /// 같은 값이다. 첫 화면은 뷰포트 이벤트를 기다리지 않고 곧바로 채워야 체감이 종전과 같고,
    /// 뷰포트 이벤트가 어떤 이유로 오지 않아도 첫 화면은 항상 채워진다(방어).
    /// </summary>
    private const int EagerPreviewCount = TileChunkItems;

    /// <summary>
    /// A339: 뷰포트 밖 타일을 미리 채우기 시작하는 거리(DIP) — 스크롤을 시작하는 순간 이미
    /// 만들어져 있게 하는 선반입 여유다. 0으로 두면 화면에 들어온 뒤에야 읽기가 시작돼
    /// 스크롤 중 빈 타일이 눈에 띈다.
    /// </summary>
    private const double PreviewPrefetchDip = 600;

    /// <summary>
    /// A339: 타일 미리보기 생성을 <b>보이는 것(과 곧 보일 것)만</b>으로 미룬다.
    /// <para>
    /// 왜: 실체화 상한(<see cref="MaterializeLimit"/>) 2,000개 타일이 전부 파일을 읽어 미리보기를
    /// 만들고 있었지만 화면에 보이는 것은 열여섯 개 남짓이다. A334 계측판이 그 비용을 실측했다 —
    /// txt 10,000개 폴더에서 <c>prev0&gt;fillN 3,703ms</c>(목록 항목 생성과 미리보기 얹기가 같은
    /// 구간에서 겹쳐 돈다)와 <c>UI stall max 488ms @prev0&gt;fillN</c>. 나머지 1,984개는 스크롤하지
    /// 않으면 아무도 보지 않는다.
    /// </para>
    /// <para>
    /// 앞쪽 <see cref="EagerPreviewCount"/>개는 종전대로 즉시 채운다 — 판정은 지금 몇 번째 타일을
    /// 만드는 중인가이고, 그 값이 곧 <c>TileGrid.Items.Count</c>다(타일은 순서대로 append되고
    /// 이 함수는 Add <b>앞에서</b> 불린다). 별도 인덱스를 네 갈래에 흘려 넣지 않아도 되는 이유다.
    /// </para>
    /// 나머지는 <c>EffectiveViewportChanged</c>로 미룬다: 뷰포트까지 남은 거리가
    /// <see cref="PreviewPrefetchDip"/> 이내로 들어오면 채우고 <b>구독을 즉시 해제</b>한다
    /// (한 타일당 한 번만 채운다 — 스크롤을 오갈 때 같은 타일을 다시 읽지 않는다).
    /// 낡음 방어는 종전 그대로다: 채우는 쪽 클로저가 <c>_showSeq</c>를 들고 있어 폴더가 바뀌면
    /// 그 완료가 버려지고, 타일 자체도 <c>ShowEntries</c>가 전량 새로 만든다.
    /// </summary>
    private void DeferPreview(Grid host, Action fill)
    {
        if (TileGrid.Items.Count < EagerPreviewCount)
        {
            fill();
            return;
        }
        void OnViewport(FrameworkElement sender, EffectiveViewportChangedEventArgs args)
        {
            if (args.BringIntoViewDistanceX > PreviewPrefetchDip ||
                args.BringIntoViewDistanceY > PreviewPrefetchDip) return;
            host.EffectiveViewportChanged -= OnViewport;
            fill();
        }
        host.EffectiveViewportChanged += OnViewport;
    }

    /// <summary>
    /// A192: 타일 실체화 상한(ExplorerPane.MaterializeLimit와 같은 값) — 초과분은 타일을 만들지
    /// 않고 말미에 비상호작용 안내 1타일만 붙인다(MakeOverflowNotice). 상한은 컨테이너 실체화에만
    /// 걸린다 — ShowEntries로 받는 Entry 목록 자체는 전체 그대로다(원본은 좌 리스트, A93).
    /// </summary>
    private const int MaterializeLimit = 2000;

    /// <summary>폴더 더블클릭 — 셸이 좌 리스트를 그 폴더로 항해시킨다(상태 공유의 되돌이 경로).</summary>
    public event Action<string>? FolderActivated;

    /// <summary>파일 더블클릭 열기 — 셸이 재사용 규칙(A24)을 적용해 라우팅한다.</summary>
    public event Action<string>? FileActivated;

    /// <summary>명시적 새 창 열기(A24: Shift+더블클릭·우클릭 메뉴) — 셸이 항상 새 창으로.</summary>
    public event Action<string>? FileActivatedNewWindow;

    /// <summary>
    /// 타일 선택 변경 (A200) — 셸이 우측 정보 패널의 "선택 우선" 표시에 쓴다. 인자 없음:
    /// 셸이 <see cref="SelectedEntry"/>를 질의한다(선택 상태의 원본은 그리드 하나 — A86/A90의
    /// 질의 API 관례). 목록 재구축(ShowEntries)·다중 선택 조작에서도 그리드가 알아서 발화한다.
    /// </summary>
    public event Action? SelectionChanged;

    /// <summary>
    /// 파일 경로 → 담당 모듈 ID (액센트 색 타일용). 셸이 라우터로 주입한다 —
    /// 이 컨트롤이 FileTypeRouter를 직접 알면 DI 없이 못 만드는 컨트롤이 된다.
    /// </summary>
    public Func<string, string?>? ModuleIdForFile { get; set; }

    private int _columns = 8; // 기본 = 도크 둘 다 닫힘(전폭) 기준 — 셸이 곧 SetColumns(4/6/8)로 덮는다

    /// <summary>A192: 조립 재진입 가드 — ShowEntries가 올 때마다 증가(ExplorerPane._loadSeq 관용구).
    /// 진행 중 루프의 틱은 append 직전에 이 값과 대조해 낡은 조각을 버린다.</summary>
    private int _showSeq;

    /// <summary>
    /// A192: 분할 조립 루프의 프레임 틱 핸들러(null = 루프 없음). CompositionTarget.Rendering은
    /// static 이벤트라 뷰 수명 안에서 반드시 해제한다 — 남기면 닫힌 뷰가 통째로 누수된다
    /// (DocumentView._renderAppendHandler와 같은 사정). 해제의 단일 지점 = StopTileAppendLoop.
    /// 호출부 전수 = Unloaded·ShowEntries 기동 직전 방어·틱 내부(완료/seq 중단/예외).
    /// </summary>
    private EventHandler<object>? _tileAppendHandler;

    /// <summary>
    /// A233: 텍스트 프리뷰 읽기 전용 풀 — ExplorerPane._fetchPool과 같은 규칙(A194: 항목별
    /// 독립 작업만·지연 생성·Unloaded에서 Dispose 후 다시 로드되면 되살아난다). UI 스레드에서
    /// await하면 완료 시 UI 스레드로 복귀한다(ModuleWorker 계약 — 완료 반영의 seq 재대조가
    /// 디스패치 없이 성립하는 근거).
    /// </summary>
    private ModuleWorkerPool? _textPool;

    /// <summary>
    /// A233: 텍스트 읽기 발사 게이트 — 동시 TextPreviewConcurrency건까지만
    /// (ExplorerPane.LoadDetailInfoAsync의 SemaphoreSlim 게이트와 같은 A194 관용구.
    /// 그쪽은 발사 루프 지역 변수지만 여기는 타일 생성 시점마다 개별 예약이라 인스턴스 필드다 —
    /// 재스캔(A131 빈발)을 건너 상한이 유지되고, 낡은 예약은 게이트 통과 시점의 seq 대조로
    /// 발사 없이 접힌다 = 자연 배압. static 아님 — 뷰 수명과 함께 버려진다).
    /// </summary>
    private readonly SemaphoreSlim _textReadGate = new(TextPreviewConcurrency);

    /// <summary>A242: 셸 썸네일 추출 전용 풀 — _textPool과 같은 규칙(A194: 항목별 독립 작업만·
    /// 지연 생성·Unloaded에서 Dispose 후 다시 로드되면 되살아난다. ModuleWorker 계약: UI 스레드
    /// await 후속부는 UI 스레드 복귀 — 완료 반영의 seq 재대조가 디스패치 없이 성립).</summary>
    private ModuleWorkerPool? _thumbPool;

    /// <summary>A242: 셸 썸네일 발사 게이트 — _textReadGate와 같은 A194 관용구(타일 생성 시점마다
    /// 개별 예약이라 인스턴스 필드·낡은 예약은 게이트 통과 시점 seq 대조로 발사 없이 접힌다 =
    /// 자연 배압. static 아님 — 뷰 수명과 함께 버려진다).</summary>
    private readonly SemaphoreSlim _thumbFetchGate = new(ThumbFetchConcurrency);

    private (string Path, DateTime At)? _lastClick;
    private (string Path, DateTime At)? _lastActivation; // A85: ItemClick 쌍·DoubleTapped 겹침을 1회로 억제
    private (string Path, DateTime At)? _lastPress;      // A131: 원시 눌림 쌍 — 항목 재구축을 건너 살아남는 최후 폴백

    /// <summary>
    /// Ctrl+Shift+N(새 폴더) 직후의 편집 진입 예약 (A94 2차). 이 뷰의 재스캔은 좌 리스트 경유
    /// 비동기(FolderActivated → 셸 → ViewChanged → ShowEntries)라 완료 시점을 직접 기다릴 수 없다 —
    /// 다음으로 <b>완주한</b> 조립(FinishShowEntries — A192에서 분할 조립 완료 시점으로 이동)이
    /// 이 경로의 타일을 찾아 이름변경 편집으로 진입하고 지운다(1회성 — 그 타일이 뒤 조각에
    /// 있을 수 있어 조립 도중에는 소비하지 않는다).
    /// </summary>
    private string? _pendingRenamePath;

    /// <summary>
    /// 지금 그리고 있는 폴더 경로 (A94 — 빈 영역 드랍·붙여넣기의 대상). ShowEntries가 좌 리스트의
    /// ViewChanged에서 받은 폴더로 갱신한다 — 이 컨트롤은 폴더 상태의 원본이 아니다(A93).
    /// </summary>
    public string? CurrentFolder { get; private set; }

    /// <summary>
    /// 선택된 파일 타일의 경로 — 폴더·무선택이면 null (A86: 셸 Enter "선택 파일 있으면 열기").
    /// A94(Extended)부터 다중 선택이 가능하지만 이 속성은 첫 선택(SelectedItem) 기준을 유지한다.
    /// ※ A94 6차(v0.153.0)부터 일괄 열기는 <see cref="OpenSelectedFiles"/> —
    /// 이 속성은 "첫 선택 파일" 질의 API로만 남았다(A86 서술의 원형).
    /// </summary>
    public string? SelectedFilePath =>
        TileGrid.SelectedItem is FrameworkElement { Tag: ExplorerListing.Entry { IsFolder: false } entry }
            ? entry.Path : null;

    /// <summary>선택된 항목(파일·폴더 불문) — 없으면 null (A90: S4 Enter "선택 열기 우선" 판정).</summary>
    public ExplorerListing.Entry? SelectedEntry =>
        TileGrid.SelectedItem is FrameworkElement { Tag: ExplorerListing.Entry entry } ? entry : null;

    /// <summary>
    /// A336: 선택 표시를 걷는다 — 셸이 <b>다른 표면</b>(좌 리스트)에서 선택이 일어났을 때 부른다.
    /// 선택 축은 하나뿐이고(A200 _selectedBrowse) 우측 정보 패널도 한 파일만 보여 주므로,
    /// 표시도 한 표면에만 남아야 한다(사용자 확정 — "이 때 썸네일 뷰 속 선택 표시는 사라져야 함").
    /// <para>
    /// 이 대입은 GridView의 SelectionChanged를 발화시켜 셸의 <c>SelectionChanged</c> 중계까지
    /// 올라간다 — 그 되먹임은 <b>셸 쪽 표지</b>(MainWindow._syncingBrowseSelection)가 끊는다.
    /// 여기서 끊지 않는 이유: 두 표면을 동기화하는 주체가 셸이라 표지도 셸이 들고 있어야 축이
    /// 한 곳에서만 판정된다(표면끼리 직접 지우는 양방향 결선을 피한 A336의 설계 근거).
    /// </para>
    /// 이 표면에는 A323의 "열린 콘텐츠 표시"가 없다 — 중앙 썸네일 뷰는 콘텐츠가 없을 때(S1)만
    /// 뜨고, S4 그리드는 탐색 전용이다. 그래서 좌 리스트와 달리 되돌릴 표시가 없고 비우면 끝이다.
    /// </summary>
    internal void ClearSelection()
    {
        if (TileGrid.SelectedItems.Count == 0) return; // 이미 없음 — 잉여 발화도 만들지 않는다
        TileGrid.SelectedItems.Clear();
    }

    /// <summary>A233: 지연 생성 — Unloaded로 정리된 뒤 다시 로드돼도 되살아난다
    /// (ExplorerPane.FetchPool과 같은 규칙. 워커 수 = 동시 읽기 상한).
    /// A333: 우선순위 BelowNormal — 타일 내용은 이미 그려진 타일에 뒤늦게 얹히는 배경성 작업이다
    /// (근거·불변식은 ExplorerPane.FetchPool 주석 한 곳에 모아 뒀다).</summary>
    private ModuleWorkerPool TextPool =>
        _textPool ??= new ModuleWorkerPool(
            "KOTU tile text", TextPreviewConcurrency, ThreadPriority.BelowNormal);

    /// <summary>A242: 지연 생성 — TextPool과 같은 규칙(워커 수 = 동시 추출 상한, A333 우선순위 포함).</summary>
    private ModuleWorkerPool ThumbPool =>
        _thumbPool ??= new ModuleWorkerPool(
            "KOTU tile thumb", ThumbFetchConcurrency, ThreadPriority.BelowNormal);

    public ThumbnailExplorer()
    {
        InitializeComponent();
        // A34: 타일 그리드에 포커스가 있어도 모듈 버튼 핫키는 통과 — 타이핑 탐색(첫 글자 점프) 우선
        // (ExplorerPane의 IconGrid·ListPane과 같은 규칙). A90의 S4 키맵("A34 문자 핫키 = 무동작")도
        // 이 태그 하나로 충족된다 — S4 그리드에 포커스가 있는 동안 HotkeySupport가 전부 통과시킨다.
        TileGrid.Tag = HotkeySupport.PassThroughTag;
        // A90: Enter = 선택 항목 열기 (keymap S1 "선택 파일 있으면 열기"·S4 "선택 열기 우선"의
        // 그리드 쪽 구현). GridView의 기본 Enter 처리(ItemClick — 이 클래스에선 더블클릭 판정에만
        // 쓰여 단발 Enter로는 안 열린다)가 이벤트를 Handled로 만들 수 있어 handledEventsToo로 받는다
        // (MainWindow의 루트 KeyDown 구독과 같은 관용구).
        TileGrid.AddHandler(UIElement.KeyDownEvent,
            new KeyEventHandler(OnGridKeyDown), handledEventsToo: true);
        // A131: 원시 눌림 쌍 폴백 — 아래 두 더블클릭 판정(ItemClick 쌍·DoubleTapped)은 둘 다 항목
        // 컨테이너 수명에 묶여 있어, 두 클릭 사이·클릭 도중에 목록 재구축(A94 5차 폴더 감시 재스캔
        // 등 — ShowEntries가 타일을 전부 새로 만든다)이 끼면 눌림·뗌이 다른 요소가 되어 클릭이
        // 성립하지 않고(ItemClick 침묵) 새 컨테이너에는 제스처 상태가 없어 DoubleTapped도 뜨지
        // 않는다 — 열기 요청이 셸에 도달하지 못한 채 완전 침묵(압축 모듈 zip 무반응으로 관측).
        // 눌림은 요소 교체와 무관하게 매번 도착하므로 경로 키 판정이 재구축을 건너 살아남는다.
        // handledEventsToo = 리스트가 눌림을 소비해도 판정은 돌아야 한다(셸 A58 홀드 취소 구독과
        // 같은 관용구). Handled는 건드리지 않는다 — 순수 관찰(선택·드래그·제스처 무간섭).
        // A212: 구독 지점을 TileGrid → LayoutRoot로 올렸다. 타일 밖 빈 영역 눌림은 그리드에
        // 배경이 없으면 TileGrid 서브트리에 히트되지 않고 배경 있는 LayoutRoot가 원본이 된다
        // (아래 ContextFlyout를 LayoutRoot에 거는 "히트 보장"과 같은 근거) — 종전 TileGrid 구독은
        // 그 눌림을 아예 못 봤다. 타일 위 눌림은 버블링 + handledEventsToo로 종전과 동일하게
        // 도착하므로 A131 쌍 판정은 무변경이고, 빈 영역 눌림의 포커스 정착(A212 —
        // OnSurfacePointerPressed의 빈 영역 분기)이 이 구독으로 성립한다.
        LayoutRoot.AddHandler(UIElement.PointerPressedEvent,
            new PointerEventHandler(OnSurfacePointerPressed), handledEventsToo: true);
        // A94 4차: 잘라내기 표시(프로세스 전역 1벌)가 바뀌면 이미 그려 둔 타일의 흐림만 다시 맞춘다.
        // 구독을 Loaded/Unloaded로 묶는 이유 = 정적 이벤트가 닫힌 창의 컨트롤을 붙들지 않게
        // (ExplorerPane과 같은 수명 규칙). 중복 구독은 -= 선행으로 막는다.
        Loaded += (_, _) =>
        {
            ExplorerFileOps.CutMarksChanged -= ApplyCutMarks;
            ExplorerFileOps.CutMarksChanged += ApplyCutMarks;
        };
        Unloaded += (_, _) =>
        {
            ExplorerFileOps.CutMarksChanged -= ApplyCutMarks;
            StopTileAppendLoop(); // A192 — CompositionTarget.Rendering은 static: 남기면 닫힌 뷰 통째 누수
            // A233: 보류 텍스트 읽기 전부 무산 — seq를 올리면 게이트 대기 중이던 예약이 깨어나도
            // 발사 없이 접힌다(대조 실패). 풀을 먼저 닫으면 진행 중 읽기는 워커가 마저 끝내고
            // 스레드 종료(ModuleWorker 계약), 그 결과도 seq 대조가 버린다. 풀을 null로 두면
            // 재로드 시 TextPool이 지연 재생성한다(ExplorerPane Unloaded와 같은 정리 규칙).
            // seq 선증가가 중요: 닫힌 풀의 Run은 취소 Task라 어차피 무해지만, 낡은 예약이
            // 지연 재생성으로 새 풀을 되살리는 길을 이 한 줄이 막는다.
            _showSeq++;
            _textPool?.Dispose();
            _textPool = null;
            _thumbPool?.Dispose(); // A242 — 텍스트 풀과 같은 정리 규칙(보류 무산은 위 seq 선증가가 겸한다)
            _thumbPool = null;
        };
        // A200: 선택 변경을 셸로 중계 — 우측 정보 패널의 선택 우선 표시(파일 정보 직접 조회)용.
        // 그리드 자체 이벤트를 얇게 감싸기만 한다(선택 판정·해석은 셸 몫 — SelectedEntry 질의).
        TileGrid.SelectionChanged += (_, _) => SelectionChanged?.Invoke();
        // A94 6차: 빈 영역(타일이 아닌 곳) 우클릭 메뉴 — New folder / Paste / Refresh.
        // 타일 메뉴와의 이중 발화는 ContextFlyout 규칙이 원천 차단한다: 컨텍스트 요청은 원본
        // 요소에서 위로 버블링하며 **가장 안쪽의 ContextFlyout 하나만** 뜨므로, 타일 위 우클릭은
        // 타일 컨테이너(AttachContextMenu)가 받고 여기까지 오지 않는다. 배경이 있는 LayoutRoot에
        // 거는 이유 = 그리드 자체 배경이 없어도 요청이 반드시 여기까지 올라오기 때문(히트 보장).
        LayoutRoot.ContextFlyout = MakeSurfaceMenu();
    }

    /// <summary>
    /// 잘라내기(Ctrl+X) 표시 반영 (A94 4차): 이미 그려 둔 타일의 콘텐츠 투명도를 경로 매칭으로
    /// 다시 맞춘다 — 재스캔이 아니라 제자리 갱신이라 선택·스크롤이 보존된다. 새로 그려지는 타일은
    /// MakeTile이 같은 규칙(ExplorerFileOps.ApplyCutMark)으로 처음부터 반영한다.
    /// </summary>
    private void ApplyCutMarks()
    {
        foreach (var item in TileGrid.Items) ExplorerFileOps.ApplyCutMark(item);
    }

    // A176: 구 UseTranslucentBackground(S4 중앙 반투명 — A33 아크릴/A129 스왑체인 폴백)는
    // 반투명 축과 함께 철거됐다. A316: S4 중앙 반투명이 돌아왔지만 이 API는 되살리지 않는다 —
    // 배경은 이제 호스트 몫이다(LayoutRoot = Transparent 공통 1벌, S1은 ExplorerHost 불투명 /
    // S4는 셸 S4CenterBackdrop 반투명 — XAML LayoutRoot 주석 참고). 인스턴스 상태 분기 0.

    /// <summary>썸네일 그리드로 포커스 이동 (A90: S4 진입 시) — 실패해도 무해(포커스만 안 옮겨진다).</summary>
    public void FocusGrid() => TileGrid.Focus(FocusState.Programmatic);

    /// <summary>
    /// Enter = 선택 항목 열기 (A90 — 위 생성자 주석 참고. 선택이 없으면 셸 분배로 흘린다) +
    /// 클립보드 키 (A94): Ctrl+C/X/V/A + 2차(v0.125.0): F2 = 이름변경(첫 선택 타일만),
    /// Del = 휴지통 삭제, Ctrl+Shift+N = 새 폴더 — 이 그리드에 포커스가 있을 때만 온다
    /// (KeyDown 버블링이라 문서 에디터 등 텍스트 표면으로 새지 않고, A34 통과 규칙과도 겹치지 않는다).
    /// 4차(v0.151.0): Shift+Del = 영구 삭제(확인 대화상자 뒤), Esc = 잘라내기 표시 해제(비소비).
    /// 6차(v0.153.0): Enter가 **다중 선택이면 선택된 파일 전부**를 연다(폴더 제외 — 아래 주석).
    /// </summary>
    private async void OnGridKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.KeyStatus.WasKeyDown) return;
        // A94 2차: 이름변경 편집 상자(TextBox) 안의 키는 전부 편집 몫 — handledEventsToo 구독이라
        // 편집 상자가 Handled를 걸어도 여기까지 오므로 원본 요소로 걸러낸다
        // (Enter가 '항목 열기'로, Del·Ctrl+A/V가 파일 조작으로 새면 안 된다).
        if (e.OriginalSource is TextBox) return;
        if (e.Key == VirtualKey.Enter)
        {
            if (SelectedEntry is not { } entry) return; // 선택 없음 — 비소비(A151: 탐색기 표면 포커스의 Enter는 셸도 양보라 무동작)
            e.Handled = true; // 다른 버블 수신자 방지 — 셸(A274 터널링 OnShellEnter)은 ShouldPassThrough로 이 표면에 이미 양보했다
            _lastClick = null; // 같은 Enter가 만든 ItemClick 기록이 더블클릭 판정에 섞이지 않게
            // A94 6차: 다중 선택이면 선택된 '파일' 전부를 연다(폴더는 일괄 열기에서 제외).
            // 선택에 파일이 하나도 없으면(폴더만 다중) 아래 현행 첫 항목 동작으로 떨어진다.
            if (TileGrid.SelectedItems.Count > 1 && OpenFiles(SelectedFilePaths())) return;
            if (entry.IsFolder) FolderActivated?.Invoke(entry.Path);
            else FileActivated?.Invoke(entry.Path);
            return;
        }

        // A94 2차: F2 = 이름변경(첫 선택 타일 1개 — 다중 선택이어도 첫 항목만), Del = 휴지통 삭제.
        // A158: 셸 패널 키가 F1/F2에서 F11/F12로 옮겨가 F2 충돌은 소멸했다 — 이름변경은 F2 그대로
        // 유지하고(사용자 확정), "선택이 있을 때만 Handled"라는 기존 소비 규칙도 그대로 둔다.
        if (e.Key == VirtualKey.F2)
        {
            if (TileGrid.SelectedItem is not GridViewItem selected) return;
            e.Handled = true;
            BeginRenameOf(selected);
            return;
        }
        if (e.Key == VirtualKey.Delete)
        {
            // Del = 휴지통 / Shift+Del = 영구 삭제(A94 4차). Ctrl+Del은 우리 조합이 아니라 비켜 준다.
            if (ExplorerFileOps.IsCtrlDown()) return;
            var targets = SelectedPaths();
            if (targets.Count == 0) return;
            e.Handled = true;
            if (ExplorerFileOps.IsShiftDown()) await PermanentDeleteWithConfirmAsync(targets);
            else await DeleteWithNoticeAsync(targets);
            return;
        }
        if (e.Key == VirtualKey.Escape)
        {
            // A94 4차 — 잘라내기 표시 해제(탐색기 동등). A202 개정: **실제로 지운 표시가 있을
            // 때만 소비**한다(ExplorerPane.OnSurfaceKeyDown의 Esc와 같은 규칙 — 무조건 흘리면
            // 셸 Esc의 새 콘텐츠 닫기 층과 겹쳐 한 번에 두 층이 움직인다). 지울 게 없으면
            // 종전대로 흘려 셸 체인(전체화면 → S4 복귀 → 콘텐츠 닫기)이 받는다.
            // 클립보드 자체는 건드리지 않는다.
            if (ExplorerFileOps.ClearCutMarks()) e.Handled = true;
            return;
        }

        if (!ExplorerFileOps.IsCtrlDown()) return;
        switch (e.Key)
        {
            case VirtualKey.N: // Ctrl+Shift+N = 새 폴더 (Shift 없는 Ctrl+N 아님 —
                // 앱 전역 Shift+N 새 창(A84)과도 다른 조합. 판정 = Ctrl(위) && Shift && N)
                if (!ExplorerFileOps.IsShiftDown() || CurrentFolder is not { Length: > 0 }) return;
                e.Handled = true;
                await CreateFolderThenRenameAsync();
                break;
            case VirtualKey.A:
                e.Handled = true;
                TileGrid.SelectAll(); // Extended 모드 전제
                break;
            case VirtualKey.C:
            case VirtualKey.X:
                var paths = SelectedPaths();
                if (paths.Count == 0) return;
                e.Handled = true;
                await CopyWithNoticeAsync(paths, cut: e.Key == VirtualKey.X);
                break;
            case VirtualKey.V:
                if (CurrentFolder is not { Length: > 0 } folder) return;
                e.Handled = true;
                await PasteIntoAsync(folder);
                break;
        }
    }

    /// <summary>
    /// Ctrl+Shift+N·빈 영역 메뉴 New folder (A94 2차 본문을 6차에서 메서드로 분리 — 동작 무변경):
    /// "New folder" 생성(충돌 = "New folder (2)") 후 재스캔을 예약하고, 그 결과가 돌아오면
    /// (ShowEntries) 그 타일로 이름변경 편집에 진입한다. 이 뷰의 재스캔은 좌 리스트 경유 비동기라
    /// 완료를 직접 기다릴 수 없어 <see cref="_pendingRenamePath"/> 예약 방식이다.
    /// </summary>
    private async Task CreateFolderThenRenameAsync()
    {
        if (CurrentFolder is not { Length: > 0 } parent) return;
        var (created, notice, denied) = ExplorerFileOps.CreateFolder(parent);
        if (notice is not null) await ExplorerFileOps.ReportAsync(notice, denied ? 1 : 0, MakeOpUi());
        if (created is null) return;
        _pendingRenamePath = created; // 재스캔 결과(ShowEntries)가 돌아오면 그 타일로 편집 진입
        FolderActivated?.Invoke(parent); // 단일 원본(좌 리스트) 경유 재스캔 — A93 경로
    }

    /// <summary>
    /// 빈 영역 메뉴 New file (A189 — 위 CreateFolderThenRenameAsync의 파일 판본, 흐름 동일):
    /// "New file.txt" 생성(충돌 = "New file (2).txt") 후 재스캔을 예약하고, 그 결과가 돌아오면
    /// 그 타일로 이름변경 편집에 진입한다. 감시(A94 5차) 재스캔·편집 중 보류(EditEnded)는
    /// New folder와 같은 경로를 그대로 타므로 별도 처리가 없다. 현재 목록이 모듈 확장자로
    /// 필터돼 .txt가 안 보이는 모듈에서는 파일만 만들어지고 편집 진입은 조용히 생략된다
    /// (_pendingRenamePath 미매칭 — New folder의 "그새 사라짐" 폴백과 같은 무해 경로).
    /// </summary>
    private async Task CreateFileThenRenameAsync()
    {
        if (CurrentFolder is not { Length: > 0 } parent) return;
        var (created, notice, denied) = ExplorerFileOps.CreateFile(parent);
        if (notice is not null) await ExplorerFileOps.ReportAsync(notice, denied ? 1 : 0, MakeOpUi());
        if (created is null) return;
        _pendingRenamePath = created; // 재스캔 결과(ShowEntries)가 돌아오면 그 타일로 편집 진입
        FolderActivated?.Invoke(parent); // 단일 원본(좌 리스트) 경유 재스캔 — A93 경로
    }

    /// <summary>
    /// 클립보드 적재 공용 (A94 6차 — Ctrl+C/X와 우클릭 메뉴 Cut/Copy가 같은 경로).
    /// 잘라내기 반투명 표시(4차)는 ExplorerFileOps가 적재 성공 시에만 갱신한다.
    /// </summary>
    private async Task CopyWithNoticeAsync(IReadOnlyList<string> paths, bool cut)
    {
        if (paths.Count == 0) return;
        if (await ExplorerFileOps.CopyToClipboardAsync(paths, cut) is { } notice) ShowNotice(notice);
    }

    /// <summary>
    /// 붙여넣기 공용 (A94 6차 — Ctrl+V·빈 영역 메뉴는 현재 폴더, 폴더 타일 메뉴는 그 폴더).
    /// 갱신은 종전대로 단일 원본(좌 리스트) 경유 재스캔 1회 — A93 경로.
    /// </summary>
    private async Task PasteIntoAsync(string targetFolder)
    {
        if (targetFolder.Length == 0) return;
        var ui = MakeOpUi(); // A94 3차 — 충돌 대화상자·진행 문구, 4차 — 접근 거부 안내
        var (didWork, result, notice) = await ExplorerFileOps.PasteFromClipboardAsync(targetFolder, ui);
        if (didWork) RefreshViaShell();
        await ExplorerFileOps.ReportAsync(notice, result.Denied, ui);
    }

    /// <summary>선택 타일 경로 전부(폴더 포함) — 항목 = 컨테이너 직접 추가라 Tag에서 꺼낸다(A94).</summary>
    private IReadOnlyList<string> SelectedPaths() =>
        TileGrid.SelectedItems
            .OfType<FrameworkElement>()
            .Select(i => i.Tag)
            .OfType<ExplorerListing.Entry>()
            .Select(e => e.Path)
            .ToList();

    /// <summary>선택 타일 중 **파일**만의 경로 (A94 6차 — 일괄 열기 대상. 폴더는 제외한다).</summary>
    private IReadOnlyList<string> SelectedFilePaths() =>
        TileGrid.SelectedItems
            .OfType<FrameworkElement>()
            .Select(i => i.Tag)
            .OfType<ExplorerListing.Entry>()
            .Where(e => !e.IsFolder)
            .Select(e => e.Path)
            .ToList();

    /// <summary>
    /// 잡은 타일의 조작 대상 (A94: 드래그·삭제 규칙 — 그 타일이 선택에 포함돼 있으면 선택 전부,
    /// 아니면 그 타일 하나). 6차에서 Cut·Copy도 같은 규칙을 쓰게 메서드로 뽑았다(동작 무변경).
    /// </summary>
    private IReadOnlyList<string> PathsFor(ExplorerListing.Entry entry)
    {
        var selected = SelectedPaths();
        return selected.Contains(entry.Path, StringComparer.OrdinalIgnoreCase) ? selected : [entry.Path];
    }

    /// <summary>
    /// 선택 파일 일괄 열기 (A94 6차) — 종전 "SelectedFilePath 하나를 OpenFileRouted"를 대체한다.
    /// 그리드 자체 Enter·더블클릭과 같은 규칙(아래 OpenFiles).
    /// ※ A151: 셸 Enter가 모드 순환이 되면서 셸 호출부는 사라졌다 — 그리드 자체 Enter 처리와
    /// 대칭인 공개 실행 API로 남긴다(외부 소비자 0인 상태 유지 무해).
    /// </summary>
    public bool OpenSelectedFiles() => OpenFiles(SelectedFilePaths());

    /// <summary>
    /// 일괄 열기 실행 (A94 6차): 상한(10) 적용 뒤 **첫 파일 = 기존 단일 열기 경로**
    /// (newWindowFirst면 Shift+더블클릭과 같은 새 인스턴스, 아니면 셸이 재사용 규칙 A24 적용),
    /// **나머지 = 전부 새 인스턴스**. 창 생성은 기존 이벤트(FileActivated·FileActivatedNewWindow)로만
    /// 나가므로 이 컨트롤은 창 규칙을 알지 않는다. 루프를 동기로 도는 근거 = 창 생성·파일 열기가
    /// 단일 UI 스레드에서 동기 완결이라는 A124 복원 루프의 전례(WindowManager.TryRestoreSession).
    /// 반환 = 하나라도 열었는지.
    /// </summary>
    private bool OpenFiles(IReadOnlyList<string> files, bool newWindowFirst = false)
    {
        if (files.Count == 0) return false;
        var batch = ExplorerFileOps.TakeBatchOpen(files, ShowNotice);
        if (newWindowFirst) FileActivatedNewWindow?.Invoke(batch[0]);
        else FileActivated?.Invoke(batch[0]);
        for (var i = 1; i < batch.Count; i++) FileActivatedNewWindow?.Invoke(batch[i]);
        return true;
    }

    /// <summary>열 수 지정(A213: 8 − 2×열린 도크 수 = 둘 다 4 / 하나 6 / 없음 8). 바뀌면 타일 크기 재계산.</summary>
    public void SetColumns(int columns)
    {
        if (columns == _columns) return;
        _columns = columns;
        ApplyTileSize();
    }

    /// <summary>
    /// 표시 목록 교체 — 좌 리스트(ExplorerPane)가 정렬·필터를 적용해 넘긴 결과를 그대로 그린다.
    /// folder = 그 목록의 폴더 경로(A94 — 드랍·붙여넣기 대상으로 기억한다).
    /// 이미지 미리보기는 BitmapImage가 스스로 비동기 디코드하므로 별도 로드 루프가 없다.
    /// A192: 종전 전량 동기 생성을 분할 조립으로 대체 — 첫 조각(TileChunkItems)만 즉시 만들고
    /// 나머지는 CompositionTarget.Rendering 틱당 한 조각씩 append한다(StartTileAppendLoop —
    /// DocumentView.StartRenderAppendLoop의 A193 구조 복제). 실체화 상한(MaterializeLimit)을
    /// 넘는 초과분은 만들지 않고 완료 시점(FinishShowEntries)에 안내 1타일만 붙는다.
    /// 재진입(감시 재스캔·정렬·폴더 전환 — 전부 이 메서드로 다시 온다)은 명시 해제 +
    /// 틱 진입 seq 대조의 이중 방어. UpdateLayout은 전량 조립 뒤 1회에서 <b>첫 조각 직후 1회</b>로
    /// 축소 — 목적(ApplyTileSize가 캐스트하는 ItemsPanelRoot의 실체화)은 항목 수와 무관하게
    /// 첫 레이아웃 한 번이면 성립하고, 이후 조각은 패널 속성(ItemWidth/ItemHeight)이 셀 크기를
    /// 자동 적용한다(폴백 경로 보정은 FinishShowEntries 주석).
    /// </summary>
    public void ShowEntries(string folder, IReadOnlyList<ExplorerListing.Entry> entries)
    {
        var seq = ++_showSeq;
        StopTileAppendLoop(); // 방어: 직전 조립 루프가 남아 있으면 먼저 해제(A193 관용구)
        CurrentFolder = folder;
        TileGrid.Items.Clear();

        var cap = Math.Min(entries.Count, MaterializeLimit);
        var first = Math.Min(TileChunkItems, cap);
        for (var i = 0; i < first; i++)
            TileGrid.Items.Add(MakeTile(entries[i]));
        EmptyText.Text = "No matching files here"; // A243 — ShowLoading의 "Loading..."을 원문구로 복원
        EmptyText.Visibility = entries.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        // 계측 cfill0: 중앙 첫 조각(최대 60타일) 생성 완료 — 바로 아래 동기 레이아웃 패스와
        // 비용을 분리하려고 UpdateLayout 앞에 둔다.
        NavDiagnostics.Mark("cfill0");

        TileGrid.UpdateLayout(); // 첫 조각(상한 60타일)의 패널 실체화 — 아래 타일 크기 반영이 헛돌지 않게
        ApplyTileSize();
        // 계측 clay: UpdateLayout(동기 레이아웃 패스)의 비용 — UI 스레드를 통째로 잡는 유일한
        // 명시 호출이라 따로 잰다.
        NavDiagnostics.Mark("clay");

        if (first < cap) StartTileAppendLoop(seq, entries, first, cap);
        else FinishShowEntries(entries, seq); // 소형 폴더 — 조립이 여기서 동기 완료(종전 동작 동일)
    }

    /// <summary>
    /// A243: 폴더 실변경 항해의 시작 통지 — 스캔 완료(ShowEntries)를 기다리지 않고 즉시 옛 폴더
    /// 타일을 지우고 로딩 문구를 띄운다(대형·OneDrive 폴더에서 수 초 무반응으로 보이던 체감 해소).
    /// 실변경 판정은 좌 리스트(ExplorerPane.NavigateToAsync)가 단일 지점으로 하고, 같은 폴더 감시
    /// 재스캔(400ms 디바운스)·정렬·필터 재작성은 이 경로로 오지 않아 종전대로 무Clear(깜빡임 방지).
    /// _showSeq 증가 = 보류 중 텍스트 프리뷰(A233)·셸 썸네일(A242) 예약 전부 무산(Unloaded와 같은
    /// 장치 — 낡은 완료는 고아 host 갱신일 뿐이라 무해). 스캔 결과는 반드시 ShowEntries로 돌아와
    /// 문구·목록을 덮는다(실패 경로도 빈 목록 ViewChanged를 쏜다 — 로딩 문구가 잔존하지 않는 근거).
    /// _pendingRenamePath는 건드리지 않는다 — 다음으로 완주한 조립(FinishShowEntries)이 소비한다.
    /// </summary>
    public void ShowLoading(string folder)
    {
        _showSeq++;
        StopTileAppendLoop(); // 직전 조립 루프가 빈 판에 낡은 조각을 붙이지 않게(seq 대조와 이중)
        CurrentFolder = folder; // 좌 리스트(_folder)와 같은 시점 갱신 — 로딩 중 드랍·붙여넣기 대상 일치
        TileGrid.Items.Clear();
        EmptyText.Text = "Loading...";
        EmptyText.Visibility = Visibility.Visible;
        // 계측 cload: 중앙 썸네일 쪽 로딩 화면 전환 완료(좌 리스트의 load 바로 뒤 — 두 표면의
        // 비용을 갈라 본다). diag.navTiming이 꺼져 있으면 즉시 반환한다.
        NavDiagnostics.Mark("cload");
    }

    /// <summary>
    /// A192: 첫 조각 이후의 나머지 타일을 CompositionTarget.Rendering 틱마다 한 조각
    /// (TileChunkItems)씩 append한다 — UI 스레드 점유 상한 = 조각 1개 생성
    /// (DocumentView.StartRenderAppendLoop과 같은 프레임 틱 관용구·같은 해제 의무).
    /// 중단 판정 = 매 틱 append 직전의 seq 대조(한 틱 = 한 조각이라 틱 진입 시 1회로 충분):
    /// ShowEntries 재진입(감시 디바운스 재스캔 포함)이 _showSeq를 올린다 — 구 루프가 새 목록에
    /// 낡은 타일을 붙이는 사고를 막는다. 틱 핸들러는 본문 전체가 try/catch다(static 이벤트라
    /// 예외가 새면 앱 전역 크래시) — 조각 생성 예외 = 루프 중단(부분 타일 잔존은 다음
    /// ShowEntries가 덮는다).
    /// </summary>
    private void StartTileAppendLoop(int seq, IReadOnlyList<ExplorerListing.Entry> entries, int start, int cap)
    {
        StopTileAppendLoop(); // 방어: 기동 직전 잔존 루프 해제(A193 관용구)

        var next = start;
        // A342: 이 루프의 직전 틱 시작 시각(틱). 0 = 아직 첫 틱 전이라 간격을 잴 수 없다.
        var lastTickStart = 0L;
        void OnTick(object? sender, object? e)
        {
            // A342: 정지 487ms가 prev0>fillN 구간에서 나는데 미리보기 개수와 무관해, 어느 틱이
            // 주인인지 틱 단위로 좁힌다. 진단이 꺼져 있으면 여기서 비용이 0이다(0 = 미계측 표지).
            var tickStart = NavDiagnostics.Enabled ? System.Diagnostics.Stopwatch.GetTimestamp() : 0L;
            // A342 배치 2: 이 틱 동안 늘어난 GC 정지를 함께 잰다 — 240ms짜리 틱의 주인이
            // 조립인지 GC인지 가르는 값이다(ms 단위 — Stopwatch 틱과 섞지 않는다).
            var pauseStart = tickStart == 0 ? 0L : NavDiagnostics.PauseMs();
            try
            {
                if (seq != _showSeq)
                {
                    StopTileAppendLoop(); // 그새 다른 목록이 왔다 — 낡은 타일을 붙이지 않는다
                    return;
                }
                var count = Math.Min(TileChunkItems, cap - next);
                for (var i = next; i < next + count; i++)
                    TileGrid.Items.Add(MakeTile(entries[i]));
                next += count;
                if (next >= cap)
                {
                    StopTileAppendLoop(); // 완료 — 더 깨울 이유가 없다
                    FinishShowEntries(entries, seq);
                }
                // A342: 조각 append(마지막 틱이면 FinishShowEntries까지 포함)가 끝난 뒤에만
                // 기록한다 — 예외 경로는 남기지 않는다.
                if (tickStart != 0)
                {
                    NavDiagnostics.NoteTick(
                        'C',
                        next - 1,
                        System.Diagnostics.Stopwatch.GetTimestamp() - tickStart,
                        lastTickStart == 0 ? 0 : tickStart - lastTickStart,
                        NavDiagnostics.PauseMs() - pauseStart);
                    lastTickStart = tickStart;
                }
            }
            catch (Exception)
            {
                StopTileAppendLoop();
                // A342 배치 4: 이 갈래는 FinishShowEntries에 닿지 못한다 — 여기서 풀지 않으면
                // 중앙 타일 몫이 영영 남는다. 낡은 루프의 예외는 새 항해 것을 깎지 않게 seq를 본다.
                if (seq == _showSeq) NavGcScope.Leave(NavGcScope.Participant.Grid);
            }
        }
        _tileAppendHandler = OnTick;
        CompositionTarget.Rendering += OnTick;
    }

    /// <summary>A192: 분할 조립 루프 해제의 단일 지점 — 구독 해제 + 표지 소거(루프 없으면 무동작).
    /// 기동은 StartTileAppendLoop 한 곳뿐이라 구독 중 핸들러 = 이 필드 하나가 불변식이다.</summary>
    private void StopTileAppendLoop()
    {
        if (_tileAppendHandler is { } handler)
        {
            CompositionTarget.Rendering -= handler;
            _tileAppendHandler = null;
        }
    }

    /// <summary>
    /// A192: 조립 완료의 단일 마무리 — ① 상한 초과분 안내 1타일 부착, ② 폴백 크기 재적용,
    /// ③ 보류 중 이름변경 편집 진입. ③을 완료 뒤로 옮긴 이유: 새 폴더 타일이 뒤 조각에 있으면
    /// FindTileByPath가 조립 중에는 못 찾는다 — 편집 진입 예약(_pendingRenamePath)의 소비를
    /// "처음으로 완주한 조립"으로 미룬다(조립이 도중 무산되면 예약이 남아 다음 완주가 소비 —
    /// 종전 '다음 ShowEntries가 소비'와 같은 1회성). ②는 ApplyTileSize의 폴백 경로(패널이
    /// ItemsWrapGrid가 아닐 때 타일 직접 지정) 전용 보정 — 그 경로는 첫 조각만 크기를 받았으므로
    /// 완료 시 한 번 더 전체 적용한다(정상 경로에서는 패널 속성 재대입 한 줄이라 무해).
    /// 낡은 완료(폐기된 루프의 마지막 틱)는 seq 대조로 걸러진다.
    /// </summary>
    private void FinishShowEntries(IReadOnlyList<ExplorerListing.Entry> entries, int seq)
    {
        if (seq != _showSeq) return; // 방어 — 낡은 완료가 편집 진입을 훔치지 않게
        // A342 배치 4: 중앙 타일 몫의 GC 실험 구간 해제 — 이 표면의 정상 완료 단일 지점이다
        // (소형 폴더는 ShowEntries가 여기를 동기로 부른다: entries가 비어도 first < cap이 거짓이라
        // 반드시 도달한다). 낡은 완료는 바로 위 seq 대조에서 돌아갔다.
        NavGcScope.Leave(NavGcScope.Participant.Grid);
        // 계측 cfillN / cpaint: 중앙 타일 조립 완료와 그 뒤 첫 렌더 프레임(좌 리스트의 fillN·
        // paint와 대응). 좌·중앙 중 늦게 끝난 쪽이 사용자가 보는 "확 바뀌는" 순간이다.
        NavDiagnostics.Mark("cfillN");
        NavDiagnostics.ArmPaint("cpaint");
        if (entries.Count > MaterializeLimit)
            TileGrid.Items.Add(MakeOverflowNotice(entries.Count - MaterializeLimit));
        ApplyTileSize();

        // A94 2차: 새 폴더(Ctrl+Shift+N) 직후의 재스캔이면 그 타일을 선택하고 곧바로 이름변경
        // 편집 진입(탐색기 관례). 반드시 '재스캔 결과가 그려진 뒤' — 편집 중 재스캔은 편집 UI를 지운다.
        if (_pendingRenamePath is { } pending)
        {
            _pendingRenamePath = null; // 1회성 — 다음 갱신(다른 폴더 이동 등)에 재발화하지 않게
            if (FindTileByPath(pending) is { } tile)
            {
                TileGrid.SelectedItem = tile;
                TileGrid.ScrollIntoView(tile);
                TileGrid.UpdateLayout(); // 컨테이너 실체화 — 편집 상자 삽입·포커스가 성립하게
                BeginRenameOf(tile);
            }
        }
    }

    /// <summary>
    /// A192: 실체화 상한 초과 안내 — 비상호작용 1타일. Tag 없음(타일 조회·조작 루틴은 전부
    /// Tag의 Entry 패턴 매칭이라 자연 제외된다: FindTileByPath·SelectedPaths·EntryFromSource·
    /// ApplyCutMark·OnItemClick 전수 확인), 계약 훅(메뉴·드래그·더블클릭) 미부착,
    /// IsEnabled=false로 포커스·클릭 대상에서도 뺀다. 문구는 좌 리스트(ExplorerPane)와 동일 사양.
    /// </summary>
    private static GridViewItem MakeOverflowNotice(int hidden) => new()
    {
        Content = new TextBlock
        {
            Text = $"{hidden} more items are not shown. Refine the filter to see them.",
            FontSize = 11,
            Opacity = 0.6,
            TextWrapping = TextWrapping.Wrap,
            TextAlignment = TextAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(4),
        },
        IsEnabled = false,
    };

    /// <summary>경로로 타일 컨테이너 찾기 — 항목 = 컨테이너 직접 추가(Tag = Entry) 구조 전제.</summary>
    private GridViewItem? FindTileByPath(string path) =>
        TileGrid.Items.OfType<GridViewItem>().FirstOrDefault(i =>
            i.Tag is ExplorerListing.Entry entry &&
            string.Equals(entry.Path, path, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// F2·우클릭 Rename 진입 (A94 2차): 타일 캡션 TextBlock을 인라인 편집(ExplorerRenameBox)으로
    /// 바꾼다. 캡션 위치 = MakeTile의 tile.Children[1](아래 행 캡션 — 인덱스 수동 동기).
    /// 커밋 성공 갱신 = RefreshViaShell(편집이 끝난 뒤에만 — 편집 중 재스캔 금지).
    /// </summary>
    private void BeginRenameOf(GridViewItem item)
    {
        if (item.Tag is not ExplorerListing.Entry entry) return;
        if (item.Content is not Grid { Children.Count: > 1 } tile ||
            tile.Children[1] is not TextBlock caption) return;
        ExplorerRenameBox.Begin(tile, caption, entry.Path, MakeOpUi(), RefreshViaShell);
    }

    /// <summary>조작 후 갱신 — 폴더 상태의 단일 원본(좌 리스트)을 셸이 다시 항해시키는 A93 경로.</summary>
    private void RefreshViaShell()
    {
        if (CurrentFolder is { Length: > 0 } folder) FolderActivated?.Invoke(folder);
    }

    /// <summary>
    /// 파일 조작용 UI 문맥 (A94 3차) — 이 그리드 창의 DispatcherQueue·XamlRoot(충돌 대화상자용)와
    /// ShowNotice 채널(진행 문구 라이브 갱신용)을 조작 시작 시점에 캡처한다. 4차부터는 영구 삭제
    /// 확인·접근 거부 안내(관리자 재시작 제안)와 이름변경·새 폴더 실패 보고까지 같은 문맥을 쓴다.
    /// </summary>
    private ExplorerFileOps.OpUi MakeOpUi() => new(DispatcherQueue, XamlRoot, ShowNotice);

    /// <summary>
    /// Del·우클릭 Delete (A94 2차): 휴지통 경유 삭제(StorageDeleteOption.Default —
    /// ExplorerFileOps 주석). 확인 대화상자 없음(탐색기 관례) — 실패만 안내 문구,
    /// 권한 부족은 관리자 재시작 제안(A94 4차 — ReportAsync).
    /// </summary>
    private async Task DeleteWithNoticeAsync(IReadOnlyList<string> paths)
    {
        var ui = MakeOpUi();
        var result = await ExplorerFileOps.DeleteToRecycleAsync(paths);
        RefreshViaShell();
        await ExplorerFileOps.ReportAsync(result.Notice("deleted"), result.Denied, ui);
    }

    /// <summary>
    /// Shift+Del = 영구 삭제 (A94 4차): 탐색기 동등으로 **영구 삭제만** 확인창을 띄우고(기본 버튼 =
    /// Cancel), 확인하면 휴지통을 거치지 않고 지운다. 대상 선택 규칙·재스캔·실패 안내는 Del과
    /// 같은 경로다(좌 리스트 단일 원본 경유 재스캔). 취소하면 아무것도 하지 않는다.
    /// </summary>
    private async Task PermanentDeleteWithConfirmAsync(IReadOnlyList<string> paths)
    {
        var ui = MakeOpUi();
        if (!await ExplorerDialogs.ConfirmPermanentDeleteAsync(ui.Dispatcher, ui.Root, paths)) return;
        var result = await ExplorerFileOps.DeletePermanentlyAsync(paths);
        RefreshViaShell();
        await ExplorerFileOps.ReportAsync(result.Notice("deleted"), result.Denied, ui);
    }

    /// <summary>
    /// 타일 한 변 = floor(그리드 실폭 / 열 수) (A93 확정 수식). GridView의 기본 아이템 패널
    /// (ItemsWrapGrid)의 셀 크기(ItemWidth/ItemHeight)로 지정한다 — 셀이 균일하면 줄바꿈이
    /// 정확히 열 수대로 떨어진다. 패널이 아직 없거나 다른 타입이면(테마·템플릿 변형 대비)
    /// 타일 루트에 직접 크기를 주는 폴백으로 같은 결과를 낸다.
    /// </summary>
    private void ApplyTileSize()
    {
        var width = TileGrid.ActualWidth;
        if (width <= 0) return;
        var size = Math.Floor(width / _columns);
        if (size < 24) return; // 극단적으로 좁은 창 보호 — 이전 크기 유지가 낫다

        if (TileGrid.ItemsPanelRoot is ItemsWrapGrid wrap)
        {
            wrap.ItemWidth = size;
            wrap.ItemHeight = size;
            return;
        }
        foreach (var obj in TileGrid.Items)
            if (obj is GridViewItem { Content: FrameworkElement tile })
            {
                tile.Width = size;
                tile.Height = size;
            }
    }

    private void OnSizeChanged(object sender, SizeChangedEventArgs e) => ApplyTileSize();

    // ---------- 타일 구성 ----------

    /// <summary>균일 타일: 위(미리보기/글리프/확장자) + 아래 파일명 1줄 말줄임 캡션(A93).</summary>
    private GridViewItem MakeTile(ExplorerListing.Entry entry)
    {
        var preview = entry.IsFolder ? MakeFolderGlyph()
            : IsImageFile(entry.Name) ? MakeImagePreview(entry)
            : IsTextPreviewFile(entry) ? MakeTextPreview(entry) // A233 — 내용 프리뷰(지연 교체)
            : MakeShellThumbTile(entry); // A242 — 그 외 전 파일: 셸 썸네일 지연 교체(단일 판정 지점)

        var tile = new Grid();
        tile.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        tile.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        tile.Children.Add(preview);
        // A337: 클라우드 전용(placeholder) 파일 표시 — 이 줄이 없으면 "썸네일이 안 나온다"가
        // 사용자에게 고장으로 읽힌다(실기기 문의: 같은 폴더의 PNG 두 개가 갈렸다). 원인은 A175
        // 사양이다 — 원본을 열면 하이드레이션(전체 다운로드)이 일어나므로 캐시된 썸네일만
        // 시도하고 실패하면 확장자 타일을 유지한다. 그 사실을 타일이 스스로 밝힌다.
        if (!entry.IsFolder && entry.IsPlaceholder) tile.Children.Add(MakeCloudBadge());

        var caption = new TextBlock
        {
            Text = entry.Name,
            FontSize = 11,
            TextTrimming = TextTrimming.CharacterEllipsis, // 1줄 말줄임(A93) — 2줄 아님
            TextAlignment = TextAlignment.Center,
            Margin = new Thickness(4, 0, 4, 4),
        };
        Grid.SetRow(caption, 1);
        tile.Children.Add(caption);
        ToolTipService.SetToolTip(tile, entry.Name);

        var item = new GridViewItem
        {
            Content = tile,
            Tag = entry,
            // 셀(ItemWidth/ItemHeight)을 타일이 꽉 채워야 미리보기 영역이 균일해진다
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
            VerticalContentAlignment = VerticalAlignment.Stretch,
        };
        ExplorerFileOps.ApplyCutMark(item); // A94 4차 — 잘라내기 중인 경로면 처음부터 반투명
        AttachContextMenu(item, entry); // A24 — 좌 리스트와 같은 우클릭 메뉴
        AttachDragDrop(item, entry); // A94 — 드래그 아웃 + 폴더 타일 드랍
        item.IsDoubleTapEnabled = true; // A85 — 압축 모듈 내부 리스트(ArchiveView)와 같은 명시
        item.DoubleTapped += OnItemDoubleTapped; // A85 — 더블클릭 열기의 기본 경로
        return item;
    }

    /// <summary>
    /// 타일에 드래그 아웃(전 항목)과 드랍 대상(폴더 타일만)을 건다 (A94 —
    /// ExplorerPane.AttachDragDrop과 같은 구성: 데퍼럴이 있는 컨테이너 CanDrag 경로).
    /// 잡은 타일이 선택에 포함돼 있으면 선택 전부를, 아니면 그 타일 하나만 싣는다(윈도우 관례).
    /// 폴더 타일 핸들러가 Handled를 걸므로 루트(LayoutRoot) 핸들러와 이중 처리되지 않는다.
    /// </summary>
    private void AttachDragDrop(GridViewItem item, ExplorerListing.Entry entry)
    {
        item.CanDrag = true;
        item.DragStarting += async (_, args) =>
        {
            var deferral = args.GetDeferral();
            try
            {
                var selected = SelectedPaths();
                IReadOnlyList<string> paths = selected.Contains(entry.Path, StringComparer.OrdinalIgnoreCase)
                    ? selected
                    : [entry.Path];
                if (!await ExplorerFileOps.FillDragDataAsync(args.Data, paths))
                    args.Cancel = true; // 실을 항목이 없다(그새 삭제 등)
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

    /// <summary>폴더 타일: Segoe Fluent 폴더 글리프 — ExplorerPane 그리드/리스트와 같은 E8B7.</summary>
    private static FontIcon MakeFolderGlyph() => new()
    {
        Glyph = "\uE8B7",
        FontSize = 40,
        HorizontalAlignment = HorizontalAlignment.Center,
        VerticalAlignment = VerticalAlignment.Center,
    };

    /// <summary>이미지 모듈 담당 확장자인지 — 담당 목록(ImageFolderNavigator)을 그대로 재사용(A93).</summary>
    private static bool IsImageFile(string name) =>
        ExplorerListing.MatchesExtension(name, KOTU.Module.Image.ImageFolderNavigator.SupportedExtensions);

    /// <summary>
    /// 이미지 실제 축소 미리보기: BitmapImage + DecodePixelWidth(A93 지정) — 디코드는 XAML
    /// 파이프라인이 비동기로 한다. WIC 밖 포맷(psd)·손상 파일은 ImageFailed로 확장자 타일 폴백.
    /// </summary>
    private UIElement MakeImagePreview(ExplorerListing.Entry entry)
    {
        // A175: 클라우드 전용(placeholder) 파일은 원본 디코드가 하이드레이션(전체 다운로드)을
        // 일으킨다 — 원본은 절대 열지 않고, 캐시·클라우드 제공 썸네일만 비동기로 시도한다.
        if (entry.IsPlaceholder) return MakePlaceholderPreview(entry);

        // A339: 확장자 타일을 먼저 깔고 원본 디코드는 보이는 타일만 시작한다(다른 세 갈래와 같은
        // 구조가 됐다 — 종전에는 이 갈래만 조립 시점에 BitmapImage를 걸어 2,000장 디코드를
        // 한꺼번에 예약했다). 실패·예외 폴백이 이제 "확장자 타일로 되돌리기"가 아니라
        // "확장자 타일을 그대로 두기"라 host.Children.Clear()가 필요 없다.
        var host = new Grid();
        var fallback = MakeExtensionTile(entry);
        host.Children.Add(fallback);
        DeferPreview(host, () =>
        {
            try
            {
                var bitmap = new BitmapImage { DecodePixelWidth = PreviewDecodeWidth };
                bitmap.UriSource = new Uri(entry.Path);
                var image = new Image
                {
                    Source = bitmap,
                    Stretch = Stretch.Uniform,
                    Margin = new Thickness(4),
                };
                // 성공하면 확장자 타일을 걷는다 — 그냥 겹쳐 두면 **투명 PNG의 투명한 부분으로
                // 아래 타일이 비쳐 보인다**(종전에는 host에 이미지만 있었으므로 없던 문제다).
                // 실패(ImageFailed)에는 걷지 않아 확장자 타일이 그대로 남는다 = 종전 폴백과 같은 결과.
                image.ImageOpened += (_, _) => host.Children.Remove(fallback);
                image.ImageFailed += (_, _) => host.Children.Remove(image);
                host.Children.Add(image);
            }
            catch
            {
                // 경로가 Uri가 못 되는 극단 케이스 — 확장자 타일 그대로.
            }
        });
        return host;
    }

    /// <summary>
    /// 클라우드 전용(placeholder) 이미지 타일 (A175): 즉시 확장자 타일을 그려 두고, 캐시된
    /// 썸네일(ReturnOnlyIfCached — 원본을 열지 않는다)이 있으면 비동기로 바꿔 끼운다.
    /// 없으면 확장자 타일 그대로 — 어떤 경우에도 하이드레이션은 일어나지 않는다.
    /// </summary>
    private UIElement MakePlaceholderPreview(ExplorerListing.Entry entry)
    {
        var host = new Grid();
        host.Children.Add(MakeExtensionTile(entry));
        DeferPreview(host, () => _ = FillCachedThumbnailAsync(host, entry.Path)); // A339
        return host;
    }

    /// <summary>
    /// 캐시·클라우드 제공 썸네일을 UI 스레드 비동기로 받아 host에 채운다 (A175).
    /// ReturnOnlyIfCached라 원본 파일은 열리지 않는다(캐시에 없으면 실패 → 확장자 타일 유지).
    /// 폴더 이동으로 host가 트리에서 떨어져도(ShowEntries가 타일을 전부 새로 만든다)
    /// 고아 Grid 갱신일 뿐이라 무해하다 — 재진입 가드가 필요 없는 이유.
    /// </summary>
    private static async Task FillCachedThumbnailAsync(Grid host, string path)
    {
        try
        {
            var file = await StorageFile.GetFileFromPathAsync(path);
            using var thumb = await file.GetThumbnailAsync(
                ThumbnailMode.SingleItem, PreviewDecodeWidth, ThumbnailOptions.ReturnOnlyIfCached);
            if (thumb is null || thumb.Size == 0) return;
            // A270 ③: 파일 종류 아이콘은 무정보다 — 확장자 타일을 덮지 않는다(FetchTilePreview와
            // 같은 판정·같은 복구법: 이 줄만 지우면 종전 동작). 두 번째 GetThumbnailAsync 호출부.
            if (thumb.Type == ThumbnailType.Icon) return;

            // 스트림 → 바이트 → BitmapImage: ExplorerPane.FetchThumbnail과 같은 변환 관용구
            // (검증된 형태만 복제 — thumb를 SetSourceAsync에 직접 넘기는 선례가 저장소에 없다).
            using var stream = thumb.AsStreamForRead();
            using var buffer = new MemoryStream((int)thumb.Size);
            await stream.CopyToAsync(buffer);
            buffer.Position = 0;
            var bitmap = new BitmapImage();
            await bitmap.SetSourceAsync(buffer.AsRandomAccessStream());

            // A335 계측: 타일 내용이 화면에 처음 얹히는 순간. Mark는 같은 이름을 한 번만
            // 기록하므로(NavDiagnostics.Mark) 세 갈래(캐시 썸네일·텍스트 미리보기·셸
            // 썸네일) 어디서 먼저 와도 첫 것만 남는다. 이 마크가 필요한 이유: A334 실측의
            // 정지(343ms → 파일 1만 개에서 1,289ms)가 마지막 마크 <b>이후</b>로 잡혀
            // "모든 반영이 끝난 뒤"까지만 알 수 있었다 — 그 구간이 내용 얹기인지 그보다
            // 뒤인지 가르는 자가 없었다. 이제 정지 라벨이 prev0 앞뒤로 갈린다.
            NavDiagnostics.Mark("prev0");
            host.Children.Clear();
            host.Children.Add(new Image
            {
                Source = bitmap,
                Stretch = Stretch.Uniform,
                Margin = new Thickness(4),
            });
        }
        catch
        {
            // 캐시 썸네일 없음·읽기 실패 — 확장자 타일 유지. 원본은 어떤 폴백에서도 열지 않는다.
        }
    }

    /// <summary>
    /// 이미지 외 파일 타일: 담당 모듈 액센트 색 배경 + 확장자 대문자 (A93).
    /// 담당 모듈이 없으면(액센트 null) 중립 레이어 색 — Branding.ModuleAccent의 폴백 규칙 그대로.
    /// </summary>
    private UIElement MakeExtensionTile(ExplorerListing.Entry entry)
    {
        var ext = Path.GetExtension(entry.Name).TrimStart('.').ToUpperInvariant();
        var accent = Branding.ModuleAccent(ModuleIdForFile?.Invoke(entry.Path));

        var label = new TextBlock
        {
            Text = ext.Length > 0 ? ext : "?",
            FontSize = 16,
            FontWeight = FontWeights.SemiBold,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        };
        if (accent is not null) // 액센트 배경 위에서만 흰 글자 — 중립 배경은 테마 기본색 유지
            label.Foreground = new SolidColorBrush(Microsoft.UI.Colors.White);

        return new Border
        {
            Margin = new Thickness(8),
            CornerRadius = new CornerRadius(6),
            Background = accent is { } color
                ? new SolidColorBrush(color)
                : (Brush)Application.Current.Resources["LayerFillColorDefaultBrush"],
            Child = label,
        };
    }

    // ---------- 텍스트 내용 프리뷰 (A233) ----------

    /// <summary>
    /// 내용 프리뷰 대상 텍스트 파일인지 (A233) — 문서 모듈 담당 목록(DocumentModule.Extensions)
    /// 재사용에서 .pdf만 뺀다(ExplorerPane.InfoKindOf의 Text 갈래와 같은 판정 — 그쪽은 Pdf
    /// 갈래가 먼저 잡고 여기는 명시 제외. A224류 목록 추가분 자동 추종). 클라우드 전용
    /// (placeholder) 파일은 앞부분 읽기조차 하이드레이션이라 제외한다(A175 — 확장자 타일 유지).
    /// </summary>
    private static bool IsTextPreviewFile(ExplorerListing.Entry entry) =>
        !entry.IsPlaceholder
        && !string.Equals(Path.GetExtension(entry.Name), ".pdf", StringComparison.OrdinalIgnoreCase)
        && ExplorerListing.MatchesExtension(entry.Name, KOTU.Module.Document.DocumentModule.Extensions);

    /// <summary>
    /// 오디오 정보 표기(A270) 대상인지 — 오디오 모듈 담당 목록(AudioModule.Extensions)을 그대로
    /// 재사용한다(ExplorerPane.InfoKindOf의 Audio 갈래와 같은 모듈 public static 참조 선례라
    /// 목록 추가분을 자동 추종한다). 클라우드 전용(placeholder) 파일은 셸 속성 조회조차
    /// 하이드레이션(전체 다운로드)을 부를 수 있어 제외한다(A175 — 확장자 타일 그대로).
    /// </summary>
    private static bool IsAudioInfoFile(ExplorerListing.Entry entry) =>
        !entry.IsPlaceholder
        && ExplorerListing.MatchesExtension(entry.Name, KOTU.Module.Audio.AudioModule.Extensions);

    /// <summary>
    /// 텍스트 파일 타일 (A233): 즉시 확장자 타일을 그려 두고, 워커 읽기가 끝나면 그 타일의
    /// 내용만 교체한다(A175 MakePlaceholderPreview의 지연 교체 구조 — 조립 루프(ShowEntries·
    /// A192 append 틱)는 예약만 하고 블로킹되지 않는다. 이미지 타일의 지연 디코드와 같은 감각).
    /// </summary>
    private UIElement MakeTextPreview(ExplorerListing.Entry entry)
    {
        var host = new Grid();
        host.Children.Add(MakeExtensionTile(entry));
        // A339: _showSeq는 예약 시점이 아니라 실제 발사 시점의 값을 잡아야 한다 — 미룬 사이에
        // 폴더가 바뀌었으면 이 타일은 이미 버려진 목록의 것이고, 그 회차의 seq로 발사하면
        // 낡음 판정(FillTextPreviewAsync의 seq 대조)이 제 몫을 한다.
        DeferPreview(host, () => _ = FillTextPreviewAsync(host, entry.Path, _showSeq));
        return host;
    }

    /// <summary>
    /// 게이트(동시 TextPreviewConcurrency건) 획득 후 워커에서 파일 앞부분을 읽어 타일 내용을
    /// 교체한다 (A233). UI 스레드에서 시작하므로 await 후속부도 UI 스레드다(ExplorerPane.
    /// LoadDetailInfoAsync의 A194 발사 구조 — 별도 디스패치 없이 seq 재대조가 성립하는 근거).
    /// 낡음 이중 방어(A192 관용구): ① 게이트 통과 시점 — 재스캔 빈발(A131)로 보류가 쌓여도
    /// 낡은 예약은 읽기 자체를 시작하지 않는다(자연 배압 — 상한 초과분은 게이트 대기 큐에서
    /// 잠들었다가 여기서 접힌다), ② 읽기 완료 시점 — 결과를 버린다. host는 이 타일 전용 클로저
    /// 캡처라(컨테이너 재사용·풀 없음 — ShowEntries가 타일을 매번 새로 만든다) 교체가 다른
    /// 타일로 갈 수 없다. Unloaded는 _showSeq를 올려 보류 전부를 무산시킨다(생성자 주석).
    /// 실패(잠김·삭제 경합·풀 닫힘 취소)는 조용히 확장자 타일 유지 — 안내 없음(사양).
    /// </summary>
    private async Task FillTextPreviewAsync(Grid host, string path, int seq)
    {
        await _textReadGate.WaitAsync(); // UI 문맥 await — 후속부는 UI 스레드로 복귀
        try
        {
            if (seq != _showSeq) return; // ① 대기 중 낡음 — 발사 자체를 접는다
            string? text;
            try
            {
                text = await TextPool.Run(_ => ReadTextPreview(path));
            }
            catch
            {
                return; // 읽기 실패·풀 닫힘(취소 Task) — 확장자 타일 유지
            }
            if (seq != _showSeq || text is null) return; // ② 완료 시점 재대조(이중 방어)
            // A335 계측: 타일 내용이 화면에 처음 얹히는 순간. Mark는 같은 이름을 한 번만
            // 기록하므로(NavDiagnostics.Mark) 세 갈래(캐시 썸네일·텍스트 미리보기·셸
            // 썸네일) 어디서 먼저 와도 첫 것만 남는다. 이 마크가 필요한 이유: A334 실측의
            // 정지(343ms → 파일 1만 개에서 1,289ms)가 마지막 마크 <b>이후</b>로 잡혀
            // "모든 반영이 끝난 뒤"까지만 알 수 있었다 — 그 구간이 내용 얹기인지 그보다
            // 뒤인지 가르는 자가 없었다. 이제 정지 라벨이 prev0 앞뒤로 갈린다.
            NavDiagnostics.Mark("prev0");
            host.Children.Clear();
            host.Children.Add(MakeTextPreviewBlock(text));
        }
        finally
        {
            _textReadGate.Release(); // 예외·낡음 경로 포함 — 누락되면 상한 건 뒤 조용히 멈춘다(A194)
        }
    }

    /// <summary>
    /// 워커 스레드: 파일 앞부분(상한 TextPreviewMaxBytes)만 읽어 프리뷰 문자열을 만든다 (A233).
    /// 인코딩 판정은 DocumentView.ReadTextSmart의 최소 복제(모듈 뷰 내부 private이라 직접 참조
    /// 불가 — DocumentQuickInfo가 같은 사정으로 같은 규칙을 복제한 선례): BOM(UTF-8·UTF-16 LE/BE)
    /// 우선, 없으면 엄격 UTF-8 시도, 깨질 때만 CP949 폴백. 상한에서 잘린 버퍼의 불완전한 UTF-8
    /// 꼬리는 떼고 판정한다(진짜 UTF-8이 CP949로 오판되지 않게 — DocumentQuickInfo와 동일 처리).
    /// CP949는 제공자 등록 없는 직접 취득(Cp949ZipReader·SubtitleCharset 관용구) — 취득 실패는
    /// null. 표시는 앞 TextPreviewMaxLines줄까지, 전부 공백이면 null(호출부가 확장자 타일 유지).
    /// 예외(잠김·삭제 경합 등)는 호출부 catch가 삼킨다.
    /// </summary>
    private static string? ReadTextPreview(string path)
    {
        using var stream = File.OpenRead(path);
        if (stream.Length == 0) return null; // 빈 파일 — 보여 줄 내용이 없다
        var truncated = stream.Length > TextPreviewMaxBytes;
        var bytes = new byte[Math.Min(stream.Length, TextPreviewMaxBytes)];
        stream.ReadExactly(bytes);

        string text;
        if (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF)
            text = Encoding.UTF8.GetString(bytes, 3, bytes.Length - 3);
        else if (bytes.Length >= 2 && bytes[0] == 0xFF && bytes[1] == 0xFE)
            text = Encoding.Unicode.GetString(bytes, 2, bytes.Length - 2);
        else if (bytes.Length >= 2 && bytes[0] == 0xFE && bytes[1] == 0xFF)
            text = Encoding.BigEndianUnicode.GetString(bytes, 2, bytes.Length - 2);
        else
        {
            var length = truncated ? TrimIncompleteUtf8Tail(bytes) : bytes.Length;
            try
            {
                text = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false,
                    throwOnInvalidBytes: true).GetString(bytes, 0, length);
            }
            catch (DecoderFallbackException)
            {
                // 레거시 한글(CP949) — 프리뷰는 근사면 충분해 UTF-16 BOM 없는 파일 등이 여기로
                // 떨어져도 깨진 글자 타일일 뿐 무해하다(열기는 문서 모듈의 정식 판정이 한다).
                var cp949 = CodePagesEncodingProvider.Instance.GetEncoding(949);
                if (cp949 is null) return null; // 제공자 취득 실패 — 프리뷰 없음
                text = cp949.GetString(bytes);
            }
        }

        // 앞 N줄만 — 타일에 그 이상 안 보인다. CR은 떼서 CRLF 파일의 줄 끝 제어문자를 지운다.
        var lines = text.Split('\n');
        var preview = string.Join('\n',
            lines.Take(Math.Min(lines.Length, TextPreviewMaxLines)).Select(l => l.TrimEnd('\r')));
        return preview.Trim().Length == 0 ? null : preview;
    }

    /// <summary>버퍼 끝의 불완전한 UTF-8 시퀀스를 제외한 길이 — DocumentQuickInfo.
    /// TrimIncompleteUtf8Tail의 복제(그쪽이 private이라 참조 불가 — 동작 동일, A233).
    /// 끝에서 최대 3바이트의 연속 바이트(상위 2비트 = 0x80)를 거슬러 리드 바이트를 찾고,
    /// 그 시퀀스의 기대 길이가 버퍼를 넘으면 리드 바이트 앞에서 자른다.</summary>
    private static int TrimIncompleteUtf8Tail(byte[] bytes)
    {
        for (var i = bytes.Length - 1; i >= 0 && i >= bytes.Length - 4; i--)
        {
            if ((bytes[i] & 0xC0) == 0x80) continue; // 연속 바이트 — 더 거슬러 간다
            var expected = (bytes[i] & 0xE0) == 0xC0 ? 2
                : (bytes[i] & 0xF0) == 0xE0 ? 3
                : (bytes[i] & 0xF8) == 0xF0 ? 4
                : 1; // ASCII 또는 불량 리드 — 불완전으로 보지 않는다
            return bytes.Length - i < expected ? i : bytes.Length;
        }
        return bytes.Length; // 끝 4바이트가 전부 연속 바이트 — 불량이므로 그대로 검사
    }

    /// <summary>
    /// 내용 프리뷰 요소 (A233): 확장자 타일(MakeExtensionTile)과 같은 테두리 구성 — 테마 대응
    /// 중립 레이어 배경(LayerFillColorDefaultBrush) 위 소형 고정폭 텍스트(Consolas — A142 ②
    /// 에디터와 같은 Windows 동봉 고정폭, 한글은 시스템 폴백으로 그려진다). 긴 줄은 줄마다
    /// 말줄임(캡션과 같은 CharacterEllipsis), 세로 넘침은 위부터 그려진다(줄 수 상한 12가
    /// 이미 있어 실용상 충분 — 확장자 라벨은 유지하지 않는다: 겹침 없는 단순한 쪽 사양).
    /// </summary>
    private static UIElement MakeTextPreviewBlock(string text) => new Border
    {
        Margin = new Thickness(8),
        CornerRadius = new CornerRadius(6),
        Background = (Brush)Application.Current.Resources["LayerFillColorDefaultBrush"],
        Padding = new Thickness(6, 4, 6, 4),
        Child = new TextBlock
        {
            Text = text,
            FontSize = 9,
            FontFamily = new FontFamily("Consolas"),
            TextWrapping = TextWrapping.NoWrap,
            TextTrimming = TextTrimming.CharacterEllipsis,
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Top,
        },
    };

    // ---------- 비이미지 셸 썸네일 (A242) ----------

    /// <summary>
    /// 비이미지 파일 타일 (A242): 즉시 확장자 타일 + 우하단 대기 배지를 그려 두고, 워커 추출이
    /// 끝나면 셸 썸네일로 교체한다(A233 MakeTextPreview의 지연 교체 구조 그대로 — 조립 루프는
    /// 예약만 하고 블로킹되지 않는다). 대상 = 폴더·이미지·텍스트(A233) 제외 전 파일 — 갈래
    /// 선택은 MakeTile 한 곳이라 다른 갈래와 이중 로드가 없다. 실패·썸네일 없음 = 배지만 걷고
    /// 확장자 타일 유지(안내 없음·사양). 자체 캐시는 두지 않는다 — 셸 썸네일 캐시가 이미 있어
    /// 재추출이 싸다(ExplorerPane.RefreshView 주석과 같은 근거). 성공한 타일은 다음 재스캔
    /// (타일 전량 재생성)까지 다시 바뀌지 않는다 — 감시 재스캔 시 재요청은 기존 파이프라인 규칙.
    /// </summary>
    private UIElement MakeShellThumbTile(ExplorerListing.Entry entry)
    {
        var host = new Grid();
        host.Children.Add(MakeExtensionTile(entry));
        // A339: 대기 배지도 미루는 안쪽에서 만든다 — 미리보기를 아직 요청하지도 않은 타일에
        // "기다리는 중" 표시가 붙어 있으면 거짓말이 되고, 배지 객체도 안 만든 만큼 아낀다.
        // A270: 오디오면 같은 워커 왕복에서 셸 속성(길이·비트레이트·샘플레이트·채널)도 함께 읽는다.
        DeferPreview(host, () =>
        {
            var badge = MakePendingBadge();
            host.Children.Add(badge);
            _ = FillShellThumbnailAsync(host, badge, entry, IsAudioInfoFile(entry), _showSeq);
        });
        return host;
    }

    /// <summary>
    /// A243: 지연 교체 대기 배지 — 정적 글리프(E895 Sync). ProgressRing은 기각(CI가 컴파일
    /// 전용이라 애니메이션을 검증할 수 없다 — A94류 보수 관용구). 확장자 타일을 가리지 않게
    /// 우하단 소형·저투명으로 겹치고, 히트 테스트에서도 뺀다(타일 히트 판정은 조상 탐색
    /// (EntryFromSource)이라 영향이 없지만 명시해 둔다).
    /// </summary>
    private static FontIcon MakePendingBadge() => new()
    {
        Glyph = "\uE895",
        FontSize = 10,
        Opacity = 0.55,
        HorizontalAlignment = HorizontalAlignment.Right,
        VerticalAlignment = VerticalAlignment.Bottom,
        Margin = new Thickness(0, 0, 12, 12),
        IsHitTestVisible = false,
    };

    /// <summary>
    /// A337: 클라우드 전용(온라인 전용) 파일 배지 — 대기 배지(<see cref="MakePendingBadge"/>)와
    /// 같은 한 벌(FontIcon·소형·반투명)이고 <b>자리만 반대쪽 위</b>다: 대기
    /// 배지는 우하단이라 겹치지 않고, 둘이 동시에 보이는 순간(클라우드 파일의 캐시 썸네일을
    /// 기다리는 동안)에도 서로를 가리지 않는다.
    /// <para>
    /// <b>썸네일이 성공해도 남는다</b>: 이 배지가 말하는 것은 "미리보기가 없다"가 아니라
    /// "이 파일은 로컬에 없다"이고, 그 사실은 캐시 썸네일을 찾았는지와 무관하다. 열면 다운로드가
    /// 일어난다는 예고이기도 하다.
    /// </para>
    /// 글리프 <c>E753</c> = Segoe Fluent Icons의 Cloud — 윈도우 탐색기가 같은 상태에 쓰는
    /// 그림이라 사용자가 이미 아는 기호다(별도 학습이 필요 없다).
    /// <para>
    /// 대기 배지와 <b>다른 점 하나</b>: 히트 테스트에서 빼지 않는다 — 툴팁이 이 배지의 존재
    /// 이유(왜 미리보기가 없는지)를 설명하는 유일한 자리이고, 히트 테스트를 끄면 툴팁이
    /// 뜨지 않는다. 포인터 이벤트는 조상으로 버블링되므로 타일 선택·더블클릭은 그대로다
    /// (타일 히트 판정도 조상 탐색 EntryFromSource라 영향이 없다).
    /// </para>
    /// <b>클라우드 전용 파일에만</b> 만든다 — 로컬 파일은 객체가 0개 늘어난다(타일 조립이
    /// 개수에 비례하는 병목이라: A334 실측 clay&gt;fillN).
    /// </summary>
    private static FontIcon MakeCloudBadge()
    {
        var badge = new FontIcon
        {
            Glyph = "\uE753",
            FontSize = 10,
            Opacity = 0.55,
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(0, 12, 12, 0),
        };
        ToolTipService.SetToolTip(badge, "Online-only file — preview needs a download");
        return badge;
    }

    /// <summary>
    /// 게이트(동시 ThumbFetchConcurrency건) 획득 후 워커에서 셸 썸네일을 추출해 타일 내용을
    /// 교체한다 (A242 — FillTextPreviewAsync의 A194 발사 구조 그대로: UI 스레드에서 시작하므로
    /// await 후속부도 UI 스레드, 낡음 이중 방어 = ① 게이트 통과 시점 ② 완료 시점 seq 대조,
    /// host·badge는 이 타일 전용 클로저 캡처라 교체가 다른 타일로 갈 수 없고, Unloaded·
    /// ShowLoading(A243)은 _showSeq를 올려 보류 전부를 무산시킨다). 클라우드 전용(placeholder)
    /// 파일은 entry.IsPlaceholder가 그대로 워커의 cachedOnly가 되어 캐시·클라우드 제공 썸네일만
    /// 요청한다(A175 하이드레이션 금지 불변 — 속성 조회도 IsAudioInfoFile이 미리 접는다).
    /// 추출 실패·썸네일 없음·비트맵 디코드 실패는 배지만 걷고 확장자 타일 유지(안내 없음).
    /// A270: 한 워커 왕복이 썸네일과 오디오 정보를 함께 물어 오므로 seq 대조도 한 벌이다 —
    /// 교체 없음(아이콘형·실패) 갈래에서는 확장자 타일 하단에 정보를 얹고(MakeAudioInfoText),
    /// 앨범아트로 교체된 갈래에서는 아트 하단 반투명 띠로 같은 정보를 얹는다(MakeAudioInfoBand).
    /// 배지는 어느 갈래에서든 정보 표기보다 먼저 걷히므로(교체 갈래는 Clear가 겸한다) 겹치지 않는다.
    /// </summary>
    private async Task FillShellThumbnailAsync(
        Grid host, FontIcon badge, ExplorerListing.Entry entry, bool wantAudioInfo, int seq)
    {
        await _thumbFetchGate.WaitAsync(); // UI 문맥 await — 후속부는 UI 스레드로 복귀
        try
        {
            if (seq != _showSeq) return; // ① 대기 중 낡음 — 발사 자체를 접는다(고아 host라 배지도 무의미)
            (byte[]? Bytes, string? Info) result;
            try
            {
                result = await ThumbPool.Run(
                    _ => FetchTilePreview(entry.Path, entry.IsPlaceholder, wantAudioInfo));
            }
            catch
            {
                result = (null, null); // 추출 실패·풀 닫힘(취소 Task) — 아래 공통 실패 경로(배지 걷기)로
            }
            if (seq != _showSeq) return; // ② 완료 시점 재대조(이중 방어)
            // 튜플을 지역 변수로 풀어 둔다 — 아래 null 판정·재사용이 종전(단일 bytes) 형태 그대로.
            var bytes = result.Bytes;
            var info = result.Info;
            if (bytes is null)
            {
                // 실패·썸네일 없음·아이콘형(A270 ③) — 확장자 타일 유지(사양). 정보가 있으면 얹는다.
                host.Children.Remove(badge);
                if (info is not null) host.Children.Add(MakeAudioInfoText(entry, info));
                return;
            }
            try
            {
                // 바이트 → BitmapImage: ExplorerPane.LoadThumbnailsAsync의 반영 관용구 그대로
                var bitmap = new BitmapImage();
                using (var stream = new MemoryStream(bytes))
                    await bitmap.SetSourceAsync(stream.AsRandomAccessStream());
                if (seq != _showSeq) return;
                // A335 계측: 타일 내용이 화면에 처음 얹히는 순간. Mark는 같은 이름을 한 번만
                // 기록하므로(NavDiagnostics.Mark) 세 갈래(캐시 썸네일·텍스트 미리보기·셸
                // 썸네일) 어디서 먼저 와도 첫 것만 남는다. 이 마크가 필요한 이유: A334 실측의
                // 정지(343ms → 파일 1만 개에서 1,289ms)가 마지막 마크 <b>이후</b>로 잡혀
                // "모든 반영이 끝난 뒤"까지만 알 수 있었다 — 그 구간이 내용 얹기인지 그보다
                // 뒤인지 가르는 자가 없었다. 이제 정지 라벨이 prev0 앞뒤로 갈린다.
                NavDiagnostics.Mark("prev0");
                host.Children.Clear();
                host.Children.Add(new Image
                {
                    Source = bitmap,
                    Stretch = Stretch.Uniform,
                    Margin = new Thickness(4),
                });
                // A270 ②: 앨범아트 위 정보 띠 — 배지는 위 Clear가 이미 걷었다(겹침 없음).
                if (info is not null) host.Children.Add(MakeAudioInfoBand(info));
            }
            catch
            {
                host.Children.Remove(badge); // 손상 데이터 디코드 실패 — 확장자 타일 유지
                if (info is not null) host.Children.Add(MakeAudioInfoText(entry, info));
            }
        }
        finally
        {
            _thumbFetchGate.Release(); // 예외·낡음 경로 포함 — 누락되면 상한 건 뒤 조용히 멈춘다(A194)
        }
    }

    /// <summary>
    /// 워커 스레드: 타일 지연 교체 1회분 — 셸 썸네일 바이트(A242)와 오디오 정보 텍스트(A270)를
    /// 한 번의 왕복으로 함께 읽는다(StorageFile 취득도 1회. 파일당 워커 왕복이 2회가 되면
    /// 게이트 상한이 사실상 반토막 나고 seq 대조도 두 벌이 된다 — 통합이 그 둘을 다 막는다).
    /// 썸네일 = ExplorerPane.FetchThumbnail 이식·요청 크기만 PreviewDecodeWidth(이미지 실디코드
    /// 폭과 통일 — A275에서 256 → 768). StorageFile API는 agile이라 워커에서 불러도 되고, WinRT 비동기는
    /// 여기서 동기 대기한다(전용 스레드라 UI 교착 없음). cachedOnly(A175): 옵션 없는 호출은
    /// 캐시가 비면 시스템이 원본을 열어 생성하므로 placeholder에서는 하이드레이션(전체
    /// 다운로드)이 된다 — ReturnOnlyIfCached로 캐시에 없으면 null.
    /// <b>A270 ③</b>: 셸이 돌려준 것이 파일 종류 아이콘(Type = Icon)이면 Bytes = null로 접는다 —
    /// 무정보 제네릭 아이콘이 정보가 있는 확장자 타일을 덮는 반개선을 막는 전 파일 공통 규칙
    /// (되돌리려면 Type 판정 한 줄만 지우면 A242 종전 동작으로 복귀한다).
    /// 예외(잠김·삭제 경합)는 호출부 catch가 삼킨다.
    /// </summary>
    private static (byte[]? Bytes, string? Info) FetchTilePreview(
        string path, bool cachedOnly, bool wantAudioInfo)
    {
        var file = StorageFile.GetFileFromPathAsync(path).AsTask().GetAwaiter().GetResult();

        string? info = null;
        if (wantAudioInfo)
        {
            try
            {
                info = FetchAudioInfo(file);
            }
            catch
            {
                info = null; // 속성 핸들러 없음·조회 실패 — 정보 없이 썸네일만 간다(조각 전부 생략)
            }
        }

        using var thumb = (cachedOnly
                ? file.GetThumbnailAsync(ThumbnailMode.SingleItem, PreviewDecodeWidth,
                    ThumbnailOptions.ReturnOnlyIfCached)
                : file.GetThumbnailAsync(ThumbnailMode.SingleItem, PreviewDecodeWidth))
            .AsTask().GetAwaiter().GetResult();
        if (thumb is null || thumb.Size == 0) return (null, info);
        if (thumb.Type == ThumbnailType.Icon) return (null, info); // A270 ③ — 교체 생략(복구 = 이 줄 삭제)

        using var stream = thumb.AsStreamForRead();
        using var buffer = new MemoryStream((int)thumb.Size);
        stream.CopyTo(buffer);
        return (buffer.ToArray(), info);
    }

    // ---------- 오디오 타일 정보 (A270) ----------

    /// <summary>
    /// 워커 스레드: 오디오 셸 속성 4종을 읽어 타일 정보 2줄을 만든다 (A270) —
    /// ExplorerPane.FetchDurationTicks(A6)와 같은 RetrievePropertiesAsync 관용구이고 키만
    /// System.Media.Duration + System.Audio.* 3종이다. 표기 = "3:45 · 320 kbps" / "44.1 kHz · 2ch",
    /// 값이 없는 조각은 빼고(속성 핸들러가 없는 컨테이너·손상 파일) 남는 조각이 없으면 null =
    /// 표기 자체를 걸지 않는다(ExplorerPane 상세 줄의 조각 생략 규칙과 같은 폴백).
    /// </summary>
    private static string? FetchAudioInfo(StorageFile file)
    {
        var props = file.Properties.RetrievePropertiesAsync(
                ["System.Media.Duration", "System.Audio.EncodingBitrate",
                 "System.Audio.SampleRate", "System.Audio.ChannelCount"])
            .AsTask().GetAwaiter().GetResult();

        var ticks = props.TryGetValue("System.Media.Duration", out var d) ? (long)PropNumber(d) : 0L;
        var duration = ticks > 0
            ? ExplorerListing.FormatDuration(TimeSpan.FromTicks(ticks))
            : string.Empty;
        // bit/s · Hz · 채널 수 — 셸 속성 핸들러가 없는 컨테이너는 키가 통째로 빠진다(= 0 = 생략).
        var bitrate = props.TryGetValue("System.Audio.EncodingBitrate", out var b) ? PropNumber(b) : 0UL;
        var sampleRate = props.TryGetValue("System.Audio.SampleRate", out var s) ? PropNumber(s) : 0UL;
        var channels = props.TryGetValue("System.Audio.ChannelCount", out var c) ? PropNumber(c) : 0UL;

        // 1kbps 미만·1kHz 미만은 값이 아니라 잡음으로 보고 버린다(0 포함 — 조각 생략).
        var top = JoinFragments(" · ", duration, bitrate >= 1000 ? $"{bitrate / 1000} kbps" : string.Empty);
        var bottom = JoinFragments(" · ",
            sampleRate >= 1000 ? $"{sampleRate / 1000.0:0.#} kHz" : string.Empty,
            channels > 0 ? $"{channels}ch" : string.Empty);
        var info = JoinFragments("\n", top, bottom);
        return info.Length == 0 ? null : info;
    }

    /// <summary>
    /// 셸 속성 값을 부호 없는 정수로 (A270). 속성 핸들러가 주는 실제 형은 키마다 다르고
    /// (Duration = UInt64 100ns 틱, System.Audio.* 3종 = UInt32) 값 없음은 null로 들어오므로
    /// 형 분기 + 그 밖 전부 = 0으로 눕힌다. 0 = "값 없음" = 호출부의 조각 생략.
    /// 인자를 object로 받는 이유: 사전 자체를 넘기면 WinRT 투영 사전의 형 표기(원소 null 허용
    /// 여부 포함)에 서명이 묶인다 — 값만 받으면 기존 조회 관용구(TryGetValue + 형 검사)를
    /// 그대로 두고 형 분기만 한 곳에 모을 수 있다.
    /// </summary>
    private static ulong PropNumber(object? value) => value switch
    {
        ulong u => u,
        uint ui => ui,
        long l when l > 0 => (ulong)l,
        int i when i > 0 => (ulong)i,
        _ => 0UL,
    };

    /// <summary>빈 조각을 건너뛰는 두 조각 잇기 (A270) — 둘 다 비면 빈 문자열.</summary>
    private static string JoinFragments(string separator, string first, string second) =>
        first.Length == 0 ? second
        : second.Length == 0 ? first
        : first + separator + second;

    /// <summary>
    /// 확장자 타일 위 오디오 정보 표기 (A270 ①): 타일 하단 중앙 소형 2줄. 확장자 라벨은 타일
    /// 중앙에 그대로 남고 이 텍스트만 아래에 겹친다 — 액센트 배경이면 라벨과 같은 흰 글자
    /// (MakeExtensionTile의 대비 규칙 그대로). 히트 테스트 제외는 대기 배지(MakePendingBadge)와
    /// 같은 이유다. 좁은 타일에서 줄이 넘치면 줄마다 말줄임(캡션과 같은 CharacterEllipsis).
    /// </summary>
    private UIElement MakeAudioInfoText(ExplorerListing.Entry entry, string info)
    {
        var text = new TextBlock
        {
            Text = info,
            FontSize = 9,
            TextAlignment = TextAlignment.Center,
            TextWrapping = TextWrapping.NoWrap,
            TextTrimming = TextTrimming.CharacterEllipsis,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Bottom,
            Margin = new Thickness(10, 0, 10, 16),
            Opacity = 0.9,
            IsHitTestVisible = false,
        };
        if (Branding.ModuleAccent(ModuleIdForFile?.Invoke(entry.Path)) is not null)
            text.Foreground = new SolidColorBrush(Microsoft.UI.Colors.White);
        return text;
    }

    /// <summary>
    /// 앨범아트 위 정보 띠 (A270 ②): 아트 하단에 반투명 검정 띠 + 흰 소형 2줄. 테마 브러시가
    /// 아니라 고정 반투명 검정(FromArgb — MarkdownRenderer의 브러시 관용구)인 이유 = 띠가 아트
    /// 위에 얹히므로 테마색으로는 대비가 보장되지 않는다. 아트와 같은 여백(4)이라 이미지 밖으로
    /// 튀지 않고, 히트 테스트에서 빠지는 것도 정보 표기·배지와 같은 규칙이다.
    /// </summary>
    private static UIElement MakeAudioInfoBand(string info) => new Border
    {
        Background = new SolidColorBrush(Windows.UI.Color.FromArgb(0xB0, 0x00, 0x00, 0x00)),
        Margin = new Thickness(4),
        Padding = new Thickness(4, 2, 4, 2),
        CornerRadius = new CornerRadius(0, 0, 4, 4),
        VerticalAlignment = VerticalAlignment.Bottom,
        IsHitTestVisible = false,
        Child = new TextBlock
        {
            Text = info,
            FontSize = 9,
            TextAlignment = TextAlignment.Center,
            TextWrapping = TextWrapping.NoWrap,
            TextTrimming = TextTrimming.CharacterEllipsis,
            Foreground = new SolidColorBrush(Microsoft.UI.Colors.White),
        },
    };

    /// <summary>
    /// 항목 우클릭 메뉴 — ExplorerPane.AttachContextMenu와 같은 구성(A94 2차 신설 → 6차 확장).
    /// 순서는 탐색기 관례 근사: 파일 = "Open in new instance"(A24) → 구분선 → Cut·Copy →
    /// 구분선 → Rename·Delete, 폴더 = Cut·Copy·**Paste(대상 = 그 폴더)** → 구분선 → Rename·Delete.
    /// Delete·Cut·Copy 대상은 드래그와 같은 규칙 — 그 타일이 선택에 포함돼 있으면 선택 전부,
    /// 아니면 그 타일 하나(PathsFor).
    /// Rename은 플라이아웃이 닫히며 포커스를 되돌린 '뒤'에 진입해야 편집 상자가 곧장 LostFocus
    /// 커밋으로 닫혀 버리지 않는다 — 디스패처로 한 박자 미룬다.
    /// </summary>
    /// <remarks>
    /// A335: 타일이 만들어질 때는 <b>빈 MenuFlyout 하나만</b> 달고 내용은 <b>열릴 때</b> 채운다 —
    /// 좌 리스트(ExplorerPane.AttachContextMenu)와 같은 수리이고 근거도 같다(그 주석이 정본).
    /// 요지: 종전에는 타일마다 메뉴를 통째로 조립해 항목 1개당 XAML 객체가 열 개 남짓 늘었고,
    /// A334 계측판이 그 비용을 <c>clay&gt;fillN 8,539ms</c>(파일 10,000개)로 찍었다.
    /// </remarks>
    private void AttachContextMenu(GridViewItem item, ExplorerListing.Entry entry)
    {
        var flyout = new MenuFlyout();
        flyout.Opening += (_, _) => BuildTileContextMenu(flyout, item, entry);
        item.ContextFlyout = flyout;
    }

    /// <summary>
    /// A335: 타일 메뉴의 실제 내용 — 열릴 때마다 새로 채운다(구성·순서·활성 조건·대상 규칙은
    /// 종전 그대로, 옮긴 것은 <b>시점</b>뿐). 매번 비우므로 두 번째 우클릭에 겹쳐 쌓이지 않는다.
    /// </summary>
    private void BuildTileContextMenu(MenuFlyout flyout, GridViewItem item, ExplorerListing.Entry entry)
    {
        flyout.Items.Clear();
        if (!entry.IsFolder)
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
        AddClipboardItems(flyout, entry); // A94 6차 — Cut·Copy·(폴더면 Paste) + 구분선
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
        delete.Click += async (_, _) => await DeleteWithNoticeAsync(PathsFor(entry));
        flyout.Items.Add(delete);
    }

    /// <summary>
    /// 타일 메뉴의 클립보드 묶음 (A94 6차): Cut · Copy · (폴더면) Paste + 뒤따르는 구분선.
    /// 조작은 Ctrl+C/X/V와 **완전히 같은 경로**다(CopyWithNoticeAsync·PasteIntoAsync) —
    /// 폴더 Paste만 대상이 현재 폴더가 아니라 그 폴더다(PasteFromClipboardAsync가 이미 대상
    /// 폴더를 인자로 받으므로 넓힐 것이 없었다). Paste 활성 판정은 메뉴가 열릴 때 한다.
    /// </summary>
    private void AddClipboardItems(MenuFlyout flyout, ExplorerListing.Entry entry)
    {
        var cutItem = new MenuFlyoutItem
        {
            Text = "Cut",
            Icon = new FontIcon { Glyph = "\uE8C6" }, // Cut
        };
        cutItem.Click += async (_, _) => await CopyWithNoticeAsync(PathsFor(entry), cut: true);
        flyout.Items.Add(cutItem);

        var copyItem = new MenuFlyoutItem
        {
            Text = "Copy",
            Icon = new FontIcon { Glyph = "\uE8C8" }, // Copy
        };
        copyItem.Click += async (_, _) => await CopyWithNoticeAsync(PathsFor(entry), cut: false);
        flyout.Items.Add(copyItem);

        if (entry.IsFolder)
        {
            var pasteItem = new MenuFlyoutItem
            {
                Text = "Paste",
                Icon = new FontIcon { Glyph = "\uE77F" }, // Paste
            };
            pasteItem.Click += async (_, _) => await PasteIntoAsync(entry.Path);
            // A335: 조립 자체가 Opening 안에서 도므로 지금 판정이 곧 "열리는 순간의 판정"이다 —
            // 종전처럼 Opening을 더 구독하면 열 때마다 죽은 핸들러가 쌓인다(좌 리스트와 같은 사정).
            pasteItem.IsEnabled = ExplorerFileOps.CanPasteFromClipboard();
            flyout.Items.Add(pasteItem);
        }
        flyout.Items.Add(new MenuFlyoutSeparator());
    }

    /// <summary>
    /// 빈 영역 우클릭 메뉴 (A94 6차 → A189에서 New file 추가): New folder / New file / Paste /
    /// Refresh — 전부 기존 경로 재사용이다(Ctrl+Shift+N의 CreateFolderThenRenameAsync와 그 파일
    /// 판본 CreateFileThenRenameAsync = 생성 후 이름 편집 진입까지 · 현재 폴더 붙여넣기 ·
    /// 단일 원본 경유 재스캔 RefreshViaShell). 이 뷰는 표면이 하나라 메뉴도 한 벌이다.
    /// 활성 판정은 메뉴가 열릴 때: 아직 폴더가 정해지지 않았으면 전부 비활성, Paste는 클립보드에
    /// 파일 항목이 있을 때만(판정 실패 시 활성 — CanPasteFromClipboard 주석).
    /// </summary>
    private MenuFlyout MakeSurfaceMenu()
    {
        var newFolder = new MenuFlyoutItem
        {
            Text = "New folder",
            Icon = new FontIcon { Glyph = "\uE8F4" }, // NewFolder
        };
        newFolder.Click += async (_, _) => await CreateFolderThenRenameAsync();

        // A189: New file - New folder 옆, 같은 흐름(생성 후 이름변경 편집 진입)의 파일 판본.
        var newFile = new MenuFlyoutItem
        {
            Text = "New file",
            Icon = new FontIcon { Glyph = "\uE7C3" }, // 문서(파일) — 탐색기 파일 타일과 같은 글리프
        };
        newFile.Click += async (_, _) => await CreateFileThenRenameAsync();

        var paste = new MenuFlyoutItem
        {
            Text = "Paste",
            Icon = new FontIcon { Glyph = "\uE77F" }, // Paste
        };
        paste.Click += async (_, _) => await PasteIntoAsync(CurrentFolder ?? string.Empty);

        var refresh = new MenuFlyoutItem
        {
            Text = "Refresh",
            Icon = new FontIcon { Glyph = "\uE72C" }, // Refresh
        };
        refresh.Click += (_, _) => RefreshViaShell();

        var flyout = new MenuFlyout();
        flyout.Items.Add(newFolder);
        flyout.Items.Add(newFile);
        flyout.Items.Add(paste);
        flyout.Items.Add(refresh);
        flyout.Opening += (_, _) =>
        {
            var ready = CurrentFolder is { Length: > 0 };
            newFolder.IsEnabled = ready;
            newFile.IsEnabled = ready; // A189: 새 폴더와 같은 판정(폴더 확정 전 비활성)
            paste.IsEnabled = ready && ExplorerFileOps.CanPasteFromClipboard();
            refresh.IsEnabled = ready;
        };
        return flyout;
    }

    // ---------- 입력 ----------

    /// <summary>
    /// 원시 눌림(PointerPressed) 쌍 = 더블클릭 최후 폴백 (A131 — 배선 근거는 생성자 주석).
    /// 왼쪽 눌림 "전이"만 태운다(A112 XButton1 판정과 같은 관용구 — 다른 버튼이 눌린 채 겹쳐 온
    /// 눌림은 전이 종류가 달라 걸리지 않는다). Ctrl 눌림은 다중 선택 토글 제스처라 쌍에서
    /// 제외한다(Shift는 제외하지 않는다 — Shift+더블클릭 = 새 창(A24)은 Activate가 해석한다).
    /// 정상 환경에서는 기존 두 판정과 같은 제스처에서 겹쳐 발화하지만 Activate의 _lastActivation
    /// 억제(A85)가 1회로 누른다 — 두 번째 눌림 시점 발화는 탐색기 관례(WM_LBUTTONDBLCLK)와 같다.
    /// A212: 빈 영역(타일 밖) 눌림의 포커스 정착(FocusGrid)도 여기서 한다 — 구독이 LayoutRoot로
    /// 올라간 근거·고아 갈래 설명은 생성자와 본문 주석 참고.
    /// </summary>
    private void OnSurfacePointerPressed(object sender, PointerRoutedEventArgs e)
    {
        if (e.GetCurrentPoint(TileGrid).Properties.PointerUpdateKind
            != Microsoft.UI.Input.PointerUpdateKind.LeftButtonPressed) return;
        if (e.OriginalSource is TextBox) return; // 이름변경 편집 상자(A94 2차) — 더블클릭은 텍스트 선택 몫
        // A212: 빈 영역(타일 밖) 눌림 = 그리드로 포커스 정착. 빈 공간의 히트 대상(LayoutRoot·
        // 스크롤 표면)은 전부 비포커스 요소라, 이 클릭이 포커스를 XAML 트리 밖/null로 흘리면
        // 이후 셸 KeyDown(RootLayout 버블 수신 — F11/F12·Enter·Esc 전부)이 아예 안 와 키가
        // 전멸한다(A135 감사 §표1 34행 확정 갈래·A209 RecoverChromeFocusOrphan과 같은 계보 —
        // 그쪽은 크롬 붕괴 "전이 시점"만 지키고 클릭 시점은 무방비였다). FocusGrid = S4 진입
        // (A90)과 같은 정착 관용구 재사용이다. Ctrl 판정보다 앞에 두는 이유: Ctrl+빈 영역
        // 클릭도 같은 고아 갈래다 — 두 갈래 모두 쌍을 끊고 반환하므로 순서 교환은 A131 의미론
        // 무변경. 이름변경 편집 상자 위 눌림은 위 TextBox 가드가 먼저 걸러 포커스를 뺏지 않고,
        // 편집 중 빈 영역 클릭은 포커스 이동의 LostFocus 커밋(ExplorerRenameBox — "딴 곳 클릭 =
        // 커밋" 탐색기 관례)이 그대로 성립한다.
        if (EntryFromSource(e.OriginalSource) is not { } entry)
        {
            FocusGrid();
            _lastPress = null; // 빈 영역·스크롤바 — 항목 밖 눌림은 쌍을 끊는다
            return;
        }
        if (ExplorerFileOps.IsCtrlDown())
        {
            _lastPress = null; // Ctrl 토글 선택 — 진행 중이던 쌍 판정을 끊는다
            return;
        }
        var now = DateTime.UtcNow;
        var isPair = _lastPress is { } last && last.Path == entry.Path &&
                     (now - last.At).TotalMilliseconds < DoubleClickMs;
        _lastPress = isPair ? null : (entry.Path, now);
        if (isPair) Activate(entry);
    }

    /// <summary>눌림의 원본 요소에서 타일 컨테이너(Tag = Entry)를 찾는다 — 조상 상향 탐색
    /// (깊이 상한 64 = HotkeySupport.MaxAncestorDepth와 같은 방어).</summary>
    private static ExplorerListing.Entry? EntryFromSource(object source)
    {
        var node = source as DependencyObject;
        for (var depth = 0; node is not null && depth < 64; depth++)
        {
            if (node is GridViewItem { Tag: ExplorerListing.Entry entry }) return entry;
            node = VisualTreeHelper.GetParent(node);
        }
        return null;
    }

    /// <summary>
    /// 클릭 2회(500ms 내 같은 항목) = 더블클릭 — ExplorerPane.OnItemClick과 같은 판정.
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
        Activate(entry);
    }

    /// <summary>
    /// 컨테이너 DoubleTapped = 더블클릭 열기 (A85). 실기기에서는 두 번째 클릭이 더블탭 제스처로
    /// 소비되어 두 번째 ItemClick이 오지 않아, 클릭 쌍 판정(OnItemClick)만으로는 열기가 조용히
    /// 무시됐다(압축 모듈 내부 리스트는 처음부터 DoubleTapped라 이 증상이 없었다 — 같은 배선).
    /// </summary>
    private void OnItemDoubleTapped(object sender, DoubleTappedRoutedEventArgs e)
    {
        if (e.OriginalSource is TextBox) return; // 이름변경 편집 상자(A94 2차) — 더블클릭은 텍스트 선택 몫
        if (sender is not GridViewItem { Tag: ExplorerListing.Entry entry }) return;
        e.Handled = true;
        _lastClick = null; // 이 제스처를 이룬 클릭 기록이 다음 클릭 쌍 판정에 섞이지 않게
        Activate(entry);
    }

    /// <summary>
    /// 더블클릭 열기 공통 종착점 (A85): 폴더 = 좌 리스트 항해(FolderActivated), 파일 = 열기
    /// (Shift = 새 창, A24). ItemClick 쌍과 DoubleTapped가 같은 제스처에서 둘 다 발화하는
    /// 환경이 있어, 같은 경로의 연속 발화를 판정 창(DoubleClickMs) 안에서 1회로 누른다 —
    /// A24 "항상 새 창" 설정에서 창이 두 개 뜨는 이중 열기 방지.
    /// A94 6차: 활성화한 타일이 **다중 선택에 포함돼 있으면** 선택된 파일 전부를 연다(폴더 제외 —
    /// 선택에 파일이 하나도 없으면 종전대로 그 타일 하나. Enter 규칙과 같다).
    /// </summary>
    private void Activate(ExplorerListing.Entry entry)
    {
        var now = DateTime.UtcNow;
        if (_lastActivation is { } last && last.Path == entry.Path &&
            (now - last.At).TotalMilliseconds < DoubleClickMs)
            return;
        _lastActivation = (entry.Path, now);

        var shift = Microsoft.UI.Input.InputKeyboardSource
            .GetKeyStateForCurrentThread(Windows.System.VirtualKey.Shift)
            .HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down);

        // A94 6차 — 다중 선택 일괄 열기(잡은 타일이 선택 밖이면 그 타일만: 드래그·삭제와 같은 규칙)
        if (TileGrid.SelectedItems.Count > 1 &&
            SelectedPaths().Contains(entry.Path, StringComparer.OrdinalIgnoreCase) &&
            OpenFiles(SelectedFilePaths(), shift))
            return;

        if (entry.IsFolder)
        {
            FolderActivated?.Invoke(entry.Path);
            return;
        }

        if (shift) FileActivatedNewWindow?.Invoke(entry.Path);
        else FileActivated?.Invoke(entry.Path);
    }

    // ---------- 드래그 앤 드랍 (A94 1차, v0.124.0 — A93의 무동작 소비를 실동작으로 전환) ----------

    /// <summary>
    /// 중앙(탐색기) 빈 영역·파일 타일 위 드래그 = 현재 폴더로 이동/복사(같은 볼륨 이동/다른 볼륨
    /// 복사·Ctrl 복사 강제·Shift 이동 강제 — ExplorerFileOps.DecideOperation). 폴더 타일 위는
    /// 타일 자체 핸들러(AttachDragDrop)가 먼저 Handled로 받는다. 목록이 아직 없으면(폴더 미정)
    /// None으로 소비만 — 어느 쪽이든 Handled라 창 전체 "열기" 폴백(OnWindowDrop)에 안 넘어간다.
    /// </summary>
    private void OnDragOver(object sender, DragEventArgs e) =>
        ExplorerFileOps.HandleTargetDragOver(e, CurrentFolder);

    /// <summary>빈 영역·파일 타일 위 드랍 — 대상 = 현재 폴더.</summary>
    private void OnDrop(object sender, DragEventArgs e)
    {
        if (CurrentFolder is { Length: > 0 } folder) HandleDrop(e, folder);
        else e.Handled = true; // 폴더 미정 — A93 때처럼 소비만
    }

    /// <summary>
    /// 드랍 실행(A94): 조작은 워커에서 비동기, 완료 후 FolderActivated로 현재 폴더를 다시 항해 —
    /// 폴더 상태의 단일 원본인 좌 리스트(ExplorerPane)를 셸이 항해시키고 결과가 ViewChanged로
    /// 돌아와 이 그리드까지 갱신된다(A93 경로 그대로 — 5차의 폴더 감시가 같은 변경을 또 봐도
    /// 디바운스가 흡수하므로 명시 재스캔은 유지한다).
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
        FolderActivated?.Invoke(CurrentFolder is { Length: > 0 } current ? current : targetFolder);
        await ExplorerFileOps.ReportAsync(result.Notice(move), result.Denied, ui);
    }

    // ---------- 조작 실패 안내 (A94 — A92류 일시 문구) ----------
    // Storyboard 페이드는 CI(컴파일 전용)로 검증할 수 없고 실패 시 상태가 남을 수 있어(A92 선례)
    // A90 강조와 같은 타이머 + Visibility 두 단계로만 구현 — 최악의 실패도 "문구가 안 보인다".

    private static readonly TimeSpan NoticeHoldFor = TimeSpan.FromSeconds(2.5); // A92 표시 시간과 동일

    private DispatcherTimer? _noticeTimer;

    private void ShowNotice(string text)
    {
        NoticeText.Text = text;
        NoticeText.Visibility = Visibility.Visible;
        if (_noticeTimer is null)
        {
            var timer = new DispatcherTimer { Interval = NoticeHoldFor };
            timer.Tick += (_, _) =>
            {
                timer.Stop(); // 반복 타이머 — Tick에서 반드시 멈춘다(A92 관용구)
                NoticeText.Visibility = Visibility.Collapsed;
            };
            _noticeTimer = timer;
        }
        _noticeTimer.Stop(); // 연속 실패 시 표시 시간 되감기
        _noticeTimer.Start();
    }
}
