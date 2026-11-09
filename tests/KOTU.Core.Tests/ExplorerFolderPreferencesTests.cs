using KOTU.Core.Routing;
using KOTU.Core.Settings;
using Xunit;

namespace KOTU.Core.Tests;

public class ExplorerFolderPreferencesTests
{
    private const string A = @"C:\fixtures\alpha";
    private const string B = @"C:\fixtures\beta";

    private static ExplorerListing.Entry[] Entries(string folder) =>
    [
        new(folder + @"\alpha.txt", "alpha.txt", false, 30, DateTime.UnixEpoch.AddDays(1), DateTime.UnixEpoch),
        new(folder + @"\beta.txt", "beta.txt", false, 10, DateTime.UnixEpoch.AddDays(2), DateTime.UnixEpoch),
        new(folder + @"\gamma.txt", "gamma.txt", false, 20, DateTime.UnixEpoch.AddDays(3), DateTime.UnixEpoch),
        new(folder + @"\folder", "folder", true, 0, DateTime.UnixEpoch, DateTime.UnixEpoch),
    ];

    private static void Visit(ExplorerSession session, string folder)
    {
        if (session.BeginNavigation(folder)) session.ClearNavigationEntries();
        session.AcceptScan(Entries(folder));
        session.ArrangeFromScan();
        session.MarkDisplayed();
    }

    private static string[] Names(ExplorerSession session) => session.Display.Select(entry => entry.Name).ToArray();

