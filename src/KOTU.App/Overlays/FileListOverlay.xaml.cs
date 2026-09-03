using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using Windows.ApplicationModel.DataTransfer;
using KOTU.Core.Routing;
using KOTU.Core.Settings;
using KOTU.Input;

namespace KOTU.App.Overlays;

/// <summary>
/// 파일 리스트 패널 공용 컨트롤 (A57 ②) — 기존 MainWindow의 AltOverlayRoot(우측,
/// v0.25.0)를 추출해 좌측으로 스왑(A57 ①)한 것. 내부는 ExplorerPane 리스트 전용 모드 재사용.
/// A176: 반투명(오버레이) 축 폐지 — 열림 상태는 사이드바(불투명 도크) 하나뿐이다.
/// 패널 폭은 전 상태 공통 25%(A116 — 종전 "콘텐츠 30% / S1 25%" 2값 폐지, SetPanelPercent).
/// 컨텍스트는 모듈이 주입한다: Show(folder, extensions)의 확장자 목록이 모듈별 필터(A57 ③)가 되고,
/// ExplorerPane의 A7 드롭다운은 그 안에서 추가로 좁힌다. 적용 대상은 파일 모듈
/// (Image·Video·Audio·Document·Archive) — 정보(H/W)·설정 모듈은 셸이 파일 경로가 없어
/// 애초에 Show를 부르지 않는다(현행 동작 유지).
/// 상단 25%는 디스크 계층 트리(A57 ④): 폴더만 표시, 노드 펼침 시점 지연 로드,
/// Show() 시 현재 폴더까지 자동 펼침·선택·스크롤. 트리 선택은 하단 리스트를 그 폴더로 옮긴다
/// (NavigateTo 재사용이라 A5 정렬·A7 필터·A8 경로 표시가 그대로 따라온다).
/// 입력(A176: F11 단타 = 열기/닫기 토글, 핀 버튼 동일)은 셸(MainWindow)이 담당한다 —
/// 이 컨트롤은 Show/Hide/SetPanelPercent만 받는다(구 SetState는 반투명 축과 함께 폐지 —
/// 사이드바 안내 문구는 Show가 직접 띄운다).
/// </summary>
public sealed partial class FileListOverlay : UserControl
{
    private ExplorerPane? _list; // 지연 생성 (기존 MainWindow._altList와 동일 수명)
    private IReadOnlyList<string> _extensions = []; // 마지막 Show()의 모듈 필터 — 트리 이동에 재사용
    private int _expandSeq; // 연속 Show() 시 늦은 자동 펼침 폐기

    /// <summary>
    /// A324: 상단 트리가 <b>지금 가리키고 있는</b> 폴더 — 자동 펼침(ExpandToFolderAsync)이
    /// 취소되지 않고 끝났을 때와 사용자가 트리에서 폴더를 고를 때만 갱신된다.
    /// 트리를 다시 세우면(EnsureDriveRoots 재구성·RebuildTree) null로 되돌린다.
    /// "같은 폴더인가"가 아니라 "트리가 이미 그 폴더를 가리키는가"로 동기 여부를 판정하기 위한
    /// 기억이다(TreeReflects) — 선택 노드 비교만으로는 <b>끝까지 가지 못한 경로</b>를 구분할 수
    /// 없다: 숨김 폴더를 지나거나(A324 AddPathNode 이전 동작) 다른 볼륨(UNC)이면 선택 노드가
    /// 목표와 영원히 달라, 항해 때마다 헛된 재펼침·스크롤이 돈다(A323이 없앤 트리 튐의 재발).
    /// </summary>
    private string? _treeFolder;

    /// <summary>파일 더블클릭 열기 — 셸이 재사용 규칙(A24)을 적용해 라우팅한다.</summary>
    public event Action<string>? FileActivated;

    /// <summary>명시적 새 창 열기(A24: Shift+더블클릭·우클릭 메뉴) — 셸이 항상 새 창으로.</summary>
    public event Action<string>? FileActivatedNewWindow;

    /// <summary>
    /// 내부 리스트(ExplorerPane)의 표시 목록이 다시 그려질 때 폴더 경로와 함께 그대로 전달 (A93 —
    /// 폴더 인자는 A94: 썸네일 뷰가 드랍·붙여넣기 대상 폴더를 알아야 한다).
    /// 셸이 구독해 S1 중앙 썸네일 뷰(ThumbnailExplorer)를 같은 목록으로 갱신한다 —
    /// 좌 리스트와 중앙이 폴더·필터(A7)·정렬(A5) 상태를 공유하는 유일한 통로.
    /// </summary>
    public event Action<string, IReadOnlyList<ExplorerListing.Entry>>? ViewChanged;

    /// <summary>
    /// A240: 내부 리스트의 선택 변경 중계 — 셸이 우측 정보 패널의 "선택 우선"(A200) 갱신에 쓴다.
    /// **떠 있을 때만 발화한다**(SelectedFilePath의 "보이지 않는 선택은 열지 않는다" 규칙과 동일
    /// 축) — 닫힌 도크의 목록 재작성(NavigateList → Fill의 Items.Clear)이 만드는 선택 소멸이
    /// null 발화가 되어 썸네일 축의 선택(마지막 발화 우선)을 지우는 사고를 막는다.
    /// </summary>
    public event Action? SelectionChanged;

    /// <summary>
    /// A241: 내부 리스트의 조립 완료(FinishFill) 중계 — 셸이 우측 정보 패널의 폴더 단위 EXIF
    /// 프리페치를 기동한다. ViewChanged와 달리 표시 여부와 무관하게 흘린다(도크가 닫혀 있어도
    /// 이 리스트가 폴더 상태의 단일 원본 — 캐시를 데워 두는 게 목적이라 가시성 조건이 없다).
    /// </summary>
    public event Action<IReadOnlyList<ExplorerListing.Entry>>? FillCompleted;

