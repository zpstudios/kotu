using KOTU.DocumentModel;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.Storage.Streams;

namespace KOTU.Module.Document;

public sealed partial class PdfPane
{
    private byte[]? _searchBytes;
    private string? _searchPassword;
    private IRandomAccessStream? _renderStream;
    private (double Width, double Height)[] _searchPageSizes = [];
    private CancellationTokenSource _pdfSearchLifetime = new();
    private Task<PagedTextDocument>? _pdfIndexTask;
    private PagedTextDocument? _pdfIndex;
    private PageSearchResults? _pdfResults;
    private int _highlightPage = -1;
    private IReadOnlyList<SearchRectangle> _highlightRectangles = [];
    private long _pdfSearchRevision;

    private void SuspendPdfSearch()
    {
        _pdfSearchLifetime.Cancel();
        _pdfIndexTask = null;
        _pdfIndex = null;
        ((IReadOnlySearchSource)this).ClearSearch();
    }

    private void ResetPdfSearch()
    {
        SuspendPdfSearch();
        // Tokens already captured by running workers remain usable after their source is disposed.
        _pdfSearchLifetime.Dispose();
        _pdfSearchLifetime = new();
        _searchBytes = null;
        _searchPassword = null;
        _searchPageSizes = [];
    }

    async Task<ReadOnlySearchSummary> IReadOnlySearchSource.SearchAsync(string query, CancellationToken cancellation)
    {
        if (_doc is null) return new(0, Notice: "PDF is still loading.");
        if (_searchBytes is not { } bytes) return new(0, Notice: "Search unavailable: PDF exceeds 256 MiB.");
        var generation = _loadSeq;
        var revision = _pdfSearchRevision;
        var count = _items.Count;
        if (_pdfSearchLifetime.IsCancellationRequested)
        {
            _pdfSearchLifetime.Dispose();
            _pdfSearchLifetime = new();
        }
        var lifetime = _pdfSearchLifetime.Token;
        var password = _searchPassword;
        var sizes = _searchPageSizes;
        _pdfIndexTask ??= Worker.Run(_ =>
        {
            var indexed = PdfSearchReader.Read(bytes, password, count, lifetime);
            for (var i = 0; i < indexed.Pages.Count; i++)
            {
                lifetime.ThrowIfCancellationRequested();
                var page = indexed.Pages[i];
                // PdfPig points and Windows.Data.Pdf DIP must describe the same visible page.
                if (Math.Abs(page.Width * 4 / 3 - sizes[i].Width) > Math.Max(2, sizes[i].Width * .01) ||
                    Math.Abs(page.Height * 4 / 3 - sizes[i].Height) > Math.Max(2, sizes[i].Height * .01))
                    throw new InvalidDataException("PDF search geometry differs from its displayed pages.");
            }
            return indexed;
        }, lifetime);
        var document = await _pdfIndexTask;
        cancellation.ThrowIfCancellationRequested();
        if (generation != _loadSeq || revision != _pdfSearchRevision) throw new OperationCanceledException();
        var result = await Worker.Run(_ => document.Find(query, cancellation), cancellation);
        cancellation.ThrowIfCancellationRequested();
        if (generation != _loadSeq || revision != _pdfSearchRevision) throw new OperationCanceledException();
        _pdfIndex = document;
        _pdfResults = result;
        return new(result.Hits.Count, result.Limited, document.Notice ?? (!document.HasText ? "No searchable text in this PDF." : null));
    }

    async Task IReadOnlySearchSource.SelectMatchAsync(int index, CancellationToken cancellation)
    {
        if (_pdfResults is not { } result || _pdfIndex is not { } document || index < 0 || index >= result.Hits.Count) return;
        var generation = _loadSeq;
        var revision = _pdfSearchRevision;
        var hit = result.Hits[index];
        var rectangles = await Worker.Run(_ => document.Rectangles(hit, cancellation), cancellation);
        cancellation.ThrowIfCancellationRequested();
        if (generation != _loadSeq || revision != _pdfSearchRevision || !ReferenceEquals(result, _pdfResults)) return;
        _highlightPage = hit.Page;
        _highlightRectangles = rectangles;
        RefreshSearchHighlight();
        HookScroll();
        if (_scroll is null || hit.Page >= _items.Count) return;
        var item = _items[hit.Page];
        var zoom = _scroll.ZoomFactor;
        var top = _pageOffsets[hit.Page] + 9 + (rectangles.Count > 0 ? rectangles[0].Y * item.Height : 0);
        var left = rectangles.Count > 0 ? rectangles[0].X * item.Width : 0;
        // Preserve the user's zoom/Fit mode and expose the match in both axes at high zoom.
        var pageLeft = _alignPagesLeft ? 9 : Math.Max(9, (_scroll.ViewportWidth / zoom - item.Width) / 2);
        _scroll.ChangeView(Math.Max(0, (pageLeft + left) * zoom - _scroll.ViewportWidth * .2),
            Math.Max(0, top * zoom - VisibleViewportHeight() * .2), null, disableAnimation: true);
    }

    void IReadOnlySearchSource.ClearSearch()
    {
        _pdfSearchRevision++;
        _pdfResults = null;
        _highlightPage = -1;
        _highlightRectangles = [];
        RefreshSearchHighlight();
    }

    void IReadOnlySearchSource.FocusDocument() => PageList.Focus(FocusState.Programmatic);

    private void RefreshSearchHighlight()
    {
        // Only realized containers: never create a visual for an off-screen page.
        if (PageList.ItemsPanelRoot is not { } panel) return;
        foreach (var child in panel.Children)
            if (child is ListViewItem { Content: PageItem item, ContentTemplateRoot: Border { Child: Grid grid } })
                PaintSearchHighlight((Canvas)grid.Children[1], item);
    }

    private void PaintSearchHighlight(Canvas canvas, PageItem item)
    {
        canvas.Children.Clear();
        if (item.Index != _highlightPage) return;
        foreach (var rectangle in _highlightRectangles)
        {
            var marker = new Microsoft.UI.Xaml.Shapes.Rectangle
            {
                Width = rectangle.Width * item.Width,
                Height = Math.Max(2, rectangle.Height * item.Height),
                Fill = new SolidColorBrush(Windows.UI.Color.FromArgb(100, 255, 196, 0)),
                Stroke = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 136, 76, 0)),
                StrokeThickness = 1
            };
            Canvas.SetLeft(marker, rectangle.X * item.Width);
            Canvas.SetTop(marker, rectangle.Y * item.Height);
            canvas.Children.Add(marker);
        }
    }
}
