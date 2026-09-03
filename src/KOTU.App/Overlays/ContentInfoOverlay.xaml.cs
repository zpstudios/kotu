using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using Windows.ApplicationModel.DataTransfer;
using KOTU.Core.Contracts;
using KOTU.Core.Routing;
using KOTU.Core.Threading;

namespace KOTU.App.Overlays;

/// <summary>
/// 콘텐츠 정보 패널 공용 컨트롤 (A57 ②) — 기존 MainWindow의 InfoOverlayRoot(좌측,
/// v0.25.0)를 추출해 우측으로 스왑(A57 ①)한 것.
/// A176: 반투명(오버레이) 축 폐지 — 열림 상태는 사이드바(불투명 도크) 하나뿐이다.
/// 패널 폭은 전 상태 공통 25%(A116 — 종전 "콘텐츠 30% / S1 25%" 2값 폐지,
/// SetPanelPercent). 정보 로드 로직(v0.25.0의
/// LoadContentInfoAsync — 파일별 1회 캐시·경쟁 방지 시퀀스·기본 파일 정보 폴백)도 함께 이관.
/// 정보 항목은 모듈이 주입한다: ShowFor()에 넘기는 IContentInfoProvider(모듈 뷰)가 내용을 만들고,
/// 없거나 실패하면 파일 기본 정보로 대체한다. 정보(H/W)·설정 모듈은 셸이 파일 경로가 없어
/// 애초에 ShowFor를 부르지 않는다(현행 동작 유지).
/// 입력(A176: F12 단타 = 열기/닫기 토글, 핀 버튼 동일)은 셸(MainWindow)이 담당한다 —
/// 이 컨트롤은 ShowFor/ShowForSelection(A200 — 브라우징 선택 파일)/ShowPlaceholder/Hide/
/// SetPanelPercent/PrefetchSelectionInfo(A241 — 폴더 단위 EXIF 사전 스캔)만 받는다
/// (구 SetState는 반투명 축과 함께 폐지 — 사이드바 안내·히트테스트는 표시 메서드가 직접 켠다).
/// </summary>
public sealed partial class ContentInfoOverlay : UserControl
{
    private int _seq;             // 정보 로드 경쟁 방지 (기존 MainWindow._infoSeq)
    private string? _activePath;  // 마지막으로 요청된 파일 — 늦게 도착한 결과 폐기 판단
    private string? _cachePath;   // 열린 콘텐츠(provider) 정보 캐시 — 마지막 1건 (기존 _infoPath/_infoText.
                                  // A241: 선택 축은 _selectionCache(다건)로 분리 — 구 _cacheSelection
                                  // 소스 축 판별 필드는 폐지됐다. 두 캐시가 물리적으로 갈려 "같은
                                  // 파일을 선택했다가 연" 경우에도 소스가 섞일 길이 없다)
    private IReadOnlyList<ContentInfoItem>? _cacheItems; // A150: 문자열 → 라벨·값 행 목록 (provider 축 전용)
    private bool _cacheProvisional;  // A332: 캐시된 열림 축 결과가 "빈 결과"라 재조회 1회가 남아 있다
    private string? _emptyRetryPath; // A332: 그 경로에서 재조회 1회를 이미 소진했다(무한 재조회 차단)
    private ModuleWorker? _worker; // A200: 선택 조회(SelectionQuickInfo) 전용 — UI 스레드 금지(A42)
    private CancellationTokenSource? _selectionCts; // A200: 직전 선택 조회 취소 — 빠른 연속 선택
                                                    // (그리드 화살표 이동)이 직렬 워커 큐에 낡은
                                                    // PDF 열기 등을 쌓지 않게(Run은 차례가 오기
                                                    // 전에 취소되면 실행 없이 건너뛴다)

    /// <summary>지연 생성: Unloaded로 정리된 뒤 다시 로드돼도 되살아난다(ExplorerPane과 같은 규칙).</summary>
    private ModuleWorker Worker => _worker ??= new ModuleWorker("KOTU info worker");

    /// <summary>오버레이가 화면에 떠 있는지 — 셸의 표시 갱신 판단에 쓴다.</summary>
    public bool IsOpen => Visibility == Visibility.Visible;

    /// <summary>
    /// 인포 영역에 파일이 드랍됨 (A93) = "그 파일 열기". 셸이 OpenFile로 배선한다 —
    /// 콘텐츠가 없으면 라우터(A59)가 담당 모듈로 전환한 뒤 여는 기존 경로 그대로다.
    /// </summary>
    public event Action<string>? FileDropped;

