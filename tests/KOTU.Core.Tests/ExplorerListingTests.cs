using KOTU.Core.Routing;
using Xunit;

namespace KOTU.Core.Tests;

public class ExplorerListingTests : IDisposable
{
    private readonly string _dir =
        Path.Combine(Path.GetTempPath(), "kotu-explorer-test-" + Guid.NewGuid().ToString("N"));

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

    // ---------- 숨김·시스템 표시 (A160) ----------
    // 숨김/시스템 속성은 윈도우 전용이고 CI(build.yml)가 windows-latest에서 dotnet test를 돌린다.

    [Fact]
    public void List_숨김과_시스템은_기본으로_빼고_includeHidden이면_함께_보인다()
    {
        Make("plain.jpg");
        var hiddenFile = Make("secret.jpg");
        File.SetAttributes(hiddenFile, File.GetAttributes(hiddenFile) | FileAttributes.Hidden);
        var systemFolder = Make("sysdir", folder: true);
        File.SetAttributes(systemFolder, File.GetAttributes(systemFolder) | FileAttributes.System);

        // 기본(false) = 종전 동작 — 숨김 파일도 시스템 폴더도 없다.
        Assert.Equal(
            ["plain.jpg"],
            ExplorerListing.List(_dir, [".jpg"]).Select(e => e.Name).ToArray());

        // 켜면 둘 다 보인다(한 옵션으로 묶는다는 결정). 폴더 먼저·각각 이름순 규칙은 그대로.
        Assert.Equal(
            ["sysdir", "plain.jpg", "secret.jpg"],
            ExplorerListing.List(_dir, [".jpg"], includeHidden: true).Select(e => e.Name).ToArray());
    }

    [Fact]
    public void ShouldShow_숨김이든_시스템이든_한_옵션이_결정한다()
    {
        Assert.True(ExplorerListing.ShouldShow(FileAttributes.Normal, includeHidden: false));
        Assert.False(ExplorerListing.ShouldShow(FileAttributes.Hidden, includeHidden: false));
        Assert.False(ExplorerListing.ShouldShow(FileAttributes.System, includeHidden: false));
        Assert.True(ExplorerListing.ShouldShow(FileAttributes.Hidden, includeHidden: true));
        Assert.True(ExplorerListing.ShouldShow(FileAttributes.System, includeHidden: true));
    }

    // ---------- Arrange (A5·A7) ----------

    // day = 수정일(1월 n일), createdDay = 만든 날짜(A117 — 생략하면 수정일과 같은 날).
    // 두 날짜를 어긋나게 줘야 "만든 날짜 정렬이 수정일 정렬과 다른 순서를 낸다"를 증명할 수 있다.
    private static ExplorerListing.Entry AFile(string name, long size, int day, int? createdDay = null) =>
        new($@"C:\t\{name}", name, false, size,
            new DateTime(2026, 1, day), new DateTime(2026, 1, createdDay ?? day));

    private static ExplorerListing.Entry AFolder(string name, int day, int? createdDay = null) =>
        new($@"C:\t\{name}", name, true, 0,
            new DateTime(2026, 1, day), new DateTime(2026, 1, createdDay ?? day));

    [Fact]
    public void Arrange_이름순_폴더_먼저_이름_오름차순()
    {
        var entries = new[] { AFile("b.jpg", 1, 1), AFolder("z", 1), AFile("A.jpg", 2, 2), AFolder("a", 2) };

        var result = ExplorerListing.Arrange(entries, ExplorerListing.SortKey.Name);

        Assert.Equal(["a", "z", "A.jpg", "b.jpg"], result.Select(e => e.Name).ToArray());
    }

    [Fact]
    public void Arrange_크기순_파일은_큰_것부터_폴더는_이름순()
    {
        var entries = new[] { AFolder("z", 1), AFolder("a", 2), AFile("small.jpg", 10, 1), AFile("big.jpg", 999, 1) };

        var result = ExplorerListing.Arrange(entries, ExplorerListing.SortKey.Size);

        Assert.Equal(["a", "z", "big.jpg", "small.jpg"], result.Select(e => e.Name).ToArray());
    }

    [Fact]
    public void Arrange_수정일순_최신부터_폴더도_수정일순()
    {
        var entries = new[] { AFolder("old", 1), AFolder("new", 9), AFile("old.jpg", 1, 2), AFile("new.jpg", 1, 8) };

        var result = ExplorerListing.Arrange(entries, ExplorerListing.SortKey.Modified);

        Assert.Equal(["new", "old", "new.jpg", "old.jpg"], result.Select(e => e.Name).ToArray());
    }

    [Fact]
    public void Arrange_만든날짜순_최신부터_폴더도_만든날짜순()
    {
        // 수정일(day)은 일부러 뒤집어 둔다 — 만든 날짜(createdDay)만 보고 정렬해야 통과한다(A117).
        var entries = new[]
        {
            AFolder("old", day: 9, createdDay: 1),
            AFolder("new", day: 1, createdDay: 9),
            AFile("old.jpg", 1, day: 8, createdDay: 2),
            AFile("new.jpg", 1, day: 2, createdDay: 8),
        };

        var result = ExplorerListing.Arrange(entries, ExplorerListing.SortKey.Created);

        Assert.Equal(["new", "old", "new.jpg", "old.jpg"], result.Select(e => e.Name).ToArray());
    }

    [Fact]
    public void Arrange_숨김_확장자_파일만_거르고_폴더는_남긴다()
    {
        var entries = new[] { AFolder("sub", 1), AFile("a.jpg", 1, 1), AFile("b.png", 1, 1), AFile("c.JPG", 1, 1) };

        var result = ExplorerListing.Arrange(entries, ExplorerListing.SortKey.Name, [".jpg"]);

        Assert.Equal(["sub", "b.png"], result.Select(e => e.Name).ToArray());
    }

    [Fact]
    public void FormatSize_단위를_고른다()
    {
        Assert.Equal("512 B", ExplorerListing.FormatSize(512));
        Assert.Equal("1 KB", ExplorerListing.FormatSize(1024));
        Assert.Equal("1.5 MB", ExplorerListing.FormatSize(1024 * 1024 + 512 * 1024));
    }

    [Fact]
    public void FormatDuration_시간_유무에_따라_포맷을_고른다()
    {
        Assert.Equal(string.Empty, ExplorerListing.FormatDuration(TimeSpan.Zero));
        Assert.Equal("0:03", ExplorerListing.FormatDuration(TimeSpan.FromSeconds(3)));
        Assert.Equal("4:07", ExplorerListing.FormatDuration(new TimeSpan(0, 4, 7)));
        Assert.Equal("1:02:03", ExplorerListing.FormatDuration(new TimeSpan(1, 2, 3)));
        Assert.Equal("27:00:00", ExplorerListing.FormatDuration(TimeSpan.FromHours(27)));
    }
}
