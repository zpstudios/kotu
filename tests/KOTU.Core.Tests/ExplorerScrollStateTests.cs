using KOTU.Core.Routing;
using Xunit;

namespace KOTU.Core.Tests;

public class ExplorerScrollStateTests
{
    [Fact]
    public void List_end_anchor_survives_preview_clamp_and_return_to_list()
    {
        var scroll = new ExplorerScrollState();
        scroll.BeginUserNavigation();
        Assert.True(scroll.ObservePhysical(new("entry-1190", false)));
        var original = scroll.Logical;
        var preview = scroll.BeginRestore(original);
        Assert.False(scroll.ObservePhysical(new("entry-1169", false)));
        scroll.CompleteRestore(preview);
        Assert.False(scroll.ObservePhysical(new("entry-1169", false)));
        var list = scroll.BeginRestore(scroll.Logical);
        Assert.Equal(original, scroll.Logical);
        scroll.CompleteRestore(list);
        Assert.Equal("entry-1190", scroll.Logical.Path);
    }

    [Fact]
    public void A_later_user_scroll_replaces_the_logical_anchor_and_invalidates_old_restore_completion()
    {
        var scroll = new ExplorerScrollState();
        var pending = scroll.BeginRestore(new("entry-1190", false));
        scroll.BeginUserNavigation();
        scroll.ObservePhysical(new("entry-1150", false));
        scroll.CompleteRestore(pending);
        Assert.True(scroll.UserNavigation);
        Assert.False(scroll.ProgrammaticRestore);
        Assert.Equal("entry-1150", scroll.Logical.Path);
    }

    [Fact]
    public void Stale_completion_cannot_end_a_newer_programmatic_restore()
    {
        var scroll = new ExplorerScrollState();
        var old = scroll.BeginRestore(new("old", false));
        var current = scroll.BeginRestore(new("new", false));
        scroll.CompleteRestore(old);
        Assert.True(scroll.ProgrammaticRestore);
        Assert.False(scroll.ObservePhysical(new("clamped", false)));
        scroll.CompleteRestore(current);
        Assert.False(scroll.ProgrammaticRestore);
        Assert.False(scroll.UserNavigation);
    }

    [Fact]
    public void Shared_handoff_preserves_logical_anchor_without_promoting_focus_to_active()
    {
        var session = new ExplorerPresentationState();
        var s4 = new ExplorerScrollState();
        s4.BeginUserNavigation();
        s4.ObservePhysical(new("1190", false));
        session.Save("folder", new("0000", ["0000"], "1197", false, s4.Logical.Path, false));
        var snapshot = session.Read("folder", ["0000", "1190", "1197"]);
        var s1 = new ExplorerScrollState();
        s1.BeginRestore(new(snapshot.Anchor, snapshot.ParentAnchor));
        Assert.False(s1.ObservePhysical(new("1169", false)));
        Assert.Equal("1190", s1.Logical.Path);
        Assert.Equal("0000", snapshot.Active);
        Assert.Equal("1197", snapshot.Focused);
    }

    [Fact]
    public void Removed_anchor_and_new_folder_do_not_reuse_old_scroll_identity()
    {
        var shared = new ExplorerPresentationState();
        shared.Save("old", new("a", ["a"], "a", false, "a", false));
        var scroll = new ExplorerScrollState();
        var filtered = shared.Read("old", ["b"]);
        scroll.BeginRestore(new(filtered.Anchor, filtered.ParentAnchor));
        Assert.Null(scroll.Logical.Path);
        Assert.False(scroll.ObservePhysical(new("a", false)));
        var next = shared.Read("new", ["a"]);
        scroll.BeginRestore(new(next.Anchor, next.ParentAnchor));
        Assert.True(scroll.Logical.Parent);
        Assert.Null(scroll.Logical.Path);
    }

    [Fact]
    public void Large_wheel_transient_unknown_does_not_destroy_last_anchor_and_later_layout_commits()
    {
        var scroll = new ExplorerScrollState();
        var epoch = scroll.BeginUserNavigation();
        scroll.ObservePhysical(new("0000", false));
        Assert.False(scroll.ObserveSettledPhysical(new(null, false), 9570.897, epoch));
        Assert.Equal("0000", scroll.Logical.Path);
        Assert.False(scroll.ObservePhysical(new(null, false)));
        Assert.False(scroll.ObserveSettledPhysical(new("0237", false), 9570.897, epoch));
        Assert.True(scroll.ObserveSettledPhysical(new("0237", false), 9570.897, epoch));
        Assert.Equal("0237", scroll.Logical.Path);
    }