    /// <summary>
    /// A243: 내부 리스트의 폴더 실변경 항해 시작 중계(ExplorerPane.NavigationStarted) — 셸이
    /// 중앙 썸네일(ThumbnailExplorer.ShowLoading)에 즉시 화면 전환을 지시한다. ViewChanged
    /// (스캔 완료)와 같은 축의 앞 단 통지라 가시성 조건 없이 흘린다(도크가 닫혀 있어도 이
    /// 리스트가 폴더 상태의 단일 원본 — A93. FillCompleted의 무조건 중계와 같은 방침).
    /// </summary>
    public event Action<string>? NavigationStarted;

    /// <summary>
    /// 떠 있는 동안의 선택 항목(파일·폴더 불문) — 닫혀 있으면 null (A240 —
    /// SelectedFilePath의 가시성 규칙과 동일. 해석은 셸 몫).
    /// </summary>
    public ExplorerListing.Entry? SelectedEntry => IsOpen ? _list?.SelectedEntry : null;

    /// <summary>정렬 키 저장용(A5) — 셸이 리스트 첫 생성 전에 주입한다. 없어도 동작(기본 이름순).</summary>
    public ISettingsService? Settings { get; set; }

    /// <summary>오버레이가 화면에 떠 있는지 — 셸의 표시 갱신·경계 버튼 위치 판단에 쓴다.</summary>
    public bool IsOpen => Visibility == Visibility.Visible;

    /// <summary>
    /// 내부 리스트가 지금 보고 있는 폴더 — 리스트 미생성·항해 전이면 null (A174).
    /// 셸이 "모듈 전환에도 현재 위치 유지"의 세션 위치 원본으로 읽는다(ExplorerStartFolder).
    /// 표시 여부와 무관하다: 도크가 닫혀 있어도 이 리스트가 폴더 상태의 단일 원본(A93)이다.
    /// </summary>
    public string? CurrentFolder => _list?.CurrentFolder is { Length: > 0 } folder ? folder : null;

    /// <summary>
    /// A323: 지금 표시 중인 목록(정렬·필터 반영) — 리스트 미생성이면 빈 목록.
    /// 셸이 재스캔 없이 S4 그리드를 시드할 때만 읽는다(ExplorerPane.DisplayEntries 주석 참고).
    /// </summary>
    public IReadOnlyList<ExplorerListing.Entry> CurrentEntries => _list?.DisplayEntries ?? [];

    /// <summary>
    /// A323: <see cref="CurrentEntries"/>가 속한 폴더 — 스캔이 도는 중이면 <see cref="CurrentFolder"/>와
    /// 다르다(ExplorerPane.DisplayFolder 주석). 셸의 시드는 두 값이 같을 때만 한다.
    /// </summary>
    public string? DisplayFolder => _list?.DisplayFolder is { Length: > 0 } folder ? folder : null;

    /// <summary>
    /// A323: 셸이 "지금 열려 있는 콘텐츠 파일"을 알려 준다 — 내부 리스트가 그 항목을 선택
    /// 표시로 보여주고, 필요하면 보이도록 스크롤한다(ExplorerPane.SetCurrentFile).
    /// 선택 축(A200)은 세우지 않는다 — 그쪽 계약은 그 메서드 주석에 있다.
    /// 리스트가 아직 없으면 무동작이다(호출부 ShowListOverlay가 Show 뒤에 부르므로 항상 있다).
    /// </summary>
    public void SetCurrentFile(string? path) => _list?.SetCurrentFile(path);

    /// <summary>
    /// 떠 있는 동안의 선택 파일 경로 (A86 — 셸 Enter의 "선택 파일 있으면 열기" 판정).
    /// 닫혀 있거나(보이지 않는 선택은 열지 않는다) 선택이 폴더·없음이면 null.
    /// ※ A94 6차(v0.153.0)부터 일괄 열기는 <see cref="OpenSelectedFiles"/> —
    /// 이 속성은 "첫 선택 파일" 질의 API로만 남았다(A86 서술의 원형).
    /// </summary>
    public string? SelectedFilePath => IsOpen ? _list?.SelectedFilePath : null;

    /// <summary>
    /// 떠 있는 동안의 선택 파일 일괄 열기 (A94 6차). 종전 "SelectedFilePath
    /// 하나를 OpenFileRouted"의 대체: 첫 파일은 재사용 규칙(A24) 경로, 나머지는 새 인스턴스
    /// (상한 10 — ExplorerPane.OpenSelectedFiles). 닫혀 있으면 아무것도 하지 않는다 —
    /// 보이지 않는 선택은 열지 않는다(SelectedFilePath와 같은 규칙).
    /// ※ A151: 셸 Enter가 모드 순환이 되면서 셸 호출부는 사라졌다 — 표면(리스트) 자체의
    /// Enter 처리와 대칭인 공개 질의/실행 API로 남긴다(외부 소비자 0인 상태 유지 무해).
    /// </summary>
    public bool OpenSelectedFiles() => IsOpen && _list is { } list && list.OpenSelectedFiles();

    public FileListOverlay()
    {
        InitializeComponent();
        // A34: 폴더 트리에 포커스가 있는 동안에도 모듈 버튼 핫키는 통과시킨다
        // (트리 타이핑 탐색 우선 — 하단 리스트는 ExplorerPane이 같은 표시를 건다).
        FolderTree.Tag = HotkeySupport.PassThroughTag;
    }

