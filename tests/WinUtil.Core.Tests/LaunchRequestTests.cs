using WinUtil.Core.Cli;
using Xunit;

namespace WinUtil.Core.Tests;

public class LaunchRequestTests
{
    // ---------- Tokenize ----------

    [Fact]
    public void 따옴표_안_공백은_한_토큰으로_유지된다()
    {
        var tokens = LaunchRequest.Tokenize("--extract-here \"C:\\내 문서\\a b.zip\"");

        Assert.Equal(new[] { "--extract-here", @"C:\내 문서\a b.zip" }, tokens);
    }

    [Fact]
    public void 연속_공백과_빈_입력을_견딘다()
    {
        Assert.Equal(new[] { "a", "b" }, LaunchRequest.Tokenize("  a   b  "));
        Assert.Empty(LaunchRequest.Tokenize(""));
        Assert.Empty(LaunchRequest.Tokenize("   "));
    }

    // ---------- Parse ----------

    [Fact]
    public void 파일만_주면_Open이다()
    {
        var r = LaunchRequest.Parse([@"C:\v\movie.mp4"]);

        Assert.Equal(LaunchVerb.Open, r.Verb);
        Assert.Equal(@"C:\v\movie.mp4", r.FilePath);
        Assert.Null(r.VerbToken);
    }

    [Fact]
    public void 여기에_풀기_동사와_파일을_해석한다()
    {
        var r = LaunchRequest.Parse(["--extract-here", @"C:\d\a.zip"]);

        Assert.Equal(LaunchVerb.ExtractHere, r.Verb);
        Assert.Equal(@"C:\d\a.zip", r.FilePath);
        Assert.Equal("--extract-here", r.VerbToken);
    }

    [Fact]
    public void 압축_동사는_파일이_동사보다_앞에_와도_된다()
    {
        var r = LaunchRequest.Parse([@"C:\d\folder", "--compress"]);

        Assert.Equal(LaunchVerb.Compress, r.Verb);
        Assert.Equal(@"C:\d\folder", r.FilePath);
    }

    [Fact]
    public void 모르는_옵션은_무시한다()
    {
        var r = LaunchRequest.Parse(["--unknown", @"C:\a.jpg"]);

        Assert.Equal(LaunchVerb.Open, r.Verb);
        Assert.Equal(@"C:\a.jpg", r.FilePath);
    }

    [Fact]
    public void 인자가_없으면_Open에_파일_없음()
    {
        var r = LaunchRequest.Parse([]);

        Assert.Equal(LaunchVerb.Open, r.Verb);
        Assert.Null(r.FilePath);
    }

    // ---------- ParseCommandLine (재전달 경로) ----------

    [Fact]
    public void 재전달_커맨드라인의_선행_exe는_건너뛴다()
    {
        var r = LaunchRequest.ParseCommandLine(
            "\"C:\\Apps\\WinUtil\\WinUtil.App.exe\" --extract-here \"C:\\d\\a b.zip\"");

        Assert.Equal(LaunchVerb.ExtractHere, r.Verb);
        Assert.Equal(@"C:\d\a b.zip", r.FilePath);
    }

    [Fact]
    public void exe_없이_인자만_와도_해석된다()
    {
        var r = LaunchRequest.ParseCommandLine("\"C:\\사진\\여름 휴가.jpg\"");

        Assert.Equal(LaunchVerb.Open, r.Verb);
        Assert.Equal(@"C:\사진\여름 휴가.jpg", r.FilePath);
    }
}
