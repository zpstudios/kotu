using KOTU.Core.Integration;
using Xunit;

namespace KOTU.Core.Tests;

/// <summary>
/// shell command 값의 exe 경로 추출·비교(A78) 회귀 가드.
/// ExplorerIntegration이 레지스트리에 쓰는 형태("{exe}" "%1")가 기준이지만,
/// 손상·수동 편집된 값도 관대하게 읽어야 한다(못 읽으면 재등록으로 복구되므로 null도 정답이 된다).
/// </summary>
public class ShellCommandTests
{
    [Theory]
    // 우리가 쓰는 표준 형태
    [InlineData(@"""C:\Apps\KOTU\KOTU.exe"" ""%1""", @"C:\Apps\KOTU\KOTU.exe")]
    // verb command 형태(토큰 인자 포함)
    [InlineData(@"""C:\Apps\KOTU\KOTU.exe"" --extract-here ""%1""", @"C:\Apps\KOTU\KOTU.exe")]
    // 공백 포함 경로
    [InlineData(@"""C:\Program Files\KOTU\KOTU.exe"" ""%1""", @"C:\Program Files\KOTU\KOTU.exe")]
    // 따옴표 없는 값(수동 편집) — 첫 공백 전까지
    [InlineData(@"C:\Apps\KOTU.exe %1", @"C:\Apps\KOTU.exe")]
    // 인자 없는 값
    [InlineData(@"C:\Apps\KOTU.exe", @"C:\Apps\KOTU.exe")]
    // 앞 공백 허용
    [InlineData(@"  ""C:\Apps\KOTU.exe""", @"C:\Apps\KOTU.exe")]
    public void ExtractExePath_ReturnsExe(string command, string expected) =>
        Assert.Equal(expected, ShellCommand.ExtractExePath(command));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    // 닫는 따옴표가 없는 손상 값
    [InlineData("\"C:\\Apps\\KOTU.exe")]
    // 빈 따옴표
    [InlineData("\"\" \"%1\"")]
    public void ExtractExePath_ReturnsNullOnBrokenInput(string? command) =>
        Assert.Null(ShellCommand.ExtractExePath(command));

    [Theory]
    // 대소문자 무시
    [InlineData(@"C:\Apps\KOTU\KOTU.exe", @"c:\apps\kotu\kotu.exe", true)]
    // 동일 경로
    [InlineData(@"C:\Apps\KOTU\KOTU.exe", @"C:\Apps\KOTU\KOTU.exe", true)]
    // 다른 exe명 (A64 시나리오: WinUtil.App.exe → KOTU.exe)
    [InlineData(@"C:\Apps\KOTU\WinUtil.App.exe", @"C:\Apps\KOTU\KOTU.exe", false)]
    // 다른 폴더
    [InlineData(@"C:\Old\KOTU.exe", @"C:\New\KOTU.exe", false)]
    // 빈 쪽이 있으면 어긋남 취급
    [InlineData(null, @"C:\Apps\KOTU.exe", false)]
    [InlineData(@"C:\Apps\KOTU.exe", "", false)]
    public void IsSameExe_ComparesNormalized(string? a, string? b, bool expected) =>
        Assert.Equal(expected, ShellCommand.IsSameExe(a, b));

    [Fact]
    public void IsSameExe_NormalizesRelativeSegments()
    {
        // Windows 러너 전제(CI가 windows-latest) — 경로 정규화로 ..\ 세그먼트가 접힌다.
        if (!OperatingSystem.IsWindows()) return;
        Assert.True(ShellCommand.IsSameExe(@"C:\Apps\KOTU\..\KOTU\KOTU.exe", @"C:\Apps\KOTU\KOTU.exe"));
    }
}