    public ContentInfoOverlay()
    {
        InitializeComponent();
        // A200: 선택 조회 워커 정리 — 진행 중 작업은 워커가 마저 끝내고 스레드 종료
        // (ImageViewerView와 같은 Unloaded 수명 규칙. 재로드되면 지연 생성이 되살린다).
        Unloaded += (_, _) =>
        {
            _worker?.Dispose();
            _worker = null;
            // A241: 보류 프리페치 전부 무산 — seq 선증가가 중요: 닫힌 풀의 Run은 취소 Task라
            // 어차피 무해지만, 낡은 예약이 지연 재생성으로 새 풀을 되살리는 길을 이 한 줄이
            // 막는다(ThumbnailExplorer A233과 같은 정리 규칙).
            _prefetchSeq++;
            _prefetchPool?.Dispose();
            _prefetchPool = null;
        };
    }

    /// <summary>
    /// 모듈 컨텍스트를 주입받아 표시한다: path = 현재 콘텐츠 파일,
    /// provider = 모듈 뷰의 정보 계약(IContentInfoProvider, null이면 파일 기본 정보).
    /// A176: 사이드바가 유일한 열림 상태 — 히트테스트·안내를 여기서 함께 켠다(구 SetState의 자리.
    /// ShowHint의 동일 문구 중복 억제가 반복 호출(ApplyOverlayStates 경유)을 걸러 준다).
    /// </summary>
    public void ShowFor(string path, IContentInfoProvider? provider)
    {
        Visibility = Visibility.Visible;
        OverlayBorder.IsHitTestVisible = true;
        ShowHint(OverlayHints.Docked(OverlayHints.InfoKey));
        _ = LoadAsync(path, provider);
    }

    /// <summary>
    /// A200: 브라우징 표면(썸네일 그리드·A240부터 좌 리스트)에서 **선택**(클릭, 열기 아님)된
    /// 파일의 정보 표시 — ShowFor의 선택 축 판본. 모듈 뷰를 경유하지 않고 셸 조회기
    /// (SelectionQuickInfo)를 오버레이 전용 워커에서 돌린다(UI 스레드 금지 — A42).
    /// isPlaceholder(A175 클라우드 전용 파일)면 조회 자체를 생략하고 파일 기본 정보 +
    /// (이미지면) EXIF 빈 라벨(A239 ②)만 그린다 — 원본을 여는 자동 조회는 하이드레이션
    /// (전체 다운로드)을 일으키므로 금지. modifiedTicks(A241) = 목록 열거가 이미 아는
    /// 수정시각 — 다건 캐시(_selectionCache)의 키 절반이다(경로 + 수정시각).
    /// </summary>
    public void ShowForSelection(string path, bool isPlaceholder, long modifiedTicks)
    {
        Visibility = Visibility.Visible;
        OverlayBorder.IsHitTestVisible = true;
        ShowHint(OverlayHints.Docked(OverlayHints.InfoKey));
        _ = LoadAsync(path, provider: null, selection: true, placeholder: isPlaceholder,
            modifiedTicks: modifiedTicks);
    }

    public void Hide()
    {
        Visibility = Visibility.Collapsed;
        OverlayBorder.IsHitTestVisible = false;
        HideHint(); // A92 — 다시 열릴 때 안내가 처음부터 다시 보이게 상태를 비운다
    }

    /// <summary>
    /// 파일 없는 상태의 플레이스홀더 (A81 — 빈 모듈에서 기본 도크로 뜰 때):
    /// 보여줄 파일 정보가 없으므로 간단한 안내만 표시한다.
    /// A93: 드랍 안내(Drop a file here...)는 인포에 아무것도 표시 중이 아닐 때만 —
    /// 이 플레이스홀더가 정확히 그 상태라 여기서만 문구를 낸다(파일 정보 표시 중에는 없음).
    /// 진행 중이던 로드가 늦게 도착해 문구를 덮지 않게 캐시·시퀀스를 함께 무효화한다.
    /// 히트테스트·사이드바 안내는 ShowFor와 같은 규칙(A176)으로 여기서 켠다.
    /// </summary>
    public void ShowPlaceholder()
    {
        InvalidateCache();
        Visibility = Visibility.Visible;
        OverlayBorder.IsHitTestVisible = true;
        ShowHint(OverlayHints.Docked(OverlayHints.InfoKey));
        ShowMessage("No file open\nDrop a file here to open it");
    }

    /// <summary>
    /// 패널 폭(전폭 대비 %) 지정 — 셸이 전 상태 공통 SidebarPercent(25, A116)를 넘긴다.
    /// 내부 별 분할이 셸 도크 컬럼과 같은 비율이어야 사이드바에서 픽셀 단위로 정렬되고,
    /// 경계 버튼 옆 안내 문구(A108 — RestColumn 기준 배치)의 x도 이 분할이 정한다.
    /// FileListOverlay에도 같은 메서드가 있다.
    /// </summary>
    public void SetPanelPercent(double percent)
    {
        PanelColumn.Width = new GridLength(percent, GridUnitType.Star);
        RestColumn.Width = new GridLength(100 - percent, GridUnitType.Star);
    }

