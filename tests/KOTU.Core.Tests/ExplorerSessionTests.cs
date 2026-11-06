using KOTU.Core.Routing;
using KOTU.Core.Settings;
using Xunit;

namespace KOTU.Core.Tests;

public class ExplorerSessionTests
{
    private const string Folder = "C:/fixture";
    private static ExplorerListing.Entry E(string name, bool folder = false, long size = 1,
        int modified = 0, int created = 0) => new(Folder + "/" + name, name, folder, size,
            DateTime.UnixEpoch.AddDays(modified), DateTime.UnixEpoch.AddDays(created));

    private static ExplorerSession Loaded(params ExplorerListing.Entry[] entries)
    {
        var session = new ExplorerSession();
        Assert.True(session.BeginNavigation(Folder));
        session.ClearNavigationEntries();
        Publish(session, entries);
        return session;
    }

    private static void Publish(ExplorerSession session, IReadOnlyList<ExplorerListing.Entry> entries)
    {
        session.AcceptScan(entries);
        session.ArrangeFromScan();
        session.MarkDisplayed();
    }

    [Theory]
    [InlineData("name", ExplorerListing.SortKey.Name, false)]
    [InlineData("type", ExplorerListing.SortKey.Type, false)]
    [InlineData("size", ExplorerListing.SortKey.Size, true)]
    [InlineData("created", ExplorerListing.SortKey.Created, true)]
    [InlineData("modified", ExplorerListing.SortKey.Modified, true)]
    [InlineData("unknown", ExplorerListing.SortKey.Name, false)]
    public void Settings_without_direction_use_legacy_key_defaults(string stored,
        ExplorerListing.SortKey expected, bool descending)
    {
        var settings = new CountingSettings();
        settings.Set("explorer.sort", stored);
        var session = new ExplorerSession { Settings = settings };
        Assert.Equal(expected, session.SortKey);
        Assert.Equal(descending, session.Descending);
        Assert.False(session.ShowHidden);
        Assert.Equal(0, settings.Saves);
    }

    [Theory]
    [InlineData(ExplorerListing.SortKey.Name, false)]
    [InlineData(ExplorerListing.SortKey.Type, false)]
    [InlineData(ExplorerListing.SortKey.Size, true)]
    [InlineData(ExplorerListing.SortKey.Created, true)]
    [InlineData(ExplorerListing.SortKey.Modified, true)]
    public void Sort_switch_defaults_same_key_toggles_and_settings_round_trip(
        ExplorerListing.SortKey key, bool defaultDescending)
    {
        var settings = new CountingSettings();
        var session = Loaded(E("a.txt"), E("b.txt"));
        session.Settings = settings;
        // Start on a different key even for Name.
        session.SelectSort(key == ExplorerListing.SortKey.Name
            ? ExplorerListing.SortKey.Type : ExplorerListing.SortKey.Name);
        session.SelectSort(key);
        Assert.Equal(defaultDescending, session.Descending);
        session.SelectSort(key);
        Assert.Equal(!defaultDescending, session.Descending);
        Assert.Equal(key.ToString().ToLowerInvariant(), settings.Get("explorer.sort", ""));
        Assert.Equal(!defaultDescending, settings.Get("explorer.sortDesc", defaultDescending));
        Assert.Equal(3, settings.Saves);
        var restored = new ExplorerSession { Settings = settings };
        Assert.Equal(key, restored.SortKey);
        Assert.Equal(session.Descending, restored.Descending);
        Assert.Empty(restored.Entries); // settings don't persist the prior in-memory tie order
    }

    [Fact]
    public void Show_hidden_round_trips_but_does_not_filter_existing_scan_or_change_checks()
    {
        var item = E("a.txt");
        var session = Loaded(item);
        var settings = new CountingSettings();
        session.Settings = settings;
        session.SetItemChecked(Folder, item.Path, true);
        session.SetShowHidden(true);
        Assert.True(new ExplorerSession { Settings = settings }.ShowHidden);
        Assert.Equal(1, settings.Saves);
        Assert.Same(item, Assert.Single(session.Display));
        Assert.True(session.IsChecked(item.Path));
    }

