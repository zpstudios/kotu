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
        // A94 4차: 잘라내기 표시(전역 1벌)가 바뀌면 이미 그려 둔 항목의 흐림만 다시 맞춘다.
        // 구독을 Loaded/Unloaded로 묶는 이유 = 정적 이벤트가 닫힌 창의 컨트롤을 붙들지 않게
        // (Unloaded로 워커를 정리하는 아래 관용구와 같은 수명 규칙). 중복 구독은 -= 선행으로 막는다.
        Loaded += (_, _) =>
        {
            ExplorerFileOps.CutMarksChanged -= ApplyCutMarks;
            ExplorerFileOps.CutMarksChanged += ApplyCutMarks;
        };
        Unloaded += (_, _) =>
        {
            ExplorerFileOps.CutMarksChanged -= ApplyCutMarks;
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
    /// 항목 우클릭 메뉴: 파일 = "Open in new instance"(A24) + Rename·Delete, 폴더 = Rename·Delete만
    /// (A94 2차 — 종전에는 파일 전용 메뉴라 폴더에 안 달았다. 빈 영역 메뉴는 원래 없어 이번에도
    /// 안 만든다 — 새 폴더는 키만: docs/A94-matrix.md 명기). Delete 대상은 드래그와 같은 규칙 —
    /// 그 항목이 선택에 포함돼 있으면 선택 전부, 아니면 그 항목 하나(PathsForDrag 재사용).
    /// Rename은 플라이아웃이 닫히며 포커스를 되돌린 '뒤'에 진입해야 편집 상자가 곧장 LostFocus
    /// 커밋으로 닫혀 버리지 않는다 — 디스패처로 한 박자 미룬다(BeginRenameOf).
    /// </summary>
    private void AttachContextMenu(SelectorItem item, ExplorerListing.Entry entry, ListViewBase owner)
    {
        var flyout = new MenuFlyout();
        if (!entry.IsFolder) AddOpenInNewInstance(flyout, entry);
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

    /// <summary>리스트 행: 아이콘 + 이름 + 길이(미디어만, 지연 로드 — A6) + 크기(파일만).</summary>
    private ListViewItem MakeListItem(ExplorerListing.Entry entry)
    {
        var row = new Grid { ColumnSpacing = 8 };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var icon = new FontIcon
        {
            Glyph = entry.IsFolder ? "\uE8B7" : "\uE7C3",
            FontSize = 13,
            VerticalAlignment = VerticalAlignment.Center,
        };
        var name = new TextBlock
        {
            Text = entry.Name,
            FontSize = 12,
            VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis,
        };
        Grid.SetColumn(name, 1);
        // Children[2] = 길이 자리 — LoadDurationsAsync가 나중에 채운다(A6). 인덱스 수동 동기.
        var duration = new TextBlock
        {
            FontSize = 11,
            Opacity = 0.6,
            VerticalAlignment = VerticalAlignment.Center,
        };
        Grid.SetColumn(duration, 2);
        var size = new TextBlock
        {
            Text = entry.IsFolder ? string.Empty : ExplorerListing.FormatSize(entry.Size),
            FontSize = 11,
            Opacity = 0.6,
            VerticalAlignment = VerticalAlignment.Center,
        };
        Grid.SetColumn(size, 3);

        row.Children.Add(icon);
        row.Children.Add(name);
        row.Children.Add(duration);
        row.Children.Add(size);
        ToolTipService.SetToolTip(row, entry.Name);

        var item = new ListViewItem { Content = row, Tag = entry };
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
    /// 미디어 파일의 재생 길이를 리스트 행 셋째 칸에 채운다(A6).
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
            // MakeListItem의 Children[2] = 길이 TextBlock (인덱스 수동 동기)
            if (item.Content is Grid row && row.Children.Count > 3 && row.Children[2] is TextBlock tb)
                tb.Text = text;
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

    /// <summary>파일 조작 뒤 현재 폴더 재스캔 — FileSystemWatcher가 없어 명시 갱신이 유일한 경로.</summary>
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
                var cut = e.Key == Windows.System.VirtualKey.X;
                var paths = SelectedPathsOf(owner);
                if (paths.Count == 0) return;
                e.Handled = true;
                if (await ExplorerFileOps.CopyToClipboardAsync(paths, cut) is { } copyNotice)
                    Notice?.Invoke(copyNotice);
                break;
            case Windows.System.VirtualKey.V:
                if (_folder.Length == 0) return;
                e.Handled = true;
                var pasteUi = MakeOpUi(); // A94 3차 — 충돌 대화상자·진행 문구, 4차 — 접근 거부 안내
                var (didWork, pasteResult, pasteNotice) =
                    await ExplorerFileOps.PasteFromClipboardAsync(_folder, pasteUi);
                if (didWork) RefreshAfterFileOp();
                await ExplorerFileOps.ReportAsync(pasteNotice, pasteResult.Denied, pasteUi);
                break;
        }
    }

    // ---------- 이름변경 · 새 폴더 · 휴지통 삭제 (A94 2차, v0.125.0) ----------

    /// <summary>
    /// F2·우클릭 Rename 진입: 항목의 이름 TextBlock을 찾아 인라인 편집(ExplorerRenameBox)으로 바꾼다.
    /// 이름 TextBlock 위치 = MakeGridItem(StackPanel의 Children[1])·MakeListItem(Grid 행의 Children[1]) —
    /// 두 생성 코드 모두 콘텐츠 패널 둘째 자식이 이름이다(인덱스 수동 동기 — LoadDurationsAsync 관례).
    /// 커밋 성공 갱신 = RefreshAfterFileOp(편집이 끝난 뒤에만 — 편집 중 재스캔은 편집 UI를 지운다).
    /// </summary>
    private void BeginRenameOf(SelectorItem item)
    {
        if (item.Tag is not ExplorerListing.Entry entry) return;
        if (item.Content is not Panel { Children.Count: > 1 } panel ||
            panel.Children[1] is not TextBlock nameBlock) return;
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
        Activate(entry);
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
        if (sender is not SelectorItem { Tag: ExplorerListing.Entry entry }) return;
        e.Handled = true;
        _lastClick = null; // 이 제스처를 이룬 클릭 기록이 다음 클릭 쌍 판정에 섞이지 않게
        Activate(entry);
    }

    /// <summary>
    /// 더블클릭 열기 공통 종착점 (A85): 폴더 = 진입(NavigateTo), 파일 = 열기(Shift = 새 창, A24).
    /// ItemClick 쌍과 DoubleTapped가 같은 제스처에서 둘 다 발화하는 환경이 있어, 같은 경로의
    /// 연속 발화를 판정 창(DoubleClickMs) 안에서 1회로 누른다 — A24 "항상 새 창" 설정에서
    /// 창이 두 개 뜨는 이중 열기 방지.
    /// </summary>
    private void Activate(ExplorerListing.Entry entry)
    {
        var now = DateTime.UtcNow;
        if (_lastActivation is { } last && last.Path == entry.Path &&
            (now - last.At).TotalMilliseconds < DoubleClickMs)
            return;
        _lastActivation = (entry.Path, now);

        if (entry.IsFolder)
        {
            NavigateTo(entry.Path, _extensions);
            return;
        }

        var shift = Microsoft.UI.Input.InputKeyboardSource
            .GetKeyStateForCurrentThread(Windows.System.VirtualKey.Shift)
            .HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down);
        if (shift) FileActivatedNewWindow?.Invoke(entry.Path);
        else FileActivated?.Invoke(entry.Path);
    }

    private void OnUpClicked(object sender, RoutedEventArgs e)
    {
        if (Directory.GetParent(_folder) is { } parent)
            NavigateTo(parent.FullName, _extensions);
    }
}
