using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.Storage;
using Windows.Storage.FileProperties;
using WinUtil.Core.Routing;

namespace WinUtil.App;

/// <summary>
/// 내장 탐색기 컨트롤 (v0.25.0, docs/explorer-plan.md).
/// 좌 70% 썸네일 그리드 + 우 30% 리스트로 같은 폴더를 두 방식으로 보여준다.
/// 폴더는 전부, 파일은 주입된 담당 확장자만(사용자 확정). 더블클릭: 폴더=진입, 파일=FileActivated.
/// Alt 오버레이용으로는 ConfigureListOnly()로 리스트만 남겨 재사용한다.
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

    public ExplorerPane()
    {
        InitializeComponent();
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
            entries = await Task.Run(() => ExplorerListing.List(folder, extensions));
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
    /// 파일 썸네일을 비동기로 채운다(그리드 타일의 글리프를 이미지로 교체).
    /// GetThumbnailAsync는 항목마다 느릴 수 있으므로 상한을 두고, 폴더 이동 시 중단한다.
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
                var file = await StorageFile.GetFileFromPathAsync(entry.Path);
                using var thumb = await file.GetThumbnailAsync(ThumbnailMode.SingleItem, 96);
                if (seq != _loadSeq) return;
                if (thumb is null || thumb.Size == 0) continue;

                var bitmap = new BitmapImage();
                await bitmap.SetSourceAsync(thumb);

                var host = (Grid)((StackPanel)item.Content).Children[0];
                host.Children.Clear();
                host.Children.Add(new Image
                {
                    Source = bitmap,
                    Stretch = Microsoft.UI.Xaml.Media.Stretch.Uniform,
                });
                loaded++;
            }
            catch
            {
                // 썸네일 실패는 글리프 유지로 충분하다.
            }
        }
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
