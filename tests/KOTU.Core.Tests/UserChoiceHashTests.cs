using KOTU.Core.Integration;
using Xunit;

namespace KOTU.Core.Tests;

/// <summary>
/// UserChoice 해시(A38)의 정확성 회귀 가드.
/// 벡터는 Mozilla WindowsUserChoice gtest에서 가져왔으며 — 64-bit Windows 10 Pro 20H2(19042.928)의
/// 시스템 설정이 실제로 기록한 해시다. length mod 8 = 0·2·4·6(블록 경계)과 non-ASCII를 모두 커버한다.
/// 알고리즘이 틀어지면(오타·상수 오류 등) 이 테스트가 CI에서 즉시 깨진다 — 무음 실패 방지.
/// </summary>
public class UserChoiceHashTests
{
    // Mozilla gtest와 동일한 테스트용 SID.
    private const string Sid = "S-1-5-21-636376821-3290315252-1794850287-1001";

    private static long FileTime(int year, int month, int day, int hour, int minute) =>
        new DateTime(year, month, day, hour, minute, 0, DateTimeKind.Utc).ToFileTimeUtc();

    [Theory]
    // length mod 8 = 0
    [InlineData("https", "FirefoxURL-308046B0AF4A39CB", 2021, 4, 19, 23, 7, "uzpIsMVyZ1g=")]
    // length mod 8 = 2 (불완전 마지막 블록을 버리는지 확인)
    [InlineData(".html", "FirefoxHTML-308046B0AF4A39CB", 2021, 4, 19, 23, 7, "7fjRtUPASlc=")]
    // length mod 8 = 4
    [InlineData("https", "MSEdgeHTM", 2021, 4, 19, 23, 3, "Fz0kA3Ymmps=")]
    // length mod 8 = 6
    [InlineData(".html", "ChromeHTML", 2021, 4, 19, 23, 6, "R5TD9LGJ5Xw=")]
    // non-ASCII (UTF-16 서로게이트·악센트 포함)
    [InlineData(".html", "FirefoxHTML-ÀBÇDË😀†", 2021, 4, 20, 0, 38, "F3NsK3uNv5E=")]
    public void Generate_Windows가_기록한_해시와_일치한다(
        string assoc, string progId, int y, int mo, int d, int h, int mi, string expected)
    {
        var hash = UserChoiceHash.Generate(assoc, Sid, progId, FileTime(y, mo, d, h, mi));
        Assert.Equal(expected, hash);
    }

    [Fact]
    public void FloorToMinute_초와_밀리초를_제거한다()
    {
        var aligned = FileTime(2021, 4, 19, 23, 7);
        // 같은 분에 37.5초를 더해도 분 경계로 내려가면 동일해야 한다.
        var withSeconds = aligned + (375L * 1_000_000L); // +37.5s (100ns 단위)

        Assert.Equal(aligned, UserChoiceHash.FloorToMinute(withSeconds));
        Assert.Equal(aligned, UserChoiceHash.FloorToMinute(aligned)); // 멱등
    }

    [Fact]
    public void Generate_시각이_같은_분이면_해시도_같다()
    {
        var t0 = FileTime(2021, 4, 19, 23, 7);
        var t1 = UserChoiceHash.FloorToMinute(t0 + (59L * 10_000_000L)); // +59초 → 같은 분
        Assert.Equal(
            UserChoiceHash.Generate("https", Sid, "FirefoxURL-308046B0AF4A39CB", t0),
            UserChoiceHash.Generate("https", Sid, "FirefoxURL-308046B0AF4A39CB", t1));
    }
}
