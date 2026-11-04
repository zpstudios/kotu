using KOTU.Core.Routing;
using Xunit;

namespace KOTU.Core.Tests;

public class ExplorerSearchTests
{
    private static ExplorerListing.Entry E(string name, bool folder = false, long size = 1) =>
        new("C:/fixture/" + name, name, folder, size, DateTime.UnixEpoch, DateTime.UnixEpoch);
    private static readonly HashSet<string> NoHidden = new(StringComparer.OrdinalIgnoreCase);

    [Fact]
    public void Repeated_letters_cycle_current_order_and_wrap_without_mutating_entries()
    {
        var entries = new[] { E("amber"), E("beta"), E("alpha"), E("aab") };
        var original = entries.ToArray();
        var search = new ExplorerTypeAhead();
        Assert.Equal(0, search.Find("a", 10, entries, -1));
        Assert.Equal(2, search.Find("a", 20, entries, 0));
        Assert.Equal(3, search.Find("A", 30, entries, 2));
        Assert.Equal(0, search.Find("a", 40, entries, 3));
        Assert.Equal(original, entries);
    }

    [Fact]
    public void Mixed_prefix_keeps_every_repeated_letter()
    {
        var entries = new[] { E("alpha"), E("amber"), E("ab"), E("aab"), E("aabc") };
        var search = new ExplorerTypeAhead();
        Assert.Equal(0, search.Find("a", 10, entries, -1));
        Assert.Equal(1, search.Find("a", 20, entries, 0));
        Assert.Equal(3, search.Find("b", 30, entries, 1));
        Assert.Equal("aab", search.Prefix);
        Assert.Equal(4, search.Find("c", 40, entries, 3));
    }

    [Fact]
    public void Timeout_restarts_at_boundary_and_no_match_does_not_reset_buffer()
    {
        var entries = new[] { E("alpha"), E("beta") };
        var search = new ExplorerTypeAhead();
        Assert.Equal(0, search.Find("a", 1, entries, -1));
        Assert.Equal(-1, search.Find("z", 1000, entries, 0));
        Assert.Equal("az", search.Prefix);
        Assert.Equal(1, search.Find("b", 2000, entries, 0));
        Assert.Equal("b", search.Prefix);
    }

    [Fact]
    public void Empty_list_and_invalid_selection_have_explicit_no_match()
    {
        var search = new ExplorerTypeAhead();
        Assert.Equal(-1, search.Find("q", 1, [], -1));
        search.Reset();
        Assert.Equal(0, search.Find("a", 2, new[] { E("alpha") }, 999));
        Assert.Equal(-1, search.Find("b", 3, new[] { E("alpha") }, 0));
    }

    [Fact]
    public void Reset_breaks_sequences_after_folder_order_or_focus_changes()
    {
        var search = new ExplorerTypeAhead();
        var entries = new[] { E("alpha"), E("beta") };
        search.Find("a", 1, entries, -1);
        search.Reset();
        Assert.Equal(1, search.Find("b", 2, entries, 0));
        search.Reset();
        Assert.Equal(0, search.Find("b", 3, entries.Reverse().ToArray(), -1));
    }

    [Fact]
    public void Unicode_committed_text_and_utf16_pairs_are_not_partial_matches()
    {
        var entries = new[] { E("한글.txt"), E("😀-one"), E("😀-two"), E("Éclair") };
        var search = new ExplorerTypeAhead();
        Assert.Equal(0, search.Find("한", 1, entries, -1));
        search.Reset();
        Assert.Equal(-1, search.Find("\ud83d", 2, entries, -1));
        Assert.Equal(1, search.Find("\ude00", 3, entries, -1));
        Assert.Equal(2, search.Find("😀", 4, entries, 1));
        search.Reset();
        Assert.Equal(-1, search.Find("\ude00", 5, entries, -1));
        Assert.Equal(3, search.Find("é", 6, entries, -1));
    }

    [Fact]
    public void Keyword_contains_folders_and_extensions_while_typeahead_is_prefix_only()
    {
        var view = new ExplorerNameView();
        view.Arrange(new[] { E("my report.txt"), E("report folder", true), E("other.txt"), E("report.jpg") },
            ExplorerListing.SortKey.Name, false, new HashSet<string> { ".jpg" });
        view.SetQuery("REPORT");
        Assert.Equal(new[] { "report folder", "my report.txt" }, view.Display.Select(e => e.Name));
        Assert.Equal(0, new ExplorerTypeAhead().Find("report", 1, view.Display, -1));
        view.SetQuery(".TXT");
        Assert.Equal(new[] { "my report.txt", "other.txt" }, view.Display.Select(e => e.Name));
        Assert.Equal(-1, new ExplorerTypeAhead().Find(".TXT", 1, view.Display, -1));
    }

