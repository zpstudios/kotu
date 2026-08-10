namespace KOTU.Core.Integration;

/// <summary>
/// 레지스트리 shell command 값 문자열 처리 (A78, v0.89.0).
/// 예: "C:\Apps\KOTU\KOTU.exe" "%1" 에서 exe 경로만 뽑아 현재 경로와 비교한다.
/// 레지스트리 비의존 순수 함수 — CI에서 단위 테스트 가능(ShellCommandTests).
/// </summary>
public static class ShellCommand
{
    /// <summary>
    /// command 값에서 실행 파일 경로를 추출한다.
    /// 따옴표로 감싼 첫 토큰을 우선하고, 따옴표가 없으면 첫 공백 전까지를 경로로 본다
    /// (우리가 쓰는 값은 항상 따옴표 형태지만, 손상·수동 편집된 값도 관대하게 읽는다).
    /// </summary>
    public static string? ExtractExePath(string? command)
    {
        if (string.IsNullOrWhiteSpace(command)) return null;
        var s = command.TrimStart();
        if (s.StartsWith('"'))
        {
            var end = s.IndexOf('"', 1);
            return end > 1 ? s[1..end] : null;
        }
        var space = s.IndexOf(' ');
        var path = space < 0 ? s : s[..space];
        return path.Length == 0 ? null : path;
    }

    /// <summary>
    /// 두 exe 경로가 같은 파일을 가리키는지 — 대소문자 무시 + 경로 정규화(A78 요구).
    /// 어느 한쪽이라도 비어 있으면 false(= 어긋남으로 보고 재등록하게).
    /// </summary>
    public static bool IsSameExe(string? a, string? b)
    {
        if (string.IsNullOrWhiteSpace(a) || string.IsNullOrWhiteSpace(b)) return false;
        try
        {
            return string.Equals(
                Path.GetFullPath(a.Trim()), Path.GetFullPath(b.Trim()),
                StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            // 잘못된 경로 문자 등 정규화 불가 시 문자열 비교로 폴백.
            return string.Equals(a.Trim(), b.Trim(), StringComparison.OrdinalIgnoreCase);
        }
    }
}
