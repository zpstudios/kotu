using KOTU.Core.Routing;
using Xunit;

namespace KOTU.Core.Tests;

public class ExplorerTileLayoutTests
{
    [Theory]
    [InlineData(6, 375, 62)]
    [InlineData(9, 375, 41)]
    [InlineData(12, 375, 31)]
    [InlineData(6, 575.99, 95)]
    [InlineData(6, 576, 96)]
    [InlineData(9, 863.99, 95)]
    [InlineData(9, 864, 96)]
    [InlineData(12, 1151.99, 95)]
    [InlineData(12, 1152, 96)]
    public void Narrow_cells_never_reduce_the_requested_column_count(int requested, double available, double width)
    {
        var layout = ExplorerTileLayout.Measure(available, requested)!.Value;
        Assert.Equal(requested, layout.Columns);
        Assert.Equal(width, layout.ItemWidth);
        Assert.True(layout.Columns * layout.ItemWidth <= available);
        Assert.Equal(Math.Max(width + 4, 64), layout.ItemHeight);
    }

    [Theory]
    [InlineData(6, 1001.75, 166)]
    [InlineData(9, 1801.75, 200)]
    [InlineData(12, 2401.75, 200)]
    public void Ordinary_widths_keep_the_existing_cell_width_and_height(int requested, double available, double width)
    {
        var layout = ExplorerTileLayout.Measure(available, requested)!.Value;
        Assert.Equal(requested, layout.Columns);
        Assert.Equal(width, layout.ItemWidth);
        Assert.Equal(width + 4, layout.ItemHeight);
        Assert.Equal(1, ExplorerTileLayout.MeasureOverlayScale(layout.ItemWidth));
    }

    [Theory]
    [InlineData(0.25)]
    [InlineData(1)]
    [InlineData(23.75)]
    public void Extremely_narrow_surfaces_still_fit_all_requested_columns(double available)
    {
        foreach (var requested in new[] { 6, 9, 12 })
        {
            var layout = ExplorerTileLayout.Measure(available, requested)!.Value;
            Assert.Equal(requested, layout.Columns);
            Assert.True(layout.ItemWidth > 0);
            Assert.True(layout.Columns * layout.ItemWidth <= available);
            Assert.Equal(64, layout.ItemHeight);
        }
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    [InlineData(double.NegativeInfinity)]
    public void Unmeasured_or_invalid_surfaces_do_not_replace_the_live_layout(double width)
        => Assert.Null(ExplorerTileLayout.Measure(width, 6));

    [Theory]
    [InlineData(24, 0.25)]
    [InlineData(48, 0.5)]
    [InlineData(60, 0.625)]
    [InlineData(95.5, 95.5 / 96)]
    [InlineData(96, 1)]
    [InlineData(192, 1)]
    public void Checks_badges_and_margins_share_a_proportional_scale_without_enlargement(double width, double expected)
    {
        var scale = ExplorerTileLayout.MeasureOverlayScale(width);
        Assert.Equal(expected, scale, 12);
        Assert.Equal(28 * scale, 20 * scale + 2 * 4 * scale, 12);
        Assert.Equal(22 * scale, 10 * scale + 12 * scale, 12);
        Assert.True(28 * scale <= width);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    [InlineData(double.NegativeInfinity)]
    public void Unmeasured_overlay_slots_do_not_create_invalid_sizes(double width)
        => Assert.Equal(0, ExplorerTileLayout.MeasureOverlayScale(width));

    [Theory]
    [InlineData(6)]
    [InlineData(9)]
    [InlineData(12)]
    public void Narrowing_and_expanding_preserves_columns_and_restores_the_original_overlay_size(int requested)
    {
        var normal = ExplorerTileLayout.Measure(1536, requested)!.Value;
        foreach (var available in new[] { 375d, 150d, 375d, 1536d })
        {
            var layout = ExplorerTileLayout.Measure(available, requested)!.Value;
            Assert.Equal(requested, layout.Columns);
            Assert.InRange(ExplorerTileLayout.MeasureOverlayScale(layout.ItemWidth), 0, 1);
        }
        Assert.Equal(normal, ExplorerTileLayout.Measure(1536, requested)!.Value);
        Assert.Equal(1, ExplorerTileLayout.MeasureOverlayScale(normal.ItemWidth));
    }

    [Fact]
    public void Invalid_column_preferences_cannot_create_zero_width_or_division_overflow()
    {
        foreach (var requested in new[] { int.MinValue, 0, int.MaxValue })
        {
            var layout = ExplorerTileLayout.Measure(375, requested)!.Value;
            Assert.Equal(Math.Max(1, requested), layout.Columns);
            Assert.True(double.IsFinite(layout.ItemWidth) && layout.ItemWidth > 0);
            Assert.True(layout.Columns * layout.ItemWidth <= 375);
        }
    }
}