    /// <summary>
    /// 모듈 컨텍스트를 주입받아 표시한다: folder = 현재 파일의 폴더,
    /// extensions = 모듈 담당 확장자(IModule.SupportedExtensions — A57 ③ 모듈별 필터).
    /// 이미 떠 있으면 폴더·필터만 갱신한다(모듈 전환 시 ExplorerPane이 A7 필터를 재구성).
    /// </summary>
    public void Show(string folder, IReadOnlyList<string> extensions)
    {
        // A323(깜빡임 수리): 같은 폴더·같은 필터면 목록을 **다시 만들지 않는다**.
        // 종전에는 표시 종착점(ApplyOverlayStates)이 이 메서드를 부를 때마다 NavigateList →
        // ExplorerPane.NavigateToAsync가 무조건 재스캔하고 Fill이 Items.Clear로 항목을 통째로
        // 새로 만들었다 — 뷰 내부 ◀/▶ 항해(OnContentOpened → ApplyOverlayStates)가 매번 그 경로를
        // 타서 ⓐ 리스트가 "리프레시"되는 깜빡임 ⓑ 열린 파일의 선택 표시 소멸이 함께 났다.
        // 외부 변경은 폴더 감시(A94 5차 — 도크가 닫혀 있어도 살아 있다)가 같은 재스캔 경로로
        // 반영하므로 여기서 재스캔을 걸러도 목록이 낡지 않는다.
        // 폴더·필터가 실제로 바뀌면 종전대로 재항해한다(그때의 재작성은 정상 — A323 사양 ④).
        var reopened = !IsOpen; // 숨김 → 표시 전이(드라이브 구성·트리 동기를 한 번은 확인한다)
        var sameView = _list is not null
                       && string.Equals(TrimSep(_list.CurrentFolder), TrimSep(folder),
                           StringComparison.OrdinalIgnoreCase)
                       && _extensions.SequenceEqual(extensions, StringComparer.OrdinalIgnoreCase);
        if (!sameView) NavigateList(folder, extensions);
        Visibility = Visibility.Visible;
        // A176: 사이드바(유일한 열림 상태) 안내 — 구 SetState의 자리. ShowHint의 동일 문구
        // 중복 억제(_hintVisible)가 반복 Show(ApplyOverlayStates 경유 다회 호출)를 걸러 준다.
        ShowHint(OverlayHints.Docked(OverlayHints.ListKey));

        // A323: 상단 트리 쪽도 같은 조건으로 묶는다 — 루트 재구성은 DriveInfo.GetDrives(네트워크
        // 드라이브면 블로킹)를, ExpandToFolderAsync는 ScrollTreeTo(트리 튐)를 부른다. 연속 항해
        // (키 반복)마다 이것들이 돌면 안 된다. (A333: 그 열거 자체는 워커로 옮겼지만 조건은 유지 —
        // 비용이 사라진 게 아니라 UI 스레드 밖으로 나갔을 뿐이고, 불필요한 재구성은 여전히 낭비다.)
        // A324(수리): **트리 동기 자체는 이 조건에서 뺀다.** A323은 EnsureDriveRoots와 트리 동기를
        // 한 덩어리로 묶어 "같은 폴더 재표시면 트리도 이미 그 자리"라고 가정했는데, 트리가 그 폴더에
        // 도달하지 못한 채(자동 펼침 취소·숨김 경로·루트 재구성으로 선택 소실) 남아 있으면 그 가정이
        // 깨져 리스트와 트리가 영영 어긋난다(사용자 보고 — 리스트는 AppData 아래인데 트리는 상위에
        // 멈춤). 종전에는 Show마다 무조건 다시 펼쳐 저절로 복구됐다.
        // 그래서 SyncTreeToFolder는 늘 부르되, **판정을 그 안(TreeReflects)에 둔다** — 이미 그 폴더를
        // 가리키고 있으면 문자열 비교 두 번으로 끝나고 펼침·스크롤은 일어나지 않는다(A323의 이득 유지).
        // EnsureDriveRoots(GetDrives)만 종전 조건 그대로 두어 연속 항해에서 0회를 지킨다 —
        // 루트가 아직 없을 때(트리 최초 구성·열거 실패 후 재시도)만 한 갈래를 더 연다.
        // A333: 루트 재구성은 이제 비동기다(드라이브 열거를 워커로 뺐다 — EnsureDriveRootsAsync).
        // 트리 동기는 반드시 "루트가 선 뒤에" 와야 한다: 루트가 비어 있는 채로 ExpandToFolderAsync가
        // 돌면 시작 노드를 못 찾아 "이 폴더에 대해 트리가 할 일 없음"(_treeFolder = folder)으로
        // 기억해 버려, 그 폴더로는 다시 펼치지 않는다(A324 계약). 그래서 두 갈래로 나눈다 —
        // 재구성이 필요하면 완료 후 동기, 아니면 종전대로 즉시 동기.
        if (!sameView || reopened || FolderTree.RootNodes.Count == 0)
            _ = EnsureDriveRootsThenSyncAsync(folder); // A57 ④ — 드라이브 구성이 바뀌었으면(USB 등) 루트 재구성
        else
            SyncTreeToFolder(folder);
    }