    /// <summary>
    /// A317: 패널 바탕(PanelBackdrop)의 불투명도 지정 — 셸이 표시 종착점(ApplyOverlayStates)에서
    /// 모드2·3의 S4는 S4TranslucentOpacity(A316 단일 출처), 그 외 전부는 1.0을 넘긴다.
    /// 바탕만 낮춘다(요소 Opacity — S4CenterBackdrop과 같은 관용구): 정보 글자는 이 바탕
    /// **위**에 불투명하게 남아 읽힌다. FileListOverlay에도 같은 메서드가 있다.
    /// </summary>
    public void SetBackdropOpacity(double opacity) => PanelBackdrop.Opacity = opacity;

    /// <summary>
    /// 인포 영역 드랍 = 그 파일 열기 (A93 드랍 규칙). 좌·중(탐색기 영역)의 무동작과 달리
    /// 여기만 실제 동작이 있다. A176: 떠 있는 동안은 항상 히트테스트 가능(홀드 반투명 소멸).
    /// </summary>
    private void OnPanelDragOver(object sender, DragEventArgs e)
    {
        if (!e.DataView.Contains(StandardDataFormats.StorageItems)) return;
        e.AcceptedOperation = DataPackageOperation.Copy;
        // A272: Copy는 유지하되(None이면 드랍 자체가 불가) OS가 그리는 배지 문구만 "Open"으로
        // 덮는다 — 이 경로는 파일을 복사하지 않고 열기만 한다(OnPanelDrop → FileDropped).
        // DragUIOverride는 드래그 원본이 UI 사용자 지정을 허용할 때만 오므로 null 가드를 둔다.
        if (e.DragUIOverride is { } dragUi)
        {
            dragUi.Caption = "Open";
            dragUi.IsCaptionVisible = true;
        }
        e.Handled = true; // 창 전체 핸들러(콘텐츠 영역 규칙)가 다시 판정하지 않게
    }

    private async void OnPanelDrop(object sender, DragEventArgs e)
    {
        if (!e.DataView.Contains(StandardDataFormats.StorageItems)) return;
        e.Handled = true;
        var items = await e.DataView.GetStorageItemsAsync();
        var path = items.OfType<Windows.Storage.StorageFile>()
            .Select(f => f.Path)
            .FirstOrDefault(p => !string.IsNullOrEmpty(p));
        if (path is not null) FileDropped?.Invoke(path);
    }

    // ---------- 안내 문구 일시 표시 (A92, v0.115.0 — 문구·키 표기는 A107부터 OverlayHints가 단일 출처) ----------
    // A176: 구 SetState(모드·고정 반영)는 반투명 축과 함께 폐지 — 호출원은 ShowFor/ShowPlaceholder뿐.
    // ⚠️ FileListOverlay·SidePanelHost(A119)에 같은 상수·필드·메서드(표시 타이밍 장치)가 한 벌씩
    // 더 있다. 문구 문자열은 A107에서 OverlayHints로 모았지만 타이밍 장치는 세 벌 —
    // 한쪽을 고치면 반드시 나머지도 맞출 것. A133(v0.155.0)부터는 **판(다크 반투명 Border) 규격**도
    // 세 벌 공통이다: Background #CC202020 · CornerRadius 4 · Padding 10,6 · 글씨 White ·
    // 요소 Opacity 1(A12 칩과 같은 값 — 출처 VideoPlayerView.xaml StartOverlay).
    // A108(v0.135.0): 표시 위치가 패널 안 → 경계 버튼 옆(세로 중앙)으로 이동 — XAML만 바뀌었고
    // 타이밍 장치는 그대로다.
    // A133: 표시·숨김·페이드의 대상 요소가 PinnedText → PinnedPlate(감싼 판)로 올라갔다.
    // 문구 대입만 PinnedText가 받는다. Opacity 애니메이션은 UIElement 공통 속성이라 대상이
    // TextBlock이든 Border든 같은 경로("Opacity")로 성립한다(실기기 확인 포인트 — CI 검증 불가).

    private const double HintOpacity = 1; // XAML PinnedPlate.Opacity와 같아야 한다(페이드 후 되돌릴 값 — A133에서 0.6 → 1)
    private static readonly TimeSpan HintHoldFor = TimeSpan.FromSeconds(2.5);      // 표시 시간(구현 시 결정)
    private static readonly TimeSpan HintFadeFor = TimeSpan.FromMilliseconds(300); // 페이드아웃 시간

