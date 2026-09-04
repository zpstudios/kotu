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
/// <para>
/// A345 배치 3(UI 가상화): 이 표면은 더 이상 타일 컨테이너를 직접 만들지 않는다 —
/// <c>ShowEntries</c>가 뷰모델 목록(<see cref="ExplorerEntryVm"/>)을 <c>ItemsSource</c>로
/// 대입하면 XAML이 <b>화면에 보이는 타일만</b> 실체화한다(DataTemplate + x:Bind +
/// ContainerContentChanging — 정본 선례는 PdfPane, 좌 리스트 판본은 배치 2). 그와 함께
/// 사라진 것: 분할 조립 루프(A192)·실체화 상한과 안내 타일·뷰포트 지연 미리보기(A339
/// DeferPreview). 미리보기 4갈래는 위상 0(폴백 타일을 동기로) → 위상 1(비동기 요청)로 옮겼다.
/// </para>
/// <para>
/// <b>이 표면의 전제가 뒤집힌 지점</b>: 종전 주석이 곳곳에서 근거로 삼던 "타일은 재사용되지
/// 않는다 · ShowEntries가 전량 새로 만든다"는 이제 거짓이다. 컨테이너는 스크롤하는 동안
/// 다른 파일의 타일로 <b>재활용</b>된다. 그래서 낡음 방어가 두 겹이다:
/// ① 폴더 전환은 종전대로 <c>_showSeq</c> 대조, ② <b>같은 폴더 안 재활용</b>은
/// <c>ReferenceEquals(item.Content, vm)</c> 대조 — seq는 후자를 못 막는다(폴더가 안 바뀌었으니).
/// 비동기 완료가 화면을 만지기 전에는 반드시 둘 다 통과해야 하고, 통과하지 못한 결과도
/// <b>뷰모델 캐시에는 남긴다</b>(그 항목이 다시 실체화될 때 IO 없이 즉시 그려진다).
/// </para>
/// </summary>
public sealed partial class ThumbnailExplorer : UserControl
{
    /// <summary>
    /// 미리보기 요청 폭 상한(물리 px) — 원본 크기 디코드로 메모리가 폭주하지 않게.
    /// 이 파일의 미리보기 3경로가 공유하는 유일한 수치다: ① 이미지 실디코드
    /// (StartImagePreview의 BitmapImage.DecodePixelWidth) ② placeholder 캐시 전용 셸 썸네일
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

    /// <summary>DataTemplate 안 미리보기 자리(빈 Grid)의 x:Name — 조회는 이름 기반이다(A345 배치 3).
    /// XAML의 x:Name과 값이 어긋나면 미리보기가 <b>예외 없이</b> 한 장도 안 뜬다.</summary>
    private const string TilePreviewHostName = "TilePreviewHost";

    /// <summary>DataTemplate 안 캡션 TextBlock의 x:Name (A345 배치 3) — 이름변경 진입
    /// (BeginRenameOf)이 이것으로 찾는다. 종전 tile.Children[1] 인덱스 계약의 대체다.</summary>
    private const string TileCaptionName = "TileCaption";

    // A345 배치 3에서 사라진 수치·장치: 분할 조립 조각(TileChunkItems 60)·즉시 미리보기 앞
    // 구간(EagerPreviewCount)·뷰포트 선반입 거리(PreviewPrefetchDip 600)·뷰포트 지연 미리보기
    // (A339 DeferPreview = EffectiveViewportChanged 1회 구독)·실체화 상한(MaterializeLimit 500).
    // 넷의 목적이 전부 "다 만들면 비싸니 앞쪽만 만들자"였는데, 가상화는 애초에 보이는 것만
    // 만들므로 그 목적 자체가 소멸했다(A339의 뷰포트 판정도 이제 XAML 패널이 대신한다).
    // 상한이 사라져 10,000개 폴더도 마지막 항목까지 보인다.

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

    /// <summary>A192: 재진입 가드 — ShowEntries·ShowLoading·Unloaded가 올 때마다 증가
    /// (ExplorerPane._loadSeq 관용구). A345 배치 3부터 이 값의 역할은 <b>폴더 전환</b> 한 가지다:
    /// 진행 중이던 미리보기 요청이 옛 폴더의 것이면 그 완료를 버린다. 같은 폴더 안에서 컨테이너가
    /// 다른 파일로 재활용되는 경우는 seq가 안 바뀌므로 <c>ReferenceEquals</c> 대조가 따로 막는다.</summary>
    private int _showSeq;

    /// <summary>
    /// 지금 그리고 있는 표시 목록의 뷰모델 (A345 배치 3) — <c>TileGrid.ItemsSource</c>에 대입한
    /// 바로 그 목록이다. 경로 조회(FindVmByPath)·잘라내기 흐림 재적용이 컨테이너가 아니라 이
    /// 목록을 돈다: 가상화 뒤에는 컨테이너가 화면 분량뿐이라 순회 결과가 스크롤 위치에 따라
    /// 달라지지만, 이 목록은 화면 밖 항목까지 전부 들고 있다.
    /// </summary>
    private IReadOnlyList<ExplorerEntryVm> _vms = [];

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
    /// 다음 <see cref="ShowEntries"/>가 이 경로의 항목을 찾아 이름변경 편집으로 진입하고
    /// 지운다(1회성). A345 배치 3에서 소비 시점이 단순해졌다 — 조립이라는 개념이 사라져
    /// "완주한 조립을 기다린다"는 조건이 없어졌고(목록은 대입 즉시 전부 있다), 편집할 행의
    /// 컨테이너만 그때 실체화한다(BeginRenameByPath).
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
        VmOf(TileGrid.SelectedItem) is { IsFolder: false } vm ? vm.Path : null; // A345 배치 3 — VmOf 단일 해석

    /// <summary>선택된 항목(파일·폴더 불문) — 없으면 null (A90: S4 Enter "선택 열기 우선" 판정).</summary>
    public ExplorerListing.Entry? SelectedEntry =>
        VmOf(TileGrid.SelectedItem)?.Entry; // A345 배치 3 — 반환은 종전대로 Entry

    // ---------- 항목 해석의 단일 지점 (A345 배치 3) ----------

