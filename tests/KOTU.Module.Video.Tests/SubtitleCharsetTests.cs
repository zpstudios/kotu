using System.Text;
using Xunit;

namespace KOTU.Module.Video.Tests;

public class SubtitleCharsetTests
{
    private const string Korean = "1\n00:00:01,000 --> 00:00:02,000\n안녕하세요 자막입니다\n";

    private static byte[] Cp949(string text) =>
        (CodePagesEncodingProvider.Instance.GetEncoding(949)
            ?? throw new InvalidOperationException("CP949 없음")).GetBytes(text);

    // ---------- 판별 ----------

    [Fact]
    public void ASCII는_UTF8로_인정되어_변환하지_않는다()
    {
        var bytes = Encoding.ASCII.GetBytes("1\n00:00:01,000 --> 00:00:02,000\nhello\n");

        Assert.True(SubtitleCharset.IsValidUtf8(bytes));
        Assert.False(SubtitleCharset.NeedsConversion(bytes));
    }

    [Fact]
    public void 한글_UTF8은_변환하지_않는다()
    {
        var bytes = new UTF8Encoding(false).GetBytes(Korean);

        Assert.False(SubtitleCharset.NeedsConversion(bytes));
    }

    [Fact]
    public void UTF8_BOM도_변환하지_않는다()
    {
        var bytes = Encoding.UTF8.GetPreamble()
            .Concat(new UTF8Encoding(false).GetBytes(Korean)).ToArray();

        Assert.False(SubtitleCharset.NeedsConversion(bytes));
    }

    [Fact]
    public void CP949_한글은_변환이_필요하다()
    {
        Assert.True(SubtitleCharset.NeedsConversion(Cp949(Korean)));
    }

    [Fact]
    public void UTF16_BOM은_변환이_필요하다()
    {
        var bytes = Encoding.Unicode.GetPreamble()
            .Concat(Encoding.Unicode.GetBytes(Korean)).ToArray();

        Assert.True(SubtitleCharset.NeedsConversion(bytes));
    }

    // ---------- 디코드 ----------

    [Fact]
    public void CP949를_원문으로_복원한다() =>
        Assert.Equal(Korean, SubtitleCharset.DecodeAuto(Cp949(Korean)));

    [Fact]
    public void UTF16LE_BOM을_원문으로_복원한다()
    {
        var bytes = Encoding.Unicode.GetPreamble()
            .Concat(Encoding.Unicode.GetBytes(Korean)).ToArray();

        Assert.Equal(Korean, SubtitleCharset.DecodeAuto(bytes));
    }

    [Fact]
    public void UTF8_BOM은_BOM을_떼고_복원한다()
    {
        var bytes = Encoding.UTF8.GetPreamble()
            .Concat(new UTF8Encoding(false).GetBytes(Korean)).ToArray();

        Assert.Equal(Korean, SubtitleCharset.DecodeAuto(bytes));
    }

    // ---------- EnsureUtf8File ----------

    [Fact]
    public void UTF8_파일은_원본_경로를_그대로_쓴다()
    {
        var path = Path.Combine("subs", "movie.srt");
        var result = SubtitleCharset.EnsureUtf8File(
            path,
            readBytes: _ => new UTF8Encoding(false).GetBytes(Korean),
            writeUtf8Copy: (_, _) => throw new InvalidOperationException("호출되면 안 됨"));

        Assert.Equal(path, result);
    }

    [Fact]
    public void CP949_파일은_UTF8_사본_경로를_돌려준다()
    {
        var copyPath = Path.Combine("temp", "converted.srt");
        string? written = null;

        var result = SubtitleCharset.EnsureUtf8File(
            Path.Combine("subs", "movie.srt"),
            readBytes: _ => Cp949(Korean),
            writeUtf8Copy: (_, text) => { written = text; return copyPath; });

        Assert.Equal(copyPath, result);
        Assert.Equal(Korean, written);
    }
}
