using KOTU.Module.Image;
using Xunit;

namespace KOTU.Module.Image.Tests;

public class ImageFolderNavigatorTests
{
    private const string Dir = @"C:\pics";

    /// <summary>가짜 파일 열거를 주입해 내비게이터를 만든다.</summary>
    private static ImageFolderNavigator Create(string currentFile, params string[] folderFiles) =>
        new(currentFile, ImageFolderNavigator.SupportedExtensions, _ => folderFiles);

    private static string P(string name) => Path.Combine(Dir, name);

    // ---------- 자연 정렬 ----------

    [Fact]
    public void 자연_정렬_img2가_img10보다_앞이다()
    {
        var nav = Create(P("img1.jpg"), P("img10.jpg"), P("img2.jpg"), P("img1.jpg"));

        Assert.Equal(P("img1.jpg"), nav.Current);
        Assert.True(nav.MoveNext());
        Assert.Equal(P("img2.jpg"), nav.Current);
        Assert.True(nav.MoveNext());
        Assert.Equal(P("img10.jpg"), nav.Current);
    }

    [Fact]
    public void 자연_정렬_선행_0과_대소문자를_처리한다()
    {
        var nav = Create(P("a002.png"), P("A010.png"), P("a002.png"), P("a1.png"));

        Assert.Equal(1, nav.CurrentIndex); // a1 < a002 < A010
        Assert.True(nav.MovePrevious());
        Assert.Equal(P("a1.png"), nav.Current);
    }

    [Fact]
    public void 숫자가_아주_길어도_수치_비교가_된다()
    {
        var nav = Create(
            P("f99999999999999999999.jpg"),
            P("f100000000000000000000.jpg"),
            P("f99999999999999999999.jpg"));

        Assert.Equal(0, nav.CurrentIndex); // 20자리 < 21자리
    }

    // ---------- 필터링 / 초기 상태 ----------

    [Fact]
    public void 지원하지_않는_확장자는_목록에서_제외된다()
    {
        var nav = Create(P("b.jpg"), P("a.txt"), P("b.jpg"), P("c.mp4"), P("d.png"));

        Assert.Equal(2, nav.Count);
        Assert.Equal(P("b.jpg"), nav.Current);
    }

    [Fact]
    public void 확장자_대소문자는_무시된다()
    {
        var nav = Create(P("a.JPG"), P("a.JPG"), P("b.PnG"));

        Assert.Equal(2, nav.Count);
        Assert.Equal(0, nav.CurrentIndex);
    }

    [Fact]
    public void 열린_파일이_열거에_없어도_목록에_포함된다()
    {
        var nav = Create(P("solo.jpg"), P("other.png"));

        Assert.Equal(2, nav.Count);
        Assert.Equal(P("solo.jpg"), nav.Current);
    }

    // ---------- 이동 / 경계 (순환 없음) ----------

    [Fact]
    public void 마지막에서_MoveNext는_false이고_멈춘다()
    {
        var nav = Create(P("b.jpg"), P("a.jpg"), P("b.jpg"));

        Assert.False(nav.MoveNext());
        Assert.Equal(P("b.jpg"), nav.Current); // 순환하지 않음
    }

    [Fact]
    public void 처음에서_MovePrevious는_false이고_멈춘다()
    {
        var nav = Create(P("a.jpg"), P("a.jpg"), P("b.jpg"));

        Assert.False(nav.MovePrevious());
        Assert.Equal(P("a.jpg"), nav.Current);
    }

    [Fact]
    public void 파일이_하나면_양방향_모두_false다()
    {
        var nav = Create(P("only.gif"), P("only.gif"));

        Assert.False(nav.MoveNext());
        Assert.False(nav.MovePrevious());
        Assert.Equal(0, nav.CurrentIndex);
        Assert.Equal(1, nav.Count);
    }

    // ---------- Remove ----------

    [Fact]
    public void 현재_파일_제거시_다음_파일이_현재가_된다()
    {
        var nav = Create(P("b.jpg"), P("a.jpg"), P("b.jpg"), P("c.jpg"));

        Assert.True(nav.Remove(P("b.jpg")));
        Assert.Equal(P("c.jpg"), nav.Current);
        Assert.Equal(2, nav.Count);
    }

    [Fact]
    public void 마지막_파일_제거시_이전_파일이_현재가_된다()
    {
        var nav = Create(P("c.jpg"), P("a.jpg"), P("b.jpg"), P("c.jpg"));

        Assert.True(nav.Remove(P("c.jpg")));
        Assert.Equal(P("b.jpg"), nav.Current);
    }

    [Fact]
    public void 현재보다_앞의_파일_제거시_현재는_유지된다()
    {
        var nav = Create(P("c.jpg"), P("a.jpg"), P("b.jpg"), P("c.jpg"));

        Assert.True(nav.Remove(P("a.jpg")));
        Assert.Equal(P("c.jpg"), nav.Current);
        Assert.Equal(1, nav.CurrentIndex);
    }

    [Fact]
    public void 모두_제거하면_Current는_null이다()
    {
        var nav = Create(P("a.jpg"), P("a.jpg"), P("b.jpg"));

        Assert.True(nav.Remove(P("a.jpg")));
        Assert.True(nav.Remove(P("b.jpg")));
        Assert.Null(nav.Current);
        Assert.Equal(-1, nav.CurrentIndex);
        Assert.Equal(0, nav.Count);
        Assert.False(nav.MoveNext());
        Assert.False(nav.MovePrevious());
    }

    [Fact]
    public void 없는_파일_제거는_false다()
    {
        var nav = Create(P("a.jpg"), P("a.jpg"));

        Assert.False(nav.Remove(P("ghost.jpg")));
        Assert.Equal(1, nav.Count);
    }

    [Fact]
    public void Remove는_경로_대소문자를_무시한다()
    {
        var nav = Create(P("a.jpg"), P("a.jpg"), P("b.jpg"));

        Assert.True(nav.Remove(P("A.JPG")));
        Assert.Equal(P("b.jpg"), nav.Current);
    }

    // ---------- NaturalStringComparer 직접 검증 ----------

    [Theory]
    [InlineData("img2", "img10", -1)]
    [InlineData("img10", "img2", 1)]
    [InlineData("img2", "img2", 0)]
    [InlineData("img02", "img2", 0)]   // 선행 0은 수치상 동일
    [InlineData("a", "b", -1)]
    [InlineData("a1b2", "a1b10", -1)]
    public void 자연_정렬_비교자_기본_케이스(string x, string y, int expectedSign)
    {
        var c = new NaturalStringComparer().Compare(x, y);
        Assert.Equal(expectedSign, Math.Sign(c));
    }
}
