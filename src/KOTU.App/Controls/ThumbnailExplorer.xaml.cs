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
    private (string Path, DateTime At)? _lastActivation; // A85: ItemClick 쌍·DoubleTapped 겹침을 1회로 억제

    /// <summary>
    /// Ctrl+Shift+N(새 폴더) 직후의 편집 진입 예약 (A94 2차). 이 뷰의 재스캔은 좌 리스트 경유
    /// 비동기(FolderActivated → 셸 → ViewChanged → ShowEntries)라 완료 시점을 직접 기다릴 수 없다 —
    /// 다음 ShowEntries가 이 경로의 타일을 찾아 이름변경 편집으로 진입하고 지운다(1회성).
    /// </summary>
    private string? _pendingRenamePath;

    /// <summary>
    /// 지금 그리고 있는 폴더 경로 (A94 — 빈 영역 드랍·붙여넣기의 대상). ShowEntries가 좌 리스트의
    /// ViewChanged에서 받은 폴더로 갱신한다 — 이 컨트롤은 폴더 상태의 원본이 아니다(A93).
    /// </summary>
    public string? CurrentFolder { get; private set; }

    /// <summary>
    /// 선택된 파일 타일의 경로 — 폴더·무선택이면 null (A86: 셸 Enter "선택 파일 있으면 열기").
    /// A94(Extended)부터 다중 선택이 가능하지만 열기류 단일 대상 동작은 첫 선택(SelectedItem) 기준 유지.
    /// </summary>
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

    /// <summary>
    /// Enter = 선택 항목 열기 (A90 — 위 생성자 주석 참고. 선택이 없으면 셸 분배로 흘린다) +
    /// 클립보드 키 (A94): Ctrl+C/X/V/A + 2차(v0.125.0): F2 = 이름변경(첫 선택 타일만),
    /// Del = 휴지통 삭제, Ctrl+Shift+N = 새 폴더 — 이 그리드에 포커스가 있을 때만 온다
    /// (KeyDown 버블링이라 문서 에디터 등 텍스트 표면으로 새지 않고, A34 통과 규칙과도 겹치지 않는다).
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
            if (SelectedEntry is not { } entry) return; // 선택 없음 — 셸(OnShellEnter)이 상태별로 받는다
            e.Handled = true; // 셸 루트 핸들러의 이중 처리 방지 — OnShellEnter는 Handled면 물러난다
            _lastClick = null; // 같은 Enter가 만든 ItemClick 기록이 더블클릭 판정에 섞이지 않게
            if (entry.IsFolder) FolderActivated?.Invoke(entry.Path);
            else FileActivated?.Invoke(entry.Path);
            return;
        }

        // A94 2차: F2 = 이름변경(첫 선택 타일 1개 — 다중 선택이어도 첫 항목만), Del = 휴지통 삭제.
        if (e.Key == VirtualKey.F2)
        {
            if (TileGrid.SelectedItem is not GridViewItem selected) return;
            e.Handled = true;
            BeginRenameOf(selected);
            return;
        }
        if (e.Key == VirtualKey.Delete)
        {
            // Shift+Del(영구 삭제)은 이번 범위 아님(후속 등재) — 삼키지도 않고 비켜 준다.
            if (ExplorerFileOps.IsShiftDown() || ExplorerFileOps.IsCtrlDown()) return;
            var targets = SelectedPaths();
            if (targets.Count == 0) return;
            e.Handled = true;
            await DeleteWithNoticeAsync(targets);
            return;
        }

        if (!ExplorerFileOps.IsCtrlDown()) return;
        switch (e.Key)
        {
            case VirtualKey.N: // Ctrl+Shift+N = 새 폴더 (Shift 없는 Ctrl+N 아님 —
                // 앱 전역 Shift+N 새 창(A84)과도 다른 조합. 판정 = Ctrl(위) && Shift && N)
                if (!ExplorerFileOps.IsShiftDown() || CurrentFolder is not { Length: > 0 } parent) return;
                e.Handled = true;
                var (created, createNotice) = ExplorerFileOps.CreateFolder(parent);
                if (createNotice is not null) ShowNotice(createNotice);
                if (created is null) return;
                _pendingRenamePath = created; // 재스캔 결과(ShowEntries)가 돌아오면 그 타일로 편집 진입
                FolderActivated?.Invoke(parent); // 단일 원본(좌 리스트) 경유 재스캔 — A93 경로
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
                if (await ExplorerFileOps.CopyToClipboardAsync(paths, cut: e.Key == VirtualKey.X)
                    is { } copyNotice)
                    ShowNotice(copyNotice);
                break;
            case VirtualKey.V:
                if (CurrentFolder is not { Length: > 0 } folder) return;
                e.Handled = true;
                var (didWork, pasteNotice) =
                    await ExplorerFileOps.PasteFromClipboardAsync(folder, MakeOpUi()); // A94 3차
                if (didWork) FolderActivated?.Invoke(folder); // 단일 원본(좌 리스트) 경유 재스캔 — A93 경로
                if (pasteNotice is not null) ShowNotice(pasteNotice);
                break;
        }
    }

    /// <summary>선택 타일 경로 전부(폴더 포함) — 항목 = 컨테이너 직접 추가라 Tag에서 꺼낸다(A94).</summary>
    private IReadOnlyList<string> SelectedPaths() =>
        TileGrid.SelectedItems
            .OfType<FrameworkElement>()
            .Select(i => i.Tag)
            .OfType<ExplorerListing.Entry>()
            .Select(e => e.Path)
            .ToList();

    /// <summary>열 수 지정(A93: 도크 둘 다 열림 4 / 하나라도 닫힘 8). 바뀌면 타일 크기 재계산.</summary>
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
    /// </summary>
    public void ShowEntries(string folder, IReadOnlyList<ExplorerListing.Entry> entries)
    {
        CurrentFolder = folder;
        TileGrid.Items.Clear();
        foreach (var entry in entries)
            TileGrid.Items.Add(MakeTile(entry));
        EmptyText.Visibility = entries.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

        TileGrid.UpdateLayout(); // 새 항목의 패널 실체화 — 아래 타일 크기 반영이 헛돌지 않게
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
        ExplorerRenameBox.Begin(tile, caption, entry.Path, ShowNotice, RefreshViaShell);
    }

    /// <summary>조작 후 갱신 — 폴더 상태의 단일 원본(좌 리스트)을 셸이 다시 항해시키는 A93 경로.</summary>
    private void RefreshViaShell()
    {
        if (CurrentFolder is { Length: > 0 } folder) FolderActivated?.Invoke(folder);
    }

    /// <summary>
    /// 이동/복사/붙여넣기용 UI 문맥 (A94 3차) — 이 그리드 창의 DispatcherQueue·XamlRoot(충돌
    /// 대화상자용)와 ShowNotice 채널(진행 문구 라이브 갱신용)을 조작 시작 시점에 캡처한다.
    /// </summary>
    private ExplorerFileOps.OpUi MakeOpUi() => new(DispatcherQueue, XamlRoot, ShowNotice);

    /// <summary>
    /// Del·우클릭 Delete (A94 2차): 휴지통 경유 삭제(StorageDeleteOption.Default —
    /// ExplorerFileOps 주석). 확인 대화상자 없음(탐색기 관례) — 실패만 안내 문구.
    /// </summary>
    private async Task DeleteWithNoticeAsync(IReadOnlyList<string> paths)
    {
        var result = await ExplorerFileOps.DeleteToRecycleAsync(paths);
        RefreshViaShell();
        if (result.Notice("deleted") is { } notice) ShowNotice(notice);
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

    /// <summary>
    /// 항목 우클릭 메뉴 — ExplorerPane.AttachContextMenu와 같은 구성(A94 2차):
    /// 파일 = "Open in new instance"(A24) + Rename·Delete, 폴더 = Rename·Delete만
    /// (종전에는 파일 전용 메뉴라 폴더 타일에 안 달았다. 빈 영역 메뉴는 원래 없어 이번에도
    /// 안 만든다 — 새 폴더는 키만: docs/A94-matrix.md 명기). Delete 대상은 드래그와 같은 규칙 —
    /// 그 타일이 선택에 포함돼 있으면 선택 전부, 아니면 그 타일 하나.
    /// Rename은 플라이아웃이 닫히며 포커스를 되돌린 '뒤'에 진입해야 편집 상자가 곧장 LostFocus
    /// 커밋으로 닫혀 버리지 않는다 — 디스패처로 한 박자 미룬다.
    /// </summary>
    private void AttachContextMenu(GridViewItem item, ExplorerListing.Entry entry)
    {
        var flyout = new MenuFlyout();
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
        delete.Click += async (_, _) =>
        {
            var selected = SelectedPaths();
            IReadOnlyList<string> targets =
                selected.Contains(entry.Path, StringComparer.OrdinalIgnoreCase)
                    ? selected
                    : [entry.Path];
            await DeleteWithNoticeAsync(targets);
        };
        flyout.Items.Add(delete);
        item.ContextFlyout = flyout;
    }

    // ---------- 입력 ----------

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
            FolderActivated?.Invoke(entry.Path);
            return;
        }

        var shift = Microsoft.UI.Input.InputKeyboardSource
            .GetKeyStateForCurrentThread(Windows.System.VirtualKey.Shift)
            .HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down);
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
    /// 돌아와 이 그리드까지 갱신된다(A93 경로 그대로. FileSystemWatcher가 없어 명시 갱신).
    /// </summary>
    private async void HandleDrop(DragEventArgs e, string targetFolder)
    {
        e.Handled = true; // 창 수준 라우팅과의 이중 처리 방지 (await 전에 동기로 지정해야 유효)
        var operation = ExplorerFileOps.DecideOperation(e, targetFolder);
        if (operation == DataPackageOperation.None ||
            !e.DataView.Contains(StandardDataFormats.StorageItems))
            return;
        e.AcceptedOperation = operation; // 소스(OS 탐색기 등)에 확정 동작을 알린다

        var result = await ExplorerFileOps.TransferDroppedAsync(
            e.DataView, targetFolder, operation == DataPackageOperation.Move,
            MakeOpUi()); // A94 3차 — 충돌 대화상자·진행 문구용 UI 문맥(조작 시작 시점 캡처)
        FolderActivated?.Invoke(CurrentFolder is { Length: > 0 } current ? current : targetFolder);
        if (result.Notice(operation == DataPackageOperation.Move) is { } notice) ShowNotice(notice);
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
