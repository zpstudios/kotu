using KOTU.Core.Content;
using Xunit;

namespace KOTU.Core.Tests;

public class SidebarGeometryTests
{
    [Theory]
    [InlineData(false, false, 8)]
    [InlineData(true, false, 6)]
    [InlineData(false, true, 6)]
    [InlineData(true, true, 4)]
    public void BrowserFitsBetweenVisiblePanelsAtEveryViewport(bool left, bool right, int columns)
    {
        foreach (var width in new[] { 640d, 947d, 1280d, 1920d })
        {
            var layout = SidebarGeometry.FromVisiblePanels(left, right, false, 25);
            var start = width * layout.ContentLeftPercent / 100;
            var end = width * (100 - layout.ContentRightPercent) / 100;
            var viewport = width * layout.ContentCenterPercent / 100;
            Assert.Equal(left ? width / 4 : 0, start);
            Assert.Equal(right ? width * .75 : width, end);
            Assert.Equal(end - start, viewport);
            Assert.Equal(columns, layout.ContentColumns);
            var cell = Math.Floor(viewport / layout.ContentColumns);
            Assert.True(cell * columns <= viewport);
            Assert.True(cell * (columns + 1) > viewport);
        }
    }

    [Fact]
    public void DelayedLeftOpenAndRepeatedContentReturnRecomputeFromActualSurfaces()
    {
        // Content hides both; on Esc the right placeholder appears immediately, then the left
        // folder probe completes. Replaying must not retain the intermediate 75%/6-column layout.
        for (var cycle = 0; cycle < 3; cycle++)
        {
            var hidden = SidebarGeometry.FromVisiblePanels(false, false, false, 25);
            Assert.Equal(100, hidden.ContentCenterPercent);
            var pending = SidebarGeometry.FromVisiblePanels(false, true, false, 25);
            Assert.Equal(75, pending.ContentCenterPercent);
            Assert.Equal(6, pending.ExplorerColumns);
            var complete = SidebarGeometry.FromVisiblePanels(true, true, false, 25);
            Assert.Equal(25, complete.ContentLeftPercent);
            Assert.Equal(50, complete.ContentCenterPercent);
            Assert.Equal(4, complete.ExplorerColumns);
            // Failed/hidden left must release its reserve, even after a previous successful open.
            Assert.Equal(pending, SidebarGeometry.FromVisiblePanels(false, true, false, 25));
        }
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public void FloatingS4PreservesContentWidthButReservesBrowserPanels(bool left, bool right)
    {
        var docked = SidebarGeometry.FromVisiblePanels(left, right, false, 25);
        var floating = SidebarGeometry.FromVisiblePanels(left, right, true, 25);
        Assert.Equal(100, floating.ContentCenterPercent);
        Assert.Equal(0, floating.DockCount);
        Assert.Equal(docked.CenterPercent, floating.CenterPercent);
        Assert.Equal(docked.ExplorerColumns, floating.ExplorerColumns);
        Assert.Equal(docked.LeftPercent, floating.LeftPercent);
        Assert.Equal(docked.RightPercent, floating.RightPercent);
    }
}
