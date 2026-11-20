using KOTU.Ui;
using Xunit;

namespace KOTU.Core.Tests;

public class ToolbarIconGeometryTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void FitIconsShareViewportAndStillFitButtonContent(bool thin)
    {
        foreach (var kind in Enum.GetValues<ToolbarIconKind>())
            Assert.Equal(kind is ToolbarIconKind.OriginalRatio or ToolbarIconKind.FitContain or
                ToolbarIconKind.FitWidth or ToolbarIconKind.FitHeight ? 24 : 18, ToolbarIconGeometry.ViewportSize(kind));

        foreach (var kind in new[] { ToolbarIconKind.OriginalRatio, ToolbarIconKind.FitContain,
            ToolbarIconKind.FitWidth, ToolbarIconKind.FitHeight })
        {
            var ink = ToolbarIconGeometry.Create(kind, thin).SelectMany(c => c).ToArray();
            Assert.All(ink, p => { Assert.InRange(p.X, 2, 22); Assert.InRange(p.Y, 2, 22); });
            Assert.Equal(24, ink.Min(p => p.X) + ink.Max(p => p.X), precision: 6);
            Assert.Equal(24, ink.Min(p => p.Y) + ink.Max(p => p.Y), precision: 6);
            Assert.True(ToolbarIconGeometry.ViewportSize(kind) <= 30 - 2 * (1 + 2));
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void AllPaintedContoursStayInsideTheirViewportWithConsistentWinding(bool thin)
    {
        foreach (var kind in Enum.GetValues<ToolbarIconKind>())
        {
            var contours = ToolbarIconGeometry.Create(kind, thin);
            Assert.NotEmpty(contours);
            foreach (var contour in contours)
            {
                Assert.All(contour, p =>
                {
                    Assert.InRange(p.X, 0, ToolbarIconGeometry.ViewportSize(kind));
                    Assert.InRange(p.Y, 0, ToolbarIconGeometry.ViewportSize(kind));
                });
                // 둥근 선을 겹쳐 그리므로 Nonzero 채움에서 접합부가 사라지지 않아야 한다.
                Assert.True(Area(contour) > 0, $"Reversed or empty contour in {kind}");
            }
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void TransportHasSamePaintedHeightAndVerticalCenter(bool thin)
    {
        foreach (var kind in new[] { ToolbarIconKind.Play, ToolbarIconKind.Pause, ToolbarIconKind.Previous, ToolbarIconKind.Next })
        {
            var points = ToolbarIconGeometry.Create(kind, thin).SelectMany(c => c).ToArray();
            var top = points.Min(p => p.Y);
            var bottom = points.Max(p => p.Y);
            Assert.InRange(top, 1.75, 2.4);
            Assert.InRange(bottom, 15.6, 16.25);
            Assert.Equal(18, top + bottom, precision: 5);
            if (kind == ToolbarIconKind.Play)
            {
                Assert.InRange(points.Average(p => p.X), 8.5, 9.5);
                Assert.Equal(9, points.Average(p => p.Y), precision: 5);
            }
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void PreviousAndNextAreMirrorsAndEachRequestOwnsItsData(bool thin)
    {
        var previous = ToolbarIconGeometry.Create(ToolbarIconKind.Previous, thin);
        var next = ToolbarIconGeometry.Create(ToolbarIconKind.Next, thin);
        var reflected = previous.SelectMany(c => c).Select(p => Rounded(18 - p.X, p.Y)).ToHashSet();
        Assert.True(reflected.SetEquals(next.SelectMany(c => c).Select(p => Rounded(p.X, p.Y))));
        var again = ToolbarIconGeometry.Create(ToolbarIconKind.Previous, thin);
        previous[0][0] = new IconPoint(99, 99);
        Assert.NotEqual(previous[0][0], again[0][0]);

        static IconPoint Rounded(double x, double y) => new(Math.Round(x, 6), Math.Round(y, 6));
    }

    [Fact]
    public void ThinProfileReducesStrokeCoverageForEveryIcon()
    {
        foreach (var kind in Enum.GetValues<ToolbarIconKind>())
        {
            var regular = ToolbarIconGeometry.Create(kind).Sum(Area);
            var thin = ToolbarIconGeometry.Create(kind, thin: true).Sum(Area);
            Assert.InRange(thin, 0.01, regular * 0.95);
        }
    }

    [Fact]
    public void RatioOutlineIsCenteredAndPreviewUsesProductionContours()
    {
        var outline = ToolbarIconGeometry.Create(ToolbarIconKind.OriginalRatio).SelectMany(c => c).ToArray();
        Assert.Equal(24, outline.Min(p => p.X) + outline.Max(p => p.X), precision: 6);
        Assert.Equal(24, outline.Min(p => p.Y) + outline.Max(p => p.Y), precision: 6);
        if (Environment.GetEnvironmentVariable("KOTU_ICON_PREVIEW_PATH") is { Length: > 0 } path)
        {
            var icons = Enum.GetValues<ToolbarIconKind>().Select(kind => new { Name = kind.ToString(), Size = ToolbarIconGeometry.ViewportSize(kind), FillRule = "nonzero", Contours = ToolbarIconGeometry.Create(kind) });
            File.WriteAllText(path, System.Text.Json.JsonSerializer.Serialize(icons));
        }
    }

    private static double Area(IconPoint[] contour) =>
        contour.Select((p, i) => p.X * contour[(i + 1) % contour.Length].Y
            - p.Y * contour[(i + 1) % contour.Length].X).Sum() / 2;
}
