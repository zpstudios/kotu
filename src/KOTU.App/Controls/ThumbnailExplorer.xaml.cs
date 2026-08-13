using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.ApplicationModel.DataTransfer;
using Windows.System;
using KOTU.Core.Routing;
using KOTU.Input;

namespace KOTU.App.Controls;

/// <summary>
/// 중앙 썸네일 탐색기 뷰 (A93) — S1(콘텐츠 없음·모듈만 실행)의 중앙 구획.
/// A81(v0.101.0)의 "좌 도크 열림 시 중앙 탐색기 숨김"을 대체한다 — S1 중앙은 항상 이 뷰다.
/// 목록은 좌 도크 리스트(ExplorerPane)가 원본: 정렬(A5)·필터(A7)가 적용된 표시 목록을
/// 셸이 ShowEntries로 밀어 넣는다(ExplorerPane.ViewChanged 경유) — 어느 쪽에서 폴더를 바꿔도
/// 둘 다 같은 목록을 그린다. 더블클릭 열기·새 인스턴스 이벤트도 ExplorerPane과 같은 배선이라
/// 셸의 기존 라우팅(OpenFileRouted·A24)을 그대로 쓴다.
/// 열 수 = 좌우 도크가 둘 다 열려 있으면 4, 하나라도 닫히면 8(A63 대체 — 크기 고정·열 수 가변이던
/// 종전 규칙을 열 수 고정·크기 가변으로 뒤집었다). 타일 한 변 = floor(실폭/열수).
/// </summary>
public sealed partial class ThumbnailExplorer : UserControl
{
    /// <summary>이미지 미리보기 디코드 폭 상한(물리 px) — 원본 크기 디코드로 메모리가 폭주하지 않게.</summary>
    private const int PreviewDecodeWidth = 256;

    /// <summary>더블클릭 판정 창 — ExplorerPane.DoubleClickMs와 같은 값(같은 감각).</summary>
    private const int DoubleClickMs = 500;

    /// <summary>폴더 더블클릭 — 셸이 좌 리스트를 그 폴더로 항해시킨다(상태 공유의 되돌이 경로).</summary>
    public event Action<string>? FolderActivated;

    /// <summary>파일 더블클릭 열기 — 셸이 재사용 규칙(A24)을 적용해 라우팅한다.</summary>
    public event Action<string>? FileActivated;

    /// <summary>명시적 새 창 열기(A24: Shift+더블클릭·우클릭 메뉴) — 셸이 항상 새 창으로.</summary>
    public event Action<string>? FileActivatedNewWindow;

    /// <summary>
    /// 파일 경로 → 담당 모듈 ID (액센트 색 타일용). 셸이 라우터로 주입한다 —
    /// 이 컨트롤이 FileTypeRouter를 직접 알면 DI 없이 못 만드는 컨트롤이 된다.
    /// </summary>
    public Func<string, string?>? ModuleIdForFile { get; set; }

    private int _columns = 8; // 기본 = 도크 하나라도 닫힘(전폭) 기준 — 셸이 곧 SetColumns로 덮는다
    private (string Path, DateTime At)? _lastClick;

    /// <summary>선택된 파일 타일의 경로 — 폴더·무선택이면 null (A86: 셸 Enter "선택 파일 있으면 열기").</summary>
    public string? SelectedFilePath =>
        TileGrid.SelectedItem is FrameworkElement { Tag: ExplorerListing.Entry { IsFolder: false } entry }
            ? entry.Path : null;

