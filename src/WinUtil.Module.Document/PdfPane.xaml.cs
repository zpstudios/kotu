using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.Data.Pdf;
using Windows.Storage;
using Windows.Storage.Streams;

namespace WinUtil.Module.Document;

/// <summary>
/// PDF 뷰어 패널(A16). OS 내장 Windows.Data.Pdf로 페이지를 비트맵 렌더한다 —
/// 외부 네이티브 의존성 없음(라이선스·배포 부담 없음), unpackaged 지원.
/// 페이지 크기는 열 때 전부 훑어 레이아웃을 확정하고, 실제 렌더는 ListView 가상화로
/// 화면에 들어온 페이지만 지연 수행한다(리사이클 시 비트맵 해제 — 메모리 상한 유지).
/// 렌더 폭은 모니터 배율(RasterizationScale)을 곱해 선명하게. 암호 PDF는 물어보고 재시도.
/// </summary>
public sealed partial class PdfPane : UserControl
{
    /// <summary>스크롤 기준 현재 페이지/전체 (1-base). 문서를 내리면 (0, 0).</summary>
    public event Action<int, int>? PageChanged;

    private PdfDocument? _doc;
    private int _loadSeq;                    // 늦은 렌더·이전 문서 결과 무시용
    private List<PageItem> _items = [];
    private double[] _pageOffsets = [];      // 페이지별 누적 세로 오프셋(줌 1 기준)
    private ScrollViewer? _scroll;           // ListView 내장 ScrollViewer (지연 탐색)

    private sealed class PageItem
    {
        public int Index;      // 0-base 페이지 번호
        public double Width;   // 표시 크기(DIP) — 레이아웃 고정용
        public double Height;
    }

    public PdfPane() => InitializeComponent();

    /// <summary>문서를 열어 페이지 목록을 구성한다. 실패(암호 취소 포함)면 false.</summary>
    public async Task<bool> LoadAsync(string path)
    {
        var seq = ++_loadSeq;
        PdfDocument doc;
        try
        {
            doc = await LoadDocumentAsync(path);
        }
        catch (OperationCanceledException)
        {
            return false; // 암호 입력 취소
        }
        catch (Exception ex)
        {
            await ShowMessageAsync("Failed to open PDF", ex.Message);
            return false;
        }
        if (seq != _loadSeq) return false; // 그새 다른 문서/Clear

        _doc = doc;

        // 표시 폭: 패널 폭에 맞추되 과대 렌더 방지 상한. 레이아웃 전이면 A4쯤으로.
        var width = ActualWidth > 100 ? Math.Min(ActualWidth - 48, 1100) : 900;
        var items = new List<PageItem>((int)doc.PageCount);
        var offsets = new double[doc.PageCount];
        double y = 0;
        for (var i = 0; i < doc.PageCount; i++)
        {
            using var page = doc.GetPage((uint)i); // Size만 읽는다 — 렌더는 지연
            var aspect = page.Size.Height / Math.Max(1, page.Size.Width);
            var item = new PageItem { Index = i, Width = width, Height = width * aspect };
            items.Add(item);
            offsets[i] = y;
            y += item.Height + 16; // ItemTemplate 상하 마진 8+8
        }
        _items = items;
        _pageOffsets = offsets;
        PageList.ItemsSource = items;
        PageChanged?.Invoke(1, items.Count);
        HookScroll();
        return true;
    }

    /// <summary>문서를 내린다(텍스트 파일로 전환 등). 진행 중 렌더는 시퀀스로 무효화.</summary>
    public void Clear()
    {
        _loadSeq++;
        PageList.ItemsSource = null;
        _items = [];
        _pageOffsets = [];
        _doc = null;
        PageChanged?.Invoke(0, 0);
    }

    /// <summary>첫 시도 실패는 암호 PDF로 보고 물어본 뒤 재시도. 취소는 OperationCanceled.</summary>
    private async Task<PdfDocument> LoadDocumentAsync(string path)
    {
        var file = await StorageFile.GetFileFromPathAsync(path);
        try
        {
            return await PdfDocument.LoadFromFileAsync(file);
        }
        catch
        {
            var password = await PromptPasswordAsync()
                ?? throw new OperationCanceledException();
            return await PdfDocument.LoadFromFileAsync(file, password); // 또 실패 → 호출부 에러 표시
        }
    }