    /// <summary>A333: 루트 재구성 → 트리 동기의 직렬 연결(위 Show의 갈래 하나). 발사 후 망각이지만
    /// 본문이 전부 예외를 삼키므로(EnsureDriveRootsAsync의 catch · SyncTreeToFolder의 조기 반환)
    /// 소비되지 않는 예외가 남지 않는다.
    /// <para>
    /// 늦은 완료 폐기: 종전 동기 호출과 달리 이 사이에 다른 폴더로 Show/항해가 끼어들 수 있다 —
    /// 그때 이 옛 폴더로 트리를 펼치면 트리가 뒤로 되감긴다. 리스트의 현재 폴더(항해 시작 즉시
    /// 갱신되는 단일 원본 — ExplorerPane.CurrentFolder)와 다르면 조용히 접는다. 새 폴더 쪽은
    /// 자기 Show가 자기 갈래로 동기하므로 동기가 통째로 빠지는 일은 없다.
    /// </para></summary>
    private async Task EnsureDriveRootsThenSyncAsync(string folder)
    {
        await EnsureDriveRootsAsync();
        if (_list is { } list && !string.Equals(TrimSep(list.CurrentFolder), TrimSep(folder),
                StringComparison.OrdinalIgnoreCase))
            return;
        SyncTreeToFolder(folder);
    }

    /// <summary>
    /// 표시 여부를 바꾸지 않고 리스트만 만들어 항해시킨다 (A93 — Show에서 분리).
    /// S1에서는 좌 도크가 닫혀 있어도 이 리스트가 폴더 상태의 원본이라, 셸이 중앙 썸네일 뷰를
    /// 채우기 위해 이 경로를 쓴다(결과는 ViewChanged로 돌아온다). extensions를 생략하면
    /// 마지막 모듈 필터 유지 — 썸네일 뷰의 폴더 더블클릭이 이 형태로 온다.
    /// </summary>
    public void NavigateList(string folder, IReadOnlyList<string>? extensions = null)
    {
        if (extensions is not null) _extensions = extensions;
        if (_list is null)
        {
            _list = new ExplorerPane { Settings = Settings }; // 정렬 키 저장(A5)
            _list.ConfigureListOnly();
            // A91(v0.115.0): 주소·필터 줄을 리스트에서 떼어 패널 최상단(트리 위)에 올린다.
            // Child가 비어 있을 때만 붙인다 — 같은 UIElement를 두 부모에 넣으면 죽는다.
            // (FrameworkElement.Parent는 라이브 트리 부착 전 null이라 중복 가드로 못 쓴다:
            //  v0.111.0 COMException 0x800F1000 전례. DetachPathBar도 멱등이라 이중 안전.)
            PathBarHost.Child ??= _list.DetachPathBar();
            _list.FileActivated += path => FileActivated?.Invoke(path);
            _list.FileActivatedNewWindow += path => FileActivatedNewWindow?.Invoke(path);
            _list.ViewChanged += (folder, entries) =>
            {
                ViewChanged?.Invoke(folder, entries); // A93 — 중앙 썸네일 동기화
                SyncTreeToFolder(folder);             // A134 — 상단 트리 동기
            };
            // A243: 폴더 실변경 항해 시작 — 중앙 썸네일 로딩 화면 중계(위 이벤트 주석 참고)
            _list.NavigationStarted += folder => NavigationStarted?.Invoke(folder);
            // A240: 닫힌 채의 선택 변화(재작성이 만드는 소멸 포함)는 발화하지 않는다 — 위
            // SelectionChanged 계약 주석 참고. A241: 조립 완료는 가시성 무관 중계(캐시 데우기).
            _list.SelectionChanged += () =>
            {
                if (IsOpen) SelectionChanged?.Invoke();
            };
            _list.FillCompleted += entries => FillCompleted?.Invoke(entries);
            _list.Notice += ShowTransientNotice; // A94 — 리스트 항목 드랍·클립보드 실패 안내
            _list.ShowHiddenChanged += RebuildTree; // A160 — 트리도 같은 표시 정책으로 다시 만든다
            ListHost.Content = _list;
        }
        _list.NavigateTo(folder, _extensions);
    }

    /// <summary>
    /// 패널 폭(전폭 대비 %) 지정 — 셸이 전 상태 공통 SidebarPercent(25, A116)를 넘긴다.
    /// 내부 별 분할이 셸 도크 컬럼과 같은 비율이어야 사이드바에서 픽셀 단위로 정렬되고,
    /// 경계 버튼 옆 안내 문구(A108 — RestColumn 기준 배치)의 x도 이 분할이 정한다.
    /// ContentInfoOverlay에도 같은 메서드가 있다.
    /// </summary>
    public void SetPanelPercent(double percent)
    {
        PanelColumn.Width = new GridLength(percent, GridUnitType.Star);
        RestColumn.Width = new GridLength(100 - percent, GridUnitType.Star);
    }

    /// <summary>
    /// A317: 패널 바탕(PanelBackdrop)의 불투명도 지정 — 셸이 표시 종착점(ApplyOverlayStates)에서
    /// 모드2·3의 S4는 S4TranslucentOpacity(A316 단일 출처), 그 외 전부는 1.0을 넘긴다.
    /// 바탕만 낮춘다(요소 Opacity — S4CenterBackdrop과 같은 관용구): 리스트·트리·글자는 이 바탕
    /// **위**에 불투명하게 남아 읽힌다. ContentInfoOverlay에도 같은 메서드가 있다.
    /// </summary>
    public void SetBackdropOpacity(double opacity) => PanelBackdrop.Opacity = opacity;

    /// <summary>
    /// 좌 패널 드래그 = 현재 폴더로 이동/복사 (A94 1차, v0.124.0 — A93의 무동작 소비를 실동작으로
    /// 전환. ThumbnailExplorer의 중앙 영역 핸들러와 같은 규칙). 폴더 항목 위는 내부 리스트
    /// (ExplorerPane)의 항목 핸들러가 먼저 Handled로 받아 그 폴더가 대상이 된다.
    /// 리스트가 아직 없으면 None으로 소비만 — 어느 쪽이든 Handled라 창 전체 "열기" 폴백에
    /// 안 넘어간다(OnWindowDrop).
    /// </summary>
    private void OnPanelDragOver(object sender, DragEventArgs e) =>
        ExplorerFileOps.HandleTargetDragOver(e, _list?.CurrentFolder);

