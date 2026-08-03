namespace WinUtil.Module.Archive;

/// <summary>
/// zip 항목명 깨짐(모지바케) 감지 휴리스틱. CP949로 저장된 항목명을 7z.dll이
/// CP437/Latin-1 등으로 잘못 해석하면 U+FFFD, C1 제어문자, 라틴 확장/박스 문자가 연달아 나온다.
/// UI 비의존 순수 로직 - 단위 테스트 대상.
/// </summary>
public static class MojibakeDetector
{
    /// <summary>항목명이 깨져 보이면 true. 정상적인 한글/ASCII/서유럽권 파일명은 false.</summary>
    public static bool LooksBroken(string name)
    {
        var suspicious = 0;
        foreach (var ch in name)
        {
            // 디코딩 실패 대체 문자(U+FFFD)는 확실한 깨짐
            if (ch == '\uFFFD') return true;

            // C1 제어문자(U+0080~U+009F)는 정상 파일명에 나오지 않는다
            if (ch >= '\u0080' && ch <= '\u009F') return true;

            // CP949 바이트열을 CP437/Latin-1로 잘못 읽으면 나오는 전형적 문자 대역.
            // 단독 1개(예: "Café")는 정상일 수 있으므로 2개 이상일 때만 깨짐으로 본다.
            if ((ch >= 'À' && ch <= 'ÿ') ||   // 라틴 확장(À~ÿ)
                (ch >= '═' && ch <= '▓'))     // 박스/음영 문자(CP437 흔적)
            {
                suspicious++;
            }
        }
        return suspicious >= 2;
    }
}