    [Fact]
    public void Fast_scroll_waits_for_final_offset_and_matching_container_identity()
    {
        var scroll = new ExplorerScrollState();
        var epoch = scroll.BeginUserNavigation();
        Assert.False(scroll.ObserveSettledPhysical(new("0237", false), 9570, epoch));
        Assert.False(scroll.ObserveSettledPhysical(new("0237", false), 9600, epoch));
        Assert.False(scroll.ObserveSettledPhysical(new("0239", false), 9600, epoch));
        Assert.True(scroll.ObserveSettledPhysical(new("0239", false), 9600, epoch));
        scroll.InvalidatePhysicalSample();
        Assert.False(scroll.ObserveSettledPhysical(new("0625", false), 25000, epoch));
        Assert.True(scroll.ObserveSettledPhysical(new("0625", false), 25000, epoch));
        Assert.Equal("0625", scroll.Logical.Path);
    }

    [Fact]
    public void Invalid_realization_breaks_stability_without_erasing_logical_location()
    {
        var scroll = new ExplorerScrollState();
        var epoch = scroll.BeginUserNavigation();
        scroll.ObservePhysical(new("old", false));
        Assert.False(scroll.ObserveSettledPhysical(new("new", false), 500, epoch));
        Assert.False(scroll.ObserveSettledPhysical(new(null, false), 500, epoch));
        Assert.False(scroll.ObserveSettledPhysical(new("new", false), 500, epoch));
        Assert.Equal("old", scroll.Logical.Path);
        Assert.True(scroll.ObserveSettledPhysical(new("new", false), 500, epoch));
    }

    [Fact]
    public void Late_capture_cannot_join_samples_from_different_user_epochs()
    {
        var scroll = new ExplorerScrollState();
        var old = scroll.BeginUserNavigation();
        scroll.ObserveSettledPhysical(new("old", false), 100, old);
        var current = scroll.BeginUserNavigation();
        Assert.False(scroll.ObserveSettledPhysical(new("old", false), 100, old));
        Assert.False(scroll.ObserveSettledPhysical(new("new", false), 200, current));
        Assert.False(scroll.ObserveSettledPhysical(new("old", false), 100, old));
        Assert.True(scroll.ObserveSettledPhysical(new("new", false), 200, current));
    }

    [Fact]
    public void View_restore_discards_pending_samples_and_preserves_bottom_clamped_logical_anchor()
    {
        var scroll = new ExplorerScrollState();
        var user = scroll.BeginUserNavigation();
        scroll.ObservePhysical(new("1190", false));
        scroll.ObserveSettledPhysical(new("1180", false), 500, user);
        var restore = scroll.BeginRestore(scroll.Logical);
        scroll.CompleteRestore(restore);
        Assert.False(scroll.ObserveSettledPhysical(new("1180", false), 500, user));
        Assert.False(scroll.ObserveSettledPhysical(new("1169", false), 400, restore));
        Assert.Equal("1190", scroll.Logical.Path);
    }

    [Fact]
    public void Real_parent_can_settle_but_nonfinite_geometry_cannot()
    {
        var scroll = new ExplorerScrollState();
        var epoch = scroll.BeginUserNavigation();
        Assert.False(scroll.ObserveSettledPhysical(new(null, true), double.NaN, epoch));
        Assert.False(scroll.ObserveSettledPhysical(new(null, true), 0, epoch));
        Assert.True(scroll.ObserveSettledPhysical(new(null, true), 0, epoch));
        Assert.True(scroll.Logical.Parent);
    }

    [Theory]
    [InlineData(100, 1000, 400, 100, 44, 100)]
    [InlineData(100, 1000, 400, -12, 44, 88)]
    [InlineData(51324.78, 51400.67, 400, 433.21, 42.67, 51400.66)]
    [InlineData(980, 1000, 400, 390, 44, 1000)]
    [InlineData(10, 1000, 400, -100, 44, 0)]
    [InlineData(100, 1000, 400, 50, 500, 150)]
    [InlineData(100, 1000, 0, 500, 44, 100)]
    public void Focus_visibility_adjustment_is_minimal_and_clamped(double current, double maximum,
        double height, double top, double rowHeight, double expected) =>
        Assert.Equal(expected, ExplorerScrollState.VisibleOffset(current, maximum, height, top, rowHeight), 5);
}