    /// <summary>빈 영역·파일 항목·트리 영역 드랍 — 대상 = 현재 폴더 (A94).</summary>
    private async void OnPanelDrop(object sender, DragEventArgs e)
    {
        e.Handled = true; // 창 수준 라우팅과의 이중 처리 방지 (await 전에 동기로 지정해야 유효)
        if (_list?.CurrentFolder is not { Length: > 0 } folder) return;
        var operation = ExplorerFileOps.DecideOperation(e, folder);
        if (operation == DataPackageOperation.None ||
            !e.DataView.Contains(StandardDataFormats.StorageItems))
            return;
        e.AcceptedOperation = operation; // 소스(OS 탐색기 등)에 확정 동작을 알린다

        // A94 3차 — 충돌 대화상자·진행 문구용 UI 문맥(조작 시작 시점의 이 창 기준 캡처).
        // 4차 — 접근 거부(UAC 필요) 안내·관리자 재시작 제안도 같은 문맥으로 간다.
        var ui = new ExplorerFileOps.OpUi(DispatcherQueue, XamlRoot, ShowTransientNotice);
        var move = operation == DataPackageOperation.Move;
        var result = await ExplorerFileOps.TransferDroppedAsync(e.DataView, folder, move, ui);
        NavigateList(folder); // 단일 원본(내부 리스트) 재스캔 — ViewChanged로 중앙 썸네일까지 갱신
        await ExplorerFileOps.ReportAsync(result.Notice(move), result.Denied, ui);
    }

    /// <summary>
    /// 파일 조작 실패 안내(A94): A92 힌트 표시 경로 재사용 — 상태를 비우고 다시 띄워
    /// 같은 문구의 연속 실패에도 다시 보이게 한다(ShowHint의 동일 문구 중복 억제 우회).
    /// </summary>
    private void ShowTransientNotice(string text)
    {
        HideHint();
        ShowHint(text);
    }

    public void Hide()
    {
        Visibility = Visibility.Collapsed;
        HideHint(); // A92 — 다시 열릴 때 안내가 처음부터 다시 보이게 상태를 비운다
    }

    // ---------- 안내 문구 일시 표시 (A92, v0.115.0 — 문구·키 표기는 A107부터 OverlayHints가 단일 출처) ----------
    // ⚠️ ContentInfoOverlay·SidePanelHost(A119)에 같은 상수·필드·메서드(표시 타이밍 장치)가 한 벌씩
    // 더 있다. 문구 문자열은 A107에서 OverlayHints로 모았지만 타이밍 장치는 세 벌 —
    // 한쪽을 고치면 반드시 나머지도 맞출 것. A133(v0.155.0)부터는 **판(다크 반투명 Border) 규격**도
    // 세 벌 공통이다: Background #CC202020 · CornerRadius 4 · Padding 10,6 · 글씨 White ·
    // 요소 Opacity 1(A12 칩과 같은 값 — 출처 VideoPlayerView.xaml StartOverlay).
    // A176: 구 SetState(모드·고정 반영)가 폐지되면서 호출원은 Show(사이드바 안내)와
    // ShowTransientNotice(A94 실패 안내)만 남았다 — 타이밍 장치는 무변경.
    // A108(v0.135.0): 표시 위치가 패널 하단 → 경계 버튼 옆(세로 중앙)으로 이동 — XAML만 바뀌었고
    // 타이밍 장치는 그대로다. PinnedText를 재사용하는 A94 실패 안내(ShowTransientNotice)도
    // 같은 자리에 뜬다(요소 하나 = 위치 하나 — A107 단일화 유지의 의도된 결과).
    // A133: 그래서 A94 실패 안내도 같은 판을 얻는다 — 가독성이 목적이라 의도된 결과다.
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

    // ---------- 디스크 계층 트리 (A57 ④) ----------

    private const int TreeChildLimit = 1000; // 초대형 폴더 보호 — 하위 폴더 노드 상한

    /// <summary>트리 노드 콘텐츠. 기본 항목 템플릿이 ToString()을 표시하므로 Display를 돌려준다.</summary>
    private sealed record FolderNode(string Path, string Display)
    {
        public override string ToString() => Display;
    }

    /// <summary>
    /// 루트(드라이브) 노드를 채운다. 이미 같은 드라이브 구성이면 그대로 두어(펼침 상태 유지),
    /// 탈착 등으로 구성이 바뀌었을 때만 다시 만든다. 드라이브는 이름+종류만 간단 표기.
    /// <para>
    /// <b>A333</b>: 드라이브 열거를 UI 스레드에서 뺐다(CLAUDE.md 1.8 — 주기·블로킹 작업은 워커).
    /// <see cref="DriveInfo.GetDrives"/>와 그 뒤의 <c>DriveType</c>·<c>RootDirectory</c> 조회는
    /// 네트워크 드라이브·미준비 미디어(탈착식·광학)에서 초 단위로 막힐 수 있고, 이 메서드는 좌
    /// 패널 표시 종착점(Show)에서 <b>폴더가 바뀔 때마다</b> 불린다 — 즉 사용자가 폴더를 여는
    /// 바로 그 순간의 UI 스레드였다. 워커에서 (경로, 표기) 쌍까지 다 만들어 오고
    /// (<see cref="ListDriveRoots"/>) UI 스레드는 <b>노드 비교·생성만</b> 한다.
    /// 판정식·재구성 조건·A324의 기억 비우기는 전부 종전 그대로다.
    /// </para>
    /// </summary>
    private async Task EnsureDriveRootsAsync()
    {
        (string Root, string Display)[] drives;
        try
        {
            drives = await Task.Run(ListDriveRoots); // 워커 — 완료 후속부는 UI 스레드로 복귀
        }
        catch
        {
            return; // 드라이브 열거 실패 — 기존 트리 유지
        }

        var current = FolderTree.RootNodes
            .Select(n => (n.Content as FolderNode)?.Path)
            .Where(p => p is not null)
            .ToList();
        if (current.Count == drives.Length &&
            drives.Select(d => d.Root)
                  .All(p => current.Contains(p, StringComparer.OrdinalIgnoreCase)))
            return;

        // A324: 루트를 새로 만들면 선택·펼침이 통째로 사라진다 — 트리는 더 이상 어떤 폴더도
        // 가리키지 않으므로 기억을 비운다(다음 SyncTreeToFolder가 다시 펼친다).
        _treeFolder = null;
        FolderTree.RootNodes.Clear();
        foreach (var drive in drives)
            FolderTree.RootNodes.Add(new TreeViewNode
            {
                Content = new FolderNode(drive.Root, drive.Display),
                HasUnrealizedChildren = true, // 하위는 펼칠 때 로드
            });
    }