    /// <summary>
    /// 어떤 "항목 객체"에서든 뷰모델을 꺼낸다 — <b>이 배치의 최대 함정을 막는 단일 깔때기</b>다
    /// (좌 리스트의 같은 이름 함수와 같은 역할·같은 근거). ItemsSource로 바뀌면서
    /// <c>SelectedItem</c>·<c>ClickedItem</c>·<c>SelectedItems</c>가 <b>뷰모델 자체</b>가 되고
    /// 컨테이너(GridViewItem)의 Content도 뷰모델이다 — 종전 <c>Tag</c> 패턴이 한 곳이라도 남으면
    /// Enter·F2·Del·Ctrl+C/X·드래그·정보 패널이 <b>예외 없이</b> 죽는다(컴파일도 통과한다).
    /// </summary>
    private static ExplorerEntryVm? VmOf(object? o) => o switch
    {
        ExplorerEntryVm vm => vm,
        GridViewItem { Content: ExplorerEntryVm vm } => vm,
        _ => null,
    };

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
    /// 잘라내기(Ctrl+X) 표시 반영 (A94 4차): 표시 목록의 흐림 값을 경로 매칭으로 다시 맞춘다 —
    /// 재스캔이 아니라 제자리 갱신이라 선택·스크롤이 보존된다.
    /// <para>
    /// A345 배치 3: 순회 대상이 컨테이너(TileGrid.Items)에서 <b>뷰모델 목록</b>으로 바뀌었다 —
    /// 가상화 뒤에는 컨테이너가 화면 분량뿐이라 컨테이너를 돌면 화면 밖 항목의 흐림이 스크롤
    /// 위치에 따라 빠진다. 화면 반영은 DataTemplate의 x:Bind(ContentOpacity, OneWay)가 맡는다.
    /// </para>
    /// </summary>
    private void ApplyCutMarks()
    {
        foreach (var vm in _vms) ExplorerFileOps.ApplyCutMark(vm);
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
            // A345 배치 3: 선택은 뷰모델이라 컨테이너를 먼저 실체화해야 편집 상자를 끼울 대상이
            // 생긴다(보수안 ⓐ). Handled는 그 컨테이너를 실제로 얻은 뒤에만 건다 — 못 얻었는데
            // 소비해 버리면 F2가 아무 데도 도달하지 않고 조용히 사라진다.
            if (VmOf(TileGrid.SelectedItem) is not { } target) return;
            if (RealizeTileContainer(target) is not { } container) return;
            e.Handled = true;
            BeginRenameOf(container);
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

    /// <summary>선택 타일 경로 전부(폴더 포함) — A345 배치 3부터 SelectedItems가 <b>뷰모델</b>이다(A94).</summary>
    private IReadOnlyList<string> SelectedPaths() =>
        TileGrid.SelectedItems
            .OfType<ExplorerEntryVm>()
            .Select(vm => vm.Path)
            .ToList();

    /// <summary>선택 타일 중 **파일**만의 경로 (A94 6차 — 일괄 열기 대상. 폴더는 제외한다).</summary>
    private IReadOnlyList<string> SelectedFilePaths() =>
        TileGrid.SelectedItems
            .OfType<ExplorerEntryVm>() // A345 배치 3 — 선택 항목 자체가 뷰모델
            .Where(vm => !vm.IsFolder)
            .Select(vm => vm.Path)
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
    /// <para>
    /// A345 배치 3: 조립이 사라졌다 — 뷰모델 목록을 만들어 <c>ItemsSource</c>에 대입하는 것이
    /// 전부이고, 컨테이너는 XAML이 보이는 만큼만 만든다. 그와 함께 사라진 것: 분할 조립 루프
    /// (A192)·실체화 상한과 안내 타일·완료 마무리 단계(FinishShowEntries). 대입은 동기 1회라
    /// 기다릴 것이 없고, 안내 타일은 ItemsSource 상태에서 Items.Add가 즉시 예외라 성립하지 않는다.
    /// </para>
    /// <para>
    /// 순서가 규칙이다: <c>ItemsSource</c> 대입 → <c>UpdateLayout</c> → <c>ApplyTileSize</c>.
    /// 타일 크기는 아이템 패널(ItemsWrapGrid)의 셀 크기로 거는데 그 패널은 <b>첫 레이아웃 뒤에만</b>
    /// 존재한다(ItemsPanelRoot) — 종전에는 패널이 없을 때를 위한 폴백(타일마다 직접 크기 대입)이
    /// 있었지만 가상화 뒤에는 Items가 컨테이너가 아니라 데이터라 그 폴백이 성립하지 않는다.
    /// 그래서 첫 화면에 셀 크기가 즉시 먹으려면 이 세 줄의 순서가 유일한 길이다.
    /// </para>
    /// 이미지 미리보기는 BitmapImage가 스스로 비동기 디코드하므로 별도 로드 루프가 없다.
    /// ※ 좌 리스트와 뷰모델 <b>객체</b>를 공유하지는 않는다(조사 문서의 "ViewChanged 훅 변경") —
    /// 그러려면 공개 시그니처(ShowEntries의 Entry 목록)를 바꿔 셸까지 손대야 해서 배치 4 후보로
    /// 남겼다. 지금은 이 표면이 자기 뷰모델을 따로 만든다(표시 상태도 표면별로 독립).
    /// </summary>
    public void ShowEntries(string folder, IReadOnlyList<ExplorerListing.Entry> entries)
    {
        _showSeq++; // 진행 중이던 미리보기 요청 전부 낡음 처리(폴더 전환·재스캔 공통)
        CurrentFolder = folder;
        TileGrid.ItemsSource = null; // 옛 목록 해제(같은 참조 재대입이 무시되는 일도 함께 막는다)

        var vms = entries.Select(e => new ExplorerEntryVm(e)).ToList();
        // A94 4차: 잘라내기 중인 경로면 처음부터 흐리게 — 종전 MakeTile의 생성 시점 반영을
        // 뷰모델 쪽으로 옮긴 것이다(화면 반영은 x:Bind ContentOpacity).
        foreach (var vm in vms) ExplorerFileOps.ApplyCutMark(vm);
        _vms = vms;
        EmptyText.Text = "No matching files here"; // A243 — ShowLoading의 "Loading..."을 원문구로 복원
        EmptyText.Visibility = entries.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        TileGrid.ItemsSource = _vms;
        // 계측 cfill0: 목록 대입 완료(배치 3 이후의 뜻 — 종전 "첫 조각 60타일 생성 완료"가 아니다).
        // 실제 컨테이너 생성은 레이아웃이 보이는 만큼만 하므로 여기부터는 개수에 비례하지 않는다.
        NavDiagnostics.Mark("cfill0");

        TileGrid.UpdateLayout(); // 아이템 패널 실체화 — 아래 타일 크기 반영이 헛돌지 않게
        ApplyTileSize();
        // 계측 clay: UpdateLayout(동기 레이아웃 패스)의 비용 — UI 스레드를 통째로 잡는 유일한
        // 명시 호출이라 따로 잰다(배치 3 이후 이 패스가 만드는 것은 화면 분량의 타일뿐이다).
        NavDiagnostics.Mark("clay");
        // 계측 cfillN / cpaint: 배치 3 이후 clay 직후가 곧 "목록 반영 끝"이다 — 조립이 동기 1회로
        // 접혀 clay와 cfillN 사이가 사실상 0이 됐다(종전에는 마지막 조각까지의 프레임 구간이었다).
        NavDiagnostics.Mark("cfillN");
        NavDiagnostics.ArmPaint("cpaint");

        // A94 2차: 새 폴더·새 파일(Ctrl+Shift+N / 메뉴) 직후의 재스캔이면 그 항목으로 곧바로
        // 이름변경 편집 진입. 조립 개념이 사라져 "완주한 조립을 기다린다"는 조건도 함께 사라졌다 —
        // 목록은 이미 전부 여기 있고, 편집할 행의 컨테이너는 BeginRenameByPath가 실체화한다.
        if (_pendingRenamePath is { } pending)
        {
            _pendingRenamePath = null; // 1회성 — 다음 갱신(다른 폴더 이동 등)에 재발화하지 않게
            BeginRenameByPath(pending);
        }
    }

    /// <summary>
    /// A243: 폴더 실변경 항해의 시작 통지 — 스캔 완료(ShowEntries)를 기다리지 않고 즉시 옛 폴더
    /// 타일을 지우고 로딩 문구를 띄운다(대형·OneDrive 폴더에서 수 초 무반응으로 보이던 체감 해소).
    /// 실변경 판정은 좌 리스트(ExplorerPane.NavigateToAsync)가 단일 지점으로 하고, 같은 폴더 감시
    /// 재스캔(400ms 디바운스)·정렬·필터 재작성은 이 경로로 오지 않아 종전대로 무Clear(깜빡임 방지).
    /// _showSeq 증가 = 보류 중 텍스트 프리뷰(A233)·셸 썸네일(A242) 예약 전부 무산(Unloaded와 같은
    /// 장치 — A345 배치 3부터 낡은 완료는 seq 대조에 걸려 화면을 만지지 못한다: 컨테이너가
    /// 재활용되므로 "고아 host라 무해"는 더 이상 근거가 아니다). 스캔 결과는 반드시 ShowEntries로 돌아와
    /// 문구·목록을 덮는다(실패 경로도 빈 목록 ViewChanged를 쏜다 — 로딩 문구가 잔존하지 않는 근거).
    /// _pendingRenamePath는 건드리지 않는다 — 다음 ShowEntries가 소비한다(A345 배치 3).
    /// </summary>
    public void ShowLoading(string folder)
    {
        _showSeq++;
        CurrentFolder = folder; // 좌 리스트(_folder)와 같은 시점 갱신 — 로딩 중 드랍·붙여넣기 대상 일치
        TileGrid.ItemsSource = null; // A345 배치 3 — 목록 해제가 곧 타일 비우기다
        _vms = [];
        EmptyText.Text = "Loading...";
        EmptyText.Visibility = Visibility.Visible;
        // 계측 cload: 중앙 썸네일 쪽 로딩 화면 전환 완료(좌 리스트의 load 바로 뒤 — 두 표면의
        // 비용을 갈라 본다). diag.navTiming이 꺼져 있으면 즉시 반환한다.
        NavDiagnostics.Mark("cload");
    }

    // ---------- 컨테이너 준비 · 미리보기 위상 (A345 배치 3) ----------

    /// <summary>
    /// 타일 컨테이너 준비 — 가상화의 계약이 여기 다 모여 있다(좌 리스트
    /// OnListContainerContentChanging의 중앙 판본이고 규칙도 같다):
    /// <list type="bullet">
    /// <item>재활용 큐로 들어가는 컨테이너는 <b>편집 상자를 강제 커밋</b>하고, 미리보기 자리를
    /// 비우고, 드랍을 끈다. 미리보기를 비우는 것이 곧 비트맵 해제다(PdfPane의
    /// <c>image.Source = null</c>과 같은 역할) — 안 비우면 재활용된 타일에 <b>다른 파일의 그림</b>이
    /// 그대로 남는다.</item>
    /// <item>훅(컨텍스트 메뉴·드래그·더블탭)은 컨테이너당 <b>1회만</b> 붙이고, 핸들러 안에서는
    /// 항목을 캡처하지 않고 <see cref="VmOf"/>로 그때그때 다시 푼다.</item>
    /// <item>AllowDrop은 <b>매번</b> 다시 정한다 — 폴더였던 컨테이너가 파일 타일로 재활용되면
    /// 잔존한 AllowDrop이 파일을 드랍 대상으로 만든다.</item>
    /// </list>
    /// 표시값(캡션·툴팁·잘라내기 흐림·클라우드 배지)은 x:Bind가 새 항목 값으로 다시 평가하므로
    /// 여기서 손대지 않는다. 손대는 것은 미리보기 하나뿐이고 그것이 위상 0/1의 일이다:
    /// 위상 0 = <b>동기로 확실히 그릴 수 있는 것</b>(폴더 글리프·확장자 타일·뷰모델에 남아 있는
    /// 캐시), 위상 1 = 파일을 읽어야 하는 것(RegisterUpdateCallback — PdfPane 선례).
    /// </summary>
    private void OnTileContainerContentChanging(
        ListViewBase sender, ContainerContentChangingEventArgs args)
    {
        if (args.ItemContainer is not GridViewItem item) return;
        if (args.InRecycleQueue)
        {
            if (item.ContentTemplateRoot is Grid recycled)
            {
                ExplorerRenameBox.ForceFinish(recycled); // 편집 중 스크롤 = 데이터 사고의 방지선
                PreviewHostOf(recycled)?.Children.Clear(); // 비트맵·텍스트 블록 해제
            }
            item.AllowDrop = false; // 잔존 방지 — 다음 항목이 파일이어도 드랍을 받지 않게
            return;
        }
        if (args.Item is not ExplorerEntryVm vm) return;
        EnsureTileHooks(item);        // 컨테이너당 1회
        item.AllowDrop = vm.IsFolder; // 매 재사용마다 재설정(폴더만 드랍 대상 — A94)
        if (args.Phase != 0) return;
        // 템플릿 루트 조회는 훅 부착 뒤에 둔다 — 어떤 이유로든 루트를 못 얻어도(템플릿 미적용 등)
        // 메뉴·드래그·더블클릭은 붙어 있어야 한다(미리보기만 비는 것이 최악의 실패다).
        if (item.ContentTemplateRoot is not Grid root || PreviewHostOf(root) is not { } host) return;
        host.Children.Clear(); // 재활용 잔존 방어(재활용 큐를 거치지 않고 바로 오는 경로 대비)
        if (vm.IsFolder)
        {
            host.Children.Add(MakeFolderGlyph());
            return;
        }
        // 어느 갈래든 확장자 타일이 먼저 깔린다 — 미리보기는 그 위를 덮거나(성공) 그대로 둔다(실패).
        host.Children.Add(MakeExtensionTile(vm.Entry));
        if (vm.PreviewText is { } cached) // A233 결과 재사용 — 파일을 다시 읽지 않는다
        {
            host.Children.Clear();
            host.Children.Add(MakeTextPreviewBlock(cached));
            return;
        }
        if (vm.PreviewKnownEmpty) // 얻을 것이 없다고 확정된 항목 — 다시 요청하지 않는다
        {
            if (vm.AudioInfo is { } info) host.Children.Add(MakeAudioInfoText(vm.Entry, info)); // A270
            return;
        }
        args.RegisterUpdateCallback(OnTilePreviewPhase); // 위상 1 — 파일을 읽는 갈래로
    }

    /// <summary>
    /// 템플릿 루트 Grid에서 미리보기 자리(빈 Grid)를 <b>이름으로</b> 찾는다 (A345 배치 3) —
    /// 루트가 평평한 Grid 하나라 한 레벨 탐색으로 충분하다(좌 리스트 FindItemBlock과 같은 관용구).
    /// XAML의 x:Name과 <see cref="TilePreviewHostName"/>이 어긋나면 미리보기가 예외 없이 전멸한다.
    /// </summary>
    private static Grid? PreviewHostOf(Grid tile) =>
        tile.Children.OfType<Grid>().FirstOrDefault(g => g.Name == TilePreviewHostName);

    /// <summary>
    /// 위상 1 (A345 배치 3): 파일을 읽어야 하는 미리보기 갈래를 발사한다 — 종전 MakeTile이 조립
    /// 시점에 고르던 4갈래를 <b>보이는 타일에서만</b> 고르는 것으로 옮겼다(A339 DeferPreview의
    /// 뷰포트 판정이 하던 일을 이제 XAML 가상화 패널이 대신한다). 갈래 순서는 종전 MakeTile
    /// 그대로다: 폴더(위상 0에서 끝) → 이미지(클라우드 전용이면 캐시 썸네일만) → 텍스트(A233) →
    /// 그 외 전 파일 = 셸 썸네일(A242, 단일 판정 지점).
    /// 진입 즉시 재활용 대조(<c>ReferenceEquals</c>)를 하는 이유: 위상 콜백은 <b>다음 프레임</b>에
    /// 오므로 그 사이 컨테이너가 다른 항목으로 재활용됐을 수 있다.
    /// </summary>
    private void OnTilePreviewPhase(ListViewBase sender, ContainerContentChangingEventArgs args)
    {
        if (args.ItemContainer is not GridViewItem item ||
            args.Item is not ExplorerEntryVm vm ||
            !ReferenceEquals(item.Content, vm)) return;
        if (item.ContentTemplateRoot is not Grid root || PreviewHostOf(root) is not { } host) return;
        var entry = vm.Entry;
        var seq = _showSeq; // 발사 시점의 회차 — 폴더가 바뀌면 이 값으로 완료를 버린다
        if (IsImageFile(entry.Name))
        {
            // A175: 클라우드 전용 이미지는 원본 디코드가 하이드레이션(전체 다운로드)이다 —
            // 원본은 절대 열지 않고 캐시·클라우드 제공 썸네일만 시도한다.
            if (entry.IsPlaceholder) _ = FillCachedThumbnailAsync(item, vm, host, seq);
            else StartImagePreview(item, vm, host);
            return;
        }
        if (IsTextPreviewFile(entry)) // A233 — 내용 프리뷰
        {
            _ = FillTextPreviewAsync(item, vm, host, seq);
            return;
        }
        // A242 — 그 외 전 파일: 셸 썸네일. 대기 배지는 실제로 요청하는 이 시점에만 붙인다
        // (요청하지도 않은 타일에 "기다리는 중" 표시가 있으면 거짓말이다 — A339의 근거 승계).
        var badge = MakePendingBadge();
        host.Children.Add(badge);
        _ = FillShellThumbnailAsync(item, vm, host, badge, seq);
    }

    /// <summary>
    /// 타일 컨테이너에 계약 훅을 1회만 건다 (A345 배치 3 — 좌 리스트 EnsureListItemHooks와 같은
    /// 구조·같은 근거). "이미 붙였는가"의 표지는 <see cref="UIElement.ContextFlyout"/> 유무다 —
    /// 아래에서 반드시 하나를 걸기 때문에 별도 플래그(첨부 속성·사전)가 필요 없는 가장 값싼
    /// 판정이다. 훅은 전부 <b>지연 해석</b>이다(핸들러 안에서 VmOf로 다시 푼다) — 컨테이너는
    /// 재활용돼도 훅은 남으므로, 여기서 vm이나 entry를 캡처하면 옛 파일을 조작하게 된다.
    /// </summary>
    private void EnsureTileHooks(GridViewItem item)
    {
        if (item.ContextFlyout is not null) return; // 이미 부착됨
        AttachContextMenu(item); // A24 + A94 2차(Rename·Delete) — A335 Opening 재구성
        AttachDragDrop(item);    // A94 — 드래그 아웃 + 폴더 타일 드랍
        item.IsDoubleTapEnabled = true; // A85 — 압축 모듈 내부 리스트(ArchiveView)와 같은 명시
        item.DoubleTapped += OnItemDoubleTapped; // A85 — 더블클릭 열기의 기본 경로
    }

    // ---------- 선택 · 이름변경 (A345 배치 3 — 보수안 ⓐ) ----------

    /// <summary>
    /// 경로로 표시 목록의 뷰모델 찾기 (A345 배치 3) — 가상화 뒤의 정본 조회이고, 종전
    /// FindTileByPath(컨테이너 순회)의 대체다. 컨테이너 검색과 달리 <b>화면 밖 항목도 찾는다</b>
    /// (그것이 종전 실체화 상한·조각 대기 문제의 해소다).
    /// </summary>
    private ExplorerEntryVm? FindVmByPath(string path) =>
        _vms.FirstOrDefault(vm => string.Equals(vm.Path, path, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// 뷰모델 하나의 타일 컨테이너를 실체화해 돌려준다 (A345 배치 3 — 이름변경 보수안 ⓐ).
    /// 인라인 편집은 컨테이너 안의 캡션 TextBlock 자리에 상자를 끼우는 구조라, 가상화 뒤에는
    /// "보이게 스크롤 → 레이아웃 강제 → 컨테이너 조회"의 세 단계를 거쳐야 상자를 끼울 대상이
    /// 생긴다. 그래도 못 얻으면(목록 밖 등) null — 호출부는 무동작으로 끝낸다.
    /// </summary>
    private GridViewItem? RealizeTileContainer(ExplorerEntryVm vm)
    {
        TileGrid.ScrollIntoView(vm);
        TileGrid.UpdateLayout();
        return TileGrid.ContainerFromItem(vm) as GridViewItem;
    }

    /// <summary>
    /// 방금 만든 항목(새 폴더·새 파일)을 골라 이름변경 편집에 들여보낸다 (A345 배치 3 — 종전
    /// FinishShowEntries가 들고 있던 보류 소비 갈래를 옮긴 것이다. 호출부 = ShowEntries 한 곳).
    /// 현재 목록이 확장자 필터로 그 항목을 안 보여 주거나 그새 사라졌으면 조용히 무동작
    /// (종전 "그새 사라짐" 폴백과 같은 무해 경로).
    /// </summary>
    private void BeginRenameByPath(string path)
    {
        if (FindVmByPath(path) is not { } vm) return;
        TileGrid.SelectedItem = vm;
        if (RealizeTileContainer(vm) is { } container) BeginRenameOf(container);
    }

    /// <summary>
    /// F2·우클릭 Rename 진입 (A94 2차): 타일 캡션 TextBlock을 인라인 편집(ExplorerRenameBox)으로
    /// 바꾼다. 캡션 조회 = <b>이름 기반</b>(A345 배치 3 — 종전 tile.Children[1] 인덱스 계약은
    /// 템플릿에 클라우드 배지가 끼어 자리가 밀릴 수 있어 폐기했다. 어긋나도 예외 없이 조용한
    /// return이라 증상이 "F2 무반응"으로만 보인다 — 좌 리스트가 A156에서 같은 이유로 이미
    /// 이름 조회로 옮겼다). 편집 상자를 끼울 host = 템플릿 루트 Grid(평평한 한 겹).
    /// 커밋 성공 갱신 = RefreshViaShell(편집이 끝난 뒤에만 — 편집 중 재스캔 금지).
    /// </summary>
    private void BeginRenameOf(GridViewItem item)
    {
        if (VmOf(item) is not { } vm) return; // A345 배치 3 — VmOf 단일 해석
        if (item.ContentTemplateRoot is not Grid tile) return;
        if (tile.Children.OfType<TextBlock>().FirstOrDefault(t => t.Name == TileCaptionName)
            is not { } caption) return;
        ExplorerRenameBox.Begin(tile, caption, vm.Path, MakeOpUi(), RefreshViaShell);
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
    /// 정확히 열 수대로 떨어진다.
    /// <para>
    /// A345 배치 3: 패널이 없을 때의 폴백(타일 컨테이너를 순회하며 직접 크기 대입)을 삭제했다 —
    /// ItemsSource 상태에서 <c>Items</c>는 컨테이너가 아니라 <b>데이터</b>라 그 순회가 아무것도
    /// 못 고친다. 대신 패널이 아직 없으면 그냥 돌아간다: <c>ItemsPanelRoot</c>는 첫 레이아웃
    /// 뒤에만 존재하고, 그 뒤로는 ShowEntries의 <c>UpdateLayout</c> 직후 호출·SizeChanged·
    /// SetColumns가 다시 부른다(첫 화면 셀 크기는 ShowEntries의 대입→UpdateLayout→여기 순서가
    /// 보장한다 — 그 순서가 유일한 길이 됐다).
    /// </para>
    /// </summary>
    private void ApplyTileSize()
    {
        var width = TileGrid.ActualWidth;
        if (width <= 0) return;
        var size = Math.Floor(width / _columns);
        if (size < 24) return; // 극단적으로 좁은 창 보호 — 이전 크기 유지가 낫다

        if (TileGrid.ItemsPanelRoot is not ItemsWrapGrid wrap) return; // 아직 첫 레이아웃 전
        wrap.ItemWidth = size;
        wrap.ItemHeight = size;
    }

    private void OnSizeChanged(object sender, SizeChangedEventArgs e) => ApplyTileSize();

    // ---------- 타일 구성 ----------
    // A345 배치 3: 타일 자체(루트 Grid·캡션·클라우드 배지·컨테이너 정렬)는 이제 XAML
    // DataTemplate + ItemContainerStyle이 만든다 — 종전 MakeTile·MakeCloudBadge는 그와 함께
    // 삭제됐다. 코드에 남은 것은 미리보기 조각(폴더 글리프·확장자 타일·텍스트 블록·배지 2종·
    // 오디오 정보)뿐이고, 그 조각들을 언제 어디에 넣을지는 ContainerContentChanging이 정한다.

    /// <summary>
    /// 타일에 드래그 아웃(전 항목)과 드랍 대상(폴더 타일만)을 건다 (A94 —
    /// ExplorerPane.AttachDragDrop과 같은 구성: 데퍼럴이 있는 컨테이너 CanDrag 경로).
    /// 잡은 타일이 선택에 포함돼 있으면 선택 전부를, 아니면 그 타일 하나만 싣는다(윈도우 관례).
    /// 폴더 타일 핸들러가 Handled를 걸므로 루트(LayoutRoot) 핸들러와 이중 처리되지 않는다.
    /// </summary>
    /// <remarks>
    /// A345 배치 3: 항목을 캡처하지 않는다 — 세 핸들러 전부 발화 시점에 VmOf로 다시 푼다
    /// (재활용된 컨테이너가 옛 파일을 끌거나 옛 폴더로 드랍받는 사고의 방지선). 드랍 갈래는
    /// <b>부착 시점에 폴더/파일을 가르지 않고</b> 항상 걸어 두고 핸들러 안에서 "지금 이 컨테이너가
    /// 폴더인가"로 가른다 — 실제 수용 여부는 AllowDrop이 정하고 그 값은 매
    /// ContainerContentChanging이 다시 정한다.
    /// </remarks>
    private void AttachDragDrop(GridViewItem item)
    {
        item.CanDrag = true;
        item.DragStarting += async (_, args) =>
        {
            var deferral = args.GetDeferral();
            try
            {
                if (VmOf(item) is not { } vm ||
                    !await ExplorerFileOps.FillDragDataAsync(args.Data, PathsFor(vm.Entry)))
                    args.Cancel = true; // 실을 항목이 없다(그새 삭제·재활용 등)
            }
            finally
            {
                deferral.Complete();
            }
        };

        item.AllowDrop = VmOf(item) is { IsFolder: true }; // 초기값(이후는 CCC가 매번 다시 정한다)
        item.DragOver += (_, e) =>
        {
            if (VmOf(item) is { IsFolder: true } vm) ExplorerFileOps.HandleTargetDragOver(e, vm.Path);
        };
        item.Drop += (_, e) =>
        {
            if (VmOf(item) is { IsFolder: true } vm) HandleDrop(e, vm.Path);
        };
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
    /// 이미지 실제 축소 미리보기 (A93): BitmapImage + DecodePixelWidth — 디코드는 XAML
    /// 파이프라인이 비동기로 한다. WIC 밖 포맷(psd)·손상 파일은 ImageFailed로 확장자 타일 폴백.
    /// <para>
    /// A345 배치 3: 위상 1에서 <b>동기로</b> 건다(파일을 우리가 읽지 않으므로 워커·게이트가 필요
    /// 없다 — 종전 DeferPreview 안 본문 그대로다). 성공하면 확장자 타일을 걷는다: 그냥 겹쳐 두면
    /// <b>투명 PNG의 투명한 부분으로 아래 타일이 비쳐 보인다</b>. 실패(ImageFailed)에는 걷지 않아
    /// 확장자 타일이 그대로 남는다 = 종전 폴백과 같은 결과.
    /// 두 핸들러 모두 <c>ReferenceEquals</c>로 재활용을 대조한다 — 디코드 완료가 늦게 오는 사이
    /// 컨테이너가 다른 파일 타일이 됐으면 그 타일의 확장자 타일을 걷어 버리면 안 된다.
    /// 비트맵은 뷰모델에 캐시하지 않는다(원본 Uri 디코드 결과는 XAML 이미지 캐시가 들고 있다).
    /// </para>
    /// </summary>
    private void StartImagePreview(GridViewItem item, ExplorerEntryVm vm, Grid host)
    {
        try
        {
            var fallback = host.Children.Count > 0 ? host.Children[0] : null; // 위상 0이 깐 확장자 타일
            var bitmap = new BitmapImage { DecodePixelWidth = PreviewDecodeWidth };
            bitmap.UriSource = new Uri(vm.Path);
            var image = new Image
            {
                Source = bitmap,
                Stretch = Stretch.Uniform,
                Margin = new Thickness(4),
            };
            image.ImageOpened += (_, _) =>
            {
                if (!ReferenceEquals(item.Content, vm) || fallback is null) return;
                host.Children.Remove(fallback);
            };
            image.ImageFailed += (_, _) =>
            {
                if (!ReferenceEquals(item.Content, vm)) return;
                host.Children.Remove(image);
            };
            host.Children.Add(image);
        }
        catch
        {
            // 경로가 Uri가 못 되는 극단 케이스 — 확장자 타일 그대로.
        }
    }

    /// <summary>
    /// 캐시·클라우드 제공 썸네일을 UI 스레드 비동기로 받아 host에 채운다 (A175 — 클라우드 전용
    /// 이미지 갈래). ReturnOnlyIfCached라 원본 파일은 열리지 않는다(캐시에 없으면 확장자 타일 유지).
    /// <para>
    /// A345 배치 3의 방어 3겹: ① <see cref="ExplorerEntryVm.PreviewInFlight"/> — 같은 항목이 짧은
    /// 사이에 두 번 실체화돼도 요청은 한 번, ② <c>seq</c> 대조 — 폴더가 바뀌었으면 버린다,
    /// ③ <c>ReferenceEquals(item.Content, vm)</c> — 같은 폴더 안에서 컨테이너가 다른 파일로
    /// 재활용됐으면 <b>화면은 건드리지 않는다</b>. ③에 걸려도 "썸네일 없음"이라는 사실은 뷰모델에
    /// 남기므로(PreviewKnownEmpty) 다음 실체화가 헛되이 다시 묻지 않는다. 성공 비트맵은 남기지
    /// 않는다 — 재실체화 시 다시 가져온다(셸 썸네일 캐시가 있어 싸다는 A242 근거 그대로).
    /// </para>
    /// </summary>
    private async Task FillCachedThumbnailAsync(
        GridViewItem item, ExplorerEntryVm vm, Grid host, int seq)
    {
        if (vm.PreviewInFlight) return;
        vm.PreviewInFlight = true;
        try
        {
            if (seq != _showSeq) return; // ① 발사 전 낡음(폴더 전환)
            byte[]? bytes = null;
            try
            {
                var file = await StorageFile.GetFileFromPathAsync(vm.Path);
                using var thumb = await file.GetThumbnailAsync(
                    ThumbnailMode.SingleItem, PreviewDecodeWidth, ThumbnailOptions.ReturnOnlyIfCached);
                // A270 ③: 파일 종류 아이콘은 무정보다 — 확장자 타일을 덮지 않는다(FetchTilePreview와
                // 같은 판정·같은 복구법: Type 판정 한 줄만 지우면 종전 동작). 두 번째 호출부.
                if (thumb is not null && thumb.Size != 0 && thumb.Type != ThumbnailType.Icon)
                {
                    // 스트림 → 바이트 → BitmapImage: ExplorerPane.FetchThumbnail과 같은 변환 관용구
                    // (검증된 형태만 복제 — thumb를 SetSourceAsync에 직접 넘기는 선례가 없다).
                    using var stream = thumb.AsStreamForRead();
                    using var buffer = new MemoryStream((int)thumb.Size);
                    await stream.CopyToAsync(buffer);
                    bytes = buffer.ToArray();
                }
            }
            catch
            {
                bytes = null; // 캐시 썸네일 없음·읽기 실패 — 원본은 어떤 폴백에서도 열지 않는다
            }
            if (bytes is null)
            {
                vm.PreviewKnownEmpty = true; // 없음 확정 — 다음 실체화가 다시 묻지 않는다
                return;
            }
            if (seq != _showSeq || !ReferenceEquals(item.Content, vm)) return; // ②③
            try
            {
                var bitmap = new BitmapImage();
                using (var source = new MemoryStream(bytes))
                    await bitmap.SetSourceAsync(source.AsRandomAccessStream());
                if (seq != _showSeq || !ReferenceEquals(item.Content, vm)) return;
                // A335 계측: 타일 내용이 화면에 처음 얹히는 순간. Mark는 같은 이름을 한 번만
                // 기록하므로(NavDiagnostics.Mark) 세 갈래(캐시 썸네일·텍스트 미리보기·셸
                // 썸네일) 어디서 먼저 와도 첫 것만 남는다.
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
                vm.PreviewKnownEmpty = true; // 손상 데이터 디코드 실패 — 확장자 타일 유지
            }
        }
        finally
        {
            vm.PreviewInFlight = false;
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
    /// 게이트(동시 TextPreviewConcurrency건) 획득 후 워커에서 파일 앞부분을 읽어 타일 내용을
    /// 교체한다 (A233). UI 스레드에서 시작하므로 await 후속부도 UI 스레드다(ExplorerPane.
    /// LoadDetailInfoAsync의 A194 발사 구조 — 별도 디스패치 없이 재대조가 성립하는 근거).
    /// 게이트 통과 시점의 seq 대조가 자연 배압이다: 재스캔 빈발(A131)로 보류가 쌓여도 낡은 예약은
    /// 읽기 자체를 시작하지 않는다. 실패(잠김·삭제 경합·풀 닫힘 취소)는 조용히 확장자 타일 유지 —
    /// 안내 없음(사양). Unloaded는 _showSeq를 올려 보류 전부를 무산시킨다(생성자 주석).
    /// <para>
    /// A345 배치 3: host를 클로저로 캡처하던 종전 근거("컨테이너 재사용 없음 — ShowEntries가
    /// 타일을 매번 새로 만든다")가 <b>무효가 됐다</b>. 이제 host는 재활용되는 컨테이너의 것이라,
    /// 완료 시점에 <c>ReferenceEquals(item.Content, vm)</c>로 "이 컨테이너가 아직 이 파일의
    /// 것인가"를 확인해야 한다. 읽어 온 문자열은 그 확인보다 <b>먼저</b> 뷰모델에 저장한다 —
    /// 화면에 못 얹더라도 그 항목이 다시 실체화될 때 파일을 또 읽지 않게 된다.
    /// </para>
    /// </summary>
    private async Task FillTextPreviewAsync(
        GridViewItem item, ExplorerEntryVm vm, Grid host, int seq)
    {
        if (vm.PreviewInFlight) return; // 같은 항목의 중복 발사 방지
        vm.PreviewInFlight = true;
        try
        {
            await _textReadGate.WaitAsync(); // UI 문맥 await — 후속부는 UI 스레드로 복귀
            try
            {
                if (seq != _showSeq) return; // ① 대기 중 낡음 — 발사 자체를 접는다
                string? text;
                try
                {
                    text = await TextPool.Run(_ => ReadTextPreview(vm.Path));
                }
                catch
                {
                    return; // 읽기 실패·풀 닫힘(취소 Task) — 확장자 타일 유지(재시도 여지도 남긴다)
                }
                if (text is null)
                {
                    vm.PreviewKnownEmpty = true; // 빈 파일·전부 공백 — 다시 읽을 이유가 없다
                    return;
                }
                vm.PreviewText = text; // ② 화면 판정보다 먼저 — 재실체화 시 재읽기 없음
                if (seq != _showSeq || !ReferenceEquals(item.Content, vm)) return; // ③ 재활용 대조
                // A335 계측: 타일 내용이 화면에 처음 얹히는 순간. Mark는 같은 이름을 한 번만
                // 기록하므로(NavDiagnostics.Mark) 세 갈래(캐시 썸네일·텍스트 미리보기·셸
                // 썸네일) 어디서 먼저 와도 첫 것만 남는다.
                NavDiagnostics.Mark("prev0");
                host.Children.Clear();
                host.Children.Add(MakeTextPreviewBlock(text));
            }
            finally
            {
                _textReadGate.Release(); // 예외·낡음 경로 포함 — 누락되면 상한 건 뒤 조용히 멈춘다(A194)
            }
        }
        finally
        {
            vm.PreviewInFlight = false;
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

    // A337 클라우드 배지는 A345 배치 3에서 XAML DataTemplate으로 옮겼다(TileCloudBadge) —
    // 대기 배지와 같은 한 벌이고 자리만 반대쪽 위다. 표시 여부 = ExplorerEntryVm.
    // CloudBadgeVisibility(썸네일 성공 여부와 무관하게 남는다 — 이 배지가 말하는 것은
    // "미리보기가 없다"가 아니라 "이 파일은 로컬에 없다"이기 때문이다. 히트 테스트에서
    // 빼지 않는 것도 그대로다 — 툴팁이 그 사실을 설명하는 유일한 자리다).

    /// <summary>
    /// 게이트(동시 ThumbFetchConcurrency건) 획득 후 워커에서 셸 썸네일을 추출해 타일 내용을
    /// 교체한다 (A242 — FillTextPreviewAsync와 같은 A194 발사 구조: UI 스레드에서 시작하므로
    /// await 후속부도 UI 스레드다). 클라우드 전용(placeholder) 파일은 entry.IsPlaceholder가
    /// 그대로 워커의 cachedOnly가 되어 캐시·클라우드 제공 썸네일만 요청한다(A175 하이드레이션
    /// 금지 불변 — 속성 조회도 IsAudioInfoFile이 미리 접는다). 추출 실패·썸네일 없음·비트맵
    /// 디코드 실패는 배지만 걷고 확장자 타일 유지(안내 없음·사양).
    /// A270: 한 워커 왕복이 썸네일과 오디오 정보를 함께 물어 오므로 대조도 한 벌이다 —
    /// 교체 없음(아이콘형·실패) 갈래에서는 확장자 타일 하단에 정보를 얹고(MakeAudioInfoText),
    /// 앨범아트로 교체된 갈래에서는 아트 하단 반투명 띠로 같은 정보를 얹는다(MakeAudioInfoBand).
    /// 배지는 어느 갈래에서든 정보 표기보다 먼저 걷힌다(교체 갈래는 Clear가 겸한다).
    /// <para>
    /// A345 배치 3: host·badge를 클로저로 캡처하던 종전 근거("타일 전용 — 재사용 없음")가
    /// <b>무효가 됐다</b>. 완료 시점 판정이 두 겹이다: <c>seq</c>(폴더 전환) +
    /// <c>ReferenceEquals(item.Content, vm)</c>(같은 폴더 안 재활용). 워커 결과는 그 판정보다
    /// <b>먼저</b> 뷰모델에 남긴다 — 오디오 정보는 AudioInfo에, "썸네일 없음"은
    /// PreviewKnownEmpty에. 그래야 화면에 못 얹은 결과도 다음 실체화에서 재사용된다.
    /// 성공한 비트맵만은 남기지 않는다(메모리 — 재추출은 셸 썸네일 캐시 덕에 싸다, A242 근거).
    /// 즉 재활용 뒤 그 항목이 다시 보이면 셸 썸네일은 다시 fetch된다(사양).
    /// </para>
    /// </summary>
    private async Task FillShellThumbnailAsync(
        GridViewItem item, ExplorerEntryVm vm, Grid host, FontIcon badge, int seq)
    {
        if (vm.PreviewInFlight) return; // 같은 항목의 중복 발사 방지
        vm.PreviewInFlight = true;
        try
        {
            await _thumbFetchGate.WaitAsync(); // UI 문맥 await — 후속부는 UI 스레드로 복귀
            try
            {
                if (seq != _showSeq) return; // ① 대기 중 낡음 — 발사 자체를 접는다
                var entry = vm.Entry;
                var wantAudioInfo = IsAudioInfoFile(entry);
                (byte[]? Bytes, string? Info) result;
                try
                {
                    result = await ThumbPool.Run(
                        _ => FetchTilePreview(entry.Path, entry.IsPlaceholder, wantAudioInfo));
                }
                catch
                {
                    result = (null, null); // 추출 실패·풀 닫힘(취소 Task) — 아래 공통 실패 경로로
                }
                // 튜플을 지역 변수로 풀어 둔다 — 아래 null 판정·재사용이 종전(단일 bytes) 형태 그대로.
                var bytes = result.Bytes;
                var info = result.Info;
                // ② 화면 판정보다 먼저 캐시에 남긴다 — 재활용됐어도 다음 실체화가 재사용한다.
                if (info is not null) vm.AudioInfo = info;
                if (bytes is null) vm.PreviewKnownEmpty = true;
                if (seq != _showSeq || !ReferenceEquals(item.Content, vm)) return; // ③ 재활용 대조
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
                    if (seq != _showSeq || !ReferenceEquals(item.Content, vm)) return;
                    // A335 계측: 타일 내용이 화면에 처음 얹히는 순간. Mark는 같은 이름을 한 번만
                    // 기록하므로(NavDiagnostics.Mark) 세 갈래(캐시 썸네일·텍스트 미리보기·셸
                    // 썸네일) 어디서 먼저 와도 첫 것만 남는다.
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
                    vm.PreviewKnownEmpty = true; // 손상 데이터 — 다시 받아도 같은 결과다
                    if (!ReferenceEquals(item.Content, vm)) return;
                    host.Children.Remove(badge); // 디코드 실패 — 확장자 타일 유지
                    if (info is not null) host.Children.Add(MakeAudioInfoText(entry, info));
                }
            }
            finally
            {
                _thumbFetchGate.Release(); // 예외·낡음 경로 포함 — 누락되면 상한 건 뒤 조용히 멈춘다(A194)
            }
        }
        finally
        {
            vm.PreviewInFlight = false;
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
    /// <remarks>
    /// A345 배치 3: 대상 항목을 인자로 받지 않는다 — <b>열리는 순간</b>에 VmOf로 다시 푼다.
    /// 가상화 뒤에는 이 컨테이너가 다른 파일로 재활용되므로, 부착 시점의 entry를 캡처하면
    /// 옛 파일이 Cut·Delete 대상이 된다(재활용 잔존 사고 중 가장 위험한 갈래).
    /// 그새 항목이 풀리지 않으면 빈 메뉴로 연다 — 조작 대상이 없는 것이 옳다.
    /// </remarks>
    private void AttachContextMenu(GridViewItem item)
    {
        var flyout = new MenuFlyout();
        flyout.Opening += (_, _) =>
        {
            if (VmOf(item) is { } vm) BuildTileContextMenu(flyout, item, vm);
            else flyout.Items.Clear();
        };
        item.ContextFlyout = flyout;
    }

    /// <summary>
    /// A335: 타일 메뉴의 실제 내용 — 열릴 때마다 새로 채운다(구성·순서·활성 조건·대상 규칙은
    /// 종전 그대로, 옮긴 것은 <b>시점</b>뿐). 매번 비우므로 두 번째 우클릭에 겹쳐 쌓이지 않는다.
    /// </summary>
    /// <remarks>
    /// A345 배치 3: 대상은 <b>열린 순간에 푼 뷰모델</b>이다(vm). Rename만은 디스패처로 한 박자
    /// 미루므로 그 사이 컨테이너가 재활용될 수 있어, 실행 직전에 같은 뷰모델인지 다시 대조한다.
    /// </remarks>
    private void BuildTileContextMenu(MenuFlyout flyout, GridViewItem item, ExplorerEntryVm vm)
    {
        var entry = vm.Entry;
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

    /// <summary>눌림의 원본 요소에서 타일 컨테이너를 찾아 항목을 푼다 — 조상 상향 탐색
    /// (깊이 상한 64 = HotkeySupport.MaxAncestorDepth와 같은 방어). 컨테이너에서 뷰모델을 꺼내는
    /// 것은 VmOf 하나를 지난다(A345 배치 3 — Content가 뷰모델이다). 반환은 종전대로 Entry다.</summary>
    private static ExplorerListing.Entry? EntryFromSource(object source)
    {
        var node = source as DependencyObject;
        for (var depth = 0; node is not null && depth < 64; depth++)
        {
            if (node is GridViewItem container) return VmOf(container)?.Entry;
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
        if (VmOf(e.ClickedItem) is not { } vm) return; // A345 배치 3 — ClickedItem은 이제 뷰모델이다

        var now = DateTime.UtcNow;
        var isDouble = _lastClick is { } last && last.Path == vm.Path &&
                       (now - last.At).TotalMilliseconds < DoubleClickMs;
        _lastClick = (vm.Path, now);
        if (!isDouble) return;

        _lastClick = null;
        Activate(vm.Entry);
    }

    /// <summary>
    /// 컨테이너 DoubleTapped = 더블클릭 열기 (A85). 실기기에서는 두 번째 클릭이 더블탭 제스처로
    /// 소비되어 두 번째 ItemClick이 오지 않아, 클릭 쌍 판정(OnItemClick)만으로는 열기가 조용히
    /// 무시됐다(압축 모듈 내부 리스트는 처음부터 DoubleTapped라 이 증상이 없었다 — 같은 배선).
    /// </summary>
    private void OnItemDoubleTapped(object sender, DoubleTappedRoutedEventArgs e)
    {
        if (e.OriginalSource is TextBox) return; // 이름변경 편집 상자(A94 2차) — 더블클릭은 텍스트 선택 몫
        if (sender is not GridViewItem item || VmOf(item) is not { } vm) return; // A345 배치 3
        e.Handled = true;
        _lastClick = null; // 이 제스처를 이룬 클릭 기록이 다음 클릭 쌍 판정에 섞이지 않게
        Activate(vm.Entry);
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
