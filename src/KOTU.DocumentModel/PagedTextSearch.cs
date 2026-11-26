namespace KOTU.DocumentModel;

/// <summary>Page coordinates use a top-left origin and are normalized to the visible page.</summary>
public readonly record struct SearchRectangle(double X, double Y, double Width, double Height)
{
    public bool IsValid => double.IsFinite(X) && double.IsFinite(Y) && double.IsFinite(Width) &&
        double.IsFinite(Height) && X >= 0 && Y >= 0 && Width > 0 && Height > 0 && X + Width <= 1.001 && Y + Height <= 1.001;
}

public readonly record struct SearchGlyph(int Start, int Length, SearchRectangle Rectangle);
public sealed record SearchPage(string Text, IReadOnlyList<SearchGlyph> Glyphs, double Width = 0, double Height = 0);
public readonly record struct PageSearchHit(int Page, int Start, int Length);
public sealed record PageSearchResults(IReadOnlyList<PageSearchHit> Hits, bool Limited);

/// <summary>Immutable page snapshots; scanning and rectangle lookup run on the owning view's worker.</summary>
public sealed record PagedTextDocument(IReadOnlyList<SearchPage> Pages, bool HasText, string? Notice = null)
{
    public const int MaxQueryLength = 1024;
    public const int MaxMatches = 10000;
    public const int MaxRectangles = 256;

    public PageSearchResults Find(string query, CancellationToken cancellation = default)
    {
        if (query.Length > MaxQueryLength) throw new ArgumentOutOfRangeException(nameof(query));
        var hits = new List<PageSearchHit>();
        if (query.Length == 0) return new(hits, false);
        for (var page = 0; page < Pages.Count; page++)
        {
            var text = Pages[page].Text;
            for (var position = 0; position <= text.Length - query.Length;)
            {
                cancellation.ThrowIfCancellationRequested();
                var found = text.IndexOf(query, position, StringComparison.OrdinalIgnoreCase);
                if (found < 0) break;
                if (hits.Count == MaxMatches) return new(hits, true);
                hits.Add(new(page, found, query.Length));
                position = found + query.Length;
            }
        }
        cancellation.ThrowIfCancellationRequested();
        return new(hits, false);
    }

    public IReadOnlyList<SearchRectangle> Rectangles(PageSearchHit hit, CancellationToken cancellation = default)
    {
        if (hit.Page < 0 || hit.Page >= Pages.Count) return [];
        var glyphs = Pages[hit.Page].Glyphs;
        int low = 0, high = glyphs.Count;
        while (low < high)
        {
            var middle = low + (high - low) / 2;
            if (glyphs[middle].Start + glyphs[middle].Length <= hit.Start) low = middle + 1;
            else high = middle;
        }
        var rectangles = new List<SearchRectangle>();
        for (var i = low; i < glyphs.Count && glyphs[i].Start < hit.Start + hit.Length; i++)
        {
            cancellation.ThrowIfCancellationRequested();
            var rectangle = glyphs[i].Rectangle;
            if (!rectangle.IsValid) continue;
            // Merge adjacent glyphs on a line to keep even long matches cheap to display.
            if (rectangles.Count > 0 && rectangles[^1] is var last &&
                Math.Abs(last.Y - rectangle.Y) < Math.Max(last.Height, rectangle.Height) * .35 &&
                rectangle.X >= last.X && rectangle.X - (last.X + last.Width) < Math.Max(last.Height, rectangle.Height) * .6)
            {
                var top = Math.Min(last.Y, rectangle.Y);
                rectangles[^1] = new(last.X, top, Math.Max(last.X + last.Width, rectangle.X + rectangle.Width) - last.X,
                    Math.Max(last.Y + last.Height, rectangle.Y + rectangle.Height) - top);
            }
            else if (rectangles.Count < MaxRectangles) rectangles.Add(rectangle);
        }
        return rectangles;
    }
}
