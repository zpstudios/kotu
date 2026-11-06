using KOTU.Core.Routing;
using Xunit;

namespace KOTU.Core.Tests;

public sealed class ExplorerMenuTargetTests
{
    private const string Root = @"C:\fixture";
    private static ExplorerListing.Entry E(string name, bool folder = true) =>
        new(Root + "\\" + name, name, folder, 0, new(2026, 1, 2), new(2025, 1, 1),
            Attributes: folder ? FileAttributes.Directory : FileAttributes.Normal);
    private static readonly ExplorerListing.Entry A = E("a"), B = E("b"), C = E("c.txt", false);
    private static readonly ExplorerListing.Entry[] View = [A, B, C];

    [Fact]
    public void Checked_in_view_wins_and_hidden_checks_never_enter_working_set()
    {
        var target = ExplorerMenuTarget.Capture(Root, 1, A.Path, View, [A.Path, C.Path, E("hidden").Path], [A.Path, B.Path])!;
        Assert.Equal(ExplorerMenuScope.Checked, target.Scope);
        Assert.Equal(new[] { A, C }, target.Working);
        Assert.Equal("checked 2", target.ScopeLabel);
    }

    [Fact]
    public void Hidden_only_checks_fall_back_to_visible_selection()
    {
        var target = ExplorerMenuTarget.Capture(Root, 1, B.Path, View, [E("hidden").Path], [B.Path, C.Path, E("gone").Path])!;
        Assert.Equal(new[] { B, C }, target.Working);
        Assert.Equal("selected 2", target.ScopeLabel);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Click_outside_working_set_is_single_even_when_selected(bool checks)
    {
        var target = ExplorerMenuTarget.Capture(Root, 1, B.Path, View, checks ? [A.Path] : [], checks ? [B.Path] : [A.Path])!;
        Assert.Equal(new[] { B }, target.Working);
        Assert.Equal("this folder", target.ScopeLabel);
    }

    [Fact]
    public void Parent_missing_and_no_folder_never_capture()
    {
        Assert.Null(ExplorerMenuTarget.Capture(Root, 1, Root, View, [Root], [Root]));
        Assert.Null(ExplorerMenuTarget.Capture(Root, 1, E("missing").Path, View, [], []));
        Assert.Null(ExplorerMenuTarget.Capture(null, 1, A.Path, View, [], []));
    }

    [Fact]
    public void Capture_is_independent_of_mutable_input_lists()
    {
        var view = View.ToList();
        var checks = new List<string> { A.Path, B.Path };
        var target = ExplorerMenuTarget.Capture(Root, 2, A.Path.ToUpperInvariant(), view, checks, [])!;
        view.Clear(); checks.Clear();
        Assert.Equal(new[] { A, B }, target.Working);
        Assert.Equal("checked 2", target.ScopeLabel);
        Assert.Throws<NotSupportedException>(() => ((IList<ExplorerListing.Entry>)target.Working).Clear());
    }

    [Theory]
    [InlineData("navigate")]
    [InlineData("refresh")]
    [InlineData("removed")]
    [InlineData("kind")]
    [InlineData("metadata")]
    [InlineData("checks")]
    [InlineData("scope")]
    public void Pending_group_intent_rejects_every_changed_context(string change)
    {
        var target = ExplorerMenuTarget.Capture(Root, 4, A.Path, View, [A.Path, B.Path], [A.Path, B.Path])!;
        var view = change switch
        {
            "removed" => [B, C],
            "kind" => new[] { A with { IsFolder = false }, B, C },
            "metadata" => new[] { A with { Modified = A.Modified.AddSeconds(1) }, B, C },
            _ => View,
        };
        Assert.False(target.IsCurrent(change == "navigate" ? @"C:\other" : Root,
            change == "refresh" ? 5 : 4, view,
            change == "checks" ? [A.Path] : change == "scope" ? [] : [A.Path, B.Path], [A.Path, B.Path], true));
    }

    [Fact]
    public void Single_actions_keep_clicked_target_and_ignore_independent_checks()
    {
        var target = ExplorerMenuTarget.Capture(Root, 4, B.Path, View, [A.Path], [C.Path])!;
        Assert.True(target.IsCurrent(Root, 4, View, [B.Path, C.Path], [A.Path], false));
        Assert.Equal(B, target.Clicked);
        Assert.False(target.IsCurrent(Root, 5, View, [B.Path], [], false));
    }

    [Theory]
    [InlineData(@"C:\space 한글\folder & comma, x")]
    [InlineData(@"\\server\share\폴더")]
    [InlineData(@"C:\-argument")]
    [InlineData(@"\\?\C:\long 한글\folder")]
    [InlineData(@"\\?\UNC\server\share\folder")]
    public void Ordinary_absolute_paths_pass_unchanged(string path)
    {
        Assert.True(ExplorerMenuValidation.IsSafeAbsolutePath(path));
        string? observed = null;
        Assert.Null(ExplorerMenuValidation.Validate([A with { Path = path }], p =>
        { observed = p; return new(FileAttributes.Directory, A.Created); }));
        Assert.Equal(path, observed);
    }

    [Theory]
    [InlineData("")]
    [InlineData("relative")]
    [InlineData("C:relative")]
    [InlineData("file:///C:/folder")]
    [InlineData(@"C:\a"" /select,C:\other")]
    [InlineData(@"C:\folder\..\other")]
    [InlineData(@"\\.\device")]
    [InlineData(@"\\?\GLOBALROOT\Device\folder")]
    [InlineData(@"\\?\UNC\server")]
    [InlineData(@"C:\folder:stream")]
    [InlineData("C:\\a\nnext")]
    [InlineData(@"\\server")]
    [InlineData("C:/folder")]
    public void Malformed_paths_do_not_reach_io(string path)
    {
        var calls = 0;
        Assert.NotNull(ExplorerMenuValidation.Validate([A with { Path = path }], _ =>
        { calls++; return new(FileAttributes.Directory, A.Created); }));
        Assert.Equal(0, calls);
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("denied")]
    [InlineData("unknown")]
    [InlineData("file")]
    [InlineData("replacement")]
    public void Failure_or_replaced_folder_is_refused(string mode)
    {
        Assert.NotNull(ExplorerMenuValidation.Validate([A], _ => mode switch
        {
            "missing" => throw new FileNotFoundException(),
            "denied" => throw new UnauthorizedAccessException(),
            "unknown" => new(0, A.Created),
            "file" => new(FileAttributes.Normal, A.Created),
            _ => new(FileAttributes.Directory, A.Created.AddSeconds(1)),
        }));
    }

    [Fact]
    public void Group_validation_fails_closed_without_skipping_missing_member()
    {
        var calls = new List<string>();
        Assert.NotNull(ExplorerMenuValidation.Validate([A, B, C], p =>
        {
            calls.Add(p);
            if (p == B.Path) throw new IOException();
            return new(FileAttributes.Directory, A.Created);
        }));
        Assert.Equal(new[] { A.Path, B.Path }, calls);
    }
}
