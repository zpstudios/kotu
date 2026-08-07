using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.Storage;
using Windows.Storage.FileProperties;
using WinUtil.Core.Routing;
using WinUtil.Core.Threading;

namespace WinUtil.App;

/// <summary>
/// 내장 탐색기 컨트롤 (v0.25.0, docs/explorer-plan.md).
/// 좌 70% 썸네일 그리드 + 우 30% 리스트로 같은 폴더를 두 방식으로 보여준다.
/// 폴더는 전부, 파일은 주입된 담당 확장자만(사용자 확정). 더블클릭: 폴더=진입, 파일=FileActivated.
/// Alt 오버레이용으로는 ConfigureListOnly()로 리스트만 남겨 재사용한다.
/// 폴더 스캔·썸네일 추출은 페인 전용 워커(A42)에서 돌고, UI 스레드는 결과 반영만 한다.
/// </summary>
public sealed partial class ExplorerPane : UserControl
{
    private const int ThumbnailLimit = 300;   // 썸네일 로드 상한 (초대형 폴더 보호)
    private const int DoubleClickMs = 500;

    /// <summary>파일 더블클릭 시 전체 경로와 함께 발생. 셸이 라우팅한다.</summary>
    public event Action<string>? FileActivated;

    private IReadOnlyList<string> _extensions = [];
    private string _folder = string.Empty;
    private int _loadSeq;                     // 빠른 연속 탐색 시 늦은 결과 폐기
    private (string Path, DateTime At)? _lastClick;
    private ModuleWorker? _worker;            // 스캔·썸네일 전용 — 페인별 분리(A42 정책)

    /// <summary>지연 생성: Unloaded로 정리된 뒤 다시 로드돼도(Alt 오버레이 재오픈) 되살아난다.</summary>
    private ModuleWorker Worker => _worker ??= new ModuleWorker("ZP explorer worker");

    public ExplorerPane()
    {
        InitializeComponent();
        Unloaded += (_, _) =>
        {
            _worker?.Dispose(); // 진행 중 작업은 워커가 마저 끝내고 스레드 종료
            _worker = null;
        };
    }

    /// <summary>Alt 오버레이용: 썸네일 그리드를 숨기고 리스트만 남긴다.</summary>
    public void ConfigureListOnly()
    {
        GridColumn.Width = new GridLength(0);
        IconGrid.Visibility = Visibility.Collapsed;
        ListPane.BorderThickness = new Thickness(0);
    }

    /// <summary>폴더로 이동해 내용을 채운다. 목록 스캔은 백그라운드, UI 채우기는 이어서.</summary>
    public async void NavigateTo(string folder, IReadOnlyList<string> extensions)
    {
        _extensions = extensions;
        _folder = folder;
        PathText.Text = folder;
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
            return;
        }

        if (seq != _loadSeq) return; // 그새 다른 폴더로 이동함

        Fill(entries);
        _ = LoadThumbnailsAsync(seq);
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

    /// <summary>그리드 타일: 썸네일 자리(우선 글리프, 이후 비동기 교체) + 이름 2줄.</summary>
    private static GridViewItem MakeGridItem(ExplorerListing.Entry entry)
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

        return new GridViewItem { Content = panel, Tag = entry };
    }

    /// <summary>리스트 행: 아이콘 + 이름 + 크기(파일만).</summary>
    private static ListViewItem MakeListItem(ExplorerListing.Entry entry)
    {
        var row = new Grid { ColumnSpacing = 8 };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
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
        var size = new TextBlock
        {
            Text = entry.IsFolder ? string.Empty : ExplorerListing.FormatSize(entry.Size),
            FontSize = 11,
            Opacity = 0.6,
            VerticalAlignment = VerticalAlignment.Center,
        };
        Grid.SetColumn(size, 2);

        row.Children.Add(icon);
        row.Children.Add(name);
        row.Children.Add(size);
        ToolTipService.SetToolTip(row, entry.Name);

        return new ListViewItem { Content = row, Tag = entry };
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

    /// <summary>클릭 2회(500ms 내 같은 항목) = 더블클릭: 폴더 진입 또는 파일 열기.</summary>
    private void OnItemClick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is not FrameworkElement { Tag: ExplorerListing.Entry entry }) return;

        var now = DateTime.UtcNow;
        var isDouble = _lastClick is { } last && last.Path == entry.Path &&
                       (now - last.At).TotalMilliseconds < DoubleClickMs;
        _lastClick = (entry.Path, now);
        if (!isDouble) return;

        _lastClick = null;
        if (entry.IsFolder) NavigateTo(entry.Path, _extensions);
        else FileActivated?.Invoke(entry.Path);
    }

    private void OnUpClicked(object sender, RoutedEventArgs e)
    {
        if (Directory.GetParent(_folder) is { } parent)
            NavigateTo(parent.FullName, _extensions);
    }
}
