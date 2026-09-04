using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using KOTU.Core.Contracts;
using KOTU.Core.Routing;

namespace KOTU.Module.AllReadable;

/// <summary>
/// All Readable 화면 (A59) — 파일 형식별 모듈 뷰를 <b>중첩 호스팅</b>한다.
/// 파일을 열면 확장자로 담당 모듈을 골라 자식 뷰를 만들고, <b>센터</b>(ChildHost)와
/// <b>하단 바</b>(ChildBarHost)만 그 뷰의 것으로 바꾼다. 셸에는 이 뷰가 계속 하나의 모듈로 보이므로
/// 창 제목·아이콘·좌우 오버레이·시작 메뉴는 All Readable 것으로 유지된다.
///
/// 셸과의 계약은 전부 <b>자식에게 위임</b>하는 형태다:
///  · <see cref="IContentStateSource"/> — 자식이 연 파일을 그대로 중계(오버레이·탐색기 동기화).
///  · <see cref="IContentInfoProvider"/> — 우측 정보 오버레이가 자식의 상세 정보를 그대로 받는다.
///  · <see cref="ICloseGuard"/> — 문서 자식의 미저장 가드(A37)가 셸까지 이어진다.
///  · <see cref="IBottomBarProvider"/>·<see cref="IDriveStripHost"/> — 하단 바 한 줄과 드라이브 줄(A22).
///  · <see cref="ITrayStatusProvider"/> — 트레이 아이콘 표시 내용(A54)도 자식 것을 그대로 중계.
///  · <see cref="IPlaybackStateSource"/> — 영상 자식의 재생 상태(A186 하단 바 자동 숨김)를 중계.
///    자식이 계약을 구현할 때만 HasPlaybackSurface가 참이 된다(문서·사진 자식이면 거짓).
///  · <see cref="IPrintPageProvider"/> — 인쇄(A211)도 자식 것을 그대로 중계. 자식이 계약을
///    구현하고 인쇄할 콘텐츠가 있을 때만 CanPrintNow가 참이다(계약 문서가 이 호스트를 명시한다).
///  · <see cref="IUntitledContentSource"/> — A247: 문서 자식의 New 버튼이 상시 활성이 되면서
///    이 화면에서도 무제 개시·새 인스턴스 요청이 실동작한다 — 두 이벤트를 그대로 중계한다
///    (중계가 없으면 자식이 무제로 바뀌어도 셸 제목·오버레이가 옛 파일에 머문다).
///  · <see cref="IFileOpenTarget"/> — 셸이 "이 파일 네가 열래?"를 먼저 물어보는 지점(A24 유지).
///  · <see cref="IContentInfoChangedSource"/> — A332: 재생 자식이 "정보가 갱신됐다"고 알리면
///    그대로 중계한다(셸이 정보 패널 열림 축을 다시 묻는다).
///  · <see cref="IBrowseOrderConsumer"/> — A346: 셸이 주는 좌 리스트 표시 순서를 받아 두었다가
///    자식에게 그대로 내려 준다(자식 교체 때 다시 한 번). 이 중계가 없으면 이 화면에서 연 사진의
///    ◀/▶가 좌 리스트 순서를 못 따른다 — 여기서는 뷰 계층이 한 겹 더 있어 셸의 직접 주입이
///    자식까지 닿지 않기 때문이다. 좌 리스트는 이 화면에서 전 모듈 합집합이라 목록에 다른 형식이
///    섞여 오지만, 걸러 내는 것은 받는 자식 쪽 몫이다(계약 문서 참조).
///  · <see cref="ICurrentPathSource"/> — A348: 자식(사진)이 항해 시점에 쏘는 "보여 주려는 파일이
///    바뀌었다"를 그대로 중계한다. 이 중계가 없으면 이 화면에서 연 사진의 ←/→ 오토리피트 때
///    좌 리스트 하이라이트가 로드 완료분만 따라가 건너뛴다(셸은 ModuleHost.Content = 이 뷰에게만
///    계약을 묻기 때문 — A279 중계와 같은 이유).
///
/// <b>되돌린 시도(A331, v0.320.0)</b>: 좌 리스트를 지금 자식의 형식으로 좁히는 계약을 한 번 넣었다가
/// 되돌렸다. 제기 근거였던 "좌 리스트 = 플레이리스트" 전제가 틀렸기 때문이다 — 재생 목록은
/// <c>FolderPlaylist</c>로 자식 뷰 안에 따로 있고 좌 리스트와 무관하다(폴더를 탐색해도 재생 목록은
/// 유지된다). 게다가 좁히면 콘텐츠를 닫지 않고서는 다른 형식으로 건너뛸 수 없어, "한 화면에서 전부
/// 읽는다"는 A59의 원설계 의도를 해친다. 좌 리스트는 이 화면에서 <b>항상 전 모듈 합집합</b>이다.
///
/// <b>워커 수명(A42)</b>: 자식 뷰의 워커·재생·구독은 자식이 <c>Unloaded</c>에서 스스로 정리한다.
/// 그래서 자식 교체는 "이전 자식을 비주얼 트리에서 떼는 것"이 곧 정리다 —
/// <see cref="DetachChild"/>가 하단 바 조각을 먼저 떼고(셸 하단 바 트리에 있으므로 자동으로
/// 사라지지 않는다) 그다음 센터를 비운다. 이 뷰가 내려갈 때(Unloaded)도 같은 정리를 한 번 더 한다.
/// </summary>
public sealed partial class AllReadableView : UserControl, IContentStateSource, IContentInfoProvider,
    IBottomBarProvider, IDriveStripHost, ICloseGuard, IFileOpenTarget, ITrayStatusProvider,
    IPlaybackStateSource, IPrintPageProvider, IUntitledContentSource, IContentPathChangedSource,
    IContentInfoChangedSource, IBrowseOrderConsumer, ICurrentPathSource, IMediaTransportTarget
{
    /// <summary>자식 후보(파일 모듈만) — 모듈이 등록 시점에 추려 넘겨준다.</summary>
    private readonly IReadOnlyList<IModule> _children;

    private UIElement? _childView;   // 지금 센터에 얹힌 자식 뷰 (null = 빈 상태)
    private string? _filePath;       // 지금 보고 있는 파일
    private bool _driveStripShown;   // 셸이 지정한 드라이브 줄 표시 여부 (A22)
    private bool _childDirty;        // 자식이 알린 마지막 미저장 상태 (A37 — 자식 교체 시 되돌리기용)
    // A346: 셸이 마지막으로 준 좌 리스트 표시 순서(자식 교체 때 새 자식에게 다시 내려 준다).
    private string? _browseFolder;
    private IReadOnlyList<string> _browseFiles = [];

    /// <summary>자식이 파일을 열면(첫 로드·자식 내부 ◀/▶ 탐색 포함) 셸에 그대로 중계한다.</summary>
    public event Action<string>? ContentOpened;

    /// <summary>A279: 문서 자식의 편집 대상 파일이 갈렸다는 통지(Save as...)를 그대로 셸에 중계한다 —
    /// 셸은 이걸로 창 제목을 새 파일 이름으로 다시 만든다(IContentPathChangedSource).</summary>
    public event Action<string>? ContentPathChanged;

    /// <summary>A348: 자식이 항해로 "보여 주려는 파일"을 옮겼다는 통지를 그대로 셸에 중계한다
    /// (ICurrentPathSource — 셸은 좌 리스트의 현재 파일 표시만 즉시 옮긴다).</summary>
    public event Action<string>? CurrentPathChanged;

    /// <summary>A332: 재생 자식이 "상세 정보가 갱신됐다"고 알린 것을 그대로 셸에 중계한다
    /// (IContentInfoChangedSource — 셸은 정보 패널 열림 축을 다시 묻는다). 자식 교체 자체는
    /// 쏘지 않는다: 교체에는 언제나 콘텐츠 전환(ContentOpened 중계)이 따라와 셸이 이미 다시 묻는다.</summary>
    public event Action? ContentInfoChanged;

    /// <summary>자식(문서 편집)의 미저장 상태 변화를 셸에 중계한다 — 창 제목 ● 표시(A37).</summary>
    public event Action<bool>? UnsavedChanged;

    /// <summary>자식의 트레이 표시 값 변화를 그대로 중계한다(A54). 자식 교체 자체도 이 이벤트를 쏜다.</summary>
    public event Action? TrayStatusChanged;

    /// <summary>영상 자식의 재생 상태 변화를 그대로 중계한다(A186). 자식 교체 자체도 이 이벤트를 쏜다.</summary>
    public event Action? PlaybackStateChanged;

    /// <summary>A247: 문서 자식의 무제 진입(A189)을 그대로 셸에 중계한다(IUntitledContentSource).</summary>
    public event Action? UntitledOpened;

    /// <summary>A247: 문서 자식의 "Open in new instance" 요청을 그대로 셸에 중계한다.</summary>
    public event Action? UntitledWindowRequested;

    /// <summary>
    /// A247: 셸→뷰 무제 개시 진입로 — 자식이 계약을 구현할 때만 전달한다. 실사용 경로는
    /// 문서 모듈 직행(WindowManager.OpenUntitledDocumentInNewWindow → MainWindow가 "document"
    /// 모듈을 띄운다)이라 이 화면으로는 오지 않는다 — 계약 완결용 전달(빈 상태면 무동작).
    /// </summary>
    public void OpenUntitled()
    {
        if (_childView is IUntitledContentSource untitled) untitled.OpenUntitled();
    }

    /// <summary>재생 표면(영상 자식)이 전면인가(A186) — 문서·사진 자식·빈 상태면 false.</summary>
    public bool HasPlaybackSurface => _childView is IPlaybackStateSource { HasPlaybackSurface: true };

    /// <summary>영상 자식이 지금 재생 중인가(A186) — 자식이 계약 미구현이면 false.</summary>
    public bool IsPlaying => _childView is IPlaybackStateSource { IsPlaying: true };

    // ---------- 셸 계약: 미디어 키(SMTC) 중계 (A349 배치 3) ----------
    // 자식(영상·오디오)이 IMediaTransportTarget을 구현하면 그대로 위임하고, 그 밖의 자식
    // (문서·사진·압축)·빈 상태면 "갈 곳 없음 + 조작 무동작"이 된다. 판단은 전부 자식이 하고
    // 이 뷰는 흘리기만 한다(트레이 A54·재생 A186·인쇄 A211 중계와 같은 형).

    /// <summary>자식의 이웃 유무 변화를 그대로 셸로 올린다(A349 배치 3 — 중계만).</summary>
    public event Action? NeighborsChanged;

    /// <summary>지금 조작 가능한 자식(재생 뷰)인가 — 아니면 아래 전부가 무동작·false다.</summary>
    private IMediaTransportTarget? TransportChild => _childView as IMediaTransportTarget;

    /// <summary>
    /// A349 배치 3: 이 호스트는 자식이 재생 뷰일 때만 미디어 키 대상이다 — 이 축이 없으면
    /// PDF·텍스트·사진 자식을 열어도 셸이 SMTC 세션을 붙여, 미디어 플라이아웃에 KOTU가 뜨고
    /// 조작은 먹통이며 다른 플레이어의 미디어 키를 빼앗는다. 값이 갈리는 시점(자식 교체)에는
    /// <see cref="NeighborsChanged"/>가 나가 셸이 세션을 다시 켜고 끈다.
    /// <see cref="HasPlaybackSurface"/>(A186 — 영상 자식일 때만 참)와 <b>뜻이 다르다</b>:
    /// 오디오 자식이면 이쪽만 참이다.
    /// </summary>
    public bool HasMediaTransport => TransportChild is not null;

    /// <summary>이전 파일로 갈 수 있는가 — 자식이 재생 뷰가 아니면 false.</summary>
    public bool CanPrevious => TransportChild is { CanPrevious: true };

    /// <summary>다음 파일로 갈 수 있는가 — 자식이 재생 뷰가 아니면 false.</summary>
    public bool CanNext => TransportChild is { CanNext: true };

    /// <summary>이전 파일로 이동(자식 위임) — 재생 자식이 아니면 무동작.</summary>
    public void Previous() => TransportChild?.Previous();

    /// <summary>다음 파일로 이동(자식 위임) — 재생 자식이 아니면 무동작.</summary>
    public void Next() => TransportChild?.Next();

    /// <summary>재생(자식 위임) — 재생 자식이 아니면 무동작.</summary>
    public void Play() => TransportChild?.Play();

    /// <summary>일시정지(자식 위임) — 재생 자식이 아니면 무동작.</summary>
    public void Pause() => TransportChild?.Pause();

    /// <summary>자식의 이웃 유무 변화를 그대로 셸로 올린다(A349 배치 3 — 중계만).</summary>
    private void OnChildNeighborsChanged() => NeighborsChanged?.Invoke();

    // ---------- 셸 계약: 인쇄 중계 (A211 배치 2, v0.221.0) ----------
    // 자식의 하단 바를 통째로 얹는 구조라(ChildBarHost) 자식 모듈의 인쇄 버튼도 All Readable
    // 화면에 그대로 나타난다 — 중계가 없으면 그 버튼과 셸 Ctrl+P가 둘 다 무동작이 된다
    // (셸은 ModuleHost.Content = 이 뷰만 보고 계약을 묻는다 — MainWindow.PrintProviderView).
    // 판단은 전부 자식이 하고 이 뷰는 그대로 흘린다(트레이 A54·재생 A186 중계와 같은 형).

    /// <summary>자식의 인쇄 버튼 클릭을 그대로 셸로 올린다(A211) — 셸이 ShowModule에서 구독한다.</summary>
    public event Action? PrintRequested;

    /// <summary>지금 인쇄할 콘텐츠가 있는가 — 자식이 계약 미구현이거나 빈 상태면 false.</summary>
    public bool CanPrintNow => _childView is IPrintPageProvider { CanPrintNow: true };

    /// <summary>인쇄 작업 이름도 자식 것을 그대로(보통 파일 이름). 비면 셸이 앱 이름으로 대체한다.</summary>
    public string PrintJobName =>
        _childView is IPrintPageProvider provider ? provider.PrintJobName : string.Empty;

    /// <summary>페이지 수 — 자식이 계약 미구현이면 0(셸이 안내 페이지로 대체).</summary>
    public int GetPrintPageCount(PrintPageSpec spec) =>
        _childView is IPrintPageProvider provider ? provider.GetPrintPageCount(spec) : 0;

    /// <summary>페이지 요소 — 자식이 만든 것을 그대로 넘긴다(이 뷰는 요소를 만들지 않는다).</summary>
    public Task<object?> CreatePrintPageAsync(int pageNumber, PrintPageSpec spec) =>
        _childView is IPrintPageProvider provider
            ? provider.CreatePrintPageAsync(pageNumber, spec)
            : Task.FromResult<object?>(null);

    /// <summary>
    /// 트레이 아이콘 내용(A54): <b>지금 자식의 것을 그대로</b> 쓴다 — 자식이 영상이면 해상도·비트레이트,
    /// 사진이면 해상도 2줄(가로/세로 — A191)이 그대로 올라간다. 자식이 없으면 유휴 "ALL".
    /// 자식이 계약을 구현하지 않는 경우(방어)는 파일 기본 표기(확장자 · 용량)로 대신한다.
    /// </summary>
    public TrayStatus GetTrayStatus()
    {
        if (_childView is ITrayStatusProvider provider) return provider.GetTrayStatus();
        if (_filePath is not { } path) return TrayStatus.Idle("ALL");

        long bytes = -1;
        try
        {
            bytes = new FileInfo(path).Length;
        }
        catch
        {
            // 크기를 못 읽으면 그 줄만 "—"가 된다.
        }
        return TrayStatus.Open(TrayFormat.Extension(path), TrayFormat.Size(bytes));
    }

    public AllReadableView(OpenContext context, IReadOnlyList<IModule> children)
    {
        InitializeComponent();
        _children = children;

        // 빈 상태에서만 스스로 포커스를 잡는다 — 자식이 있으면 자식이 자기 Loaded에서 잡는다
        // (문서 편집기의 커서를 뺏으면 안 된다).
        Loaded += (_, _) =>
        {
            if (_childView is null) Focus(FocusState.Programmatic);
        };
        // A59 워커 수명: 이 뷰가 내려가면(모듈 전환·창 닫기) 자식도 반드시 함께 내린다.
        // 자식은 Unloaded에서 워커·libvlc·구독을 정리하므로, 트리에서 떼는 것이 곧 정리다.
        Unloaded += (_, _) => DetachChild();

        if (context.FilePath is { } path && File.Exists(path))
            TryOpenFile(path);
        UpdateBars();
    }

    // ---------- 셸 계약: 파일 열기 (A59 / A24) ----------

    /// <summary>
    /// 파일을 이 뷰 안에서 연다(<see cref="IFileOpenTarget"/>). 담당 자식 모듈이 없으면 false —
    /// 셸이 평소 라우팅으로 넘어가 그 파일의 전용 모듈로 연다.
    /// 미저장 가드(A37)는 셸이 이 호출 전에 이미 통과시킨다.
    /// </summary>
    public bool TryOpenFile(string path)
    {
        if (AllReadableRouting.ResolveChild(_children, path) is not { } module) return false;
        ShowChild(module, OpenContext.ForFile(path));
        return true;
    }

    /// <summary>자식 뷰를 만들어 센터와 하단 바에 얹는다. 이전 자식은 먼저 완전히 정리한다.</summary>
    private void ShowChild(IModule module, OpenContext context)
    {
        DetachChild();

        if (module.CreateView(context) is not UIElement view)
        {
            // 계약 위반 방어 — 빈 상태 유지. A349 배치 3: 자식이 사라진 채로 끝나므로
            // HasMediaTransport가 거짓이 됐다는 것을 셸에 알려야 SMTC 세션이 접힌다
            // (정상 경로는 아래 배선 끝에서 같은 이벤트를 쏜다).
            NeighborsChanged?.Invoke();
            return;
        }
        _childView = view;
        _filePath = context.FilePath;

        ChildHost.Content = view;
        // 셸이 모듈 뷰에 하는 것과 같은 배선(모듈 전환 로직의 재사용 가능한 부분) —
        // 다만 셸에만 있는 것(창 제목·아이콘·인스턴스 상태·오버레이)은 셸이 계속 담당한다.
        ChildBarHost.Content = (view as IBottomBarProvider)?.TakeBottomBar() as UIElement;
        if (view is IContentStateSource source) source.ContentOpened += OnChildContentOpened;
        if (view is ICloseGuard guard) guard.UnsavedChanged += OnChildUnsavedChanged;
        if (view is ITrayStatusProvider tray) tray.TrayStatusChanged += OnChildTrayStatusChanged;
        if (view is IPlaybackStateSource playback) playback.PlaybackStateChanged += OnChildPlaybackStateChanged;
        if (view is IPrintPageProvider print) print.PrintRequested += OnChildPrintRequested; // A211
        if (view is IUntitledContentSource untitled) // A247
        {
            untitled.UntitledOpened += OnChildUntitledOpened;
            untitled.UntitledWindowRequested += OnChildUntitledWindowRequested;
        }
        if (view is IContentPathChangedSource pathChanged) // A279
            pathChanged.ContentPathChanged += OnChildContentPathChanged;
        if (view is IContentInfoChangedSource infoChanged) // A332
            infoChanged.ContentInfoChanged += OnChildContentInfoChanged;
        if (view is ICurrentPathSource currentPath) // A348
            currentPath.CurrentPathChanged += OnChildCurrentPathChanged;
        if (view is IMediaTransportTarget transport) // A349 배치 3
            transport.NeighborsChanged += OnChildNeighborsChanged;
        // A346: 새 자식에게 지금까지 받아 둔 표시 순서를 내려 준다. 자식 생성자가 이미 파일 열기를
        // 시작했더라도(사진 자식은 폴더 스캔을 워커에서 기다린다) 자식 쪽이 스캔 완료 시점에 폴더를
        // 다시 대조해 주입 목록을 채택하므로 첫 열기부터 순서가 맞는다.
        if (view is IBrowseOrderConsumer browseChild && _browseFolder is { } browseFolder)
            browseChild.SetBrowseOrder(browseFolder, _browseFiles);
        UpdateBars();
        TrayStatusChanged?.Invoke(); // A54: 자식이 바뀌면 트레이가 옛 값에 머물지 않게 즉시 알린다
        PlaybackStateChanged?.Invoke(); // A186: 자식 교체도 재생 상태 재평가 대상이다(트레이와 같은 이유)
        NeighborsChanged?.Invoke(); // A349 배치 3: 자식이 바뀌면 이전/다음 가능 여부도 통째로 갈린다
    }

    /// <summary>
    /// 지금 자식을 떼고 정리한다. 순서가 중요하다:
    /// ① 이벤트 구독 해제 → ② 하단 바 조각 제거(셸 하단 바 트리에 있어 센터를 비워도 남는다)
    /// → ③ 센터 비우기(자식 Unloaded → 워커·재생·구독 정리) → ④ 미저장 표시 되돌리기.
    /// 자식이 없어도 안전하다(멱등) — Unloaded와 교체 경로 양쪽에서 불린다.
    /// </summary>
    private void DetachChild()
    {
        if (_childView is IContentStateSource source) source.ContentOpened -= OnChildContentOpened;
        if (_childView is ICloseGuard guard) guard.UnsavedChanged -= OnChildUnsavedChanged;
        if (_childView is ITrayStatusProvider tray) tray.TrayStatusChanged -= OnChildTrayStatusChanged;
        if (_childView is IPlaybackStateSource playback) playback.PlaybackStateChanged -= OnChildPlaybackStateChanged;
        if (_childView is IPrintPageProvider print) print.PrintRequested -= OnChildPrintRequested; // A211
        if (_childView is IUntitledContentSource untitled) // A247
        {
            untitled.UntitledOpened -= OnChildUntitledOpened;
            untitled.UntitledWindowRequested -= OnChildUntitledWindowRequested;
        }
        if (_childView is IContentPathChangedSource pathChanged) // A279
            pathChanged.ContentPathChanged -= OnChildContentPathChanged;
        if (_childView is IContentInfoChangedSource infoChanged) // A332
            infoChanged.ContentInfoChanged -= OnChildContentInfoChanged;
        if (_childView is ICurrentPathSource currentPath) // A348
            currentPath.CurrentPathChanged -= OnChildCurrentPathChanged;
        if (_childView is IMediaTransportTarget transport) // A349 배치 3
            transport.NeighborsChanged -= OnChildNeighborsChanged;
        ChildBarHost.Content = null;
        ChildHost.Content = null;
        _childView = null;

        if (_childDirty)
        {
            // 자식이 사라졌으니 창 제목의 ●도 함께 내려야 한다(셸은 지금 뷰의 상태만 본다).
            _childDirty = false;
            UnsavedChanged?.Invoke(false);
        }
    }

    /// <summary>
    /// A346: 셸이 준 좌 리스트 표시 순서를 보관하고 지금 자식에게 그대로 내려 준다(중계만 —
    /// 자기 형식으로 거르는 판단은 자식이 한다). 자식이 없거나 소비자가 아니면 보관만 한다.
    /// </summary>
    public void SetBrowseOrder(string folder, IReadOnlyList<string> files)
    {
        _browseFolder = folder;
        _browseFiles = files;
        if (_childView is IBrowseOrderConsumer child) child.SetBrowseOrder(folder, files);
    }

    /// <summary>자식의 트레이 값 변화를 그대로 셸로 올린다(A54 — 중계만, 판단은 자식이).</summary>
    private void OnChildTrayStatusChanged() => TrayStatusChanged?.Invoke();

    /// <summary>영상 자식의 재생 상태 변화를 그대로 셸로 올린다(A186 — 중계만, 판단은 셸이).</summary>
    private void OnChildPlaybackStateChanged() => PlaybackStateChanged?.Invoke();

    /// <summary>
    /// 자식의 인쇄 요청(하단 바 버튼)을 그대로 셸로 올린다(A211 — 중계만).
    /// 자식 교체 자체는 이 이벤트를 쏘지 않는다: 트레이·재생과 달리 <b>상태 변화 통지가 아니라
    /// 행동 신호</b>라, 자식이 바뀌었다고 인쇄 대화상자가 떠서는 안 된다.
    /// </summary>
    private void OnChildPrintRequested() => PrintRequested?.Invoke();

    /// <summary>
    /// A247: 문서 자식이 무제로 전환됐다 — 자체 파일 표지를 걷고 셸에 중계한다(무제는 경로가
    /// 없어 ContentOpened를 못 탄다). _filePath를 남겨 두면 트레이 폴백·빈 상태 바가 옛 파일을
    /// 가리킨다. 첫 저장(Save as)이 경로를 확정하면 ContentOpened 중계가 도로 채운다.
    /// </summary>
    private void OnChildUntitledOpened()
    {
        _filePath = null;
        UntitledOpened?.Invoke();
    }

    /// <summary>A247: 새 인스턴스 요청 — 행동 신호라 그대로 올리기만 한다(인쇄 중계와 같은 형).</summary>
    private void OnChildUntitledWindowRequested() => UntitledWindowRequested?.Invoke();

    /// <summary>
    /// A279: 문서 자식이 Save as...로 편집 대상 파일을 갈았다 — 그대로 셸로 올린다(중계만).
    /// 자체 상태(_filePath·하단 바)는 같은 저장에서 먼저 오는 ContentOpened 중계가 이미 옮긴다.
    /// 표시를 직접 만지지 않으므로 디스패치도 셸에 맡긴다(인쇄·재생 중계와 같은 형).
    /// </summary>
    private void OnChildContentPathChanged(string path) => ContentPathChanged?.Invoke(path);

    /// <summary>
    /// A332: 재생 자식이 libvlc 파싱 완료로 "상세 정보가 갱신됐다"고 알렸다 — 그대로 셸로 올린다
    /// (중계만. 정보 자체는 셸이 GetContentInfoAsync 중계로 다시 물어 자식에게서 받는다).
    /// 표시를 직접 만지지 않으므로 디스패치도 셸에 맡긴다(경로 변경·인쇄·재생 중계와 같은 형).
    /// </summary>
    private void OnChildContentInfoChanged() => ContentInfoChanged?.Invoke();

    /// <summary>
    /// A348: 사진 자식이 ◀/▶ 항해(또는 삭제 후 이웃 이동)로 보여 주려는 파일을 옮겼다 — 그대로
    /// 셸로 올린다(중계만). 자체 상태(_filePath·하단 바)는 <b>건드리지 않는다</b>: 그 축의 정본은
    /// 로드 완료의 ContentOpened 중계이고, 오토리피트 중 폐기되는 중간 파일을 여기서 대입하면
    /// 트레이 폴백·빈 상태 바가 화면에 없는 파일을 가리키게 된다.
    /// 표시를 직접 만지지 않으므로 디스패치도 셸에 맡긴다(경로 변경·인쇄·재생 중계와 같은 형).
    /// </summary>
    private void OnChildCurrentPathChanged(string path) => CurrentPathChanged?.Invoke(path);

    // A256(2026-08-27): A223의 열기 요청 중계(IOpenFileRequestSource · OnChildOpenFileRequested ·
    // OpenFileRequested)를 제거했다 — 문서 자식의 하단 바 Open 버튼이 사라져 중계할 신호 자체가
    // 없어졌고, 파일 열기는 셸 S4 'Open file'(A90)로 일원화됐다. 셸이 All Readable 안에서 파일을
    // 여는 경로(IFileOpenTarget.TryOpenFile)는 무변경이다.

    /// <summary>자식이 파일을 열었다(첫 로드·자식 내부 탐색). 셸에 중계해 오버레이·탐색기를 맞춘다.</summary>
    private void OnChildContentOpened(string path)
    {
        // 계약상 UI 스레드 보장이 없다 — 표시를 만지므로 디스패치한다.
        if (DispatcherQueue is { } queue && !queue.HasThreadAccess)
        {
            queue.TryEnqueue(() => OnChildContentOpened(path));
            return;
        }
        _filePath = path;
        UpdateBars();
        ContentOpened?.Invoke(path);
    }

    /// <summary>자식의 미저장 상태 변화를 그대로 셸로 올린다(A37).</summary>
    private void OnChildUnsavedChanged(bool dirty)
    {
        _childDirty = dirty;
        UnsavedChanged?.Invoke(dirty);
    }

    // ---------- 셸 계약: 하단 바 · 드라이브 줄 ----------

    /// <summary>하단 바 한 줄을 뷰에서 떼어 셸 하단 바에 얹는다(v0.21.0 통합 방식, 전 모듈 공통).</summary>
    public object? TakeBottomBar()
    {
        RootGrid.Children.Remove(StatusBar);
        StatusBar.Background = null; // 셸 바가 배경·여백을 제공한다
        StatusBar.Padding = new Thickness(0, 2, 0, 2);
        return StatusBar;
    }

    /// <summary>
    /// A22: 셸이 만든 공용 드라이브 줄을 자체 바 슬롯에 끼운다.
    /// 자식에게 넘기지 않는다 — 드라이브 줄은 "파일이 열려 있지 않을 때"만 보이는데
    /// 그때는 자식이 없어 자체 바가 떠 있기 때문이다.
    /// </summary>
    public void AttachDriveStrip(object strip) => DriveStripHost.Content = strip as UIElement;

    /// <summary>드라이브 줄과 파일명은 같은 칸을 나눠 쓴다 — 줄이 뜨는 동안에는 비켜준다(A22).</summary>
    public void ShowDriveStrip(bool show)
    {
        _driveStripShown = show;
        UpdateBars();
    }

    /// <summary>자식 유무에 따라 하단 바 두 줄(자식 바 / 자체 바)과 센터 안내 문구를 맞바꾼다.</summary>
    private void UpdateBars()
    {
        var hasChild = _childView is not null;
        ChildBarHost.Visibility = hasChild ? Visibility.Visible : Visibility.Collapsed;
        OwnBar.Visibility = hasChild ? Visibility.Collapsed : Visibility.Visible;
        PlaceholderText.Visibility = hasChild ? Visibility.Collapsed : Visibility.Visible;

        var showStrip = !hasChild && _driveStripShown;
        DriveStripHost.Visibility = showStrip ? Visibility.Visible : Visibility.Collapsed;
        FileNameText.Visibility = showStrip ? Visibility.Collapsed : Visibility.Visible;
        FileNameText.Text = _filePath is { } path ? Path.GetFileName(path) : "No file open";
    }

    // ---------- 셸 계약: 우측 정보 오버레이 ----------

    /// <summary>
    /// 우측 정보 오버레이 내용은 지금 자식의 것을 그대로 쓴다(영상=미디어 정보, 사진=EXIF …).
    /// 자식이 없거나 정보를 내놓지 않으면 null — 셸이 파일 기본 정보로 대신한다.
    /// </summary>
    public async Task<IReadOnlyList<ContentInfoItem>?> GetContentInfoAsync() =>
        _childView is IContentInfoProvider provider ? await provider.GetContentInfoAsync() : null;

    // ---------- 셸 계약: 미저장 가드 (A37) ----------

    public bool HasUnsavedChanges => _childView is ICloseGuard { HasUnsavedChanges: true };

    public Task<bool> ConfirmCloseAsync() =>
        _childView is ICloseGuard guard ? guard.ConfirmCloseAsync() : Task.FromResult(true);

    // ---------- 자체 바 동작 (빈 상태 전용) ----------
    // A99: 빈 상태 바의 열기 버튼·파일 대화상자(PickAndOpenAsync)는 제거 — 파일 열기는
    // 셸 S4 'Open file'(A90)로 일원화됐다(자식 라우팅은 TryOpenFile이 종전대로 담당).
    // A151: 전체화면 토글(⛶ 버튼·F11/Esc 액셀러레이터)도 제거 — 전체화면은 셸의 3단 모드
    // 체계(MainWindow — Enter 순환·Alt+Enter·Esc·모드 버튼)가 담당한다.
}
