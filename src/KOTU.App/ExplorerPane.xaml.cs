using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
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
    /// 표시 목록이 다시 그려질 때(폴더 이동·정렬 A5·필터 A7) 정렬·필터 적용 결과와 함께 발생 (A93).
    /// 중앙 썸네일 뷰(ThumbnailExplorer)가 좌 리스트와 같은 폴더 상태를 공유하는 통로 —
    /// 셸이 구독해 같은 항목을 타일로 다시 그린다. 폴더를 못 읽으면 빈 목록으로 알린다.
    /// </summary>
    public event Action<IReadOnlyList<ExplorerListing.Entry>>? ViewChanged;

    private const string SortSettingKey = "explorer.sort"; // "name"/"size"/"modified" — SortKey와 수동 동기

    private IReadOnlyList<string> _extensions = [];
    private string _folder = string.Empty;
    private int _loadSeq;                     // 빠른 연속 탐색 시 늦은 결과 폐기
    private (string Path, DateTime At)? _lastClick;
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
        Unloaded += (_, _) =>
        {
            _worker?.Dispose(); // 진행 중 작업은 워커가 마저 끝내고 스레드 종료
            _worker = null;
        };
    }

    // ---------- 정렬 (A5) ----------

    /// <summary>정렬 플라이아웃의 체크 상태를 _sortKey에 맞춘다.</summary>
    private void SyncSortChecks()
    {
        SortByName.IsChecked = _sortKey == ExplorerListing.SortKey.Name;
        SortBySize.IsChecked = _sortKey == ExplorerListing.SortKey.Size;
        SortByModified.IsChecked = _sortKey == ExplorerListing.SortKey.Modified;
    }

    private void OnSortChanged(object sender, RoutedEventArgs e)
    {
        var key = ReferenceEquals(sender, SortBySize) ? ExplorerListing.SortKey.Size
                : ReferenceEquals(sender, SortByModified) ? ExplorerListing.SortKey.Modified
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
        ViewChanged?.Invoke(arranged); // A93 — 중앙 썸네일 뷰가 같은 목록을 받아 그린다
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
    public async void NavigateTo(string folder, IReadOnlyList<string> extensions)
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
            ViewChanged?.Invoke([]); // A93 — 썸네일 뷰도 옛 폴더 목록을 남기지 않는다
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

    /// <summary>파일 항목 우클릭 메뉴(A24): "Open in new instance" 하나 — 폴더에는 안 단다.</summary>
    private void AttachContextMenu(FrameworkElement item, ExplorerListing.Entry entry)
    {
        if (entry.IsFolder) return;
        var open = new MenuFlyoutItem
        {
            Text = "Open in new instance", // A53 문구
            Icon = new FontIcon { Glyph = "\uE8A7" }, // OpenInNewWindow
        };
        open.Click += (_, _) => FileActivatedNewWindow?.Invoke(entry.Path);
        var flyout = new MenuFlyout();
        flyout.Items.Add(open);
        item.ContextFlyout = flyout;
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
        AttachContextMenu(item, entry); // A24
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
        AttachContextMenu(item, entry); // A24
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
    /// 그리드·리스트 중 선택이 있는 쪽을 쓴다 — SelectionMode가 둘 다 Single이라 동시 선택은 없다.
    /// </summary>
    internal string? SelectedFilePath =>
        PathOfSelection(IconGrid.SelectedItem) ?? PathOfSelection(ListPane.SelectedItem);

    private static string? PathOfSelection(object? item) =>
        item is FrameworkElement { Tag: ExplorerListing.Entry { IsFolder: false } entry } ? entry.Path : null;

    /// <summary>
    /// 클릭 2회(500ms 내 같은 항목) = 더블클릭: 폴더 진입 또는 파일 열기.
    /// Shift를 누른 채 더블클릭하면 파일을 새 창으로(A24) — 폴더에는 효과 없음.
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