    private DispatcherTimer? _hintTimer; // UI 스레드 타이머 (MainWindow.MakeS1FlashTimer·DriveStrip과 같은 방식)
    private Storyboard? _hintFade;
    private bool _hintVisible;    // 지금 "보여야 하는 상태"인가 — 반복 표시 호출마다 되감지 않기 위한 기억
    private string? _hintText;    // 마지막으로 띄운 문구 — 내용이 바뀔 때만 다시 띄운다

    /// <summary>
    /// 안내를 잠깐 띄운다: 2.5초 표시 → 300ms 페이드아웃 → Collapsed.
    /// 표시 메서드는 ApplyOverlayStates 경유로 여러 번 불리므로, **표시 상태로 새로 진입했거나
    /// 문구가 바뀐 경우에만** 다시 띄우고 타이머를 되감는다(매번 재시작하면 영영 안 사라진다).
    /// </summary>
    private void ShowHint(string text)
    {
        if (_hintVisible && _hintText == text) return; // 이미 이 안내를 낸 뒤 — 그대로 둔다(사라진 채라도)
        _hintVisible = true;
        _hintText = text;

        StopHint(); // 돌던 타이머·페이드를 먼저 정리해야 아래 Opacity 대입이 애니메이션에 눌리지 않는다
        PinnedText.Text = text;                 // 문구는 판 안의 TextBlock이 받는다(A133)
        PinnedPlate.Opacity = HintOpacity;      // 직전 페이드로 0이 된 채 남아 있을 수 있다
        PinnedPlate.Visibility = Visibility.Visible;

        _hintTimer ??= CreateHintTimer();
        _hintTimer.Stop();  // DispatcherTimer는 반복 타이머 — Stop 후 Start로 확실히 되감는다
        _hintTimer.Start();
    }

    /// <summary>숨겨야 하는 상태(패널 닫힘) — 타이머·페이드를 즉시 멈추고 감춘다.</summary>
    private void HideHint()
    {
        _hintVisible = false;
        _hintText = null;
        StopHint();
        PinnedPlate.Visibility = Visibility.Collapsed; // 판째로 감춘다(A133)
    }

    private DispatcherTimer CreateHintTimer()
    {
        var timer = new DispatcherTimer { Interval = HintHoldFor };
        timer.Tick += (_, _) =>
        {
            timer.Stop(); // 반복 타이머라 Tick 안에서 반드시 멈춘다(MainWindow.MakeS1FlashTimer와 같은 관용구)
            FadeOutHint();
        };
        return timer;
    }

    /// <summary>
    /// Storyboard + DoubleAnimation(Opacity) — DriveStrip 마퀴와 같은 관용구.
    /// A133: 대상이 판(PinnedPlate)이라 문구와 배경이 한 덩어리로 사라진다(같은 "Opacity" 경로).
    /// </summary>
    private void FadeOutHint()
    {
        var animation = new DoubleAnimation
        {
            From = HintOpacity,
            To = 0,
            Duration = new Duration(HintFadeFor),
            EnableDependentAnimation = true,
        };
        Storyboard.SetTarget(animation, PinnedPlate);
        Storyboard.SetTargetProperty(animation, "Opacity");

        var fade = new Storyboard();
        fade.Children.Add(animation);
        fade.Completed += (_, _) =>
        {
            if (!ReferenceEquals(_hintFade, fade)) return; // 그새 다시 띄워졌다 — 감추면 안 된다
            PinnedPlate.Visibility = Visibility.Collapsed;
        };
        _hintFade = fade;
        fade.Begin();
    }

    private void StopHint()
    {
        _hintTimer?.Stop();
        _hintFade?.Stop(); // Stop은 Completed를 부르지 않는다 — 보류 중인 Collapsed도 함께 사라진다
        _hintFade = null;
    }

    /// <summary>
    /// 파일·모듈이 바뀌었을 때 셸이 부른다 — 캐시를 비우고 진행 중 로드를 폐기해,
    /// 다음 표시에서 새 콘텐츠 기준으로 다시 읽게 한다(같은 경로 재오픈 포함).
    /// A241: 선택 다건 캐시(_selectionCache)는 비우지 않는다 — 수정시각 키가 낡음을 스스로
    /// 판별하므로 콘텐츠 전환과 무관하게 유효하고, 비우면 프리페치의 목적(폴더 단위 적중)이
    /// 콘텐츠 전환마다 무너진다. 프리페치 무산도 여기 몫이 아니다(폴더 축 — 새 목록 통지가
    /// seq를 올린다).
    /// </summary>
    public void InvalidateCache()
    {
        InvalidateContentInfoCache(); // 열림 축 캐시(+ A332 재조회 예산)를 비운다
        _activePath = null;
        _seq++;
        _selectionCts?.Cancel(); // A200: 보류 중 선택 조회도 폐기 — 실행 전이면 워커 큐에서 건너뛴다
    }

