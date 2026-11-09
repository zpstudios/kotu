using KOTU.Core.Routing;
using KOTU.Core.Settings;
using Xunit;
using Column = KOTU.Core.Routing.ExplorerColumnLayout.Column;

namespace KOTU.Core.Tests;

public class ExplorerColumnLayoutTests
{
    private const string A = @"C:\fixtures\alpha";
    private const string B = @"C:\fixtures\beta";
    private static void Visit(ExplorerSession session, string folder)
    {
        if (session.BeginNavigation(folder)) session.ClearNavigationEntries();
        session.AcceptScan([new(folder + @"\sample.txt", "sample.txt", false, 12, DateTime.UnixEpoch, DateTime.UnixEpoch)]);
        session.ArrangeFromScan();
        session.MarkDisplayed();
    }

    [Fact]
    public void Default_matches_existing_metadata_widths_and_name_takes_remainder()
    {
        var widths = ExplorerColumnLayout.Default.Measure(900);
        Assert.Equal(new double[] { 380, 88, 88, 138, 138 }, widths);
        Assert.Equal(900, widths.Sum() + 68);
    }

    [Fact]
    public void Every_visibility_combination_removes_hidden_slots_and_gaps_and_fits()
    {
        for (var mask = 0; mask < 16; ++mask)
        {
            var columns = ExplorerColumnLayout.Default;
            for (var index = 1; index < 5; ++index)
                columns = columns.WithVisibility((Column)index, (mask & (1 << (index - 1))) != 0);
            foreach (var width in new double[] { 100, 300, 507, 508, 700, 900, 1600 })
            {
                var measured = columns.Measure(width);
                Assert.Equal(width - 68, measured.Sum(), 8);
                Assert.True(measured[0] > 0);
                foreach (var column in ExplorerColumnLayout.Order)
                {
                    Assert.True(double.IsFinite(measured[(int)column]));
                    if (!columns.IsVisible(column)) Assert.Equal(0, measured[(int)column]);
                    else if (width >= 700)
                        Assert.True(measured[(int)column] >= ExplorerColumnLayout.Minimum(column));
                }
            }
        }
    }