    [Fact]
    public void Every_sort_key_arranges_the_real_display_with_folders_first()
    {
        var session = Loaded(E("a.jpg", size: 2, modified: 1, created: 3),
            E("b.txt", size: 3, modified: 3, created: 1),
            E("c.jpg", size: 1, modified: 2, created: 2), E("folder", folder: true));
        session.SelectSort(ExplorerListing.SortKey.Size);
        Assert.Equal(new[] { "folder", "b.txt", "a.jpg", "c.jpg" }, Names(session));
        session.SelectSort(ExplorerListing.SortKey.Modified);
        Assert.Equal(new[] { "folder", "b.txt", "c.jpg", "a.jpg" }, Names(session));
        session.SelectSort(ExplorerListing.SortKey.Created);
        Assert.Equal(new[] { "folder", "a.jpg", "c.jpg", "b.txt" }, Names(session));
        session.SelectSort(ExplorerListing.SortKey.Type);
        Assert.Equal(new[] { "folder", "a.jpg", "c.jpg", "b.txt" }, Names(session));
        session.SelectSort(ExplorerListing.SortKey.Name);
        Assert.Equal(new[] { "folder", "a.jpg", "b.txt", "c.jpg" }, Names(session));
    }

    [Fact]
    public void Keyword_is_only_a_projection_and_sort_ties_inherit_the_whole_previous_order()
    {
        var raw = new[] { E("a.txt"), E("b.txt"), E("c.txt") };
        var session = Loaded(raw);
        session.SelectSort(ExplorerListing.SortKey.Name); // descending
        session.SetNameFilter("b");
        session.SelectSort(ExplorerListing.SortKey.Size); // all equal, descending names survive
        Assert.Equal(new[] { "c.txt", "b.txt", "a.txt" }, session.Ordered.Select(e => e.Name));
        Assert.Equal("b.txt", Assert.Single(session.Display).Name);
        session.SelectSort(ExplorerListing.SortKey.Size); // reverse key, not equal items
        session.SetNameFilter("");
        Assert.Equal(new[] { "c.txt", "b.txt", "a.txt" }, Names(session));
        Publish(session, raw); // rescans deliberately reset stable ties to the raw order
        Assert.Equal(new[] { "a.txt", "b.txt", "c.txt" }, Names(session));
    }

    [Fact]
    public void Extension_filter_restarts_ties_and_module_change_resets_only_extensions()
    {
        var session = Loaded(E("a.txt"), E("b.jpg"), E("c.txt"));
        session.SetExtensions(new[] { ".txt", ".jpg" });
        session.SelectSort(ExplorerListing.SortKey.Name);
        session.SelectSort(ExplorerListing.SortKey.Size);
        session.SetExtensionVisible(".JPG", false);
        session.ArrangeFromScan();
        Assert.Equal(new[] { "a.txt", "c.txt" }, Names(session));
        session.SetExtensions(new[] { ".txt", ".jpg" });
        Assert.Equal(1, session.HiddenExtensionCount);
        session.SetNameFilter("c");
        session.SetItemChecked(Folder, Folder + "/c.txt", true);
        session.SetExtensions(new[] { "*" });
        session.ArrangeFromScan();
        Assert.Equal(0, session.HiddenExtensionCount);
        Assert.Equal("c", session.Query);
        Assert.True(session.IsChecked(Folder + "/c.txt"));
        Assert.Equal(3, session.Ordered.Count);
    }

    [Fact]
    public void Filter_hidden_checks_survive_but_workset_is_display_order_and_files_only_is_explicit()
    {
        var folder = E("folder", folder: true);
        var alpha = E("alpha.txt");
        var beta = E("beta.jpg");
        var session = Loaded(folder, alpha, beta);
        foreach (var entry in new[] { beta, alpha, folder })
            Assert.True(session.SetItemChecked(Folder, entry.Path, true));
        session.SelectSort(ExplorerListing.SortKey.Name);
        Assert.Equal(new[] { folder.Path, beta.Path, alpha.Path }, session.CheckedPathsInView());
        Assert.Equal(new[] { beta.Path, alpha.Path }, session.CheckedPathsInView(filesOnly: true));
        session.SetNameFilter("alpha");
        Assert.Equal(new[] { alpha.Path }, session.CheckedPathsInView());
        Assert.True(session.IsChecked(beta.Path));
        Assert.False(session.SetItemChecked(Folder, beta.Path, false)); // hidden is not a user target
        session.SetNameFilter("");
        session.SetExtensionVisible(".jpg", false);
        session.ArrangeFromScan();
        Assert.Equal(new[] { folder.Path, alpha.Path }, session.CheckedPathsInView());
        Assert.True(session.IsChecked(beta.Path));
        session.ClearExtensionFilter();
        session.ArrangeFromScan();
        Assert.Equal(new[] { folder.Path, beta.Path, alpha.Path }, session.CheckedPathsInView());
    }