    /// <summary>
    /// A332: <b>열림 축 캐시만</b> 비운다 — 뷰가 "정보가 갈렸다"고 알렸을 때(IContentInfoChangedSource)
    /// 셸이 부르는 자리다. <see cref="InvalidateCache"/>와 달리 시퀀스(_seq)를 올리지도,
    /// 선택 축 조회를 취소하지도 않는다: 이 통지는 콘텐츠 전환이 아니라 "같은 파일의 값이
    /// 늦게 도착했다"이고, 선택 축(A200·A241)은 별개 축이라 진행 중 조회를 끊을 이유가 없다
    /// (선택 축 무회귀 — 두 축은 캐시 필드부터 물리적으로 갈려 있다).
    /// 진행 중이던 열림 축 조회가 늦게 끝나도 무해하다: 뒤이은 재조회가 _seq를 올려
    /// 낡은 결과를 LoadAsync 말미의 경합 검사에서 걸러 낸다.
    /// </summary>
    public void InvalidateContentInfoCache()
    {
        _cachePath = null;
        _cacheItems = null;
        _cacheProvisional = false;
        _emptyRetryPath = null; // 새 계기 = 재조회 예산도 새로 준다
    }

    // ---------- 선택 정보 다건 캐시 + 폴더 프리페치 (A241) ----------

    /// <summary>프리페치 동시 발사 상한 — ExplorerPane.FetchConcurrency와 같은 값(A194 관용구·풀 워커 수와 동일).</summary>
    private const int PrefetchConcurrency = 3;

    /// <summary>선택 정보 캐시 상한 — ExplorerPane._infoCache의 4000 관용구와 동일(초과 시 Clear).</summary>
    private const int SelectionCacheCap = 4000;

    /// <summary>
    /// A241: 선택 정보 다건 캐시 — 경로 → (수정시각 ticks, 행 목록). 수정시각이 다르면 무효
    /// (ExplorerPane._infoCache와 같은 꼴 — 종전 마지막 1건(_cachePath 겸용)에서 분리·확장).
    /// 실패 결과(기본 행 폴백·빈 라벨)도 담는다 — 재시도하지 않고, 폴더 재진입 재스캔이
    /// 수정시각 변화로만 갱신한다(구현 시 결정). <b>UI 스레드에서만 만진다</b>(A194 관용구 —
    /// 워커 람다는 순수 조회뿐, 발사 루프와 후속부는 전부 UI 문맥).
    /// </summary>
    private readonly Dictionary<string, (long ModifiedTicks, IReadOnlyList<ContentInfoItem> Items)>
        _selectionCache = new(StringComparer.OrdinalIgnoreCase);

    private ModuleWorkerPool? _prefetchPool; // A241: 프리페치 전용 풀(워커 3) — 선택 즉시 조회(Worker)와 분리
    private int _prefetchSeq;                // A241: 새 목록 통지·언로드가 올린다 — 낡은 프리페치 무산

    /// <summary>지연 생성: Unloaded로 정리된 뒤 다시 로드돼도 되살아난다(Worker와 같은 규칙).</summary>
    private ModuleWorkerPool PrefetchPool =>
        _prefetchPool ??= new ModuleWorkerPool("KOTU info prefetch", PrefetchConcurrency);

    /// <summary>
    /// A241: 현재 폴더 목록의 **이미지 파일** 선택 정보를 미리 조회해 캐시를 데운다 — 셸이
    /// 좌 리스트의 조립 완료 훅(ExplorerPane.FinishFill → FillCompleted)에서 부른다(A192 뼈대
    /// 우선: 목록 조립이 끝난 뒤에만 부가 스캔이 붙는다). 이미지만인 이유 = EXIF가 목적 —
    /// PDF 페이지 수·zip 압축률은 열기 비용이 커 온디맨드 유지(등재문 확정). placeholder 제외
    /// (A175 하이드레이션 금지), 캐시 적중(경로+수정시각 일치)은 건너뛴다. 새 호출이 이전 호출을
    /// 무산시킨다(폴더 전환·감시 재스캔 포함 — seq 관용구). 패널이 닫혀 있어도 돈다(Visibility는
    /// Unloaded가 아니다 — ExplorerPane 감시와 같은 판단): 다음 열림·선택에서 즉시 적중이 목적이다.
    /// </summary>
    public void PrefetchSelectionInfo(IReadOnlyList<ExplorerListing.Entry> entries)
    {
        var seq = ++_prefetchSeq;
        _ = PrefetchAsync(entries, seq);
    }