    // ---------- 지연 렌더 (ListView 가상화) ----------

    private void OnContainerContentChanging(ListViewBase sender, ContainerContentChangingEventArgs args)
    {
        if (args.ItemContainer.ContentTemplateRoot is not Border border) return;
        var image = (Image)border.Child;
        if (args.InRecycleQueue)
        {
            image.Source = null; // 화면 밖 페이지 비트맵 해제
            return;
        }

        // 렌더 전에도 페이지 크기로 자리를 잡아 스크롤 길이가 흔들리지 않게 한다
        var item = (PageItem)args.Item;
        image.Width = item.Width;
        image.Height = item.Height;
        if (args.Phase == 0) args.RegisterUpdateCallback(OnRenderPhase);
    }

    private async void OnRenderPhase(ListViewBase sender, ContainerContentChangingEventArgs args)
    {
        if (_doc is null || args.ItemContainer.ContentTemplateRoot is not Border border) return;
        var seq = _loadSeq;
        var item = (PageItem)args.Item;
        var image = (Image)border.Child;
        try
        {
            var scale = XamlRoot?.RasterizationScale ?? 1.0; // 모니터 배율만큼 크게 렌더 → 선명
            using var stream = new InMemoryRandomAccessStream();
            using (var page = _doc.GetPage((uint)item.Index))
            {
                await page.RenderToStreamAsync(stream,
                    new PdfPageRenderOptions { DestinationWidth = (uint)(item.Width * scale) });
            }
            // 문서가 바뀌었거나 컨테이너가 다른 페이지로 재활용됐으면 버린다
            if (seq != _loadSeq || !ReferenceEquals(args.ItemContainer.Content, item)) return;

            var bitmap = new BitmapImage();
            await bitmap.SetSourceAsync(stream);
            if (seq != _loadSeq || !ReferenceEquals(args.ItemContainer.Content, item)) return;
            image.Source = bitmap;
        }
        catch
        {
            // 페이지 하나의 렌더 실패가 전체 문서 보기를 막으면 안 된다 — 빈 페이지로 둔다.
        }
    }

    // ---------- 현재 페이지 추적 ----------

    private void HookScroll()
    {
        _scroll ??= FindScrollViewer(PageList);
        if (_scroll is null) return;
        _scroll.ViewChanged -= OnViewChanged; // 중복 구독 방지
        _scroll.ViewChanged += OnViewChanged;
    }

    private void OnViewChanged(object? sender, ScrollViewerViewChangedEventArgs e)
    {
        if (_scroll is null || _items.Count == 0) return;
        // 뷰포트 세로 중앙이 걸친 페이지 = 현재 페이지. 오프셋은 줌 배율을 되돌려 비교.
        var center = (_scroll.VerticalOffset + _scroll.ViewportHeight / 2)
                     / Math.Max(0.1, _scroll.ZoomFactor);
        var idx = Array.BinarySearch(_pageOffsets, center);
        if (idx < 0) idx = ~idx - 1;
        idx = Math.Clamp(idx, 0, _items.Count - 1);
        PageChanged?.Invoke(idx + 1, _items.Count);
    }

    private static ScrollViewer? FindScrollViewer(DependencyObject root)
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is ScrollViewer viewer) return viewer;
            if (FindScrollViewer(child) is { } nested) return nested;
        }
        return null;
    }

    // ---------- 다이얼로그 ----------

    private async Task<string?> PromptPasswordAsync()
    {
        if (XamlRoot is null) return null;
        var box = new PasswordBox();
        var dialog = new ContentDialog
        {
            Title = "Password required",
            Content = new StackPanel
            {
                Spacing = 8,
                Children =
                {
                    new TextBlock { Text = "This PDF appears to be password-protected." },
                    box,
                },
            },
            PrimaryButtonText = "Open",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Primary,
            XamlRoot = XamlRoot,
        };
        return await dialog.ShowAsync() == ContentDialogResult.Primary && box.Password.Length > 0
            ? box.Password
            : null;
    }

    private async Task ShowMessageAsync(string title, string message)
    {
        if (XamlRoot is null) return;
        var dialog = new ContentDialog
        {
            Title = title,
            Content = message,
            CloseButtonText = "OK",
            XamlRoot = XamlRoot,
        };
        await dialog.ShowAsync();
    }
}