    /// <summary>
    /// A333 — 워커 스레드: 드라이브 열거와 표기 조립. 종전 EnsureDriveRoots 안에 있던
    /// <c>GetDrives</c> + 드라이브별 try/continue 루프를 그대로 옮긴 것이다(접근 불가 드라이브는
    /// 조용히 생략 — 같은 규칙). UI 요소를 일절 만지지 않는다.
    /// </summary>
    private static (string Root, string Display)[] ListDriveRoots()
    {
        var result = new List<(string Root, string Display)>();
        foreach (var drive in DriveInfo.GetDrives())
        {
            try
            {
                result.Add((drive.RootDirectory.FullName,
                    $"{drive.Name.TrimEnd('\\')} ({DriveKind(drive.DriveType)})"));
            }
            catch
            {
                // 접근 불가 드라이브는 조용히 생략
            }
        }
        return result.ToArray();
    }

    private static string DriveKind(DriveType type) => type switch
    {
        DriveType.Fixed => "Local Disk",
        DriveType.Removable => "Removable",
        DriveType.Network => "Network",
        DriveType.CDRom => "CD-ROM",
        DriveType.Ram => "RAM Disk",
        _ => "Drive",
    };

    /// <summary>노드 펼침 시점의 지연 로드 — 전체 스캔 없이 그 단계 하위 폴더만 열거한다.</summary>
    private async void OnTreeExpanding(TreeView sender, TreeViewExpandingEventArgs args)
    {
        await LoadChildrenAsync(args.Node);
    }

    /// <summary>
    /// 하위 폴더 노드를 한 단계 채운다. 숨김/시스템 폴더 판정은 탐색기 리스트와 <b>같은 한 벌</b>
    /// (ExplorerListing.ShouldShow) — A160(v0.169.0)에서 여기 있던 인라인 복제(속성 마스크 직접
    /// 비교)를 없앴다. 트리와 리스트가 서로 다른 집합을 보이면 안 되기 때문이다.
    /// 접근 불가 폴더(권한·미준비 드라이브)는 조용히 생략.
    /// HasUnrealizedChildren을 먼저 내려 재진입(자동 펼침과 Expanding 이벤트 중복)을 막는다.
    /// </summary>
    private async Task LoadChildrenAsync(TreeViewNode node)
    {
        if (!node.HasUnrealizedChildren || node.Content is not FolderNode folder) return;
        node.HasUnrealizedChildren = false;

        // A160: 설정은 UI 스레드에서 한 번 읽어 스냅샷 — 아래 Task.Run 안에서 다시 읽지 않는다.
        // 리스트 쪽(ExplorerPane.NavigateToAsync)과 같은 키·같은 기본값이라 두 표면이 늘 일치한다.
        var includeHidden = Settings?.Get(ExplorerPane.ShowHiddenSettingKey, false) ?? false;
        string[] children;
        try
        {
            children = await Task.Run(() =>
                new DirectoryInfo(folder.Path).EnumerateDirectories()
                    .Where(d => ExplorerListing.ShouldShow(d.Attributes, includeHidden))
                    .OrderBy(d => d.Name, StringComparer.OrdinalIgnoreCase)
                    .Take(TreeChildLimit)
                    .Select(d => d.FullName)
                    .ToArray());
        }
        catch
        {
            return; // 권한 등으로 못 읽는 폴더 — 빈 채로 둔다(펼침 표시만 사라짐)
        }

        foreach (var path in children)
            node.Children.Add(new TreeViewNode
            {
                Content = new FolderNode(path, Path.GetFileName(path)),
                HasUnrealizedChildren = true, // 실제 하위 유무는 펼칠 때 판명 — 미리 조사하지 않는다
            });
    }

