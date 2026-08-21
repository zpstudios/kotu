using KOTU.Core.Navigation;
using Xunit;

namespace KOTU.Core.Tests;

/// <summary>
/// A11 폴더 재생 목록(FolderPlaylist)의 단위 테스트.
/// 대부분은 열거 델리게이트를 가짜로 주입해 파일 시스템 없이 돈다 — 정렬·필터·경계·제거.
/// 숨김·시스템 제외만 실제 속성이 필요해 임시 폴더를 쓴다(ExplorerListingTests와 같은 방식).
/// </summary>
public class FolderPlaylistTests : IDisposable
{
    private const string Dir = @"C:\clips";

    /// <summary>영상 모듈이 넘길 확장자 목록의 축약판(실제 주입 형태와 같다).</summary>
    private static readonly string[] Exts = [".mp4", ".mkv", ".avi"];

    private readonly string _tempDir =
        Path.Combine(Path.GetTempPath(), "kotu-playlist-test-" + Guid.NewGuid().ToString("N"));

    public FolderPlaylistTests() => Directory.CreateDirectory(_tempDir);

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, recursive: true); }
        catch { /* 청소 실패는 테스트 결과와 무관 */ }
    }

    /// <summary>가짜 파일 열거를 주입해 목록을 만든다(숨김 필터 없음 = 순수 경로).</summary>
    private static FolderPlaylist Create(string currentFile, params string[] folderFiles) =>
        new(currentFile, Exts, _ => folderFiles);

    private static string P(string name) => Path.Combine(Dir, name);

    private string Make(string name)
    {
        var path = Path.Combine(_tempDir, name);
        File.WriteAllText(path, "x");
        return path;
    }

    // ---------- 자연 정렬 (이미지 선례와 동일한 순서여야 한다) ----------

    [Fact]
    public void 자연_정렬_ep2가_ep10보다_앞이다()
    {
        var list = Create(P("ep1.mp4"), P("ep10.mp4"), P("ep2.mp4"), P("ep1.mp4"));

        Assert.Equal(P("ep1.mp4"), list.Current);
        Assert.True(list.MoveNext());
        Assert.Equal(P("ep2.mp4"), list.Current);
        Assert.True(list.MoveNext());
        Assert.Equal(P("ep10.mp4"), list.Current);
        Assert.False(list.MoveNext()); // 끝에서 순환하지 않는다
    }

    [Fact]
    public void 자연_정렬_선행_0과_대소문자를_처리한다()
    {
        var list = Create(P("a002.mp4"), P("A010.mp4"), P("a002.mp4"), P("a1.mp4"));

        Assert.Equal(1, list.CurrentIndex); // a1 < a002 < A010
        Assert.True(list.MovePrevious());
        Assert.Equal(P("a1.mp4"), list.Current);
    }

    [Fact]
    public void 숫자가_아주_길어도_수치_비교가_된다()
    {
        var list = Create(
            P("f99999999999999999999.mp4"),
            P("f100000000000000000000.mp4"),
            P("f99999999999999999999.mp4"));

        Assert.Equal(0, list.CurrentIndex); // 20자리 < 21자리
    }

    // ---------- 확장자 필터 (호출부 주입) ----------

    [Fact]
    public void 주입된_확장자_목록_밖의_파일은_제외된다()
    {
        var list = Create(P("b.mp4"), P("a.txt"), P("b.mp4"), P("c.jpg"), P("d.mkv"));

        Assert.Equal(2, list.Count);
        Assert.Equal(P("b.mp4"), list.Current);
    }

    [Fact]
    public void 확장자_대소문자는_무시된다()
    {
        var list = Create(P("a.MP4"), P("a.MP4"), P("b.MkV"));

        Assert.Equal(2, list.Count);
        Assert.Equal(0, list.CurrentIndex);
    }

    [Fact]
    public void 열린_파일이_열거에_없거나_필터_밖이어도_목록에_포함된다()
    {
        // 처음 연 파일은 확장자 목록 밖(.mov)이어도 들어간다 — 명시적 열기는 의도된 접근.
        var list = Create(P("solo.mov"), P("other.mp4"));

        Assert.Equal(2, list.Count);
        Assert.Equal(P("solo.mov"), list.Current);
    }

    // ---------- 숨김·시스템 제외 (설계 §1.3 — ExplorerListing.ShouldShow 재사용) ----------
    // 숨김/시스템 속성은 윈도우 전용이고 CI(build.yml)가 windows-latest에서 dotnet test를 돌린다.

    [Fact]
    public void Create는_숨김과_시스템_파일을_목록에서_뺀다()
    {
        var plain = Make("plain.mp4");
        var hidden = Make("secret.mp4");
        File.SetAttributes(hidden, File.GetAttributes(hidden) | FileAttributes.Hidden);
        var system = Make("sys.mp4");
        File.SetAttributes(system, File.GetAttributes(system) | FileAttributes.System);

        var list = FolderPlaylist.Create(plain, Exts);

        Assert.Equal(1, list.Count);
        Assert.Equal(plain, list.Current);
        Assert.False(list.HasNext);
    }

    [Fact]
    public void 처음_연_파일이_숨김이면_그_파일만은_목록에_남는다()
    {
        var plain = Make("plain.mp4");
        var hidden = Make("secret.mp4");
        File.SetAttributes(hidden, File.GetAttributes(hidden) | FileAttributes.Hidden);

        var list = FolderPlaylist.Create(hidden, Exts);

        // 필터가 뺀 자리를 "처음 연 파일 포함" 규칙이 되살린다 — 숨김 파일 1개 + 보이는 파일 1개.
        Assert.Equal(2, list.Count);
        Assert.Equal(hidden, list.Current);
        Assert.Equal(plain, list.PeekPrevious); // plain.mp4 < secret.mp4
    }

    [Fact]
    public void IsVisibleOnDisk는_판정을_ShouldShow에_맡기고_없는_파일은_거짓이다()
    {
        var plain = Make("plain.mp4");
        var hidden = Make("secret.mp4");
        File.SetAttributes(hidden, File.GetAttributes(hidden) | FileAttributes.Hidden);

        Assert.True(FolderPlaylist.IsVisibleOnDisk(plain));
        Assert.False(FolderPlaylist.IsVisibleOnDisk(hidden));
        Assert.False(FolderPlaylist.IsVisibleOnDisk(Path.Combine(_tempDir, "ghost.mp4")));
    }

    // ---------- 경계 (처음/끝 — 순환 없음) ----------

    [Fact]
    public void 마지막에서_MoveNext는_false이고_멈춘다()
    {
        var list = Create(P("b.mp4"), P("a.mp4"), P("b.mp4"));

        Assert.False(list.HasNext);
        Assert.False(list.MoveNext());
        Assert.Equal(P("b.mp4"), list.Current); // 순환하지 않음
        Assert.Null(list.PeekNext);
    }

    [Fact]
    public void 처음에서_MovePrevious는_false이고_멈춘다()
    {
        var list = Create(P("a.mp4"), P("a.mp4"), P("b.mp4"));

        Assert.False(list.HasPrevious);
        Assert.False(list.MovePrevious());
        Assert.Equal(P("a.mp4"), list.Current);
        Assert.Null(list.PeekPrevious);
    }

    [Fact]
    public void 파일이_하나면_양방향_모두_false다()
    {
        var list = Create(P("only.mkv"), P("only.mkv"));

        Assert.False(list.MoveNext());
        Assert.False(list.MovePrevious());
        Assert.Equal(0, list.CurrentIndex);
        Assert.Equal(1, list.Count);
    }

    // ---------- MoveFirst (설계 §3.3 전이 3 — 목록 루프) ----------

    [Fact]
    public void 목록_끝에서_MoveFirst는_첫_파일로_되돌아간다()
    {
        var list = Create(P("c.mp4"), P("a.mp4"), P("b.mp4"), P("c.mp4"));

        Assert.Equal(2, list.CurrentIndex);
        Assert.True(list.MoveFirst());
        Assert.Equal(P("a.mp4"), list.Current);
        Assert.Equal(0, list.CurrentIndex);
        Assert.Equal(P("a.mp4"), list.PeekFirst);
    }

    [Fact]
    public void 이미_첫_파일이거나_목록이_비면_MoveFirst는_false다()
    {
        var single = Create(P("only.mp4"), P("only.mp4"));
        Assert.False(single.MoveFirst()); // 이동이 없으면 false(Move* 반환 규약)
        Assert.Equal(0, single.CurrentIndex);

        var emptied = Create(P("a.mp4"), P("a.mp4"));
        Assert.True(emptied.Remove(P("a.mp4")));
        Assert.False(emptied.MoveFirst());
        Assert.Equal(-1, emptied.CurrentIndex);
        Assert.Null(emptied.PeekFirst);
    }

    // ---------- 소실 파일 (Remove로 목록 갱신) ----------

    [Fact]
    public void 현재_파일_제거시_다음_파일이_현재가_된다()
    {
        var list = Create(P("b.mp4"), P("a.mp4"), P("b.mp4"), P("c.mp4"));

        Assert.True(list.Remove(P("b.mp4")));
        Assert.Equal(P("c.mp4"), list.Current);
        Assert.Equal(2, list.Count);
    }

    [Fact]
    public void 마지막_파일_제거시_이전_파일이_현재가_된다()
    {
        var list = Create(P("c.mp4"), P("a.mp4"), P("b.mp4"), P("c.mp4"));

        Assert.True(list.Remove(P("c.mp4")));
        Assert.Equal(P("b.mp4"), list.Current);
    }

    [Fact]
    public void 현재보다_앞의_파일_제거시_현재는_유지되고_인덱스만_당겨진다()
    {
        var list = Create(P("c.mp4"), P("a.mp4"), P("b.mp4"), P("c.mp4"));

        Assert.True(list.Remove(P("a.mp4")));
        Assert.Equal(P("c.mp4"), list.Current);
        Assert.Equal(1, list.CurrentIndex);
    }

    [Fact]
    public void 모두_제거하면_Current는_null이고_이동은_전부_false다()
    {
        var list = Create(P("a.mp4"), P("a.mp4"), P("b.mp4"));

        Assert.True(list.Remove(P("a.mp4")));
        Assert.True(list.Remove(P("b.mp4")));
        Assert.Null(list.Current);
        Assert.Equal(-1, list.CurrentIndex);
        Assert.Equal(0, list.Count);
        Assert.False(list.MoveNext());
        Assert.False(list.MovePrevious());
    }

    [Fact]
    public void 없는_파일_제거는_false이고_제거는_경로_대소문자를_무시한다()
    {
        var list = Create(P("a.mp4"), P("a.mp4"), P("b.mp4"));

        Assert.False(list.Remove(P("ghost.mp4")));
        Assert.Equal(2, list.Count);

        Assert.True(list.Remove(P("A.MP4")));
        Assert.Equal(P("b.mp4"), list.Current);
    }

    // ---------- NaturalFileNameComparer 직접 검증 ----------
    // 이미지 원본(ImageFolderNavigatorTests의 같은 Theory)과 같은 케이스·같은 기대값이다 —
    // 두 비교자가 갈라지면 이미지 좌우 탐색과 영상 목록 순서가 어긋난다.

    [Theory]
    [InlineData("ep2", "ep10", -1)]
    [InlineData("ep10", "ep2", 1)]
    [InlineData("ep2", "ep2", 0)]
    [InlineData("ep02", "ep2", 0)]   // 선행 0은 수치상 동일
    [InlineData("a", "b", -1)]
    [InlineData("a1b2", "a1b10", -1)]
    public void 자연_정렬_비교자_기본_케이스(string x, string y, int expectedSign)
    {
        var c = new NaturalFileNameComparer().Compare(x, y);
        Assert.Equal(expectedSign, Math.Sign(c));
    }
}
