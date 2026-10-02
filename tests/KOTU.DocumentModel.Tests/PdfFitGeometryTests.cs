using KOTU.Module.Document;
using Xunit;

namespace KOTU.DocumentModel.Tests;

public sealed class PdfFitGeometryTests
{
    [Theory]
    [InlineData(0, 556, 1000, 44, 556)]
    [InlineData(0, 534, 1000, 66, 534)]
    [InlineData(0, 600, 1000, 44, 600)]
    [InlineData(1000, 556, 1000, 44, 600)]
    [InlineData(-1000, 556, 1000, 44, 600)]
    [InlineData(0, 556, 1000, 0, 600)]
    [InlineData(0, 500, 1000, 44, 600)]
    [InlineData(0, -10, 1000, 610, 0)]
    [InlineData(0, 556, 1000, 43.9999, 556)]
    public void VisibleHeightUsesOnlyAnOverlappingBottomOverlay(double x, double y,
        double width, double height, double expected)
        => Assert.Equal(expected, PdfFitGeometry.VisibleHeight(1000, 600, x, y, width, height));

    [Theory]
    [InlineData(1000, 600, 44)]
    [InlineData(700, 400, 66)]
    [InlineData(1600, 1000, 0)]
    public void LetterPageAndFrameStayAboveTheBar(double width, double height, double bar)
    {
        var visible = PdfFitGeometry.VisibleHeight(width, height, 0, height - bar, width, bar);
        var zoom = PdfFitGeometry.Zoom(width, visible, 900, 900 * 792.0 / 612,
            816, PdfFitMode.Contain, 0.1, 10);
        Assert.True((900 * 792.0 / 612 + 18) * zoom <= visible + 0.00001);
        Assert.True(902 * zoom <= width + 0.00001);
        Assert.True(zoom <= 816.0 / 900);
    }

    [Fact]
    public void HiddenBarAndResizeRecomputeHeightWhileWidthAndOriginalKeepTheirMeaning()
    {
        double Zoom(double height, PdfFitMode mode) =>
            PdfFitGeometry.Zoom(1000, height, 900, 1200, 816, mode, 0.1, 10);
        Assert.True(Zoom(556, PdfFitMode.Contain) < Zoom(600, PdfFitMode.Contain));
        Assert.True(Zoom(600, PdfFitMode.Contain) < Zoom(800, PdfFitMode.Contain));
        Assert.Equal(556.0 / 1218, Zoom(556, PdfFitMode.FitHeight));
        Assert.Equal(1000.0 / 902, Zoom(556, PdfFitMode.FitWidth));
        Assert.Equal(Zoom(556, PdfFitMode.FitWidth), Zoom(600, PdfFitMode.FitWidth));
        Assert.Equal(816.0 / 900, Zoom(556, PdfFitMode.ActualSize));
        Assert.Equal(Zoom(556, PdfFitMode.ActualSize), Zoom(600, PdfFitMode.ActualSize));
    }

    [Fact]
    public void SmallPageIsNotEnlargedByContainAndZoomBoundsAreRespected()
    {
        Assert.Equal(1, PdfFitGeometry.Zoom(1000, 600, 100, 100, 100, PdfFitMode.Contain, 0.1, 10));
        Assert.Equal(0.1, PdfFitGeometry.Zoom(100, 1, 900, 1200, 816, PdfFitMode.Contain, 0.1, 10));
        Assert.Equal(10, PdfFitGeometry.Zoom(10000, 10000, 100, 100, 100, PdfFitMode.FitHeight, 0.1, 10));
    }
}