    [Theory]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    [InlineData(double.NegativeInfinity)]
    [InlineData(double.MinValue)]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(68)]
    [InlineData(double.MaxValue)]
    public void Degenerate_viewports_never_produce_negative_or_nonfinite_widths(double width)
    {
        foreach (var layout in new[] { ExplorerColumnLayout.Default, ExplorerColumnLayout.Default with { CustomWidths = true } })
        {
            var measured = layout.Measure(width);
            Assert.All(measured, value => Assert.True(double.IsFinite(value) && value >= 0));
            if (double.IsFinite(width) && width >= 0) Assert.True(measured.Sum() <= width);
        }
    }

    [Fact]
    public void Resize_transfers_only_between_adjacent_visible_columns_and_preserves_ratios_on_resize()
    {
        var initial = ExplorerColumnLayout.Default.WithVisibility(Column.Size, false).WithVisibility(Column.Created, false);
        var before = initial.Measure(900);
        var resized = initial.Resize(900, Column.Name, 16);
        var after = resized.Measure(900);
        Assert.Equal(before[0] + 16, after[0], 5);
        Assert.Equal(before[2] - 16, after[2], 5);
        Assert.Equal(before[4], after[4], 5);
        Assert.Equal(0, after[1]);
        Assert.Equal(0, after[3]);
        var wider = resized.Measure(1400);
        Assert.Equal((after[0] - 120) / (after[2] - 8 - 48), (wider[0] - 120) / (wider[2] - 8 - 48), 8);
        Assert.Equal(1400, wider.Sum() + 68, 8);
    }

    [Fact]
    public void Transfers_clamp_at_both_minima_and_invalid_or_impossible_requests_are_noops()
    {
        var initial = ExplorerColumnLayout.Default;
        var leftLimit = initial.Resize(900, Column.Name, -double.MaxValue).Measure(900);
        Assert.InRange(leftLimit[0], 120, 120.00001);
        var rightLimit = initial.Resize(900, Column.Name, double.MaxValue).Measure(900);
        Assert.InRange(rightLimit[1] - 8, 48, 48.00001);
        Assert.Equal(initial, initial.Resize(900, Column.Modified, 16));
        Assert.Equal(initial, initial.Resize(900, (Column)999, 16));
        Assert.Equal(initial, initial.Resize(500, Column.Name, 16));
        Assert.Equal(initial, initial.Resize(double.NaN, Column.Name, 16));
        Assert.Equal(initial, initial.Resize(900, Column.Name, double.PositiveInfinity));
        Assert.Equal(initial, initial.Resize(900, Column.Name, double.NaN));
        var hidden = initial.WithVisibility(Column.Size, false);
        Assert.Equal(hidden, hidden.Resize(900, Column.Size, 16));
    }

    [Theory]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    [InlineData(double.NegativeInfinity)]
    [InlineData(-1)]
    [InlineData(0)]
    [InlineData(1000001)]
    public void Invalid_persisted_weights_fall_back_per_field_without_repair_writes(double invalid)
    {
        var settings = new CountingSettings();
        settings.Set(ExplorerColumnLayout.Prefix + "custom", true);
        settings.Set(ExplorerColumnLayout.Key(Column.Name, "weight"), invalid);
        settings.Set(ExplorerColumnLayout.Key(Column.Type, "weight"), 77.0);
        settings.Set(ExplorerColumnLayout.Key(Column.Size, "visible"), false);
        settings.ClearWrites();
        var layout = ExplorerColumnLayout.Read(settings);
        Assert.Equal(ExplorerColumnLayout.Default.NameWeight, layout.NameWeight);
        Assert.Equal(77, layout.TypeWeight);
        Assert.False(layout.SizeVisible);
        Assert.Empty(settings.Written);
        Assert.Equal(0, settings.Saves);
        var malformedObject = layout with { NameWeight = invalid };
        Assert.All(malformedObject.Measure(900), width => Assert.True(double.IsFinite(width) && width >= 0));
    }

    [Fact]
    public void Restore_visit_measure_and_recreation_do_not_write()
    {
        var settings = new CountingSettings();
        settings.Set(ExplorerColumnLayout.Key(Column.Size, "visible"), false);
        settings.ClearWrites();
        using var first = new ExplorerSession { Settings = settings };
        Visit(first, A);
        first.ReadColumns().Measure(900);
        Visit(first, B);
        first.ReadColumns().Measure(400);
        using var second = new ExplorerSession { Settings = settings };
        Visit(second, A);
        Assert.False(second.Columns.SizeVisible);
        Assert.Empty(settings.Written);
        Assert.Equal(0, settings.Saves);
    }

    [Fact]
    public void Visibility_writes_only_requested_field_and_preserves_other_window_widths_and_folder_preferences()
    {
        var settings = new CountingSettings();
        using var first = new ExplorerSession { Settings = settings };
        using var second = new ExplorerSession { Settings = settings };
        Visit(first, A);
        Visit(second, B);
        first.SelectViewMode(A, ExplorerPresentationState.ViewMode.List);
        first.SelectSort(A, ExplorerListing.SortKey.Size);
        var folderView = settings.Get(ExplorerFolderPreferences.KeyFor(A)! + ".view", "");
        Assert.True(second.ResizeColumns(B, second.Columns, 900, Column.Name, 16));
        var latestWidths = second.Columns;
        settings.ClearWrites();
        Assert.True(first.SetColumnVisible(A, Column.Type, false));
        Assert.Equal(new[] { ExplorerColumnLayout.Key(Column.Type, "visible") }, settings.Written);
        Assert.Equal(latestWidths.NameWeight, first.Columns.NameWeight);
        Assert.Equal(latestWidths.SizeWeight, first.Columns.SizeWeight);
        Assert.Equal(folderView, settings.Get(ExplorerFolderPreferences.KeyFor(A)! + ".view", ""));
        Assert.Equal(ExplorerListing.SortKey.Size, first.SortKey);
        Assert.Equal(ExplorerPresentationState.ViewMode.List, first.Presentation.Mode);
        Assert.True(first.Descending);
    }

    [Fact]
    public void Resize_changes_only_width_fields_and_rejects_stale_surface_snapshot()
    {
        var settings = new CountingSettings();
        using var session = new ExplorerSession { Settings = settings };
        Visit(session, A);
        var oldSurface = session.Columns;
        Assert.True(session.SetColumnVisible(A, Column.Modified, false));
        settings.ClearWrites();
        Assert.False(session.ResizeColumns(A, oldSurface, 900, Column.Name, 16));
        Assert.Empty(settings.Written);
        Assert.True(session.ResizeColumns(A, session.Columns, 900, Column.Name, 16));
        Assert.All(settings.Written, key => Assert.True(key.EndsWith(".weight", StringComparison.Ordinal) || key == ExplorerColumnLayout.Prefix + "custom"));
        Assert.DoesNotContain(ExplorerColumnLayout.Key(Column.Modified, "weight"), settings.Written);
        Assert.False(session.Columns.ModifiedVisible);
    }

    [Fact]
    public void Reset_changes_only_columns_and_shared_surface_reads_latest_without_resetting_checks_or_view()
    {
        var settings = new CountingSettings();
        using var session = new ExplorerSession { Settings = settings };
        Visit(session, A);
        session.SelectViewMode(A, ExplorerPresentationState.ViewMode.List);
        session.SelectSort(A, ExplorerListing.SortKey.Modified);
        session.SetItemChecked(A, session.Display[0].Path, true);
        var display = session.Display;
        session.SetColumnVisible(A, Column.Created, false);
        session.ResizeColumns(A, session.Columns, 900, Column.Name, 16);
        settings.ClearWrites();
        Assert.True(session.ResetColumns(A));
        Assert.Equal(ExplorerColumnLayout.Default, session.ReadColumns());
        Assert.All(settings.Written, key => Assert.StartsWith(ExplorerColumnLayout.Prefix, key));
        Assert.Equal(ExplorerPresentationState.ViewMode.List, session.Presentation.Mode);
        Assert.Equal(ExplorerListing.SortKey.Modified, session.SortKey);
        Assert.Equal(1, session.CheckSummary.Total);
        Assert.Same(display, session.Display);
        settings.ClearWrites();
        Assert.False(session.ResetColumns(A));
        Assert.Empty(settings.Written);
    }

    [Fact]
    public void Name_invalid_columns_navigation_gaps_old_folders_and_disposal_cannot_write()
    {
        var settings = new CountingSettings();
        using var session = new ExplorerSession { Settings = settings };
        Visit(session, A);
        Assert.False(session.SetColumnVisible(A, Column.Name, false));
        Assert.False(session.SetColumnVisible(A, (Column)999, false));
        Assert.True(session.Columns.IsVisible(Column.Name));
        var original = session.Columns;
        session.BeginNavigation(B);
        Assert.False(session.SetColumnVisible(A, Column.Size, false));
        Assert.False(session.SetColumnVisible(B, Column.Size, false));
        Assert.False(session.ResizeColumns(B, original, 900, Column.Name, 16));
        Assert.False(session.ResetColumns(B));
        session.ClearFailedScan();
        Assert.False(session.SetColumnVisible(B, Column.Size, false));
        Visit(session, B);
        Assert.False(session.SetColumnVisible(A, Column.Size, false));
        session.Dispose();
        Assert.False(session.SetColumnVisible(B, Column.Size, false));
        Assert.False(session.ResizeColumns(B, original, 900, Column.Name, 16));
        Assert.False(session.ResetColumns(B));
        Assert.Empty(settings.Written);
        Assert.Equal(0, settings.Saves);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Null_and_standalone_stores_remember_across_folders_only_during_the_session(bool memory)
    {
        using var first = new ExplorerSession { Settings = memory ? new MemorySettingsService() : null };
        Visit(first, A);
        Assert.True(first.SetColumnVisible(A, Column.Type, false));
        Assert.True(first.ResizeColumns(A, first.Columns, 900, Column.Name, 16));
        var saved = first.Columns;
        Visit(first, B);
        Assert.Equal(saved, first.ReadColumns());
        using var fresh = new ExplorerSession { Settings = memory ? new MemorySettingsService() : null };
        Visit(fresh, A);
        Assert.Equal(ExplorerColumnLayout.Default, fresh.Columns);
    }

    [Fact]
    public async Task Json_restart_restores_columns_and_malformed_fields_fall_back_without_rewriting()
    {
        var directory = Path.Combine(Path.GetTempPath(), "kotu-column-layout-" + Guid.NewGuid().ToString("N"));
        var path = Path.Combine(directory, "settings.json");
        try
        {
            var store = new JsonSettingsService(path);
            ExplorerColumnLayout expected;
            using (var first = new ExplorerSession { Settings = store })
            {
                Visit(first, A);
                first.SetColumnVisible(A, Column.Created, false);
                first.ResizeColumns(A, first.Columns, 900, Column.Name, 16);
                expected = first.Columns;
            }
            await store.SaveAsync();
            var json = File.ReadAllText(path);
            using (var second = new ExplorerSession { Settings = new JsonSettingsService(path) })
            {
                Visit(second, B);
                Assert.Equal(expected, second.ReadColumns());
            }
            Assert.Equal(json, File.ReadAllText(path));
            File.WriteAllText(path, """
                { "explorer.columns.v1.custom": true, "explorer.columns.v1.name.weight": "NaN",
                  "explorer.columns.v1.size.weight": -2, "explorer.columns.v1.type.weight": 1e309,
                  "explorer.columns.v1.created.weight": null, "explorer.columns.v1.size.visible": "false",
                  "explorer.columns.v1.modified.visible": false, "explorer.columns.v1.name.visible": false }
                """);
            var malformed = File.ReadAllText(path);
            using var restored = new ExplorerSession { Settings = new JsonSettingsService(path) };
            Visit(restored, A);
            Assert.Equal(ExplorerColumnLayout.Default with { CustomWidths = true, ModifiedVisible = false }, restored.Columns);
            Assert.Equal(malformed, File.ReadAllText(path));
            Assert.False(File.Exists(path + ".tmp"));
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true); }
    }

    private sealed class CountingSettings : ISettingsService
    {
        private readonly Dictionary<string, object?> _values = [];
        internal List<string> Written { get; } = [];
        internal int Saves { get; private set; }
        public string FilePath => "Test only";
        public T Get<T>(string key, T fallback) => _values.TryGetValue(key, out var value) && value is T typed ? typed : fallback;
        public void Set<T>(string key, T value) { _values[key] = value; Written.Add(key); }
        public void Save() => ++Saves;
        public Task SaveAsync() { Save(); return Task.CompletedTask; }
        internal void ClearWrites() { Written.Clear(); Saves = 0; }
    }
}
