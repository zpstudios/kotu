using Windows.Foundation;
using Xunit;

namespace KOTU.Module.Video.Tests;

public sealed class VideoCompositionGeometryTests
{
    [Theory]
    [InlineData(0, 0, 2d / 3, 2d / 3, 1.5, 1, 1)]
    [InlineData(71, -39, 4d / 3, 4d / 3, 1.5, 2, 2)]
    [InlineData(320, 180, 4d / 3, 4d / 3, 1.5, 2, 2)]
    [InlineData(1200, 180, 2d / 3, 2d / 3, 1.5, 1, 1)]
    [InlineData(-12, 80, 2, 3, 1.25, 2.5, 3.75)]
    public void AncestorScaleUsesTranslationIndependentAxesAndRasterizationOnce(
        double left, double top, double x, double y, double raster, double expectedX, double expectedY)
    {
        Assert.True(VideoCompositionGeometry.TryGetTargetScale(new(left, top), new(left + x * 1024, top),
            new(left, top + y * 1024), raster, out var scale));
        // WinRT Point는 float 정밀도이므로 변환 원점의 뺄셈도 동일한 허용 오차로 확인한다.
        Assert.InRange(Math.Abs(expectedX - scale.Width), 0, 1e-5 * Math.Max(1, expectedX));
        Assert.InRange(Math.Abs(expectedY - scale.Height), 0, 1e-5 * Math.Max(1, expectedY));
    }

    [Theory]
    [InlineData(0, 1, -1, 0)]
    [InlineData(1, .001, 0, 1)]
    [InlineData(1, 0, .001, 1)]
    [InlineData(-1, 0, 0, 1)]
    [InlineData(0, 0, 0, 1)]
    [InlineData(double.NaN, 0, 0, 1)]
    [InlineData(1, 0, 0, double.PositiveInfinity)]
    public void RotationShearReflectionAndInvalidAxesFailOpen(double xx, double xy, double yx, double yy)
    {
        Assert.False(VideoCompositionGeometry.TryGetTargetScale(new(0, 0), new(xx * 1024, xy * 1024), new(yx * 1024, yy * 1024),
            1.5, out _));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    [InlineData(double.MaxValue)]
    public void InvalidRasterizationOrOverflowFailsOpen(double raster)
    {
        Assert.False(VideoCompositionGeometry.TryGetTargetScale(new(0, 0), new(2048, 0), new(0, 2048),
            raster, out _));
    }

    [Fact]
    public void InvalidOriginIsRejectedBeforeSubtracting()
    {
        Assert.False(VideoCompositionGeometry.TryGetTargetScale(new(double.NaN, 0), new(1, 0),
            new(0, 1), 1, out _));
    }

    [Theory]
    [InlineData(10000000)]
    [InlineData(-10000000)]
    public void ExcessiveCoordinateQuantizationFailsOpenInsteadOfHolding(double offset)
    {
        Assert.False(VideoCompositionGeometry.TryGetTargetScale(new(offset, 0),
            new(offset + 1024 * 4d / 3, 0), new(offset, 1024 * 4d / 3), 1.5, out _));
    }

    [Theory]
    [InlineData(1, 1.000009, true)]
    [InlineData(1, 1.000011, false)]
    [InlineData(2, 2.000019, true)]
    [InlineData(2, 2.000021, false)]
    [InlineData(.5, .500009, true)]
    [InlineData(.5, .500011, false)]
    [InlineData(1.5, 2, false)]
    [InlineData(1, 0, false)]
    [InlineData(1, double.NaN, false)]
    public void FloatToleranceIsRelativeWithOneUnitFloorAndBothAxesRequired(double target, double actual, bool matches)
    {
        Assert.Equal(matches, VideoCompositionGeometry.ScalesMatch(new(target, target), new(actual, actual)));
        Assert.Equal(matches, VideoCompositionGeometry.ScalesMatch(new(target, target), new(target, actual)));
        Assert.Equal(matches, VideoCompositionGeometry.ScalesMatch(new(target, target), new(actual, target)));
    }
}
