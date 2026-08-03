using Xunit;

namespace WinUtil.Module.Archive.Tests;

public class ArchiveEntryTreeTests
{
    private static ArchiveEntry File(string path, long size = 0, DateTime modified = default) =>
        new(path, IsDirectory: false, size, modified);

    private static ArchiveEntry Dir(string path, DateTime modified = default) =>
        new(path, IsDirectory: true, 0, modified);

    private static ArchiveEntryNode Child(ArchiveEntryNode parent, string name) =>
        parent.Children.Single(c => c.Name == name);

    // ---------- 트리 변환 ----------

    [Fact]
    public void 중첩_경로를_폴더_계층으로_만든다()
    {
        var root = ArchiveEntryTree.Build([
            File("a/b/c.txt", 10),
            File("a/d.txt", 20),
            File("e.txt", 30),
        ]);

        Assert.Equal(2, root.Children.Count); // a, e.txt

        var a = Child(root, "a");
        Assert.True(a.IsDirectory);
        Assert.Equal("a", a.FullPath);
        Assert.Equal(2, a.Children.Count); // b, d.txt

        var b = Child(a, "b");
        Assert.True(b.IsDirectory);
        Assert.Equal("a/b", b.FullPath);

        var c = Child(b, "c.txt");
        Assert.False(c.IsDirectory);
        Assert.Equal("a/b/c.txt", c.FullPath);
    }

    [Fact]
    public void 역슬래시_구분자와_앞뒤_구분자를_정규화한다()
    {
        var root = ArchiveEntryTree.Build([File(@"a\b\c.txt", 1)]);

        var a = Child(root, "a");
        var b = Child(a, "b");
        Assert.Equal("a/b", b.FullPath);
        Assert.Single(b.Children);
        Assert.Equal("a/b/c.txt", b.Children[0].FullPath);
    }

    [Fact]
    public void 명시적_폴더_항목이_있어도_폴더가_중복되지_않는다()
    {
        var root = ArchiveEntryTree.Build([
            Dir("a", new DateTime(2026, 1, 2, 3, 4, 5)),
            File("a/x.txt", 5),
        ]);

        Assert.Single(root.Children);
        var a = root.Children[0];
        Assert.True(a.IsDirectory);
        Assert.Equal(new DateTime(2026, 1, 2, 3, 4, 5), a.Modified); // 명시적 항목의 수정일 반영
        Assert.Single(a.Children);
    }

    // ---------- 정렬 ----------

    [Fact]
    public void 폴더가_파일보다_앞에_오고_이름순으로_정렬된다()
    {
        var root = ArchiveEntryTree.Build([
            File("z.txt"),
            File("b/1.txt"),
            File("a.txt"),
            Dir("y"),
        ]);

        var names = root.Children.Select(c => c.Name).ToArray();
        Assert.Equal(new[] { "b", "y", "a.txt", "z.txt" }, names); // 폴더(b, y) 우선, 각각 이름순
    }

    [Fact]
    public void 이름_정렬은_대소문자를_무시한다()
    {
        var root = ArchiveEntryTree.Build([
            File("Banana.txt"),
            File("apple.txt"),
            File("cherry.txt"),
        ]);

        Assert.Equal(new[] { "apple.txt", "Banana.txt", "cherry.txt" },
            root.Children.Select(c => c.Name).ToArray());
    }

    // ---------- 누적 크기 ----------

    [Fact]
    public void 폴더_크기는_하위_파일의_누적_합이다()
    {
        var root = ArchiveEntryTree.Build([
            File("a/b/c.txt", 100),
            File("a/b/d.txt", 50),
            File("a/e.txt", 25),
            File("f.txt", 7),
        ]);

        var a = Child(root, "a");
        var b = Child(a, "b");
        Assert.Equal(150, b.Size);       // c + d
        Assert.Equal(175, a.Size);       // b + e
        Assert.Equal(182, root.Size);    // a + f
    }

    [Fact]
    public void 빈_목록이면_빈_루트를_반환한다()
    {
        var root = ArchiveEntryTree.Build([]);

        Assert.True(root.IsDirectory);
        Assert.Empty(root.Children);
        Assert.Equal(0, root.Size);
    }

    // ---------- 크기 문자열 ----------

    [Theory]
    [InlineData(0, "0 B")]
    [InlineData(512, "512 B")]
    [InlineData(1023, "1023 B")]
    [InlineData(1024, "1 KB")]
    [InlineData(1536, "1.5 KB")]
    [InlineData(1048576, "1 MB")]
    [InlineData(1572864, "1.5 MB")]
    [InlineData(1073741824, "1 GB")]
    [InlineData(2199023255552, "2048 GB")] // GB 초과도 GB로 표기
    public void 크기_문자열_포맷(long bytes, string expected)
    {
        Assert.Equal(expected, ArchiveEntryTree.FormatSize(bytes));
    }

    [Fact]
    public void 음수_크기는_0으로_처리한다()
    {
        Assert.Equal("0 B", ArchiveEntryTree.FormatSize(-5));
    }

    // ---------- 경로 정규화 ----------

    [Theory]
    [InlineData(@"a\b\c", "a/b/c")]
    [InlineData("/a/b/", "a/b")]
    [InlineData("a", "a")]
    public void 경로_정규화(string input, string expected)
    {
        Assert.Equal(expected, ArchiveEntryTree.NormalizePath(input));
    }
}

public class MojibakeDetectorTests
{
    [Theory]
    [InlineData("한글 문서.txt")]
    [InlineData("report_2026.pdf")]
    [InlineData("Café.txt")]        // 라틴 확장 1개는 정상으로 본다
    [InlineData("日本語ファイル.txt")]
    public void 정상_파일명은_깨짐이_아니다(string name)
    {
        Assert.False(MojibakeDetector.LooksBroken(name));
    }

    [Fact]
    public void 대체_문자가_있으면_깨짐이다()
    {
        Assert.True(MojibakeDetector.LooksBroken("��.txt"));
        Assert.True(MojibakeDetector.LooksBroken("문서�.txt")); // 1개만 있어도 깨짐
    }

    [Fact]
    public void C1_제어문자가_있으면_깨짐이다()
    {
        Assert.True(MojibakeDetector.LooksBroken("abc\u0081def.txt"));
    }

    [Fact]
    public void 라틴_확장이_연달아_나오면_깨짐이다()
    {
        // "한글"(CP949: C7 D1 B1 DB)을 Latin-1로 잘못 읽은 형태
        Assert.True(MojibakeDetector.LooksBroken("ÇÑ±Û.txt"));
    }

    [Fact]
    public void 박스_문자가_섞이면_깨짐이다()
    {
        // CP437 오해석의 전형(음영 문자 포함)
        Assert.True(MojibakeDetector.LooksBroken("▒▒░.txt"));
    }
}
