using Xunit;
using KOTU.Core.Contracts;
using KOTU.Core.Routing;

namespace KOTU.Core.Tests;

public sealed class BrowseInfoTests
{
    private static ExplorerListing.Entry Entry(bool folder = false, FileAttributes attributes = FileAttributes.Normal) =>
        new(@"C:\fixture\sample.txt", "sample.txt", folder, 2048, new(2026, 1, 2), new(2025, 3, 4), Attributes: attributes);

    [Theory]
    [InlineData(false, false, 0, BrowseInfoTarget.None)]
    [InlineData(false, false, 2, BrowseInfoTarget.Checks)]
    [InlineData(false, true, 2, BrowseInfoTarget.OpenDocument)]
    [InlineData(true, false, 2, BrowseInfoTarget.Selection)]
    [InlineData(true, true, 2, BrowseInfoTarget.Selection)]
    public void Priority_does_not_promote_checks(bool selected, bool opened, int checks, BrowseInfoTarget expected)
    {
        var selection = selected ? BrowseSelection.From(Entry(true), 4) : BrowseSelection.None;
        Assert.Equal(expected, BrowseSelection.Resolve(selection, opened, new(checks, 0)));
        Assert.Equal(selected ? BrowseSelectionKind.Folder : BrowseSelectionKind.None, selection.Kind);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(0x1000)]
    [InlineData(0x40000)]
    [InlineData(0x400000)]
    [InlineData(0x10)]
    public void Unsafe_snapshot_does_not_even_query_attributes(int attributes)
    {
        var attrReads = 0; var richReads = 0;
        var rows = BrowseInfo.BuildVerified(Entry(attributes: (FileAttributes)attributes),
            _ => { attrReads++; return FileAttributes.Normal; }, _ => { richReads++; return []; }, BrowseInfo.BasicRows);
        Assert.Equal(0, attrReads); Assert.Equal(0, richReads);
        Assert.Contains(rows, r => r.Label == "Full path");
    }

    [Theory]
    [InlineData(0)]
    [InlineData(0x1000)]
    [InlineData(0x40000)]
    [InlineData(0x400000)]
    [InlineData(0x10)]
    public void Changed_live_attributes_block_rich_reader(int attributes)
    {
        var reads = 0;
        var rows = BrowseInfo.BuildVerified(Entry(), _ => (FileAttributes)attributes,
            _ => { reads++; return []; }, BrowseInfo.BasicRows);
        Assert.Equal(0, reads);
        Assert.Equal(attributes == 0 ? "Unknown" : ((FileAttributes)attributes).ToString(), rows.Single(r => r.Label == "Attributes").Value);
    }

    [Fact]
    public void Attribute_failure_never_falls_through_to_rich_builder()
    {
        var reads = 0;
        var rows = BrowseInfo.BuildVerified(Entry(), _ => throw new UnauthorizedAccessException(),
            _ => { reads++; return []; }, BrowseInfo.BasicRows);
        Assert.Equal(0, reads); Assert.Equal("Unknown", rows.Single(r => r.Label == "Availability").Value);
    }

    [Fact]
    public void Rich_failure_is_one_attempt_with_safe_fallback()
    {
        var reads = 0;
        var rows = BrowseInfo.BuildVerified(Entry(), _ => FileAttributes.Normal,
            _ => { reads++; throw new IOException(); }, BrowseInfo.BasicRows);
        Assert.Equal(1, reads); Assert.Equal(8, rows.Count);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(0x10)]
    [InlineData(0x410)]
    [InlineData(0x1010)]
    [InlineData(0x40010)]
    [InlineData(0x400010)]
    public void Folders_never_read_or_report_zero_bytes(int attributes)
    {
        var entry = Entry(true, (FileAttributes)attributes) with { Size = 0 };
        var rows = BrowseInfo.BuildVerified(entry, _ => throw new Exception("No attribute IO"),
            _ => throw new Exception("No child/content IO"), BrowseInfo.BasicRows);
        Assert.Equal("Not calculated automatically", rows.Single(r => r.Label == "Size").Value);
        Assert.DoesNotContain(rows, r => r.Label is "Files" or "Items");
    }

    [Fact]
    public void File_snapshot_has_bytes_dates_path_and_neutral_attributes()
    {
        var rows = BrowseInfo.BasicRows(Entry(attributes: FileAttributes.ReadOnly | FileAttributes.ReparsePoint));
        Assert.Contains("2,048", rows.Single(r => r.Label == "Size").Value);
        Assert.Contains("2 KB", rows.Single(r => r.Label == "Size").Value);
        Assert.Equal("2025-03-04 00:00:00", rows.Single(r => r.Label == "Created").Value);
        Assert.DoesNotContain(rows, r => r.Value.Contains("OneDrive") || r.Value.Contains("symlink") || r.Value.Contains("denied"));
    }

    [Fact]
    public void Missing_snapshot_metadata_is_not_reported_as_zero()
    {
        var rows = BrowseInfo.BasicRows(Entry() with { Created = default, Modified = default, Size = -1, Attributes = 0 });
        Assert.Equal(3, rows.Count(r => r.Value == "Unavailable"));
        Assert.Equal(2, rows.Count(r => r.Value == "Unknown"));
    }