    /// <summary>
    /// 현재 폴더까지 경로를 자동으로 펼치고 그 노드를 선택·스크롤한다(A57 ④ Show() 시).
    /// 각 단계는 LoadChildrenAsync로 지연 로드하며, 숨김 폴더 등으로 경로가 끊기면 거기까지만 간다.
    /// </summary>
    private async Task ExpandToFolderAsync(string folder)
    {
        var seq = ++_expandSeq;
        string full;
        try
        {
            full = Path.GetFullPath(folder);
        }
        catch
        {
            return;
        }

        var node = FolderTree.RootNodes.FirstOrDefault(n =>
            n.Content is FolderNode f &&
            full.StartsWith(f.Path, StringComparison.OrdinalIgnoreCase));
        if (node is null)
        {
            // 다른 볼륨(UNC 등) — 트리는 드라이브만 안다. A324: 이 폴더에 대해 트리가 할 수 있는
            // 일은 없다는 사실도 "가리키는 상태"로 기억한다(매 항해 헛걸음 방지).
            _treeFolder = folder;
            return;
        }

        while (node.Content is FolderNode current &&
               !string.Equals(TrimSep(current.Path), TrimSep(full), StringComparison.OrdinalIgnoreCase))
        {
            await LoadChildrenAsync(node);
            if (seq != _expandSeq) return; // 그새 다른 폴더로 Show()됨(기억은 갱신하지 않는다 — 미완주)
            node.IsExpanded = true;

            var next = node.Children.FirstOrDefault(c =>
                c.Content is FolderNode f &&
                (string.Equals(TrimSep(f.Path), TrimSep(full), StringComparison.OrdinalIgnoreCase) ||
                 full.StartsWith(TrimSep(f.Path) + Path.DirectorySeparatorChar,
                     StringComparison.OrdinalIgnoreCase)));
            // A324: 표시 정책(A160)에 걸려 없는 길목이면 그 한 칸만 예외로 끼운다 — 아래 주석 참고.
            next ??= AddPathNode(node, full);
            if (next is null) break; // 그래도 없으면(권한·소실 등) 도달한 지점까지만 선택
            node = next;
        }

        FolderTree.SelectedNode = node;
        ScrollTreeTo(node);
        // A324: 완주(또는 더 갈 수 없는 지점까지 도달)했다 — 이 폴더에 대한 동기는 끝났다.
        _treeFolder = folder;
    }

    /// <summary>
    /// A324: 목표 폴더로 가는 <b>길목</b>의 폴더를 트리에 한 칸만 끼워 넣는다 — 하위 로드
    /// (LoadChildrenAsync)가 리스트와 같은 표시 정책(A160 ExplorerListing.ShouldShow)으로 거르는
    /// 탓에, 경로 중간에 숨김·시스템 폴더가 하나라도 있으면(대표적으로 %AppData%) 트리가 거기서
    /// 멈춰 하단 리스트와 어긋났다(사용자 보고의 C:\Users\...\AppData\... 경로).
    /// 표시 정책 자체는 그대로 둔다 — 예외는 "지금 보고 있는 폴더로 가는 길"뿐이라, 형제 숨김
    /// 폴더는 여전히 보이지 않는다(트리와 리스트가 서로 다른 <b>집합</b>을 보이는 A160의 최악
    /// 회귀는 나지 않는다: 리스트에도 그 폴더의 내용이 떠 있다). OS 탐색기도 현재 위치는 트리에
    /// 드러낸다.
    /// 경로 문자열은 <paramref name="full"/>에서 잘라 쓴다(Path.Combine은 "C:" 같은 드라이브
    /// 상대 경로를 만들 수 있다). 이름 순서(OrdinalIgnoreCase)를 지켜 끼운다 — 나머지 형제가
    /// 그 순서로 들어 있다. 부모의 HasUnrealizedChildren은 호출 시점에 이미 내려가 있어
    /// (LoadChildrenAsync 직후) 나중에 같은 자식이 한 번 더 생기는 일은 없다.
    /// </summary>
    private static TreeViewNode? AddPathNode(TreeViewNode parent, string full)
    {
        if (parent.Content is not FolderNode current) return null;
        var baseDir = TrimSep(current.Path);
        if (full.Length <= baseDir.Length + 1) return null;
        var rest = full[(baseDir.Length + 1)..];
        var cut = rest.IndexOf(Path.DirectorySeparatorChar);
        var name = cut < 0 ? rest : rest[..cut];
        if (name.Length == 0) return null;
        var path = full[..(baseDir.Length + 1 + name.Length)];
        if (!Directory.Exists(path)) return null; // 소실·권한 — 없는 노드를 만들지 않는다

        var index = 0;
        while (index < parent.Children.Count &&
               parent.Children[index].Content is FolderNode sibling &&
               string.Compare(sibling.Display, name, StringComparison.OrdinalIgnoreCase) < 0)
            index++;

        var node = new TreeViewNode
        {
            Content = new FolderNode(path, name),
            HasUnrealizedChildren = true, // 그 아래는 종전대로 펼칠 때 로드(표시 정책도 종전대로)
        };
        parent.Children.Insert(index, node);
        return node;
    }

    /// <summary>
    /// 리스트가 다른 폴더로 옮겨갔을 때 상단 트리를 따라오게 한다 (A134 — 종전에는 Show()만
    /// ExpandToFolderAsync를 불러, 하단 리스트·중앙 썸네일에서 폴더로 들어가도 트리가 그대로였다).
    /// 배선은 NavigateList의 ViewChanged 람다 하나 — 리스트 항해의 모든 경로가 그리로 모인다.
    /// 닫혀 있으면 하지 않는다: 보이지 않는 트리를 펼칠 이유가 없고, 다시 열릴 때 Show()가
    /// 어차피 그 폴더로 ExpandToFolderAsync를 부른다(EnsureDriveRoots도 Show() 몫 — 여기서는
    /// IsOpen이므로 루트가 이미 만들어져 있다).
    /// 이미 그 폴더를 가리키고 있으면 조기 반환한다(A324 TreeReflects — 종전 "선택 노드가 같은
    /// 폴더면"의 확장) — ViewChanged는 폴더 변경 통지가 아니라 "표시 목록 재작성" 통지라
    /// 감시 재스캔(A94 5차)·정렬(A5)·필터(A7) 변경으로도 같은 폴더가 계속 돌아온다. 거르지 않으면
    /// ScrollTreeTo가 반복돼 트리가 튄다.
    /// A324: 표시 종착점(Show)도 이제 조건 없이 여기로 들어온다 — 동기 여부의 판정은 이 한 곳뿐이다.
    /// 재진입(펼치는 도중 다른 폴더로 항해)은 기존 _expandSeq가 막는다.
    /// 트리 선택 재대입이 항해를 되부르지 않는지: 트리의 항해 트리거는 ItemInvoked
    /// (OnTreeItemInvoked) 하나이고 SelectionChanged 구독이 없다 — 루프가 성립하지 않는다.
    /// </summary>
    private void SyncTreeToFolder(string folder)
    {
        if (!IsOpen) return;
        if (TreeReflects(folder)) return;
        _ = ExpandToFolderAsync(folder);
    }

