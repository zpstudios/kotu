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
/// 폴더 스캔·썸네일 추출은 페인 전용 워커(A42)에서 돌고, UI 스레드는 결과 반영만 한다.
/// 외부 변경(다른 앱·OS 탐색기)은 폴더 감시(A94 5차 — FileSystemWatcher + 디바운스)가
/// 같은 재스캔 경로로 반영한다(아래 "폴더 감시" 절).
/// </summary>
public sealed partial class ExplorerPane : UserControl
{
    private const int ThumbnailLimit = 300;   // 썸네일 로드 상한 (초대형 폴더 보호)
    private const int DoubleClickMs = 500;

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
    /// 파일 조작(드랍 이동/복사·붙여넣기 — A94) 실패 안내 문구. 이 페인에는 상태 표시 줄이 없어
    /// 호스트(FileListOverlay)가 받아 A92류 일시 문구로 띄운다. 성공은 조용(뷰 갱신이 피드백).
    /// </summary>
    internal event Action<string>? Notice;

    /// <summary>현재 폴더 경로 (A94 — 호스트의 패널 드랍·붙여넣기 대상). 항해 전이면 빈 문자열.</summary>
    internal string CurrentFolder => _folder;

    // "name"/"size"/"modified"/"created"(A117, v0.136.0) — SortKey.ToString().ToLowerInvariant()와 수동 동기.
    // 모르는 값(구 버전·손편집)은 이름순으로 폴백한다(아래 switch의 _ 분기).
    private const string SortSettingKey = "explorer.sort";

    private IReadOnlyList<string> _extensions = [];
    private string _folder = string.Empty;
    private int _loadSeq;                     // 빠른 연속 탐색 시 늦은 결과 폐기
    private (string Path, DateTime At)? _lastClick;
    private (string Path, DateTime At)? _lastActivation; // A85: ItemClick 쌍·DoubleTapped 겹침을 1회로 억제
    private (string Path, DateTime At)? _lastPress;      // A131: 원시 눌림 쌍 — 항목 재구축을 건너 살아남는 최후 폴백
    private ModuleWorker? _worker;            // 스캔·썸네일 전용 — 페인별 분리(A42 정책)
    private IReadOnlyList<ExplorerListing.Entry> _entries = []; // 마지막 스캔 결과 — 정렬 변경 시 재스캔 없이 재배치(A5)
    private ExplorerListing.SortKey _sortKey = ExplorerListing.SortKey.Name;
    private ISettingsService? _settings;

