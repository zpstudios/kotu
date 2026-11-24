using KOTU.Core.Routing;
using Xunit;

namespace KOTU.Core.Tests;

public class ExplorerTileLayoutTests
{
    [Theory]
    [InlineData(6, 575.99, 5, 115)]
    [InlineData(6, 576, 6, 96)]
    [InlineData(9, 863.99, 8, 107)]
    [InlineData(9, 864, 9, 96)]
    [InlineData(12, 1151.99, 11, 104)]
    [InlineData(12, 1152, 12, 96)]
    [InlineData(6, 375, 3, 125)]
    public void Columns_change_only_when_another_full_control_sized_cell_fits(
        int requested, double available, int columns, double width)
    {
        var layout = ExplorerTileLayout.Measure(available, requested)!.Value;
        Assert.Equal(columns, layout.Columns);
        Assert.Equal(width, layout.ItemWidth);
        Assert.True(layout.Columns * layout.ItemWidth <= available);
        Assert.True(layout.ItemWidth >= ExplorerTileLayout.MinimumItemWidth);
    }

    [Theory]
    [InlineData(6, 1001.75, 166)]
    [InlineData(9, 1801.75, 200)]
    [InlineData(12, 2401.75, 200)]
    public void Ordinary_widths_keep_the_existing_columns_cell_width_and_height(
        int requested, double available, double width)
    {
        var layout = ExplorerTileLayout.Measure(available, requested)!.Value;
        Assert.Equal(requested, layout.Columns);
        Assert.Equal(width, layout.ItemWidth);
        Assert.Equal(width + 4, layout.ItemHeight);
    }

    [Theory]
    [InlineData(0.25, 0.25, 64)]
    [InlineData(1, 1, 64)]
    [InlineData(23.75, 23, 64)]
    [InlineData(95.75, 95, 99)]
    public void A_surface_narrower_than_one_cell_uses_one_fitting_cell(
        double available, double width, double height)
    {
        foreach (var requested in new[] { 6, 9, 12 })
        {
            var layout = ExplorerTileLayout.Measure(available, requested)!.Value;
            Assert.Equal(1, layout.Columns);
            Assert.Equal(width, layout.ItemWidth);
            Assert.Equal(height, layout.ItemHeight);
            Assert.True(layout.ItemWidth <= available);
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
    [InlineData(6)]
    [InlineData(9)]
    [InlineData(12)]
    public void Narrowing_then_expanding_restores_the_requested_density(int requested)
    {
        var normal = ExplorerTileLayout.Measure(1536, requested)!.Value;
        Assert.Equal(3, ExplorerTileLayout.Measure(375, requested)!.Value.Columns);
        Assert.Equal(1, ExplorerTileLayout.Measure(150, requested)!.Value.Columns);
        Assert.Equal(normal, ExplorerTileLayout.Measure(1536, requested)!.Value);
    }

    [Fact]
    public void Invalid_column_preferences_cannot_create_zero_width_or_division_overflow()
    {
        foreach (var requested in new[] { int.MinValue, 0, int.MaxValue })
        {
            var layout = ExplorerTileLayout.Measure(375, requested)!.Value;
            Assert.InRange(layout.Columns, 1, 3);
            Assert.True(double.IsFinite(layout.ItemWidth) && layout.ItemWidth > 0);
            Assert.True(layout.Columns * layout.ItemWidth <= 375);
        }
    }
}
