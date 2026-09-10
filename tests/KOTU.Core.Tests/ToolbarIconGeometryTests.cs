using KOTU.Ui;
using Xunit;

namespace KOTU.Core.Tests;

public class ToolbarIconGeometryTests
{
    [Fact]
    public void OnlyOriginalExpandsAndStillFitsButtonContent()
    {
        foreach (var kind in Enum.GetValues<ToolbarIconKind>())
            Assert.Equal(kind == ToolbarIconKind.OriginalRatio ? 24 : 18, ToolbarIconGeometry.ViewportSize(kind));
        var contours = ToolbarIconGeometry.Create(ToolbarIconKind.OriginalRatio);
        Assert.Equal(22, contours[0].Max(p => p.X) - contours[0].Min(p => p.X));
        Assert.Equal(18, contours[0].Max(p => p.Y) - contours[0].Min(p => p.Y));
        foreach (var digit in contours.Skip(2).Take(2))
        {
            Assert.Equal(10, digit.Max(p => p.Y) - digit.Min(p => p.Y));
            Assert.All(digit, p => { Assert.InRange(p.X, 3, 21); Assert.InRange(p.Y, 5, 19); });
        }
        Assert.True(ToolbarIconGeometry.ViewportSize(ToolbarIconKind.OriginalRatio) <= 32 - 2 * (1 + 2));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    [InlineData(6)]
    public void PaintedContoursStayInsideSharedViewportAndHaveArea(int kind)
    {
        foreach (var contour in ToolbarIconGeometry.Create((ToolbarIconKind)kind))
        {
            Assert.All(contour, p =>
            {
                Assert.InRange(p.X, 0, ToolbarIconGeometry.ViewportSize((ToolbarIconKind)kind));
                Assert.InRange(p.Y, 0, ToolbarIconGeometry.ViewportSize((ToolbarIconKind)kind));
            });
            var area = contour.Select((p, i) => p.X * contour[(i + 1) % contour.Length].Y
                - p.Y * contour[(i + 1) % contour.Length].X).Sum() / 2;
            Assert.NotEqual(0, area);
        }
    }

    [Fact]
    public void TransportHasSamePaintedHeightAndVerticalCenter()
    {
        foreach (var kind in new[] { ToolbarIconKind.Play, ToolbarIconKind.Pause, ToolbarIconKind.Previous, ToolbarIconKind.Next })
        {
            var points = ToolbarIconGeometry.Create(kind).SelectMany(c => c).ToArray();
            Assert.Equal(2, points.Min(p => p.Y));
            Assert.Equal(16, points.Max(p => p.Y));
        }
        var triangle = ToolbarIconGeometry.Create(ToolbarIconKind.Play)[0];
        Assert.InRange(triangle.Average(p => p.X), 8.5, 9.5);
        Assert.Equal(9, triangle.Average(p => p.Y));
    }

    [Fact]
    public void PreviousAndNextAreMirrorsAndEachRequestOwnsItsData()
    {
        var previous = ToolbarIconGeometry.Create(ToolbarIconKind.Previous);
        var next = ToolbarIconGeometry.Create(ToolbarIconKind.Next);
        var reflected = previous.SelectMany(c => c).Select(p => new IconPoint(18 - p.X, p.Y)).ToHashSet();
        Assert.True(reflected.SetEquals(next.SelectMany(c => c)));
        var again = ToolbarIconGeometry.Create(ToolbarIconKind.Previous);
        previous[0][0] = new IconPoint(99, 99);
        Assert.NotEqual(previous[0][0], again[0][0]);
    }

    [Fact]
    public void RatioOutlineIsCenteredAndPreviewUsesProductionContours()
    {
        var outline = ToolbarIconGeometry.Create(ToolbarIconKind.OriginalRatio)[0];
        Assert.Equal(24, outline.Min(p => p.X) + outline.Max(p => p.X));
        Assert.Equal(24, outline.Min(p => p.Y) + outline.Max(p => p.Y));
        // 요청된 디자인 검토 산출물만 내보낸다. WinUI 화면 캡처가 아니라 같은 원본 좌표다.
        if (Environment.GetEnvironmentVariable("KOTU_ICON_PREVIEW_PATH") is { Length: > 0 } path)
        {
            var icons = Enum.GetValues<ToolbarIconKind>().Select(kind => new { Name = kind.ToString(), Size = ToolbarIconGeometry.ViewportSize(kind), Contours = ToolbarIconGeometry.Create(kind) });
            File.WriteAllText(path, System.Text.Json.JsonSerializer.Serialize(icons));
        }
    }
}