    /// <summary>
    /// A324: 트리가 이미 이 폴더를 가리키고 있는가 — 동기 여부의 <b>단일 판정</b>이다
    /// (호출부는 SyncTreeToFolder 하나. Show의 "같은 폴더인가" 가드를 대신한다).
    /// ⓐ 자동 펼침이 완주해 기억해 둔 폴더와 같거나(_treeFolder — 숨김·UNC 등으로 끝까지 가지
    /// 못한 경우도 "여기까지가 최선"으로 기억된다) ⓑ 선택 노드가 바로 그 폴더면 참이다.
    /// 참이면 아무 일도 하지 않는다 — 다시 펼치거나 스크롤하면 그게 A323이 없앤 트리 튐이다.
    /// </summary>
    private bool TreeReflects(string folder) =>
        (_treeFolder is { } known &&
         string.Equals(TrimSep(known), TrimSep(folder), StringComparison.OrdinalIgnoreCase)) ||
        (FolderTree.SelectedNode?.Content is FolderNode selected &&
         string.Equals(TrimSep(selected.Path), TrimSep(folder), StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// 숨김·시스템 표시(A160)가 바뀌면 트리를 루트부터 다시 만든다 — 내부 리스트의
    /// ShowHiddenChanged가 부른다(배선은 NavigateList의 리스트 생성 블록 한 곳).
    /// 제자리 갱신이 불가능해서 통째로 다시 만든다: 이미 펼쳐 둔 노드는 HasUnrealizedChildren이
    /// 내려가 있어 LoadChildrenAsync가 조기 반환하므로, 루트를 비우는 것 말고는 재열거를
    /// 강제할 방법이 없다. 비우면 EnsureDriveRoots가 드라이브 구성 비교에서 불일치를 보고 새로
    /// 만들고(0개 ≠ 실제 드라이브 수), 이어서 리스트와 같은 폴더까지 다시 펼친다.
    /// 닫혀 있어도 한다: 다시 열릴 때의 Show()는 루트가 그대로면 EnsureDriveRoots가 조기 반환하고
    /// ExpandToFolderAsync도 이미 로드된 노드를 다시 열거하지 않아 옛 집합이 그대로 남는다.
    /// 재진입(펼치는 도중 다시 토글)은 ExpandToFolderAsync의 _expandSeq가 막는다.
    /// </summary>
    private void RebuildTree()
    {
        _treeFolder = null; // A324: 트리를 버린다 — 가리키던 폴더 기억도 함께(드라이브 열거 실패 대비 명시)
        FolderTree.RootNodes.Clear();
        // A333: 루트 재구성이 비동기가 되면서 펼침도 그 뒤로 밀린다(Show와 같은 사정 — 루트가
        // 서기 전에 펼치면 A324 기억이 잘못 확정된다). 여기서 SyncTreeToFolder를 쓰지 않는 이유는
        // 종전과 같다: 닫혀 있어도 다시 만들어야 한다(그쪽은 !IsOpen 조기 반환).
        _ = RebuildTreeAsync();
    }

    /// <summary>A333: RebuildTree의 비동기 본체 — 루트 재구성 완료 후 현재 폴더까지 다시 펼친다.</summary>
    private async Task RebuildTreeAsync()
    {
        await EnsureDriveRootsAsync();
        if (_list?.CurrentFolder is { Length: > 0 } folder) _ = ExpandToFolderAsync(folder);
    }

    private static string TrimSep(string path) =>
        path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

    /// <summary>
    /// 선택 노드가 보이게 스크롤한다. TreeView에 공개 API가 없어 내부 리스트(TreeViewList는
    /// ListView 파생)를 비주얼 트리에서 찾아 ScrollIntoView를 부른다 — 못 찾으면 조용히 넘어간다.
    /// </summary>
    private void ScrollTreeTo(TreeViewNode node)
    {
        FolderTree.UpdateLayout(); // 방금 추가한 노드의 컨테이너 실체화
        if (FindTreeList(FolderTree) is { } list) list.ScrollIntoView(node);
    }

    private static ListView? FindTreeList(DependencyObject root)
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is ListView list) return list;
            if (FindTreeList(child) is { } found) return found;
        }
        return null;
    }

    /// <summary>트리에서 드라이브/폴더 선택 → 하단 리스트를 그 폴더로 이동(모듈 필터 유지).</summary>
    private void OnTreeItemInvoked(TreeView sender, TreeViewItemInvokedEventArgs args)
    {
        var content = args.InvokedItem is TreeViewNode node ? node.Content : args.InvokedItem;
        if (content is FolderNode folder && _list is not null)
        {
            // A324: 사용자가 트리에서 고른 자리가 곧 "트리가 가리키는 폴더"다 — 기억을 여기서
            // 맞춰 두지 않으면 옛 값이 남아, 나중에 그 옛 폴더로 Show될 때 동기가 통째로 걸러진다.
            _treeFolder = folder.Path;
            _list.NavigateTo(folder.Path, _extensions);
        }
    }
}