    /// <summary>
    /// A194 관용구 그대로(ExplorerPane.LoadDetailInfoAsync 참조): UI 스레드의 SemaphoreSlim
    /// 게이트로 동시 3건까지만 발사하고, 캐시 기록은 전부 UI 문맥에서 한다. 발사 상한 =
    /// 캐시 상한(4000) — 초대형 폴더가 Clear를 반복 유발하며 캐시를 자기 앞부분으로 재세척하는
    /// 낭비를 막는다(구현 시 결정).
    /// </summary>
    private async Task PrefetchAsync(IReadOnlyList<ExplorerListing.Entry> entries, int seq)
    {
        using var gate = new SemaphoreSlim(PrefetchConcurrency); // 동시 발사 상한 (A194)
        var running = new List<Task>();
        var stop = false; // 풀이 닫힘(취소 Task) — 남은 발사 중단. UI 스레드에서만 읽고 쓴다.

        // 항목 하나의 조회 + 캐시 기록. UI 스레드에서 시작하므로 await 후속부도 UI 스레드다.
        async Task FetchIntoAsync(ExplorerListing.Entry entry)
        {
            try
            {
                IReadOnlyList<ContentInfoItem> items;
                try
                {
                    items = await PrefetchPool.Run(_ => SelectionQuickInfo.Build(entry.Path));
                }
                catch (OperationCanceledException)
                {
                    stop = true; // 오버레이가 내려가며 풀이 닫힘 — 발사 루프도 멈춘다
                    return;
                }
                catch
                {
                    return; // 방어 — Build는 실패를 행 폴백으로 삼키므로 여기 올 일은 드물다
                }
                if (seq != _prefetchSeq) return; // 폴더 전환·언로드 — 낡은 결과 폐기
                StoreSelectionCache(entry.Path, entry.Modified.Ticks, items); // A239 확정 결과 그대로
            }
            finally
            {
                gate.Release(); // 예외·취소 경로 포함 — 누락되면 3건 뒤 조용히 멈춘다
            }
        }

        var scheduled = 0;
        foreach (var entry in entries)
        {
            if (stop || seq != _prefetchSeq || scheduled >= SelectionCacheCap) break;
            if (entry.IsFolder || entry.IsPlaceholder) continue; // placeholder = 조회 자체가 하이드레이션(A175)
            if (!ExplorerListing.MatchesExtension(
                    entry.Name, KOTU.Module.Image.ImageFolderNavigator.SupportedExtensions))
                continue; // 이미지만 — 비이미지는 온디맨드 유지
            if (_selectionCache.TryGetValue(entry.Path, out var hit) &&
                hit.ModifiedTicks == entry.Modified.Ticks)
                continue; // 캐시 적중 — 감시 재통지마다 다시 읽지 않는다(실패 결과 포함 — 재시도 없음)

            await gate.WaitAsync(); // UI 문맥 await — 후속부는 UI 스레드로 복귀
            if (stop || seq != _prefetchSeq)
            {
                gate.Release(); // 획득만 하고 발사하지 않는 경로 — 누수 방지
                break;
            }
            scheduled++;
            running.Add(FetchIntoAsync(entry));
        }
        // 발사분 완주 대기 — using gate의 Dispose가 대기 중 Release보다 앞서지 않게 한다.
        await Task.WhenAll(running);
    }

    /// <summary>A241: 캐시 기록 단일 지점 — 상한 초과 시 통째 Clear(ExplorerPane._infoCache 관용구).</summary>
    private void StoreSelectionCache(string path, long modifiedTicks, IReadOnlyList<ContentInfoItem> items)
    {
        if (_selectionCache.Count > SelectionCacheCap) _selectionCache.Clear();
        _selectionCache[path] = (modifiedTicks, items);
    }