    [Fact]
    public void Same_folder_failure_keeps_query_and_checks_until_an_accepted_scan_prunes()
    {
        var a = E("alpha.txt");
        var b = E("beta.txt");
        var session = Loaded(a, b);
        session.SetItemChecked(Folder, a.Path, true);
        session.SetItemChecked(Folder, b.Path, true);
        session.SetNameFilter("alpha");
        Assert.False(session.BeginNavigation(Folder.ToUpperInvariant()));
        Assert.True(session.IsChecked(b.Path)); // no eager pruning at scan start
        Assert.Equal(Folder, session.DisplayFolder);
        session.ClearFailedScan();
        Assert.Equal("alpha", session.Query);
        Assert.Empty(session.Entries);
        Assert.Empty(session.Ordered);
        Assert.Empty(session.Display);
        Assert.Equal("", session.DisplayFolder);
        Assert.True(session.IsChecked(b.Path)); // a failed scan is not an empty accepted scan
        Assert.Empty(session.CheckedPathsForFolder(Folder));
        session.AcceptScan(new[] { a });
        Assert.False(session.IsChecked(b.Path));
        Assert.True(session.IsChecked(a.Path));
        session.ArrangeFromScan();
        Assert.Empty(session.CheckedPathsForFolder(Folder)); // publication has not occurred yet
        session.MarkDisplayed();
        Assert.Equal(new[] { a.Path }, session.CheckedPathsForFolder(Folder));
        Assert.Equal("alpha", session.Query);
        Publish(session, []);
        Assert.False(session.IsChecked(a.Path));
    }

    [Fact]
    public void Real_navigation_clears_checks_early_and_entries_query_at_the_teardown_boundary()
    {
        var item = E("alpha.txt");
        var session = Loaded(item);
        session.SetItemChecked(Folder, item.Path, true);
        session.SetNameFilter("alpha");
        Assert.True(session.BeginNavigation("C:/other"));
        Assert.False(session.IsChecked(item.Path));
        Assert.Equal(Folder, session.DisplayFolder);
        Assert.Equal("C:/other", session.CurrentFolder);
        Assert.Single(session.Display); // old UI teardown still happens at its existing boundary
        Assert.False(session.SetItemChecked(Folder, item.Path, true));
        Assert.False(session.SetItemChecked("C:/other", item.Path, true));
        session.ClearNavigationEntries();
        Assert.Empty(session.Display);
        Assert.Empty(session.Ordered);
        Assert.Empty(session.Entries);
        Assert.Equal("", session.Query);
        Assert.Equal("", session.DisplayFolder);
    }

    [Fact]
    public void Checks_are_case_insensitive_guarded_by_both_folders_and_reject_parent_or_missing_paths()
    {
        var item = E("alpha.txt");
        var session = Loaded(item);
        Assert.False(session.SetItemChecked("C:/other", item.Path, true));
        Assert.False(session.SetItemChecked(Folder, "C:/", true)); // separate parent tile
        Assert.False(session.SetItemChecked(Folder, Folder + "/missing.txt", true));
        Assert.True(session.SetItemChecked(Folder.ToUpperInvariant(), item.Path.ToUpperInvariant(), true));
        Assert.False(session.SetItemChecked(Folder, item.Path, true)); // no duplicate change notification
        Assert.Equal(new[] { item.Path }, session.CheckedPathsForFolder(Folder.ToUpperInvariant()));
        Assert.Empty(session.CheckedPathsForFolder(null));
        Assert.Empty(session.CheckedPathsForFolder("C:/other"));
        Assert.True(session.SetItemChecked(Folder, item.Path.ToUpperInvariant(), false));
        Assert.Empty(session.CheckedPathsInView());
    }

    [Fact]
    public void Search_selection_and_other_sessions_cannot_mutate_the_checked_workset()
    {
        var a = E("alpha.txt");
        var b = E("beta.txt");
        var first = Loaded(a, b);
        var second = Loaded(a, b);
        first.SetItemChecked(Folder, a.Path, true);
        var search = new ExplorerTypeAhead();
        Assert.Equal(1, search.Find("b", 1, first.Display, 0));
        first.SetNameFilter("beta");
        Assert.Empty(first.CheckedPathsInView());
        Assert.True(first.IsChecked(a.Path));
        Assert.False(second.IsChecked(a.Path));
        first.SetNameFilter("");
        Assert.Equal(new[] { a.Path }, first.CheckedPathsInView());
    }

    private static IEnumerable<string> Names(ExplorerSession session) => session.Display.Select(e => e.Name);

    private sealed class CountingSettings : ISettingsService
    {
        private readonly MemorySettingsService _inner = new();
        public int Saves { get; private set; }
        public string FilePath => _inner.FilePath;
        public T Get<T>(string key, T defaultValue) => _inner.Get(key, defaultValue);
        public void Set<T>(string key, T value) => _inner.Set(key, value);
        public void Save() => Saves++;
        public Task SaveAsync() { Save(); return Task.CompletedTask; }
    }
}