    [Fact]
    public void Clearing_keyword_restores_hidden_items_and_stable_sort_ties()
    {
        var view = new ExplorerNameView();
        view.Arrange(new[] { E("alpha"), E("beta"), E("gamma") }, ExplorerListing.SortKey.Name, true, NoHidden);
        view.SetQuery("beta");
        view.Arrange(view.Ordered, ExplorerListing.SortKey.Size, false, NoHidden);
        Assert.Single(view.Display);
        view.SetQuery("");
        Assert.Equal(new[] { "gamma", "beta", "alpha" }, view.Display.Select(e => e.Name));
    }

    [Fact]
    public async Task Latest_query_is_applied_when_inflight_scan_completes()
    {
        var view = new ExplorerNameView();
        var pending = new TaskCompletionSource<ExplorerListing.Entry[]>();
        async Task FinishScan()
        {
            var scanned = await pending.Task;
            view.Arrange(scanned, ExplorerListing.SortKey.Name, false, NoHidden);
        }
        var completion = FinishScan();
        view.SetQuery("old");
        view.SetQuery("new");
        pending.SetResult(new[] { E("old.txt"), E("new.txt") });
        await completion;
        Assert.Equal("new.txt", Assert.Single(view.Display).Name);
    }

    [Fact]
    public void Empty_result_and_folder_reset_do_not_leave_old_items()
    {
        var view = new ExplorerNameView();
        view.Arrange(new[] { E("old.txt") }, ExplorerListing.SortKey.Name, false, NoHidden);
        Assert.True(view.SetQuery("absent"));
        Assert.Empty(view.Display);
        Assert.False(view.SetQuery("also absent"));
        view.ClearFolder();
        view.Arrange(new[] { E("new.txt") }, ExplorerListing.SortKey.Name, false, NoHidden);
        Assert.Equal("", view.Query);
        Assert.Equal("new.txt", Assert.Single(view.Display).Name);
    }

    [Fact]
    public void Surviving_selection_observes_new_metadata_without_a_selection_event()
    {
        var item = E("alpha.txt");
        var before = ExplorerNameView.SelectionMetadata(new[] { item }, item.Path);
        Assert.Equal(before, ExplorerNameView.SelectionMetadata(new[] { item with { } }, item.Path));
        var changed = item with { IsPlaceholder = true, Modified = item.Modified.AddMinutes(1) };
        var after = ExplorerNameView.SelectionMetadata(new[] { changed }, item.Path.ToUpperInvariant());
        Assert.NotEqual(before, after);
        Assert.Equal((changed.Path, true, changed.Modified.Ticks), after);
        Assert.Null(ExplorerNameView.SelectionMetadata([], item.Path));
        Assert.Null(ExplorerNameView.SelectionMetadata(new[] { item with { IsFolder = true } }, item.Path));
    }

    [Fact]
    public void Failed_same_folder_refresh_retains_query_for_successful_recovery()
    {
        var view = new ExplorerNameView();
        var entries = new[] { E("alpha.txt"), E("beta.txt") };
        view.Arrange(entries, ExplorerListing.SortKey.Name, false, NoHidden);
        view.SetQuery("alpha");
        view.ClearEntries(); // folder exists but a transient watcher/rescan read failed
        Assert.Empty(view.Display);
        Assert.Equal("alpha", view.Query);
        view.Arrange(entries, ExplorerListing.SortKey.Name, false, NoHidden);
        Assert.Equal("alpha.txt", Assert.Single(view.Display).Name);
        view.ClearFolder();
        Assert.Equal("", view.Query);
    }

    [Fact]
    public void Selection_search_does_not_change_checkbox_workset_and_filter_intersection_is_preserved()
    {
        var entries = new[] { E("alpha"), E("beta"), E("amber") };
        var checkedPaths = new HashSet<string> { entries[0].Path, entries[1].Path };
        var before = checkedPaths.ToArray();
        var search = new ExplorerTypeAhead();
        Assert.Equal(2, search.Find("a", 1, entries, 0));
        var view = new ExplorerNameView();
        view.Arrange(entries, ExplorerListing.SortKey.Name, false, NoHidden);
        view.SetQuery("a");
        Assert.Equal(before, checkedPaths);
        view.SetQuery("alpha");
        Assert.Equal(new[] { entries[0].Path }, view.Display.Where(e => checkedPaths.Contains(e.Path)).Select(e => e.Path));
        view.SetQuery("");
        Assert.Equal(2, view.Display.Count(e => checkedPaths.Contains(e.Path)));
    }
}