    [Fact]
    public async Task Real_json_restart_restores_two_folders_and_their_actual_display_order()
    {
        var directory = Path.Combine(Path.GetTempPath(), "kotu-folder-preferences-" + Guid.NewGuid().ToString("N"));
        var path = Path.Combine(directory, "settings.json");
        try
        {
            var settings = new JsonSettingsService(path);
            using (var first = new ExplorerSession { Settings = settings })
            {
                Visit(first, A);
                Assert.True(first.SelectViewMode(A, ExplorerPresentationState.ViewMode.List));
                Assert.True(first.SelectSort(A, ExplorerListing.SortKey.Size));
                Assert.True(first.SelectSort(A, ExplorerListing.SortKey.Size));
                Visit(first, B);
                Assert.True(first.SelectViewMode(B, ExplorerPresentationState.ViewMode.List));
                Assert.True(first.SelectViewMode(B, ExplorerPresentationState.ViewMode.Preview));
                Assert.True(first.SelectSort(B, ExplorerListing.SortKey.Modified));
            }
            await settings.SaveAsync();
            var persisted = File.ReadAllText(path);
            using var restarted = new ExplorerSession { Settings = new JsonSettingsService(path) };
            Visit(restarted, A);
            Assert.Equal(ExplorerPresentationState.ViewMode.List, restarted.Presentation.Mode);
            Assert.Equal(ExplorerListing.SortKey.Size, restarted.SortKey);
            Assert.False(restarted.Descending);
            Assert.Equal(new[] { "folder", "beta.txt", "gamma.txt", "alpha.txt" }, Names(restarted));
            Visit(restarted, B);
            Assert.Equal(ExplorerPresentationState.ViewMode.Preview, restarted.Presentation.Mode);
            Assert.Equal(ExplorerListing.SortKey.Modified, restarted.SortKey);
            Assert.True(restarted.Descending);
            Assert.Equal(new[] { "folder", "gamma.txt", "beta.txt", "alpha.txt" }, Names(restarted));
            Assert.Equal(persisted, File.ReadAllText(path));
            Assert.False(File.Exists(path + ".tmp"));
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true); }
    }

    [Theory]
    [InlineData(@"C:\fixtures\alpha", "c:/fixtures/./other/../alpha/")]
    [InlineData(@"C:\", "c:/./child/../")]
    [InlineData(@"\\server\share", "//SERVER/share/./child/../")]
    [InlineData(@"\\server\share\alpha", "//SERVER/SHARE/other/../alpha/")]
    [InlineData(@"C:\fixtures\alpha", @"\\?\c:\fixtures\alpha\")]
    [InlineData(@"\\server\share\alpha", @"\\?\UNC\SERVER\SHARE\alpha\")]
    public void Equivalent_absolute_windows_paths_restore_the_same_folder(string saved, string alias)
    {
        var settings = new CountingSettings();
        using var session = new ExplorerSession { Settings = settings };
        Visit(session, saved);
        Assert.True(session.SelectViewMode(saved, ExplorerPresentationState.ViewMode.List));
        session.SelectSort(saved, ExplorerListing.SortKey.Size);
        session.BeginNavigation(alias);
        Assert.Equal(ExplorerPresentationState.ViewMode.List, session.Presentation.Mode);
        Visit(session, B);
        Visit(session, alias);
        Assert.Equal(ExplorerPresentationState.ViewMode.List, session.Presentation.Mode);
        Assert.Equal(ExplorerListing.SortKey.Size, session.SortKey);
        Assert.True(session.Descending);
        Assert.Equal(2, settings.Saves);
        using var restarted = new ExplorerSession { Settings = settings };
        Visit(restarted, alias);
        Assert.Equal(ExplorerPresentationState.ViewMode.List, restarted.Presentation.Mode);
        Assert.Equal(2, settings.Saves);
    }

    [Theory]
    [InlineData("")]
    [InlineData("relative")]
    [InlineData("C:relative")]
    [InlineData(@"\rooted")]
    [InlineData(@"\\server")]
    [InlineData(@"\\?\Volume{fixture}\alpha")]
    [InlineData(@"\\.\device")]
    [InlineData(@"C:\bad*name")]
    [InlineData(@"C:\bad:name")]
    [InlineData("C:\\bad\0name")]
    public void Invalid_folder_requests_never_write_preferences(string folder)
    {
        var settings = new CountingSettings();
        using var session = new ExplorerSession { Settings = settings };
        Visit(session, folder);
        Assert.False(session.SelectViewMode(folder, ExplorerPresentationState.ViewMode.List));
        Assert.False(session.SelectSort(folder, ExplorerListing.SortKey.Size));
        Assert.Equal(0, settings.Writes);
        Assert.Equal(0, settings.Saves);
        Assert.Null(ExplorerFolderPreferences.KeyFor(folder));
    }

    [Fact]
    public void Global_sort_remains_the_unsaved_folder_default_and_visit_does_not_create_settings()
    {
        var settings = new CountingSettings();
        settings.Set("explorer.sort", "created");
        settings.Set("explorer.sortDesc", false);
        var writes = settings.Writes;
        using var session = new ExplorerSession { Settings = settings };
        Visit(session, A);
        Visit(session, B);
        Assert.Equal(ExplorerListing.SortKey.Created, session.SortKey);
        Assert.False(session.Descending);
        Assert.Equal(ExplorerPresentationState.ViewMode.Preview, session.Presentation.Mode);
        Assert.Equal(writes, settings.Writes);
        Assert.Equal(0, settings.Saves);
        session.SelectSort(B, ExplorerListing.SortKey.Size);
        Assert.Equal("size", settings.Get("explorer.sort", ""));
        Assert.True(settings.Get("explorer.sortDesc", false));
        Visit(session, A);
        Assert.Equal(ExplorerListing.SortKey.Size, session.SortKey);
        Assert.True(session.Descending);
    }

    [Fact]
    public void First_user_change_remembers_the_other_fields_without_changing_global_sort_on_view_click()
    {
        var settings = new CountingSettings();
        using var session = new ExplorerSession { Settings = settings };
        Visit(session, A);
        session.SelectViewMode(A, ExplorerPresentationState.ViewMode.List);
        Assert.Equal("absent", settings.Get("explorer.sort", "absent"));
        Visit(session, B);
        session.SelectSort(B, ExplorerListing.SortKey.Size);
        Visit(session, A);
        Assert.Equal(ExplorerPresentationState.ViewMode.List, session.Presentation.Mode);
        Assert.Equal(ExplorerListing.SortKey.Name, session.SortKey);
        Assert.False(session.Descending);
        session.SelectSort(A, ExplorerListing.SortKey.Modified);
        Visit(session, B);
        Assert.Equal(ExplorerPresentationState.ViewMode.Preview, session.Presentation.Mode);
        Assert.Equal(ExplorerListing.SortKey.Size, session.SortKey);
        Assert.True(session.Descending);
        Assert.Equal(new[] { "folder", "alpha.txt", "gamma.txt", "beta.txt" }, Names(session));
    }

    [Fact]
    public void A_partial_folder_sort_uses_its_own_direction_default_even_if_global_key_matches()
    {
        var settings = new CountingSettings();
        settings.Set("explorer.sort", "name");
        settings.Set("explorer.sortDesc", true);
        settings.Set(ExplorerFolderPreferences.KeyFor(A)! + ".sort", "name");
        using var session = new ExplorerSession { Settings = settings };
        Visit(session, A);
        Assert.False(session.Descending);
        Assert.Equal(new[] { "folder", "alpha.txt", "beta.txt", "gamma.txt" }, Names(session));
        Visit(session, B);
        Assert.True(session.Descending);
        Assert.Equal(new[] { "folder", "gamma.txt", "beta.txt", "alpha.txt" }, Names(session));
        Assert.Equal(0, settings.Saves);
    }

    [Fact]
    public void Missing_or_corrupt_fields_fall_back_independently_and_invalid_enums_are_rejected()
    {
        var settings = new CountingSettings();
        settings.Set("explorer.sort", "type");
        settings.Set("explorer.sortDesc", true);
        var key = ExplorerFolderPreferences.KeyFor(A)!;
        settings.Set(key + ".view", "999");
        settings.Set(key + ".sort", 999);
        settings.Set(key + ".sortDesc", "not a bool");
        using var session = new ExplorerSession { Settings = settings };
        Visit(session, A);
        Assert.Equal(ExplorerPresentationState.ViewMode.Preview, session.Presentation.Mode);
        Assert.Equal(ExplorerListing.SortKey.Type, session.SortKey);
        Assert.True(session.Descending);
        settings.Set(key + ".view", "list");
        settings.Set(key + ".sort", "size");
        Visit(session, B);
        Visit(session, A);
        Assert.Equal(ExplorerPresentationState.ViewMode.List, session.Presentation.Mode);
        Assert.Equal(ExplorerListing.SortKey.Size, session.SortKey);
        Assert.True(session.Descending);
        var writes = settings.Writes;
        session.SelectSort((ExplorerListing.SortKey)999);
        Assert.False(session.SelectSort(A, (ExplorerListing.SortKey)999));
        Assert.False(session.SelectViewMode(A, (ExplorerPresentationState.ViewMode)999));
        Assert.False(session.SelectViewMode(A, ExplorerPresentationState.ViewMode.List));
        Assert.Equal(writes, settings.Writes);
        Assert.Equal(0, settings.Saves);
    }

    [Fact]
    public void Stale_window_updates_preserve_other_fields_and_other_folders()
    {
        var settings = new CountingSettings();
        using var first = new ExplorerSession { Settings = settings };
        using var second = new ExplorerSession { Settings = settings };
        Visit(first, A);
        Visit(second, A);
        first.SelectViewMode(A, ExplorerPresentationState.ViewMode.List);
        second.SelectSort(A, ExplorerListing.SortKey.Size);
        second.SelectSort(A, ExplorerListing.SortKey.Size);
        Visit(second, B);
        second.SelectSort(B, ExplorerListing.SortKey.Modified);
        first.SelectViewMode(A, ExplorerPresentationState.ViewMode.Preview);
        first.SelectViewMode(A, ExplorerPresentationState.ViewMode.List);
        using var restored = new ExplorerSession { Settings = settings };
        Visit(restored, A);
        Assert.Equal(ExplorerPresentationState.ViewMode.List, restored.Presentation.Mode);
        Assert.Equal(ExplorerListing.SortKey.Size, restored.SortKey);
        Assert.False(restored.Descending);
        Visit(restored, B);
        Assert.Equal(ExplorerPresentationState.ViewMode.Preview, restored.Presentation.Mode);
        Assert.Equal(ExplorerListing.SortKey.Modified, restored.SortKey);
        Assert.True(restored.Descending);
        // 다른 창이 저장한 값은 같은 폴더 재스캔 때 현재 화면을 바꾸지 않는다.
        Visit(second, A);
        first.SelectViewMode(A, ExplorerPresentationState.ViewMode.Preview);
        var writes = settings.Writes;
        Visit(second, A);
        Assert.Equal(ExplorerPresentationState.ViewMode.List, second.Presentation.Mode);
        Assert.Equal(writes, settings.Writes);
        Visit(second, B);
        Visit(second, A);
        Assert.Equal(ExplorerPresentationState.ViewMode.Preview, second.Presentation.Mode);
    }

    [Fact]
    public void Path_alias_navigation_preserves_preference_state_and_existing_raw_check_boundary()
    {
        var settings = new CountingSettings();
        using var first = new ExplorerSession { Settings = settings };
        Visit(first, A);
        first.SelectViewMode(A, ExplorerPresentationState.ViewMode.List);
        first.SetItemChecked(A, first.Display[0].Path, true);
        using var other = new ExplorerSession { Settings = settings };
        Visit(other, A);
        other.SelectViewMode(A, ExplorerPresentationState.ViewMode.Preview);
        Assert.False(first.BeginNavigation(A.ToUpperInvariant()));
        Assert.Equal(1, first.CheckSummary.Total);
        var alias = "c:/fixtures/./alpha/";
        Assert.True(first.BeginNavigation(alias));
        Assert.Equal(ExplorerPresentationState.ViewMode.List, first.Presentation.Mode);
        Assert.Empty(first.CheckedPathsInView());
        var writes = settings.Writes;
        Assert.False(first.SelectViewMode(A, ExplorerPresentationState.ViewMode.Preview));
        Assert.False(first.SelectSort(alias, ExplorerListing.SortKey.Size));
        Assert.Equal(writes, settings.Writes);
    }

    [Fact]
    public void Navigation_gap_failure_stale_surfaces_and_disposal_cannot_write_a_different_folder()
    {
        var settings = new CountingSettings();
        using var session = new ExplorerSession { Settings = settings };
        Visit(session, A);
        session.SelectViewMode(A, ExplorerPresentationState.ViewMode.List);
        session.SelectSort(A, ExplorerListing.SortKey.Size);
        var writes = settings.Writes;
        session.BeginNavigation(B);
        Assert.Equal(ExplorerPresentationState.ViewMode.Preview, session.Presentation.Mode);
        Assert.False(session.SelectViewMode(A, ExplorerPresentationState.ViewMode.List));
        Assert.False(session.SelectViewMode(B, ExplorerPresentationState.ViewMode.List));
        Assert.False(session.SelectSort(A, ExplorerListing.SortKey.Name));
        Assert.False(session.SelectSort(B, ExplorerListing.SortKey.Name));
        session.ClearFailedScan();
        Assert.False(session.SelectViewMode(B, ExplorerPresentationState.ViewMode.List));
        Visit(session, B);
        Assert.False(session.SelectViewMode(A, ExplorerPresentationState.ViewMode.List));
        Assert.False(session.SelectSort(A, ExplorerListing.SortKey.Modified));
        session.Dispose();
        Assert.False(session.SelectViewMode(B, ExplorerPresentationState.ViewMode.List));
        Assert.False(session.SelectSort(B, ExplorerListing.SortKey.Size));
        Assert.Equal(writes, settings.Writes);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Null_settings_and_standalone_memory_remember_within_session_but_not_after_recreation(bool standaloneMemory)
    {
        ISettingsService? settings = standaloneMemory ? new MemorySettingsService() : null;
        using var session = new ExplorerSession { Settings = settings };
        Visit(session, A);
        session.SelectViewMode(A, ExplorerPresentationState.ViewMode.List);
        session.SelectSort(A, ExplorerListing.SortKey.Size);
        session.SelectSort(A, ExplorerListing.SortKey.Size);
        Visit(session, B);
        session.SelectSort(B, ExplorerListing.SortKey.Modified);
        Visit(session, A);
        Assert.Equal(ExplorerPresentationState.ViewMode.List, session.Presentation.Mode);
        Assert.Equal(ExplorerListing.SortKey.Size, session.SortKey);
        Assert.False(session.Descending);
        if (settings is not null) await settings.SaveAsync();
        using var fresh = new ExplorerSession { Settings = standaloneMemory ? new MemorySettingsService() : null };
        Visit(fresh, A);
        Assert.Equal(ExplorerPresentationState.ViewMode.Preview, fresh.Presentation.Mode);
        Assert.Equal(ExplorerListing.SortKey.Name, fresh.SortKey);
        Assert.False(fresh.Descending);
    }

    private sealed class CountingSettings : ISettingsService
    {
        private readonly MemorySettingsService _inner = new();
        public string FilePath => _inner.FilePath;
        public int Writes { get; private set; }
        public int Saves { get; private set; }
        public T Get<T>(string key, T fallback) => _inner.Get(key, fallback);
        public void Set<T>(string key, T value) { ++Writes; _inner.Set(key, value); }
        public void Save() => ++Saves;
        public Task SaveAsync() { Save(); return Task.CompletedTask; }
    }
}
