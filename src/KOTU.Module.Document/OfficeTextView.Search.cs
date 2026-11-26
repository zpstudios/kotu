using KOTU.Core.Contracts;
using KOTU.DocumentModel;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Documents;
using Microsoft.UI.Xaml.Media;

namespace KOTU.Module.Document;

public sealed partial class OfficeTextView
{
    public sealed record OfficeTextChunk(string Text, int Start);
    private IReadOnlyList<OfficeTextChunk> _textChunks = [];
    private OfficeTextSearchSource? _textSearchSource;
    private PageSearchHit? _textSearchHit;

    private void InitializeOfficeSearch()
    {
        OfficeFindBar.Bind(this, BottomBar);
        _textSearchSource = new(this);
        BottomBar.SizeChanged += (_, _) => OfficeFindBar.SetLayout(BottomBar.ActualWidth, FilenameFilterSlot.Visibility == Visibility.Visible);
        FilenameFilterSlot.RegisterPropertyChangedCallback(VisibilityProperty, (_, _) =>
            OfficeFindBar.SetLayout(BottomBar.ActualWidth, FilenameFilterSlot.Visibility == Visibility.Visible));
    }

    private void UpdateOfficeSearchSource() => OfficeFindBar.SetSource(_closed ? null : _showPages
        ? _pages is { PageCount: > 0 } ? _pages : null
        : _preview is not null ? _textSearchSource : null);

    public bool TryTextSearchCommand(TextSearchCommand command, bool fromEnter = false, bool repeat = false,
        object? focusScope = null, object? neutralFocus = null)
        => OfficeFindBar.TryCommand(command, fromEnter, repeat, focusScope: focusScope, neutralFocus: neutralFocus);

    private void OnTextContainerChanged(ListViewBase sender, ContainerContentChangingEventArgs args)
    {
        if (args.ItemContainer.ContentTemplateRoot is not TextBlock block) return;
        block.TextHighlighters.Clear();
        if (!args.InRecycleQueue && args.Item is OfficeTextChunk chunk)
        {
            var container = args.ItemContainer;
            DispatcherQueue.TryEnqueue(() =>
            {
                if (!_closed && ReferenceEquals(container.Content, chunk)) HighlightTextChunk(block, chunk);
            });
        }
    }

    private void HighlightTextChunk(TextBlock block, OfficeTextChunk chunk)
    {
        block.TextHighlighters.Clear();
        if (_textSearchHit is not { } hit) return;
        var start = Math.Max(0, hit.Start - chunk.Start);
        var end = Math.Min(chunk.Text.Length, hit.Start + hit.Length - chunk.Start);
        if (end <= start) return;
        var marker = new TextHighlighter
        {
            Background = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 255, 210, 64)),
            Foreground = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 32, 24, 0))
        };
        marker.Ranges.Add(new TextRange { StartIndex = start, Length = end - start });
        block.TextHighlighters.Add(marker);
    }

    private void RefreshTextHighlights()
    {
        if (TextList.ItemsPanelRoot is not { } panel) return;
        foreach (var child in panel.Children)
            if (child is ListViewItem { Content: OfficeTextChunk chunk, ContentTemplateRoot: TextBlock block })
                HighlightTextChunk(block, chunk);
    }

    private void RevealTextMatch(PageSearchHit hit)
    {
        int low = 0, high = _textChunks.Count;
        while (low < high)
        {
            var middle = low + (high - low) / 2;
            if (_textChunks[middle].Start <= hit.Start) low = middle + 1;
            else high = middle;
        }
        var chunkIndex = Math.Max(0, low - 1);
        if (chunkIndex >= _textChunks.Count) return;
        var chunk = _textChunks[chunkIndex];
        _textSearchHit = hit;
        TextList.ScrollIntoView(chunk, ScrollIntoViewAlignment.Leading);
        TextList.UpdateLayout();
        RefreshTextHighlights();
        if (TextList.ContainerFromIndex(chunkIndex) is not ListViewItem { ContentTemplateRoot: TextBlock block }) return;
        // The generated TextBlock has one Run; its ContentStart excludes inline boundary symbols.
        // Query a single native character rectangle, never scan or build visuals for every match.
        var origin = (block.Inlines.FirstOrDefault() as Run)?.ContentStart ?? block.ContentStart;
        var pointer = origin.GetPositionAtOffset(hit.Start - chunk.Start, LogicalDirection.Forward);
        if (pointer is null) return;
        var bounds = pointer.GetCharacterRect(LogicalDirection.Forward);
        if (bounds.Height > 0 && double.IsFinite(bounds.Y))
            block.StartBringIntoView(new BringIntoViewOptions { TargetRect = bounds, VerticalAlignmentRatio = .2, AnimationDesired = false });
    }

    private sealed class OfficeTextSearchSource(OfficeTextView owner) : IReadOnlySearchSource
    {
        private long _revision;
        private PageSearchResults? _results;

        public async Task<ReadOnlySearchSummary> SearchAsync(string query, CancellationToken cancellation)
        {
            if (owner._closed || owner._worker is not { } worker || owner._preview is not { } preview) return new(0);
            var revision = _revision;
            var result = await worker.Run(_ => new PagedTextDocument([new SearchPage(preview.Text, [])], true).Find(query, cancellation), cancellation);
            cancellation.ThrowIfCancellationRequested();
            if (revision != _revision || owner._closed) throw new OperationCanceledException();
            _results = result;
            return new(result.Hits.Count, result.Limited, preview.Truncated ? "Loaded preview only" : null);
        }

        public Task SelectMatchAsync(int index, CancellationToken cancellation)
        {
            cancellation.ThrowIfCancellationRequested();
            if (!owner._closed && _results is { } results && index >= 0 && index < results.Hits.Count)
                owner.RevealTextMatch(results.Hits[index]);
            return Task.CompletedTask;
        }

        public void ClearSearch()
        {
            _revision++;
            _results = null;
            owner._textSearchHit = null;
            owner.RefreshTextHighlights();
        }
        public void FocusDocument() => owner.TextList.Focus(FocusState.Programmatic);
    }
}
