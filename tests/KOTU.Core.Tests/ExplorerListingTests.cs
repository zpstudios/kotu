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

    // ---------- IsCloudPlaceholder (A175) ----------

    [Fact]
    public void IsCloudPlaceholder_클라우드_속성_3축만_참()
    {
        // 3축: Offline(0x1000) · RecallOnDataAccess(0x400000) · RecallOnOpen(0x40000).
        // 뒤 둘은 .NET 8 BCL에 이름이 없어 캐스트 값으로 검증한다(판정식과 독립된 원시값 대조).
        Assert.True(ExplorerListing.IsCloudPlaceholder(FileAttributes.Offline));
        Assert.True(ExplorerListing.IsCloudPlaceholder((FileAttributes)0x400000));
        Assert.True(ExplorerListing.IsCloudPlaceholder((FileAttributes)0x40000));
        Assert.True(ExplorerListing.IsCloudPlaceholder(
            FileAttributes.Archive | FileAttributes.ReparsePoint | (FileAttributes)0x400000));

        Assert.False(ExplorerListing.IsCloudPlaceholder(FileAttributes.Normal));
        // OneDrive 로컬 파일의 흔한 조합 — ReparsePoint만으로는 placeholder가 아니다.
        Assert.False(ExplorerListing.IsCloudPlaceholder(
            FileAttributes.Archive | FileAttributes.ReparsePoint));
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
    public void Arrange_크기순_내림이면_파일은_큰_것부터_폴더는_입력_순서()
    {
        // descending: true = 종전 Size 고정 방향(큰 것부터) — UI의 DefaultDescending(Size)이 넘기는 값.
        // A204: 폴더(크기 개념 없음)는 무정렬 = 입력 순서 유지(종전 "항상 이름 오름" 강제 폐지).
        var entries = new[] { AFolder("z", 1), AFolder("a", 2), AFile("small.jpg", 10, 1), AFile("big.jpg", 999, 1) };

        var result = ExplorerListing.Arrange(entries, ExplorerListing.SortKey.Size, descending: true);

        Assert.Equal(["z", "a", "big.jpg", "small.jpg"], result.Select(e => e.Name).ToArray());
    }

    [Fact]
    public void Arrange_크기순_오름이면_작은_것부터_폴더는_입력_순서()
    {
        // A155: 방향 인자화 — 1차 키만 뒤집힌다. A204: 폴더는 방향과 무관하게 입력 순서 유지.
        var entries = new[] { AFolder("z", 1), AFolder("a", 2), AFile("small.jpg", 10, 1), AFile("big.jpg", 999, 1) };

        var result = ExplorerListing.Arrange(entries, ExplorerListing.SortKey.Size);

        Assert.Equal(["z", "a", "small.jpg", "big.jpg"], result.Select(e => e.Name).ToArray());
    }

    [Fact]
    public void Arrange_수정일순_내림이면_최신부터_폴더도_수정일순()
    {
        var entries = new[] { AFolder("old", 1), AFolder("new", 9), AFile("old.jpg", 1, 2), AFile("new.jpg", 1, 8) };

        var result = ExplorerListing.Arrange(entries, ExplorerListing.SortKey.Modified, descending: true);

        Assert.Equal(["new", "old", "new.jpg", "old.jpg"], result.Select(e => e.Name).ToArray());
    }

    [Fact]
    public void Arrange_이름순_내림이면_폴더도_파일도_역순()
    {
        // A155: Name은 폴더에도 파일에도 같은 키라 방향이 둘 다에 적용된다(폴더 먼저 규칙은 유지).
        var entries = new[] { AFile("b.jpg", 1, 1), AFolder("z", 1), AFile("A.jpg", 2, 2), AFolder("a", 2) };

        var result = ExplorerListing.Arrange(entries, ExplorerListing.SortKey.Name, descending: true);

        Assert.Equal(["z", "a", "b.jpg", "A.jpg"], result.Select(e => e.Name).ToArray());
    }

    [Fact]
    public void Arrange_타입순_확장자로_묶고_같은_확장자는_입력_순서_폴더도_입력_순서()
    {
        // A155: Type = Name에서 파생한 확장자 키(대소문자 무시).
        // A204: 고정 이름 2차 키 폐지 — 같은 확장자 안은 입력 순서 보존(stable sort), 폴더는 무정렬.
        var entries = new[]
        {
            AFolder("sub", 1),
            AFile("b.png", 1, 1),
            AFile("a.PNG", 1, 1),
            AFile("z.jpg", 1, 1),
        };

        var asc = ExplorerListing.Arrange(entries, ExplorerListing.SortKey.Type);
        Assert.Equal(["sub", "z.jpg", "b.png", "a.PNG"], asc.Select(e => e.Name).ToArray());

        // 내림 = 확장자(1차 키)만 역순 — 같은 확장자 안 입력 순서와 폴더 입력 순서는 그대로다.
        var desc = ExplorerListing.Arrange(entries, ExplorerListing.SortKey.Type, descending: true);
        Assert.Equal(["sub", "b.png", "a.PNG", "z.jpg"], desc.Select(e => e.Name).ToArray());
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

        var result = ExplorerListing.Arrange(entries, ExplorerListing.SortKey.Created, descending: true);

        Assert.Equal(["new", "old", "new.jpg", "old.jpg"], result.Select(e => e.Name).ToArray());
    }

    [Fact]
    public void Arrange_숨김_확장자_파일만_거르고_폴더는_남긴다()
    {
        var entries = new[] { AFolder("sub", 1), AFile("a.jpg", 1, 1), AFile("b.png", 1, 1), AFile("c.JPG", 1, 1) };

        var result = ExplorerListing.Arrange(entries, ExplorerListing.SortKey.Name, hiddenExtensions: [".jpg"]);

        Assert.Equal(["sub", "b.png"], result.Select(e => e.Name).ToArray());
    }

    // ---------- 정렬 안정성 (A204) ----------
    // Arrange는 stable sort(동률 = 입력 순서 보존)라 직전 Arrange 결과를 입력으로 주면
    // 직전 기준이 동률의 2차 순서로 승계된다 — UI(ExplorerPane.RefreshView)의 입력 선택 계약.

    [Fact]
    public void Arrange_직전_정렬_결과를_입력으로_주면_동률은_그_순서를_유지한다()
    {
        // 사용자 예시 그대로: 이름 오름 정렬 → 크기 정렬 시 같은 크기끼리 이름 오름 순서 유지.
        // 폴더도 함께 검증 — Size 정렬의 폴더는 무정렬이라 직전(이름 오름) 순서가 살아남는다.
        var entries = new[]
        {
            AFile("c.jpg", 10, 1), AFile("a.jpg", 10, 1), AFile("b.jpg", 5, 1),
            AFolder("z", 1), AFolder("m", 1),
        };

        var byNameAsc = ExplorerListing.Arrange(entries, ExplorerListing.SortKey.Name);
        Assert.Equal(["m", "z", "a.jpg", "b.jpg", "c.jpg"], byNameAsc.Select(e => e.Name).ToArray());

        var bySize = ExplorerListing.Arrange(byNameAsc, ExplorerListing.SortKey.Size);

        // b(5) 먼저, 같은 크기(10)끼리는 직전 순서(이름 오름) a → c. 폴더도 직전 순서 m → z 그대로.
        Assert.Equal(["m", "z", "b.jpg", "a.jpg", "c.jpg"], bySize.Select(e => e.Name).ToArray());
    }

    [Fact]
    public void Arrange_같은_키_방향_토글도_동률_내_직전_순서를_유지한다()
    {
        var entries = new[] { AFile("c.jpg", 10, 1), AFile("a.jpg", 10, 1), AFile("b.jpg", 5, 1) };

        // 이름 오름 → 크기 오름: b(5), a(10), c(10) — 동률은 이름 오름 승계.
        var asc = ExplorerListing.Arrange(
            ExplorerListing.Arrange(entries, ExplorerListing.SortKey.Name),
            ExplorerListing.SortKey.Size);
        Assert.Equal(["b.jpg", "a.jpg", "c.jpg"], asc.Select(e => e.Name).ToArray());

        // 재클릭(내림)은 1차 키(크기)만 뒤집는다 — 동률(10)끼리는 입력(오름 결과) 순서 a → c 그대로.
        var desc = ExplorerListing.Arrange(asc, ExplorerListing.SortKey.Size, descending: true);
        Assert.Equal(["a.jpg", "c.jpg", "b.jpg"], desc.Select(e => e.Name).ToArray());
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