    /// <summary>
    /// 모듈 제공 정보(IContentInfoProvider) 우선, 없으면 파일 기본 정보.
    /// A200: selection=true면 provider 대신 셸 선택 조회기(SelectionQuickInfo)를 워커에서 돌리고,
    /// placeholder=true면 조회 없이 기본 정보 + (이미지면) EXIF 빈 라벨(A239 ② — A175
    /// 하이드레이션 금지 불변)만 그린다. A241: 선택 결과는 경로+수정시각 키 다건 캐시
    /// (_selectionCache — 프리페치와 공유)로, provider 결과는 종전대로 마지막 1건
    /// (_cachePath/_cacheItems)으로 담는다. 캐시 적중 시에도 _seq를 올린다 — 진행 중이던 직전
    /// 로드가 늦게 도착해 방금 그린 적중 결과를 덮는 역전 방지(적중이 흔해지는 A241에서
    /// 실사고 경로가 된다 — 종전 단건 캐시의 잠복 구멍 수리).
    /// A332: 열림 축 캐시에 <b>빈 결과가 굳지 않는다</b> — 모듈 절이 통째로 빈칸인 결과는 잠정으로만
    /// 담아 다음 표시 계기에 1회 다시 묻는다(<see cref="IsEmptyContentInfo"/>). 선택 축(다건 캐시)은
    /// 이 완화의 대상이 아니다 — 전용 워커·수정시각 키로 이미 정상이고, 불필요한 재조회를 만들지 않는다.
    /// </summary>
    private async Task LoadAsync(string path, IContentInfoProvider? provider,
        bool selection = false, bool placeholder = false, long modifiedTicks = 0)
    {
        if (selection && !placeholder &&
            _selectionCache.TryGetValue(path, out var cached) && cached.ModifiedTicks == modifiedTicks)
        {
            _seq++;
            _activePath = path;
            _selectionCts?.Cancel(); // 보류 중 선택 조회 폐기 — 적중 화면을 덮지 않게
            RenderItems(cached.Items);
            return;
        }
        if (!selection && _cachePath == path && _cacheItems is not null && !_cacheProvisional)
        {
            _seq++;
            _activePath = path;
            RenderItems(_cacheItems);
            return;
        }
        // A332: 잠정 캐시(빈 결과)면 적중을 쓰지 않고 아래 정규 조회로 내려간다 — 재조회는
        // **표시 계기가 있을 때 1회**뿐이다(폴링·타이머 없음). 예산은 여기서 즉시 소진하고
        // (_emptyRetryPath가 그 사실을 경로 단위로 기억한다) 다시 비어 있어도 그때는 확정으로
        // 굳으므로, 한 계기당 조회는 최대 2회다. 선택 축 로드는 이 예산을 건드리지 않는다
        // (열림 축 전용 상태 — 목록에서 다른 파일을 훑는 동안 열림 축 재조회 기회가 사라지면 안 된다).
        if (!selection) _cacheProvisional = false;

        var seq = ++_seq;
        _activePath = path;
        ShowMessage("Loading info...");

        IReadOnlyList<ContentInfoItem>? items = null;
        try
        {
            if (selection && placeholder)
            {
                // A239 ② + A175: 조회 0회 — FileInfo 메타데이터만이라 하이드레이션이 없고
                // UI 스레드에서 즉시 만들어도 된다(워커 불경유. 캐시에도 담지 않는다 —
                // placeholder는 프리페치 제외 대상이고 재조립이 값싸다).
                items = SelectionQuickInfo.BuildPlaceholderRows(path);
            }
            else if (selection)
            {
                _selectionCts?.Cancel(); // 직전 선택 조회는 폐기 대상 — 큐에서 실행 전이면 건너뛴다
                var cts = _selectionCts = new CancellationTokenSource();
                items = await Worker.Run(_ => SelectionQuickInfo.Build(path), cts.Token);
            }
            else if (provider is not null)
                items = await provider.GetContentInfoAsync();
        }
        catch
        {
            // 모듈 정보·선택 조회 실패 → 아래 파일 기본 정보로 대체
        }
        items ??= BuildBasicFileInfo(path);

        if (seq != _seq || _activePath != path) return; // 그새 파일이 바뀜
        if (selection && !placeholder)
            StoreSelectionCache(path, modifiedTicks, items); // A241 — 실패 결과(기본 행 폴백)도 캐시
        else if (!selection)
        {
            _cachePath = path;
            _cacheItems = items;
            // A332: 빈 결과(모듈 절이 통째로 빈칸)는 **잠정**으로만 담는다 — 다음 표시 계기에
            // 1회 더 묻고, 그 경로에서 예산을 이미 썼으면(_emptyRetryPath) 그대로 확정한다.
            var empty = IsEmptyContentInfo(items);
            _cacheProvisional = empty && _emptyRetryPath != path;
            _emptyRetryPath = empty ? path : null;
        }
        RenderItems(items);
    }

