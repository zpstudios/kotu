using Xunit;

namespace KOTU.Module.Archive.Tests;

public class ExtractHerePlannerTests
{
    private static readonly string Zip = Path.Combine("d", "abc.zip");
    private static string P(string name) => Path.Combine("d", name);

    private static Func<string, bool> Exists(params string[] existing) =>
        p => existing.Contains(p, StringComparer.OrdinalIgnoreCase);

    [Fact]
    public void 단일_루트_폴더는_이중_폴더_없이_그대로_푼다()
    {
        var plan = ExtractHerePlanner.Plan(Zip, ["abc"], Exists());

        Assert.Equal("d", plan.TargetDirectory);   // abc/abc가 아니라 d/abc가 되도록
        Assert.Equal(P("abc"), plan.ResultPath);
    }

    [Fact]
    public void 단일_루트라도_이름이_겹치면_래퍼_폴더에_번호를_붙인다()
    {
        var plan = ExtractHerePlanner.Plan(Zip, ["abc"], Exists(P("abc")));

        Assert.Equal(P("abc (2)"), plan.TargetDirectory);
        Assert.Equal(P("abc (2)"), plan.ResultPath);
    }

    [Fact]
    public void 여러_항목이면_압축_이름의_래퍼_폴더를_만든다()
    {
        var plan = ExtractHerePlanner.Plan(Zip, ["a.txt", "b.txt"], Exists());

        Assert.Equal(P("abc"), plan.TargetDirectory);
    }

    [Fact]
    public void 래퍼_폴더가_이미_있으면_빈_번호를_찾는다()
    {
        var plan = ExtractHerePlanner.Plan(
            Zip, ["a.txt", "b.txt"], Exists(P("abc"), P("abc (2)")));

        Assert.Equal(P("abc (3)"), plan.TargetDirectory);
    }

    [Fact]
    public void 단일_루트_파일도_충돌_없으면_그대로_푼다()
    {
        var plan = ExtractHerePlanner.Plan(Zip, ["readme.txt"], Exists());

        Assert.Equal("d", plan.TargetDirectory);
        Assert.Equal(P("readme.txt"), plan.ResultPath);
    }

    [Fact]
    public void UniquePath는_비어_있으면_원래_이름을_그대로_쓴다()
    {
        Assert.Equal(P("abc"), ExtractHerePlanner.UniquePath(P("abc"), Exists()));
        Assert.Equal(P("abc (2)"), ExtractHerePlanner.UniquePath(P("abc"), Exists(P("abc"))));
    }
}
