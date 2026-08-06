using WinUtil.Core.Routing;
using Xunit;

namespace WinUtil.Core.Tests;

public class ExplorerListingTests : IDisposable
{
    private readonly string _dir =
        Path.Combine(Path.GetTempPath(), "zp-explorer-test-" + Guid.NewGuid().ToString("N"));

    public ExplorerListingTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); }
        catch { /* 청소 실패는 테스트 결과와 무관 */ }
    }

    private string Make(string name, bool folder = false)
    {
        var path = Path.Combine(_dir, name);
        if (folder) Directory.CreateDirectory(path);
        else File.WriteAllText(path, "x");
        return path;
    }

    [Fact]
    public void MatchesExtension_대소문자_무시하고_점포함_확장자와_비교한다()
    {
        string[] exts = [".jpg", ".png"];
        Assert.True(ExplorerListing.MatchesExtension("cat.JPG", exts));
        Assert.True(ExplorerListing.MatchesExtension(@"C:\a\b.png", exts));
        Assert.False(ExplorerListing.MatchesExtension("clip.mp4", exts));
        Assert.False(ExplorerListing.MatchesExtension("README", exts));
    }

    [Fact]
    public void List_폴더는_전부_파일은_담당_확장자만()
    {
        Make("sub", folder: true);
        Make("cat.jpg");
        Make("dog.PNG");
        Make("movie.mp4"); // 필터에 걸러져야 함

        var entries = ExplorerListing.List(_dir, [".jpg", ".png"]);

        Assert.Equal(3, entries.Count);
        Assert.True(entries[0].IsFolder);
        Assert.Equal("sub", entries[0].Name);
        Assert.DoesNotContain(entries, e => e.Name == "movie.mp4");
    }

    [Fact]
    public void List_폴더가_먼저_각각_이름순()
    {
        Make("zzz", folder: true);
        Make("aaa", folder: true);
        Make("b.jpg");
        Make("A.jpg");

        var entries = ExplorerListing.List(_dir, [".jpg"]);

        Assert.Equal(["aaa", "zzz", "A.jpg", "b.jpg"], entries.Select(e => e.Name).ToArray());
    }

    [Fact]
    public void List_maxItems_상한을_지킨다()
    {
        for (var i = 0; i < 5; i++) Make($"f{i}.jpg");

        Assert.Equal(3, ExplorerListing.List(_dir, [".jpg"], maxItems: 3).Count);
    }

    [Fact]
    public void FormatSize_단위를_고른다()
    {
        Assert.Equal("512 B", ExplorerListing.FormatSize(512));
        Assert.Equal("1 KB", ExplorerListing.FormatSize(1024));
        Assert.Equal("1.5 MB", ExplorerListing.FormatSize(1024 * 1024 + 512 * 1024));
    }
}