    /// <summary>
    /// A332 <b>"빈 결과" 판정</b> — 첫 구분 행 뒤(= 모듈 절)에 값이 있는 행이 하나도 없는 결과.
    /// 파일 기본 3행(File·Size·Modified)은 조회가 통째로 실패해도 값이 차므로 판정에서 빼야 하고,
    /// 그 경계가 정확히 첫 구분 행이다(네 모듈의 단일 빌더가 전부 "기본 3행 + 구분 행 + 모듈 절"
    /// 형태다 — A327·A328·A329). 구분 행이 없는 결과(셸 폴백 <see cref="BuildBasicFileInfo"/> 등)는
    /// 애초에 모듈 절이 없어 판정 대상이 아니다 → false(종전대로 한 번에 확정 캐시).
    /// 행 집합·라벨·순서는 이 판정으로 바뀌지 않는다 — 읽기만 한다(부록 B 98 불변).
    /// </summary>
    private static bool IsEmptyContentInfo(IReadOnlyList<ContentInfoItem> items)
    {
        var afterSeparator = false;
        foreach (var item in items)
        {
            if (item.IsSeparator)
            {
                afterSeparator = true;
                continue;
            }
            if (afterSeparator && item.Value.Length > 0) return false; // 실질 값 1개 = 빈 결과 아님
        }
        return afterSeparator;
    }

    /// <summary>
    /// 셸 폴백(문서·압축 등 미구현 모듈·모듈 정보 실패·placeholder 선택) — A150에서 개행 문자열을
    /// 라벨·값 행으로 이식했다. 표시 항목(이름·크기·수정일·폴더)과 값 포맷은 종전 그대로다.
    /// A200: 셸 선택 조회기(SelectionQuickInfo)도 비이미지 종류의 기본 행으로 재사용한다(internal).
    /// </summary>
    internal static IReadOnlyList<ContentInfoItem> BuildBasicFileInfo(string path)
    {
        try
        {
            var info = new FileInfo(path);
            return new[]
            {
                new ContentInfoItem("File", info.Name),
                new ContentInfoItem("Size", ExplorerListing.FormatSize(info.Length)),
                new ContentInfoItem("Modified", $"{info.LastWriteTime:yyyy-MM-dd HH:mm}"),
                new ContentInfoItem("Folder", info.DirectoryName ?? string.Empty),
            };
        }
        catch (Exception ex)
        {
            return new[]
            {
                new ContentInfoItem("File", Path.GetFileName(path)),
                new ContentInfoItem("Info", "Unavailable: " + ex.Message),
            };
        }
    }

    // ---------- 정보 행 렌더 (A150 — 하드웨어 우 패널 라벨·값 2열 관용구 준용) ----------

    // 치수 출처 = HardwareView의 A172(v0.165.0) 상수(SpecLabelFontSize·SpecValueFontSize·
    // SpecLabelWidth) — 같은 25% 우측 구획이라 같은 값을 쓴다. 저쪽이 바뀌면 여기도 맞출 것.
    // 그룹 제목 행(하드웨어의 섹션 Title)은 두지 않는다 — 정보 패널은 행이 십수 개뿐이라
    // Separator(빈 행)의 여백만으로 그룹이 구분된다(계약 주석의 A150 구현 시 결정).
    private const double ItemLabelFontSize = 11;
    private const double ItemValueFontSize = 11;
    private const double ItemLabelWidth = 96;
    private const double SeparatorHeight = 8; // 그룹 사이 빈 행 높이(구현 시 결정)

    /// <summary>문구 전용 표시(플레이스홀더·Loading·실패 안내) — 행 목록과 배타 토글.</summary>
    private void ShowMessage(string text)
    {
        InfoText.Text = text;
        InfoText.Visibility = Visibility.Visible;
        InfoRows.Visibility = Visibility.Collapsed;
    }

    /// <summary>라벨·값 행 목록 표시 — 문구와 배타 토글. 행 Grid는 코드가 만든다(하드웨어 Render 관용구).</summary>
    private void RenderItems(IReadOnlyList<ContentInfoItem> items)
    {
        InfoRows.Children.Clear();
        foreach (var item in items)
            InfoRows.Children.Add(item.IsSeparator
                ? new Grid { Height = SeparatorHeight }
                : MakeItemRow(item));
        InfoText.Visibility = Visibility.Collapsed;
        InfoRows.Visibility = Visibility.Visible;
    }

    /// <summary>라벨(고정폭·흐리게) + 값(줄바꿈·선택 가능) 한 줄 — HardwareView.MakeItemRow와 같은 꼴.</summary>
    private static Grid MakeItemRow(ContentInfoItem item)
    {
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(ItemLabelWidth) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        var label = new TextBlock
        {
            Text = item.Label,
            FontSize = ItemLabelFontSize,
            Opacity = 0.65,
            TextTrimming = TextTrimming.CharacterEllipsis,
            Margin = new Thickness(0, 2, 12, 2),
        };
        var value = new TextBlock
        {
            Text = item.Value,
            FontSize = ItemValueFontSize,
            TextWrapping = TextWrapping.Wrap,
            IsTextSelectionEnabled = true,
            Margin = new Thickness(0, 2, 0, 2),
        };
        Grid.SetColumn(value, 1);
        grid.Children.Add(label);
        grid.Children.Add(value);
        return grid;
    }
}
