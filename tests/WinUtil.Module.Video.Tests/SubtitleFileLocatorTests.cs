using Xunit;

namespace WinUtil.Module.Video.Tests;

public class SubtitleFileLocatorTests
{
    private static readonly string Dir = Path.Combine("videos");
    private static readonly string Video = Path.Combine(Dir, "movie.mkv");

    private static Func<string, IEnumerable<string>> Fake(params string[] names) =>
        _ => names.Select(n => Path.Combine(Dir, n));

    [Fact]
    public void 같은_이름의_자막을_찾는다()
    {
        var found = SubtitleFileLocator.Find(Video, Fake("movie.srt", "movie.mkv"));

        Assert.Equal(new[] { Path.Combine(Dir, "movie.srt") }, found);
    }

    [Fact]
    public void 접미사_변형도_찾되_정확한_이름이_먼저다()
    {
        var found = SubtitleFileLocator.Find(
            Video, Fake("movie.ko.srt", "movie.smi", "movie.mkv"));

        Assert.Equal(
            new[] { Path.Combine(Dir, "movie.smi"), Path.Combine(Dir, "movie.ko.srt") },
            found);
    }

    [Fact]
    public void 관계없는_파일은_제외한다()
    {
        var found = SubtitleFileLocator.Find(
            Video, Fake("other.srt", "movie2.srt", "movie.txt", "movie.mkv"));

        Assert.Empty(found);
    }

    [Fact]
    public void 확장자는_대소문자를_무시한다()
    {
        var found = SubtitleFileLocator.Find(Video, Fake("MOVIE.SRT"));

        Assert.Single(found);
    }

    [Fact]
    public void 접미사끼리는_짧은_것이_먼저다()
    {
        var found = SubtitleFileLocator.Find(
            Video, Fake("movie.korean.srt", "movie.ko.srt"));

        Assert.Equal(
            new[] { Path.Combine(Dir, "movie.ko.srt"), Path.Combine(Dir, "movie.korean.srt") },
            found);
    }

    [Fact]
    public void 자막이_없으면_빈_목록이다()
    {
        Assert.Empty(SubtitleFileLocator.Find(Video, Fake("movie.mkv", "movie.jpg")));
    }
}