    [Fact]
    public void Rich_merge_preserves_image_dimensions_and_every_module_row()
    {
        ContentInfoItem[] module = [new("Dimensions", "640×480 px"), ContentInfoItem.Separator,
            new("Title", ""), new("Size", "module size"), ContentInfoItem.Separator, new("Pages", "9")];
        var rich = new[] { new ContentInfoItem("File", "old"), new("Size", "old"), new("Modified", "old") }.Concat(module).ToArray();
        var rows = BrowseInfo.BuildVerified(Entry(), _ => FileAttributes.Normal, _ => rich, BrowseInfo.BasicRows);
        Assert.Equal(module, rows.Skip(8));
    }

    [Fact]
    public void Dimensions_without_exif_separator_are_preserved()
    {
        var rows = BrowseInfo.MergeRich(Entry(), [new("File", "old"), new("Dimensions", "12×8 px")]);
        Assert.Equal("Dimensions", rows[^1].Label);
    }

    [Fact]
    public void Snapshot_identity_rejects_stale_kind_attributes_time_and_generation()
    {
        var entry = Entry(); var key = BrowseInfoKey.From(entry, 1);
        Assert.NotEqual(key, BrowseInfoKey.From(entry with { IsFolder = true }, 1));
        Assert.NotEqual(key, BrowseInfoKey.From(entry with { Attributes = FileAttributes.Offline }, 1));
        Assert.NotEqual(key, BrowseInfoKey.From(entry with { Modified = entry.Modified.AddTicks(1) }, 1));
        Assert.NotEqual(key, BrowseInfoKey.From(entry, 2));
        Assert.NotEqual(key, BrowseInfoKey.From(entry with { IsPlaceholder = true }, 1));
        Assert.Equal(key, BrowseInfoKey.From(entry with { Path = entry.Path.ToLowerInvariant() }, 1));
    }

    [Fact]
    public void Checked_summary_keeps_hidden_checks_without_active_promotion()
    {
        using var session = new ExplorerSession();
        session.BeginNavigation(@"C:\fixture");
        var a = Entry(); var b = Entry() with { Name = "beta.txt", Path = @"C:\fixture\beta.txt" };
        session.AcceptScan([a, b]); session.ArrangeFromScan(); session.MarkDisplayed();
        session.SetItemChecked(session.DisplayFolder, a.Path, true); session.SetItemChecked(session.DisplayFolder, b.Path, true);
        session.SetNameFilter("beta");
        Assert.Equal(new BrowseCheckSummary(2, 1), session.CheckSummary);
        Assert.Equal(1, session.CheckSummary.Hidden);
        Assert.Single(session.CheckedPathsForFolder(session.DisplayFolder));
        Assert.Equal(BrowseInfoTarget.Checks, BrowseSelection.Resolve(BrowseSelection.None, false, session.CheckSummary));
        session.SetNameFilter(""); Assert.Equal(new BrowseCheckSummary(2, 2), session.CheckSummary);
        session.AcceptScan([b]); session.ArrangeFromScan(); Assert.Equal(new BrowseCheckSummary(1, 1), session.CheckSummary);
    }

    [Fact]
    public void Metadata_generation_changes_on_scan_not_sort_or_filter()
    {
        using var session = new ExplorerSession();
        session.BeginNavigation(@"C:\fixture"); session.AcceptScan([Entry()]); session.ArrangeFromScan(); session.MarkDisplayed();
        var generation = session.MetadataGeneration;
        session.SelectSort(ExplorerListing.SortKey.Size); session.SetNameFilter("sample");
        Assert.Equal(generation, session.MetadataGeneration);
        session.AcceptScan([Entry()]); Assert.True(session.MetadataGeneration > generation);
        generation = session.MetadataGeneration; session.ClearFailedScan(); Assert.True(session.MetadataGeneration > generation);
    }
    [Fact]
    public void Cancellation_during_attributes_prevents_rich_read()
    {
        using var cancellation = new CancellationTokenSource(); var reads = 0;
        Assert.Throws<OperationCanceledException>(() => BrowseInfo.BuildVerified(Entry(),
            _ => { cancellation.Cancel(); return FileAttributes.Normal; },
            _ => { reads++; return []; }, BrowseInfo.BasicRows, cancellation.Token));
        Assert.Equal(0, reads);
    }

    [Fact]
    public void Already_canceled_request_never_reads_attributes()
    {
        using var cancellation = new CancellationTokenSource(); cancellation.Cancel(); var reads = 0;
        Assert.Throws<OperationCanceledException>(() => BrowseInfo.BuildVerified(Entry(),
            _ => { reads++; return FileAttributes.Normal; }, _ => [], BrowseInfo.BasicRows, cancellation.Token));
        Assert.Equal(0, reads);
    }

    [Fact]
    public void New_snapshot_after_failed_attribute_read_recovers_rich_info()
    {
        var entry = Entry(); var old = BrowseInfoKey.From(entry, 1); var next = BrowseInfoKey.From(entry, 2);
        var cached = new Dictionary<BrowseInfoKey, IReadOnlyList<ContentInfoItem>>
        { [old] = BrowseInfo.BuildVerified(entry, _ => throw new IOException(), _ => throw new Exception(), BrowseInfo.BasicRows) };
        Assert.False(cached.ContainsKey(next));
        var rows = BrowseInfo.BuildVerified(entry, _ => FileAttributes.Normal,
            _ => [ContentInfoItem.Separator, new("Pages", "5")], BrowseInfo.BasicRows);
        Assert.Contains(rows, row => row.Label == "Pages" && row.Value == "5");
    }
}
