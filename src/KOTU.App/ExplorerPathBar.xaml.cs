using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Media;
using KOTU.Core.Routing;

namespace KOTU.App;

/// <summary>Path and type-filter presentation; never creates a listing, worker, or watcher.</summary>
public sealed partial class ExplorerPathBar : UserControl
{
    private ExplorerSession? _session;
    private IReadOnlyList<string> _extensions => _session?.Extensions ?? [];
    private string _folder => _session?.CurrentFolder ?? string.Empty;
    private bool _showHidden => _session?.ShowHidden ?? false;
    internal event Action? ShowHiddenChanged;

    public ExplorerPathBar() => InitializeComponent();

    internal void Attach(ExplorerSession session)
    {
        if (ReferenceEquals(_session, session)) return;
        Detach();
        _session = session;
        _session.Updated += OnSessionUpdated;
        Sync();
    }

    internal void Detach()
    {
        if (_session is not null) _session.Updated -= OnSessionUpdated;
        _session = null;
    }

    private void OnSessionUpdated(ExplorerSessionUpdate update)
    {
        if (update.Kind is ExplorerSessionUpdateKind.PreparingNavigation or
            ExplorerSessionUpdateKind.FolderRequested or ExplorerSessionUpdateKind.PreparingDisplay)
            Sync();
    }

    internal void Sync()
    {
        if (_session is null) return;
        EnsureFilterFlyout();
        PathText.Text = _folder;
        ToolTipService.SetToolTip(PathText, _folder);
        UpButton.IsEnabled = _folder.Length > 0 && Directory.GetParent(_folder) is not null;
    }

    private void SyncFilterChecks()
    {
        if (FilterButton.Flyout is MenuFlyout flyout)
            foreach (var item in flyout.Items.OfType<ToggleMenuFlyoutItem>())
                if (!ReferenceEquals(item, _showHiddenItem)) item.IsChecked = _session!.IsExtensionVisible(item.Text);
        SyncShowHiddenCheck();
        UpdateFilterVisual();
    }

    private void RefreshView() => _session?.RefreshView();
    private void NavigateTo(string folder, IReadOnlyList<string> extensions)
    {
        if (_session is not null) _ = _session.NavigateAsync(folder, extensions);
    }
    private void RefreshAfterFileOp() => NavigateTo(_folder, _extensions);

    // ---------- 파일 종류 필터 (A7) ----------

    private IReadOnlyList<string>? _filterBuiltFor; // 마지막으로 플라이아웃을 만든 확장자 목록
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
        if (_filterBuiltFor is not null && _extensions.SequenceEqual(_filterBuiltFor))
        {
            SyncFilterChecks();
            return;
        }
        _filterBuiltFor = _extensions;

        var flyout = new MenuFlyout { Placement = FlyoutPlacementMode.BottomEdgeAlignedRight };
        // "Show all"이 되돌릴 대상 목록 — A160의 숨김 토글은 여기 담지 않는다(아래 주석).
        var extensionToggles = new List<ToggleMenuFlyoutItem>();
        foreach (var ext in _extensions)
        {
            // A196: 전체 파일 필터(ExplorerListing.AllFiles — 설정·미지원 안내 화면)의 "*"는
            // 토글로 만들지 않는다 — 좁힐 확장자 목록 자체가 없고, 숨김 판정(Arrange의
            // hiddenExtensions)은 실제 확장자 문자열이라 "*" 토글은 아무 일도 못 하는 거짓 UI다.
            if (ext == "*") continue;
            var toggle = new ToggleMenuFlyoutItem { Text = ext, IsChecked = _session!.IsExtensionVisible(ext) };
            toggle.Click += (_, _) =>
            {
                _session!.SetExtensionVisible(ext, toggle.IsChecked);
                UpdateFilterVisual();
                RefreshView(); // A204 — 필터 변경은 스캔 결과 입력(안정성 리셋, 계약대로)
            };
            extensionToggles.Add(toggle);
            flyout.Items.Add(toggle);
        }
        flyout.Items.Add(new MenuFlyoutSeparator());
        var showAll = new MenuFlyoutItem { Text = "Show all" };
        showAll.Click += (_, _) =>
        {
            if (_session!.HiddenExtensionCount == 0) return;
            _session!.ClearExtensionFilter();
            // A160: 확장자 토글만 되돌린다. 종전처럼 flyout.Items를 타입(ToggleMenuFlyoutItem)으로
            // 훑으면 아래 숨김 표시 토글까지 체크돼, 설정은 그대로인 채 메뉴만 거짓말을 한다.
            foreach (var t in extensionToggles) t.IsChecked = true;
            UpdateFilterVisual();
            RefreshView(); // A204 — 위 확장자 토글과 같은 입력 선택(스캔 결과)
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
            _session!.SetShowHidden(showHidden.IsChecked);
            // 좌 패널 폴더 트리는 자기 열거를 따로 한다 — 같은 설정으로 다시 만들라고 알린다.
            ShowHiddenChanged?.Invoke();
            // ⚠️ RefreshView()가 아니라 **재열거**여야 한다: 그건 마지막 스캔 결과를
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
        FilterIcon.Foreground = _session!.HiddenExtensionCount > 0 &&
            Application.Current.Resources["AccentTextFillColorPrimaryBrush"] is Brush accent
            ? accent
            : _filterDefaultBrush;
        ToolTipService.SetToolTip(FilterButton, _session!.HiddenExtensionCount > 0
            ? $"Filter file types ({_session!.HiddenExtensionCount} hidden)"
            : "Filter file types");
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

}
