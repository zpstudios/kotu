using KOTU.Core.Routing;
using Xunit;

namespace KOTU.Core.Tests;

public class ExplorerPresentationStateTests
{
    [Fact]
    public void Focus_active_selection_and_anchor_remain_independent_across_surface_instances()
    {
        var shared = new ExplorerPresentationState();
        shared.Save("folder", new("alpha", ["alpha", "beta"], "gamma", false, "delta", false));
        shared.Mode = ExplorerPresentationState.ViewMode.List;
        var restored = shared.Read("FOLDER", ["delta", "gamma", "beta", "alpha"]);
        Assert.Equal("alpha", restored.Active);
        Assert.Equal(new[] { "alpha", "beta" }, restored.Selected);
        Assert.Equal("gamma", restored.Focused);
        Assert.Equal("delta", restored.Anchor);
        Assert.Equal(ExplorerPresentationState.ViewMode.List, shared.Mode);
    }

    [Fact]
    public void Different_folder_clears_item_identity_but_not_view_mode()
    {
        var shared = new ExplorerPresentationState { Mode = ExplorerPresentationState.ViewMode.List };
        shared.Save("old", new("a", ["a"], "a", false, "a", false));
        var next = shared.Read("new", ["a"]);
        Assert.Null(next.Active);
        Assert.Empty(next.Selected);
        Assert.Null(next.Focused);
        Assert.Null(next.Anchor);
        Assert.Equal(ExplorerPresentationState.ViewMode.List, shared.Mode);
    }

    [Fact]
    public void Filter_projection_never_promotes_focus_or_hidden_paths_to_selection()
    {
        var shared = new ExplorerPresentationState();
        shared.Save("folder", new("a", ["a", "b"], "c", false, "a", false));
        var filtered = shared.Read("folder", ["b", "c"]);
        Assert.Equal("b", filtered.Active);
        Assert.Equal(new[] { "b" }, filtered.Selected);
        Assert.Equal("c", filtered.Focused);
        Assert.Null(filtered.Anchor);
        Assert.Equal(new[] { "a", "b" }, shared.Read("folder", ["a", "b", "c"]).Selected);
    }

    [Fact]
    public void Parent_focus_and_anchor_have_no_file_identity()
    {
        var shared = new ExplorerPresentationState();
        shared.Save("folder", new(null, [], null, true, null, true));
        var next = shared.Read("folder", ["a"]);
        Assert.True(next.ParentFocused);
        Assert.True(next.ParentAnchor);
        Assert.Empty(next.Selected);
        Assert.Null(next.Active);
    }

    [Fact]
    public void Publishing_filtered_selection_prevents_hidden_selection_from_returning()
    {
        var shared = new ExplorerPresentationState();
        shared.Save("folder", new("a", ["a"], "a", false, "a", false));
        shared.Save("folder", shared.Read("folder", ["b"]));
        var cleared = shared.Read("folder", ["a", "b"]);
        Assert.Null(cleared.Active);
        Assert.Null(cleared.Focused);
        Assert.Empty(cleared.Selected);
    }

    [Fact]
    public void Snapshots_do_not_alias_mutable_selection_arrays()
    {
        var shared = new ExplorerPresentationState();
        var paths = new[] { "a" };
        shared.Save("folder", new("a", paths, "b", false, "a", false));
        paths[0] = "b";
        var read = shared.Read("folder", ["a", "b"]);
        read.Selected[0] = "b";
        Assert.Equal(new[] { "a" }, shared.Read("folder", ["a", "b"]).Selected);
    }
}
