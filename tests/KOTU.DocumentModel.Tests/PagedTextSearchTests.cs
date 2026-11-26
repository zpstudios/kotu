using System.Text;
using KOTU.DocumentModel;
using KOTU.Module.Document;
using Xunit;

namespace KOTU.DocumentModel.Tests;

public sealed class PagedTextSearchTests
{
    [Fact]
    public void LiteralUnicodeSearchKeepsUtf16OffsetsAndPageBoundaries()
    {
        var document = new PagedTextDocument([
            new("aa😀한글 ALPHA alpha aaaa", []), new("한글 alpha", [])], true);
        Assert.Equal([new PageSearchHit(0, 4, 2), new PageSearchHit(1, 0, 2)], document.Find("한글").Hits);
        Assert.Equal(3, document.Find("alpha").Hits.Count);
        Assert.Equal(3, document.Find("aa").Hits.Count);
        Assert.Empty(document.Find("aaaa한글").Hits);
        Assert.Empty(document.Find(string.Empty).Hits);
        Assert.Throws<ArgumentOutOfRangeException>(() => document.Find(new string('x', 1025)));
    }

    [Fact]
    public void SearchCapsResultsAndChecksCancellation()
    {
        var document = new PagedTextDocument([new(new string('x', PagedTextDocument.MaxMatches + 1), [])], true);
        var result = document.Find("x");
        Assert.Equal(PagedTextDocument.MaxMatches, result.Hits.Count);
        Assert.True(result.Limited);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        Assert.Throws<OperationCanceledException>(() => document.Find("x", cancellation.Token));
    }

    [Fact]
    public void RectanglesMergeVisibleGlyphsAndIgnoreInvalidGeometry()
    {
        var document = new PagedTextDocument([new("abc", [new(0, 1, new(.1, .2, .02, .03)),
            new(1, 1, new(.125, .2, .02, .03)), new(2, 1, new(double.NaN, .2, .02, .03))])], true);
        var rectangle = Assert.Single(document.Rectangles(new(0, 0, 3)));
        Assert.Equal(.045, rectangle.Width, 6);
        Assert.True(rectangle.IsValid);
        Assert.Empty(document.Rectangles(new(5, 0, 1)));
    }

    [Fact]
    public void PdfReaderInfersWordGapsButDoesNotJoinSeparateLines()
    {
        var document = PdfSearchReader.Read(Pdf("BT /F1 20 Tf 40 300 Td (spaced) Tj 80 0 Td (phrase) Tj 0 -40 Td (next) Tj ET"), null, 1);
        Assert.Equal("spaced phrase\nnext", document.Pages[0].Text);
        var hit = Assert.Single(document.Find("spaced phrase").Hits);
        Assert.NotEmpty(document.Rectangles(hit));
        Assert.Empty(document.Find("phrasenext").Hits);
    }

    [Fact]
    public void PdfWithoutTextIsDistinctFromNoMatchesAndBadDocuments()
    {
        var empty = PdfSearchReader.Read(Pdf("0 0 100 100 re f"), null, 1);
        Assert.False(empty.HasText);
        Assert.Null(empty.Notice);
        Assert.Empty(empty.Find("alpha").Hits);
        var text = PdfSearchReader.Read(Pdf("BT /F1 20 Tf 40 300 Td (alpha) Tj ET"), null, 1);
        Assert.True(text.HasText);
        Assert.Empty(text.Find("missing").Hits);
        Assert.Throws<InvalidDataException>(() => PdfSearchReader.Read(Pdf(""), null, 2));
        Assert.ThrowsAny<Exception>(() => PdfSearchReader.Read([1, 2, 3], null, 1));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(90)]
    [InlineData(180)]
    [InlineData(270)]
    public void PdfCroppedRotatedGlyphsMapOnceIntoDisplayedPage(int rotation)
    {
        const string content = "BT /F1 20 Tf 40 300 Td (alpha) Tj ET";
        var original = PdfSearchReader.Read(Pdf(content), null, 1);
        var originalBounds = Bounds(original.Rectangles(Assert.Single(original.Find("alpha").Hits)));
        var rotated = PdfSearchReader.Read(Pdf(content, rotation), null, 1);
        var actual = Bounds(rotated.Rectangles(Assert.Single(rotated.Find("alpha").Hits)));
        var expected = rotation switch
        {
            90 => new SearchRectangle(1 - originalBounds.Y - originalBounds.Height, originalBounds.X, originalBounds.Height, originalBounds.Width),
            180 => new SearchRectangle(1 - originalBounds.X - originalBounds.Width, 1 - originalBounds.Y - originalBounds.Height, originalBounds.Width, originalBounds.Height),
            270 => new SearchRectangle(originalBounds.Y, 1 - originalBounds.X - originalBounds.Width, originalBounds.Height, originalBounds.Width),
            _ => originalBounds
        };
        Assert.True(actual.IsValid);
        Assert.Equal(expected.X, actual.X, 5);
        Assert.Equal(expected.Y, actual.Y, 5);
        Assert.Equal(expected.Width, actual.Width, 5);
        Assert.Equal(expected.Height, actual.Height, 5);
    }

    private static SearchRectangle Bounds(IReadOnlyList<SearchRectangle> rectangles)
    {
        Assert.NotEmpty(rectangles);
        var x = rectangles.Min(r => r.X);
        var y = rectangles.Min(r => r.Y);
        return new(x, y, rectangles.Max(r => r.X + r.Width) - x, rectangles.Max(r => r.Y + r.Height) - y);
    }

    private static byte[] Pdf(string content, int rotation = 0)
    {
        string[] objects = ["<< /Type /Catalog /Pages 2 0 R >>", "<< /Type /Pages /Count 1 /Kids [3 0 R] >>",
            $"<< /Type /Page /Parent 2 0 R /MediaBox [0 0 300 400] /CropBox [20 30 260 360] /Rotate {rotation} /Resources << /Font << /F1 4 0 R >> >> /Contents 5 0 R >>",
            "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>", $"<< /Length {content.Length} >>\nstream\n{content}\nendstream"];
        var output = new StringBuilder("%PDF-1.4\n");
        var offsets = new List<int>();
        for (var i = 0; i < objects.Length; i++)
        {
            offsets.Add(output.Length);
            output.Append($"{i + 1} 0 obj\n{objects[i]}\nendobj\n");
        }
        var xref = output.Length;
        output.Append($"xref\n0 {objects.Length + 1}\n0000000000 65535 f \n");
        foreach (var offset in offsets) output.Append($"{offset:D10} 00000 n \n");
        output.Append($"trailer\n<< /Size {objects.Length + 1} /Root 1 0 R >>\nstartxref\n{xref}\n%%EOF\n");
        return Encoding.ASCII.GetBytes(output.ToString());
    }
}
