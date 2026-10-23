using Windows.Foundation;
using Xunit;

namespace KOTU.Module.Image.Tests;

public sealed class RotatedImageLayoutTests
{
    [Theory]
    [InlineData(0, 320, 480)]
    [InlineData(90, 480, 320)]
    [InlineData(180, 320, 480)]
    [InlineData(270, 480, 320)]
    public void TransformedCornersExactlyFillReportedExtent(int angle, double width, double height)
    {
        var child = new Size(320, 480);
        var host = RotatedImageLayout.SwapAxes(child, angle % 180 != 0);
        Assert.Equal(width, host.Width);
        Assert.Equal(height, host.Height);
        var slot = RotatedImageLayout.CenterChild(child, host);
        var radians = angle * Math.PI / 180;
        var corners = new[] { new Point(0, 0), new Point(320, 0), new Point(0, 480), new Point(320, 480) }
            .Select(point => new Point(
                slot.X + 160 + (point.X - 160) * Math.Cos(radians) - (point.Y - 240) * Math.Sin(radians),
                slot.Y + 240 + (point.X - 160) * Math.Sin(radians) + (point.Y - 240) * Math.Cos(radians)))
            .ToArray();
        Assert.Equal(0, corners.Min(point => point.X), 8);
        Assert.Equal(0, corners.Min(point => point.Y), 8);
        Assert.Equal(width, corners.Max(point => point.X), 8);
        Assert.Equal(height, corners.Max(point => point.Y), 8);
    }

    [Theory]
    [InlineData(false, 900, 600, 900, 600)]
    [InlineData(true, 900, 600, 600, 900)]
    [InlineData(true, 900, double.PositiveInfinity, double.PositiveInfinity, 900)]
    public void AvailableConstraintUsesChildAxes(bool swaps, double availableWidth,
        double availableHeight, double width, double height)
    {
        var available = RotatedImageLayout.SwapAxes(new Size(availableWidth, availableHeight), swaps);
        Assert.Equal(width, available.Width);
        Assert.Equal(height, available.Height);
    }

    [Fact]
    public void EmptyMeasuredImageHasNoExtent()
    {
        var host = RotatedImageLayout.SwapAxes(new Size(), true);
        var slot = RotatedImageLayout.CenterChild(new Size(), host);
        Assert.Equal(new Size(), host);
        Assert.Equal(new Rect(), slot);
    }
}