    /// <summary>선택된 항목(파일·폴더 불문) — 없으면 null (A90: S4 Enter "선택 열기 우선" 판정).</summary>
    public ExplorerListing.Entry? SelectedEntry =>
        TileGrid.SelectedItem is FrameworkElement { Tag: ExplorerListing.Entry entry } ? entry : null;

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
    }

    /// <summary>
    /// 배경을 오버레이 아크릴(A33 OverlayAcrylicBrush)로 바꾼다 — S4('오픈 파일' 탐색, A90)의
    /// 중앙 오버레이 인스턴스 전용. S1 중앙(불투명 기본 배경)에서는 부르지 않는다.
    /// </summary>
    public void UseTranslucentBackground() =>
        LayoutRoot.Background = (Brush)Application.Current.Resources["OverlayAcrylicBrush"];

    /// <summary>썸네일 그리드로 포커스 이동 (A90: S4 진입 시) — 실패해도 무해(포커스만 안 옮겨진다).</summary>
    public void FocusGrid() => TileGrid.Focus(FocusState.Programmatic);

    /// <summary>Enter = 선택 항목 열기 (A90 — 위 생성자 주석 참고). 선택이 없으면 셸 분배로 흘린다.</summary>
    private void OnGridKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key != VirtualKey.Enter || e.KeyStatus.WasKeyDown) return;
        if (SelectedEntry is not { } entry) return; // 선택 없음 — 셸(OnShellEnter)이 상태별로 받는다
        e.Handled = true; // 셸 루트 핸들러의 이중 처리 방지 — OnShellEnter는 Handled면 물러난다
        _lastClick = null; // 같은 Enter가 만든 ItemClick 기록이 더블클릭 판정에 섞이지 않게
        if (entry.IsFolder) FolderActivated?.Invoke(entry.Path);
        else FileActivated?.Invoke(entry.Path);
    }

    /// <summary>열 수 지정(A93: 도크 둘 다 열림 4 / 하나라도 닫힘 8). 바뀌면 타일 크기 재계산.</summary>
    public void SetColumns(int columns)
    {
        if (columns == _columns) return;
        _columns = columns;
        ApplyTileSize();
    }

    /// <summary>
    /// 표시 목록 교체 — 좌 리스트(ExplorerPane)가 정렬·필터를 적용해 넘긴 결과를 그대로 그린다.
    /// 이미지 미리보기는 BitmapImage가 스스로 비동기 디코드하므로 별도 로드 루프가 없다.
    /// </summary>
    public void ShowEntries(IReadOnlyList<ExplorerListing.Entry> entries)
    {
        TileGrid.Items.Clear();
        foreach (var entry in entries)
            TileGrid.Items.Add(MakeTile(entry));
        EmptyText.Visibility = entries.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

        TileGrid.UpdateLayout(); // 새 항목의 패널 실체화 — 아래 타일 크기 반영이 헛돌지 않게
        ApplyTileSize();
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
            : MakeExtensionTile(entry);

        var tile = new Grid();
        tile.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        tile.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        tile.Children.Add(preview);

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
        AttachContextMenu(item, entry); // A24 — 좌 리스트와 같은 우클릭 메뉴
        return item;
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
        var host = new Grid();
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
            image.ImageFailed += (_, _) =>
            {
                host.Children.Clear();
                host.Children.Add(MakeExtensionTile(entry));
            };
            host.Children.Add(image);
        }
        catch
        {
            host.Children.Add(MakeExtensionTile(entry)); // 경로가 Uri가 못 되는 극단 케이스
        }
        return host;
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

    /// <summary>파일 항목 우클릭 메뉴(A24): "Open in new instance" — ExplorerPane과 같은 구성.</summary>
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

    // ---------- 입력 ----------

    /// <summary>
    /// 클릭 2회(500ms 내 같은 항목) = 더블클릭 — ExplorerPane.OnItemClick과 같은 판정.
    /// 폴더 = FolderActivated(셸이 좌 리스트를 항해시켜 양쪽이 함께 이동),
    /// 파일 = 열기(Shift면 새 창, A24).
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
            FolderActivated?.Invoke(entry.Path);
            return;
        }

        var shift = Microsoft.UI.Input.InputKeyboardSource
            .GetKeyStateForCurrentThread(Windows.System.VirtualKey.Shift)
            .HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down);
        if (shift) FileActivatedNewWindow?.Invoke(entry.Path);
        else FileActivated?.Invoke(entry.Path);
    }

    // ---------- 드래그 앤 드랍 (A93 — 이동/복사 자체는 A94 몫) ----------

    /// <summary>
    /// 중앙(탐색기) 영역 드랍 = 탐색기식 이동/복사 자리인데 그 구현은 A94다 — 이번에는
    /// **무동작으로 소비만** 한다. 여기서 Handled를 안 걸면 창 전체 핸들러(OnWindowDrop)가
    /// "열기"로 삼켜 A93 드랍 매트릭스(좌·중 = 무동작)가 깨진다.
    /// </summary>
    private void OnDragOver(object sender, DragEventArgs e)
    {
        e.AcceptedOperation = DataPackageOperation.None; // 커서로 "여긴 아직 안 됨"을 알린다
        e.Handled = true;
    }

    /// <summary>AcceptedOperation이 None이라 보통 오지 않지만, 와도 삼킨다(A94 전까지 무동작).</summary>
    private void OnDrop(object sender, DragEventArgs e) => e.Handled = true;
}