    /// <summary>정렬 키 저장용(A5). 셸(MainWindow)이 페인 생성 직후 주입한다 — 없어도 동작(기본 이름순).</summary>
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
                _ => ExplorerListing.SortKey.Name,
            };
            SyncSortChecks();
        }
    }

    /// <summary>지연 생성: Unloaded로 정리된 뒤 다시 로드돼도(좌 리스트 오버레이 재오픈) 되살아난다.</summary>
    private ModuleWorker Worker => _worker ??= new ModuleWorker("KOTU explorer worker");

    public ExplorerPane()
    {
        InitializeComponent();
        SyncSortChecks();
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
        // A157: 선택 → 체크 동기. 체크는 선택의 시각화일 뿐이라 집합은 ListView 선택 하나뿐이고,
        // 동기도 이 한 방향만 필요하다(반대 방향 = 체크박스 Click이 선택을 토글한다).
        // 리스트에만 건다 — 체크박스는 리스트 행(MakeListItem)에만 있다.
        // 해제 구독을 두지 않는 이유: 자기 자식 컨트롤의 인스턴스 이벤트라 페인과 수명이 같다
        // (Loaded/Unloaded 해제 규칙은 ExplorerFileOps 같은 '정적' 이벤트에만 적용된다).
        ListPane.SelectionChanged += OnListSelectionChanged;
        // A94 6차: 빈 영역(항목이 아닌 곳) 우클릭 메뉴 — New folder / Paste / Refresh.
        // 항목 메뉴와의 이중 발화는 ContextFlyout 규칙이 원천 차단한다: 컨텍스트 요청은 원본
        // 요소에서 위로 버블링하며 **가장 안쪽의 ContextFlyout 하나만** 떠서 요청을 소비하므로,
        // 항목 위 우클릭은 항목 컨테이너(AttachContextMenu)가 받고 여기까지 오지 않는다.
        // 빈 영역이 히트 테스트되도록 XAML에서 Background=Transparent를 준 것과 한 쌍이다.
        IconGrid.ContextFlyout = MakeSurfaceMenu(IconGrid);
        ListPane.ContextFlyout = MakeSurfaceMenu(ListPane);
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
            TearDownWatch(); // 감시 이벤트 전부 해제 + Dispose + 디바운스 정지 — 창 통째 누수 방지
            _worker?.Dispose(); // 진행 중 작업은 워커가 마저 끝내고 스레드 종료
            _worker = null;
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

    // ---------- 정렬 (A5) ----------

    /// <summary>정렬 플라이아웃의 체크 상태를 _sortKey에 맞춘다.</summary>
    private void SyncSortChecks()
    {
        SortByName.IsChecked = _sortKey == ExplorerListing.SortKey.Name;
        SortBySize.IsChecked = _sortKey == ExplorerListing.SortKey.Size;
        SortByModified.IsChecked = _sortKey == ExplorerListing.SortKey.Modified;
        SortByCreated.IsChecked = _sortKey == ExplorerListing.SortKey.Created; // A117
    }

    private void OnSortChanged(object sender, RoutedEventArgs e)
    {
        var key = ReferenceEquals(sender, SortBySize) ? ExplorerListing.SortKey.Size
                : ReferenceEquals(sender, SortByModified) ? ExplorerListing.SortKey.Modified
                : ReferenceEquals(sender, SortByCreated) ? ExplorerListing.SortKey.Created // A117
                : ExplorerListing.SortKey.Name;
        if (key == _sortKey) return;

        _sortKey = key;
        SyncSortChecks();
        _settings?.Set(SortSettingKey, key.ToString().ToLowerInvariant());
        _settings?.Save();
        RefreshView();
    }

    /// <summary>
    /// 캐시된 스캔 결과를 현재 정렬·필터로 재배치해 다시 그린다. 재스캔 없음.
    /// 항목이 새로 만들어지므로 썸네일도 다시 채운다(셸 썸네일 캐시라 재추출은 싸다).
    /// </summary>
    private void RefreshView()
    {
        var seq = ++_loadSeq; // 돌고 있던 길이·썸네일 루프 중단
        var arranged = ExplorerListing.Arrange(_entries, _sortKey, _hiddenExts);
        Fill(arranged);
        ViewChanged?.Invoke(_folder, arranged); // A93 — 중앙 썸네일 뷰가 같은 목록을 받아 그린다
        _ = LoadDetailsAsync(seq);
    }

    // ---------- 파일 종류 필터 (A7) ----------

    private readonly HashSet<string> _hiddenExts = new(StringComparer.OrdinalIgnoreCase);
    private IReadOnlyList<string> _filterBuiltFor = []; // 마지막으로 플라이아웃을 만든 확장자 목록
    private Brush? _filterDefaultBrush;                 // 아이콘 원래 색 (활성 표시 해제용)

    /// <summary>
    /// 담당 확장자 목록으로 필터 플라이아웃을 만든다. 목록이 그대로면 재사용(체크 상태 유지),
    /// 모듈 전환 등으로 바뀌면 새로 만들고 필터를 초기화한다. 필터는 저장하지 않는다(세션 한정).
    /// </summary>
    private void EnsureFilterFlyout()
    {
        if (_extensions.SequenceEqual(_filterBuiltFor)) return;
        _filterBuiltFor = _extensions;
        _hiddenExts.Clear();

        var flyout = new MenuFlyout { Placement = FlyoutPlacementMode.BottomEdgeAlignedRight };
        foreach (var ext in _extensions)
        {
            var toggle = new ToggleMenuFlyoutItem { Text = ext, IsChecked = true };
            toggle.Click += (_, _) =>
            {
                if (toggle.IsChecked) _hiddenExts.Remove(ext);
                else _hiddenExts.Add(ext);
                UpdateFilterVisual();
                RefreshView();
            };
            flyout.Items.Add(toggle);
        }
        flyout.Items.Add(new MenuFlyoutSeparator());
        var showAll = new MenuFlyoutItem { Text = "Show all" };
        showAll.Click += (_, _) =>
        {
            if (_hiddenExts.Count == 0) return;
            _hiddenExts.Clear();
            foreach (var i in flyout.Items)
                if (i is ToggleMenuFlyoutItem t) t.IsChecked = true;
            UpdateFilterVisual();
            RefreshView();
        };
        flyout.Items.Add(showAll);

        FilterButton.Flyout = flyout;
        UpdateFilterVisual();
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

    /// <summary>가벼운 길이 텍스트를 먼저 채우고, 무거운 썸네일을 이어서 채운다(같은 워커 직렬 큐).</summary>
    private async Task LoadDetailsAsync(int seq)
    {
        await LoadDurationsAsync(seq);
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
    /// x:Name 필드(UpButton·PathText·FilterButton·SortButton…)는 부모에서 떼어도 그대로
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
        _folder = folder;
        PathText.Text = folder;
        ToolTipService.SetToolTip(PathText, folder); // 잘려도 전체 경로 확인 가능(A8)
        UpButton.IsEnabled = Directory.GetParent(folder) is not null;
        EnsureWatch(folder); // A94 5차 — 폴더 전환 즉시 재대상(스캔 완료 전의 변경도 디바운스로 잡힌다)

        var seq = ++_loadSeq;
        IReadOnlyList<ExplorerListing.Entry> entries;
        try
        {
            entries = await Worker.Run(_ => ExplorerListing.List(folder, extensions));
        }
        catch (OperationCanceledException)
        {
            return; // 페인이 내려가며 워커가 닫힘 — 그릴 곳도 없다
        }
        catch (Exception ex)
        {
            if (seq != _loadSeq) return;
            IconGrid.Items.Clear();
            ListPane.Items.Clear();
            EmptyText.Text = "Cannot read this folder: " + ex.Message;
            EmptyText.Visibility = Visibility.Visible;
            ViewChanged?.Invoke(folder, []); // A93 — 썸네일 뷰도 옛 폴더 목록을 남기지 않는다
            return;
        }

        if (seq != _loadSeq) return; // 그새 다른 폴더로 이동함

        _entries = entries;
        RefreshView();
    }

    /// <summary>
    /// 표시 목록을 항목 컨테이너로 다시 만들어 채운다(ItemsSource·DataTemplate 없음 — 구조 규칙).
    /// A157 유의: 체크는 선택의 시각화라 별도 저장소가 없다 — 이 재생성(폴더 감시 400ms 재스캔
    /// 포함)이 돌면 선택과 함께 체크도 사라진다. 재스캔 후 선택 복원은 별도 설계가 필요해
    /// 이 배치의 범위 밖이다(등재 후보).
    /// </summary>
    private void Fill(IReadOnlyList<ExplorerListing.Entry> entries)
    {
        IconGrid.Items.Clear();
        ListPane.Items.Clear();

        foreach (var entry in entries)
        {
            if (IconGrid.Visibility == Visibility.Visible)
                IconGrid.Items.Add(MakeGridItem(entry));
            ListPane.Items.Add(MakeListItem(entry));
        }

        EmptyText.Text = "No matching files here";
        EmptyText.Visibility = entries.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>
    /// 항목 우클릭 메뉴 (A94 2차 신설 → 6차 확장). 순서는 탐색기 관례 근사:
    /// 파일 = "Open in new instance"(A24) → 구분선 → Cut·Copy → 구분선 → Rename·Delete,
    /// 폴더 = Cut·Copy·**Paste(대상 = 그 폴더)** → 구분선 → Rename·Delete.
    /// Delete·Cut·Copy 대상은 드래그와 같은 규칙 — 그 항목이 선택에 포함돼 있으면 선택 전부,
    /// 아니면 그 항목 하나(PathsForDrag 재사용).
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
    /// 빈 영역 우클릭 메뉴 (A94 6차): New folder / Paste / Refresh — 셋 다 기존 경로 재사용이다
    /// (Ctrl+Shift+N의 CreateFolderThenRenameAsync = 생성 후 이름 편집 진입까지 · 현재 폴더
    /// 붙여넣기 · 조작 후 재스캔 RefreshAfterFileOp). 표면(그리드·리스트)마다 한 벌씩 만든다 —
    /// 새 폴더 편집 진입이 자기 owner의 컨테이너를 찾아야 하기 때문.
    /// 활성 판정은 메뉴가 열릴 때: 아직 항해 전이면(폴더 미정) 셋 다 비활성, Paste는 클립보드에
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

    /// <summary>2줄째 상세 TextBlock의 조회 키 (A156) — 크기·길이·날짜를 한 줄로 합쳐 담는다.</summary>
    private const string ItemDetailBlockName = "ExplorerItemDetail";

    /// <summary>선택 체크박스의 조회 키 (A157) — 선택 동기가 항목마다 이 이름으로 찾는다.</summary>
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

    /// <summary>항목 콘텐츠 패널의 선택 체크박스 (A157) — FindItemBlock과 같은 이름 기반 규칙.</summary>
    private static CheckBox? FindItemCheckBox(object item) =>
        item is ContentControl { Content: Panel panel }
            ? panel.Children.OfType<CheckBox>().FirstOrDefault(c => c.Name == ItemCheckBoxName)
            : null;

    /// <summary>
    /// 리스트 행 2줄째 텍스트 (A156). 순서 확정: 크기 · [길이] · Created · Modified.
    /// 구분자는 저장소 관용구 "  ·  "(ImageViewerView.BuildMetaText와 같은 조립)이고,
    /// 빈 조각은 건너뛴다 = 구분자만 남는 "  ·    ·  " 모양이 생기지 않는다.
    /// 폴더는 크기 조각을 넣지 않는다(종전 리스트 행의 규칙 승계).
    /// 날짜는 시각 없이 yyyy-MM-dd — 이 줄이 폭 25% 사이드바에 두 날짜를 담아야 한다.
    /// 문화권 인자 없이 쓰는 것은 저장소 표시 관용구 그대로다(ImageViewerView·ArchiveView 동일).
    /// 크기·날짜는 빈 문자열이 될 수 없어(FormatSize는 최소 "0 B") 조각 가드가 필요 없고,
    /// 길이만 비어 올 수 있어 그것만 가드한다.
    /// ※ 길이(duration) 조각은 A155가 해상도 표기로 확장할 자리다 — 뒤 배치는 이 헬퍼의 조각
    /// 목록에 한 줄을 더하는 것으로 끝나야 한다(호출부는 이미 이 헬퍼만 부른다).
    /// </summary>
    private static string BuildDetailText(ExplorerListing.Entry entry, string duration)
    {
        var parts = new List<string>();
        if (!entry.IsFolder) parts.Add(ExplorerListing.FormatSize(entry.Size));
        if (duration.Length > 0) parts.Add(duration);
        parts.Add(entry.Created.ToString("yyyy-MM-dd"));
        parts.Add(entry.Modified.ToString("yyyy-MM-dd"));
        return string.Join("  ·  ", parts);
    }

    /// <summary>
    /// 리스트 행 툴팁 (A156): 파일명 + 라벨 붙은 상세를 줄 단위로 쌓는다.
    /// 2줄 레이아웃의 상세 줄에는 라벨이 없어 Created와 Modified를 눈으로 구분할 수 없다 —
    /// 그 구분을 툴팁이 맡는다(조각 선택 규칙은 BuildDetailText와 같다).
    /// </summary>
    private static string BuildTooltipText(ExplorerListing.Entry entry, string duration)
    {
        var lines = new List<string> { entry.Name };
        if (!entry.IsFolder) lines.Add("Size: " + ExplorerListing.FormatSize(entry.Size));
        if (duration.Length > 0) lines.Add("Length: " + duration);
        lines.Add("Created: " + entry.Created.ToString("yyyy-MM-dd"));
        lines.Add("Modified: " + entry.Modified.ToString("yyyy-MM-dd"));
        return string.Join("\n", lines);
    }

    /// <summary>
    /// 상세 줄과 툴팁을 한 벌로 (다시) 채운다 (A156) — 생성 시점(MakeListItem)과 길이 지연 로드
    /// 도착 시점(LoadDurationsAsync)이 같은 조립을 쓰게 하는 단일 깔때기.
    /// </summary>
    private static void ApplyDetail(ListViewItem item, ExplorerListing.Entry entry, string duration)
    {
        if (FindItemBlock(item, ItemDetailBlockName) is { } detail)
            detail.Text = BuildDetailText(entry, duration);
        if (item.Content is UIElement row)
            ToolTipService.SetToolTip(row, BuildTooltipText(entry, duration));
    }

    /// <summary>
    /// 리스트 행 (A156 — 2줄): 1줄 = 아이콘 + 이름, 2줄 = 크기·[길이]·Created·Modified 한 줄,
    /// 우측 끝 = 선택 체크박스(A157). 길이는 지연 로드(A6)라 처음에는 빠진 채 조립되고,
    /// 도착하면 상세 줄을 통째로 다시 만든다.
    /// 루트는 **평평한 Grid 하나**다(중첩 패널 금지) — 이름변경(ExplorerRenameBox.Begin)이
    /// host 패널에 편집 상자를 끼우고 Grid.SetRow/SetColumn으로 이름 자리에 앉히기 때문.
    /// 행 높이는 지정하지 않는다: 2줄 콘텐츠(12px + 11px 약 31px)가 WinUI 기본
    /// ListViewItemMinHeight 안에 들어갈 것으로 보고 명시 Height/MinHeight 없이 간다.
    /// 실기기에서 행이 커지면 복구 지점은 이 컨테이너에 MinHeight 한 줄을 주는 것이다
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
            FontSize = 11,
            Opacity = 0.6,
            VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis,
        };
        Grid.SetColumn(detail, 1);
        Grid.SetRow(detail, 1);

        // A157: 체크 = 선택의 시각화(집합은 ListView 선택 하나뿐 — SelectionMode는 Extended 그대로다.
        // Multiple로 바꾸면 A94의 Ctrl/Shift 관례와 PathsForDrag 계약이 그 위에 서 있어 깨진다).
        // 콘텐츠 '안'에 두는 이유 = 잘라내기 흐림(ExplorerFileOps.ApplyCutMark)이 SelectorItem.Content
        // 루트의 Opacity를 만지므로, 밖에 두면 잘라낸 항목에서 체크만 또렷하게 남는다. 잘라내기 중
        // 체크도 함께 0.5로 흐려지는 것은 수용한다(항목 전체가 흐려지는 탐색기 모양).
        // 기본 치수(MinWidth·Padding·Margin)를 0으로 눌러야 2줄 행 높이를 체크박스가 먹지 않는다.
        var check = new CheckBox
        {
            Name = ItemCheckBoxName,
            Content = null,
            IsChecked = false, // 생성 시점은 항상 미선택 — Fill이 항목을 새로 만들면 선택도 비어 있다(일관)
            MinWidth = 0,
            Padding = new Thickness(0),
            Margin = new Thickness(0),
            VerticalAlignment = VerticalAlignment.Center,
        };
        Grid.SetColumn(check, 2);
        Grid.SetRowSpan(check, 2);
        check.Click += OnItemCheckClick; // Checked/Unchecked는 구독 금지 — 근거는 OnItemCheckClick 주석

        row.Children.Add(icon);
        row.Children.Add(name);
        row.Children.Add(detail);
        row.Children.Add(check);

        var item = new ListViewItem { Content = row, Tag = entry };
        ApplyDetail(item, entry, string.Empty); // 상세 줄 + 툴팁 초판(길이는 아직 도착 전)
        ExplorerFileOps.ApplyCutMark(item); // A94 4차 — 잘라내기 중인 경로면 처음부터 반투명
        AttachContextMenu(item, entry, ListPane); // A24 + A94 2차(Rename·Delete)
        AttachDragDrop(item, entry, ListPane); // A94 — 드래그 아웃 + 폴더 항목 드랍
        item.IsDoubleTapEnabled = true; // A85 — 압축 모듈 내부 리스트(ArchiveView)와 같은 명시
        item.DoubleTapped += OnItemDoubleTapped; // A85 — 더블클릭 열기의 기본 경로
        return item;
    }

    /// <summary>재생 길이 캐시(A6): 경로→(수정시각, 표시 텍스트). 수정시각이 다르면 무효.</summary>
    private readonly Dictionary<string, (DateTime Modified, string Text)> _durationCache =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>재생 길이를 물어볼 파일인지 — 비디오(영상)·오디오(음악) 모듈 담당 확장자 기준(A6, A10 분리 반영).</summary>
    private static bool IsMediaFile(string name) =>
        ExplorerListing.MatchesExtension(name, KOTU.Module.Video.VideoModule.Extensions) ||
        ExplorerListing.MatchesExtension(name, KOTU.Module.Audio.AudioModule.Extensions);

    /// <summary>
    /// 미디어 파일의 재생 길이를 리스트 행 2줄째 상세 줄에 합쳐 넣는다(A6 → A156).
    /// 셸 속성(System.Media.Duration) 읽기는 워커에서, UI는 텍스트 반영만.
    /// 정렬·필터 재그리기는 캐시가 흡수한다(수정시각 일치 시 재조회 없음).
    /// </summary>
    private async Task LoadDurationsAsync(int seq)
    {
        var items = ListPane.Items.ToList(); // 스냅샷 — await 중 컬렉션 변경 대비
        foreach (var obj in items)
        {
            if (seq != _loadSeq) return;
            if (obj is not ListViewItem { Tag: ExplorerListing.Entry { IsFolder: false } entry } item) continue;
            if (!IsMediaFile(entry.Name)) continue;

            string text;
            if (_durationCache.TryGetValue(entry.Path, out var hit) && hit.Modified == entry.Modified)
            {
                text = hit.Text;
            }
            else
            {
                try
                {
                    var ticks = await Worker.Run(_ => FetchDurationTicks(entry.Path));
                    if (seq != _loadSeq) return;
                    text = ticks > 0
                        ? ExplorerListing.FormatDuration(TimeSpan.FromTicks(ticks))
                        : string.Empty;
                }
                catch (OperationCanceledException)
                {
                    return; // 페인이 내려가며 워커가 닫힘
                }
                catch
                {
                    continue; // 속성을 못 읽는 파일은 빈 칸 유지
                }
                if (_durationCache.Count > 4000) _durationCache.Clear(); // 장시간 세션 폭주 방지
                _durationCache[entry.Path] = (entry.Modified, text);
            }

            if (text.Length == 0) continue;
            // A156: 길이는 더 이상 독립 칸이 아니라 2줄째 상세 줄의 한 조각이다 — 대입이 아니라
            // 그 항목의 상세 줄과 툴팁을 통째로 다시 조립한다(조각 순서는 BuildDetailText가 쥔다).
            ApplyDetail(item, entry, text);
        }
    }

    /// <summary>워커 스레드: 셸 미디어 길이 속성(100ns 단위 = TimeSpan 틱)을 읽는다. 없으면 0.</summary>
    private static long FetchDurationTicks(string path)
    {
        var file = StorageFile.GetFileFromPathAsync(path).AsTask().GetAwaiter().GetResult();
        var props = file.Properties.RetrievePropertiesAsync(["System.Media.Duration"])
            .AsTask().GetAwaiter().GetResult();
        return props.TryGetValue("System.Media.Duration", out var v) && v is ulong u ? (long)u : 0L;
    }

    /// <summary>
    /// 파일 썸네일을 채운다(그리드 타일의 글리프를 이미지로 교체).
    /// 추출(셸 API 호출·스트림 읽기)은 워커에서 하고 UI 스레드는 비트맵 표시만 한다(A42).
    /// 항목마다 느릴 수 있으므로 상한을 두고, 폴더 이동 시 중단한다.
    /// </summary>
    private async Task LoadThumbnailsAsync(int seq)
    {
        var loaded = 0;
        // 스냅샷 순회: await 중 NavigateTo가 Items를 비우면 라이브 컬렉션 순회는 깨진다.
        var items = IconGrid.Items.ToList();
        foreach (var obj in items)
        {
            if (seq != _loadSeq || loaded >= ThumbnailLimit) return;
            if (obj is not GridViewItem { Tag: ExplorerListing.Entry { IsFolder: false } entry } item) continue;

            try
            {
                var png = await Worker.Run(_ => FetchThumbnail(entry.Path));
                if (seq != _loadSeq) return;
                if (png is null) continue;

                var bitmap = new BitmapImage();
                using (var stream = new MemoryStream(png))
                    await bitmap.SetSourceAsync(stream.AsRandomAccessStream());
                if (seq != _loadSeq) return;

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
                return; // 페인이 내려가며 워커가 닫힘
            }
            catch
            {
                // 썸네일 실패는 글리프 유지로 충분하다.
            }
        }
    }

    /// <summary>
    /// 워커 스레드: 셸 썸네일을 PNG/JPG 바이트로 추출한다. 없으면 null.
    /// StorageFile API는 agile이라 워커에서 불러도 되고, WinRT 비동기는 여기서 동기 대기한다
    /// (전용 스레드라 UI 교착 없음).
    /// </summary>
    private static byte[]? FetchThumbnail(string path)
    {
        var file = StorageFile.GetFileFromPathAsync(path).AsTask().GetAwaiter().GetResult();
        using var thumb = file.GetThumbnailAsync(ThumbnailMode.SingleItem, 96).AsTask().GetAwaiter().GetResult();
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
    /// </summary>
    internal string? SelectedFilePath =>
        PathOfSelection(IconGrid.SelectedItem) ?? PathOfSelection(ListPane.SelectedItem);

    private static string? PathOfSelection(object? item) =>
        item is FrameworkElement { Tag: ExplorerListing.Entry { IsFolder: false } entry } ? entry.Path : null;

    // ---------- 다중 선택 일괄 열기 (A94 6차, v0.153.0) ----------

    /// <summary>
    /// 셸(MainWindow)의 Enter 분배가 부르는 일괄 열기 (A94 6차) — 종전 "SelectedFilePath 하나를
    /// OpenFileRouted"를 대체한다. 표면 자체 Enter·더블클릭과 **같은 규칙**(아래 OpenFiles):
    /// 선택된 파일만(폴더 제외), 첫 파일은 재사용 규칙(A24) 경로, 나머지는 새 인스턴스.
    /// 반환 = 하나라도 열었는지(false면 셸이 종전 폴백 — 오버레이 토글 등으로 간다).
    /// 그리드·리스트 중 파일 선택이 있는 쪽을 쓴다(SelectedFilePath와 같은 우선순위).
    /// </summary>
    internal bool OpenSelectedFiles()
    {
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
    /// 드래그에 실을 경로들: 잡은 항목이 현재 선택에 포함돼 있으면 선택 전부(다중 드래그 —
    /// 윈도우 관례), 아니면 그 항목 하나만.
    /// </summary>
    private static IReadOnlyList<string> PathsForDrag(ListViewBase owner, ExplorerListing.Entry entry)
    {
        var selected = SelectedPathsOf(owner);
        return selected.Contains(entry.Path, StringComparer.OrdinalIgnoreCase) ? selected : [entry.Path];
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
    /// A157(v0.168.0)이 얹은 것 — Space = 포커스 항목의 선택 토글(체크박스 클릭과 같은 동작).
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
                // 선택이 없으면 삼키지 않는다 — 셸(OnShellEnter)이 상태별로 받는다.
                if (owner.SelectedItem is not SelectorItem { Tag: ExplorerListing.Entry entry }) return;
                e.Handled = true;
                _lastClick = null; // 같은 Enter가 만든 ItemClick 기록이 더블클릭 판정에 섞이지 않게
                // A94 6차: 다중 선택이면 선택된 '파일' 전부를 연다(폴더는 일괄 열기에서 제외).
                // 선택에 파일이 하나도 없으면(폴더만 다중) 아래 현행 첫 항목 동작으로 떨어진다.
                if (owner.SelectedItems.Count > 1 && OpenFiles(SelectedFilePathsOf(owner))) return;
                if (entry.IsFolder) NavigateTo(entry.Path, _extensions);
                else FileActivated?.Invoke(entry.Path);
                return;
            case Windows.System.VirtualKey.F2: // 이름변경 — 다중 선택이어도 첫 항목(SelectedItem)만
                if (owner.SelectedItem is not SelectorItem selected) return;
                e.Handled = true;
                BeginRenameOf(selected);
                return;
            case Windows.System.VirtualKey.Delete: // Del = 휴지통 / Shift+Del = 영구 삭제(A94 4차)
                if (ExplorerFileOps.IsCtrlDown()) return; // Ctrl+Del은 우리 조합이 아니다 — 종전대로 비켜 준다
                var targets = SelectedPathsOf(owner);
                if (targets.Count == 0) return;
                e.Handled = true;
                if (ExplorerFileOps.IsShiftDown()) await PermanentDeleteWithConfirmAsync(targets);
                else await DeleteWithNoticeAsync(targets);
                return;
            case Windows.System.VirtualKey.Space: // A157 — 포커스 항목의 선택 토글(체크박스 클릭과 같은 동작)
                // 포커스 항목 = 키 이벤트의 원천(포커스된 항목 컨테이너)에서 상향 탐색으로 찾는다.
                // 못 찾으면 무동작·무소비 — 표면 자체(빈 영역)에 포커스가 있을 때 Space를 삼키면
                // 스크롤 등 기본 동작을 잃는다. 편집 중에는 위의 TextBox 가드가 이미 막았다.
                if (ItemFromSource(e.OriginalSource) is not { } focused) return;
                e.Handled = true;
                // 두 표면 모두 IsItemClickEnabled=True라 키보드 조작이 ItemClick을 낳을 수 있다 —
                // Space 연타가 클릭 쌍(OnItemClick)으로 읽혀 파일이 열리는 것을 막는다.
                // 위 Enter 분기가 같은 이유로 두는 한 줄과 같은 방어(A85 관례).
                _lastClick = null;
                focused.IsSelected = !focused.IsSelected;
                return;
            case Windows.System.VirtualKey.Escape: // A94 4차 — 잘라내기 표시만 해제(탐색기 동등)
                // 소비하지 않는다: 셸 Esc(S4 복귀 — OnShellEscape)가 이 표면 포커스에서도 성립해야 한다.
                // 클립보드 자체는 건드리지 않는다(Ctrl+V로 다시 붙여넣을 수 있다 — 보고서 명기).
                ExplorerFileOps.ClearCutMarks();
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
                var paths = SelectedPathsOf(owner);
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
        if (FindItemByPath(owner, created) is not { } item) return; // 그새 사라짐 등 — 생성만으로 끝
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
            _lastPress = null; // A157: 체크박스 눌림은 선택 토글 몫 — 쌍 판정에서 빼고 끊는다
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

    // ---------- 선택 체크박스 (A157, v0.168.0) ----------

    /// <summary>
    /// 체크박스 클릭 = 그 항목의 선택 토글 (A157). 체크 상태 자체를 진실로 삼지 않는다 —
    /// 선택을 토글하면 SelectionChanged가 돌아와 체크를 맞춘다(집합은 ListView 선택 하나뿐이다).
    /// <para>
    /// **Checked/Unchecked를 구독하지 않는 이유**: 그 둘은 프로그램적 IsChecked 대입에도 발화해
    /// "선택 → 체크 → 선택" 되먹임 루프가 생긴다. Click은 사용자 입력에서만 발화하므로 루프가
    /// 구조적으로 성립하지 않는다 — 그래서 배선은 Click 하나뿐이다.
    /// </para>
    /// <para>
    /// ButtonBase.Click의 인자(RoutedEventArgs)에는 Handled가 없다(WinUI — 저장소의 e.Handled
    /// 사용처는 전부 Pointer/Key/Tapped 파생 인자다). 클릭이 더블클릭 열기로 새는 것은
    /// 체크박스가 포인터 이벤트를 스스로 소비하는 것 + IsInCheckBox 가드 두 벌이 막는다.
    /// </para>
    /// </summary>
    private void OnItemCheckClick(object sender, RoutedEventArgs e)
    {
        if (ItemFromSource(sender) is not { } item) return;
        item.IsSelected = !item.IsSelected;
    }

    /// <summary>
    /// 선택 → 체크 동기 (A157). 바뀐 항목만 훑는다 — 전량 순회면 Ctrl+A 한 번에 N번 돈다.
    /// 이 핸들러는 IsChecked 대입만 한다: 체크박스 쪽은 Click(사용자 입력 전용)만 듣기 때문에
    /// 이 대입이 다시 선택을 건드릴 경로가 없다(루프 부재의 근거 — OnItemCheckClick 주석).
    /// Fill의 Items.Clear()가 옛 컨테이너를 RemovedItems로 실어 보내지만, 트리에서 떨어진
    /// 체크박스에 false를 쓰는 것은 무해하다(그 항목은 그대로 버려진다).
    /// </summary>
    private void OnListSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        foreach (var removed in e.RemovedItems)
            if (FindItemCheckBox(removed) is { } box) box.IsChecked = false;
        foreach (var added in e.AddedItems)
            if (FindItemCheckBox(added) is { } box) box.IsChecked = true;
    }

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
        if (IsInCheckBox(e.OriginalSource)) return; // A157: 체크박스 2연타 = 선택 토글 두 번(열기 아님)
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
    /// A94 6차: 활성화한 항목이 **다중 선택에 포함돼 있으면** 선택된 파일 전부를 연다(폴더 제외 —
    /// 선택에 파일이 하나도 없으면 종전대로 그 항목 하나. Enter 규칙과 같다).
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

        // A94 6차 — 다중 선택 일괄 열기(잡은 항목이 선택 밖이면 그 항목만: 드래그·삭제와 같은 규칙)
        if (owner is not null && owner.SelectedItems.Count > 1 &&
            SelectedPathsOf(owner).Contains(entry.Path, StringComparer.OrdinalIgnoreCase) &&
            OpenFiles(SelectedFilePathsOf(owner), shift))
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
