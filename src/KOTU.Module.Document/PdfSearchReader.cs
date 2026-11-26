using System.Text;
using KOTU.DocumentModel;
using UglyToad.PdfPig;
using UglyToad.PdfPig.Content;

namespace KOTU.Module.Document;

/// <summary>Text-only PDF indexing. Windows.Data.Pdf remains the rendering and printing engine.</summary>
public static class PdfSearchReader
{
    public const int MaxSourceBytes = 256 * 1024 * 1024;
    public const int MaxPages = 10000;
    public const int MaxCharacters = 8 * 1024 * 1024;

    public static byte[]? ReadSnapshot(string path, CancellationToken cancellation = default)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (stream.Length > MaxSourceBytes) return null;
        var bytes = new byte[checked((int)stream.Length)];
        var offset = 0;
        while (offset < bytes.Length)
        {
            cancellation.ThrowIfCancellationRequested();
            var read = stream.Read(bytes, offset, Math.Min(128 * 1024, bytes.Length - offset));
            if (read == 0) throw new EndOfStreamException();
            offset += read;
        }
        return bytes;
    }

    public static PagedTextDocument Read(byte[] source, string? password, int expectedPages,
        CancellationToken cancellation = default)
    {
        if (source.Length > MaxSourceBytes) throw new InvalidDataException("PDF exceeds the search size limit.");
        cancellation.ThrowIfCancellationRequested();
        using var document = PdfDocument.Open(source, new ParsingOptions { Password = password ?? string.Empty });
        if (document.NumberOfPages != expectedPages) throw new InvalidDataException("PDF page count differs from the displayed document.");
        var pages = new List<SearchPage>();
        var characters = 0;
        var hasText = false;
        string? notice = null;
        for (var number = 1; number <= document.NumberOfPages; number++)
        {
            cancellation.ThrowIfCancellationRequested();
            if (number > MaxPages || characters >= MaxCharacters)
            {
                notice = "Search limited to the indexed pages and text.";
                break;
            }
            // PdfPig parses one page synchronously; cancellation is checked between pages and glyphs.
            var page = document.GetPage(number);
            if (!double.IsFinite(page.Width) || !double.IsFinite(page.Height) || page.Width <= 0 || page.Height <= 0)
                throw new InvalidDataException("Invalid PDF page dimensions.");
            var text = new StringBuilder();
            var glyphs = new List<SearchGlyph>();
            Letter? previous = null;
            foreach (var letter in page.Letters)
            {
                cancellation.ThrowIfCancellationRequested();
                if (letter.Value.Length == 0) continue;
                if (characters + text.Length + letter.Value.Length + 1 > MaxCharacters)
                {
                    notice = "Search limited to the indexed pages and text.";
                    break;
                }
                var separator = previous is null ? '\0' : Separator(previous, letter);
                if (separator != '\0' && text.Length > 0 && !char.IsWhiteSpace(text[^1]) && !char.IsWhiteSpace(letter.Value[0]))
                    text.Append(separator);
                var start = text.Length;
                text.Append(letter.Value);
                if (!string.IsNullOrWhiteSpace(letter.Value)) hasText = true;
                var bounds = letter.BoundingBox;
                // BoundingBox can be rotated; its Width/Height are not the axis-aligned extents.
                var left = Math.Clamp(Math.Min(Math.Min(bounds.BottomLeft.X, bounds.BottomRight.X), Math.Min(bounds.TopLeft.X, bounds.TopRight.X)) / page.Width, 0, 1);
                var right = Math.Clamp(Math.Max(Math.Max(bounds.BottomLeft.X, bounds.BottomRight.X), Math.Max(bounds.TopLeft.X, bounds.TopRight.X)) / page.Width, 0, 1);
                var top = Math.Clamp(1 - Math.Max(Math.Max(bounds.BottomLeft.Y, bounds.BottomRight.Y), Math.Max(bounds.TopLeft.Y, bounds.TopRight.Y)) / page.Height, 0, 1);
                var bottom = Math.Clamp(1 - Math.Min(Math.Min(bounds.BottomLeft.Y, bounds.BottomRight.Y), Math.Min(bounds.TopLeft.Y, bounds.TopRight.Y)) / page.Height, 0, 1);
                glyphs.Add(new(start, letter.Value.Length, new(left, top, right - left, bottom - top)));
                previous = letter;
            }
            characters += text.Length;
            pages.Add(new(text.ToString(), glyphs, page.Width, page.Height));
            if (notice is not null) break;
        }
        return new(pages, hasText, notice);
    }

    private static char Separator(Letter previous, Letter current)
    {
        var dx = previous.EndBaseLine.X - previous.StartBaseLine.X;
        var dy = previous.EndBaseLine.Y - previous.StartBaseLine.Y;
        var length = Math.Sqrt(dx * dx + dy * dy);
        if (length < .01) return '\0';
        dx /= length;
        dy /= length;
        var gapX = current.StartBaseLine.X - previous.EndBaseLine.X;
        var gapY = current.StartBaseLine.Y - previous.EndBaseLine.Y;
        var size = Math.Max(1, Math.Max(previous.PointSize, current.PointSize));
        if (Math.Abs(gapX * dy - gapY * dx) > size * .55 || gapX * dx + gapY * dy < -size)
            return '\n';
        return gapX * dx + gapY * dy > size * .2 ? ' ' : '\0';
    }
}
