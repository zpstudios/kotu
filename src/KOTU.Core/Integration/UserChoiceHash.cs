using System.Security.Cryptography;
using System.Text;

namespace KOTU.Core.Integration;

/// <summary>
/// Windows "UserChoice" 기본 앱 보호 해시를 자체 계산한다 (A38, v0.85.0).
///
/// Windows 8+는 파일 형식/프로토콜의 기본 앱 선택(UserChoice)을 프로그램이 함부로 못 바꾸도록
/// ProgId와 함께 비공개 해시를 요구한다. 이 해시 알고리즘은 공개 역공학으로 알려져 있으며,
/// 여기서는 외부 실행 파일(SetUserFTA 등) 동봉 없이 순수 관리 코드로 구현한다.
///
/// 알고리즘 출처(상수·구조 동일):
///  - Mozilla WindowsUserChoice.cpp (MPL-2.0) — Firefox가 기본 브라우저 지정에 사용.
///  - PS-SFTA / SetUserFTA (Danysys, Christoph Kolbicz).
/// Windows 설정이 실제로 만든 해시(Mozilla gtest 벡터) 5종으로 검증됨 → UserChoiceHashTests.
///
/// 주의: 비공식 방식이라 Windows 업데이트로 알고리즘이 바뀌면 무력화될 수 있다(A38 수용 리스크).
/// 그래서 호출 측(ExplorerIntegration)은 쓴 뒤 반드시 재검증하고 실패 시 폴백한다.
/// </summary>
public static class UserChoiceHash
{
    // Windows 내부에 하드코딩된 고정 문자열. 파생 방법이 알려져 있지 않아 상수로 둔다.
    private const string UserExperience =
        "User Choice set via Windows User Experience {D18B6DD5-6124-4341-9318-804003BAFA0B}";

    // FILETIME 1분 = 60초 * 10^7(100ns). 초·밀리초를 0으로 맞추려 분 경계로 내림한다.
    private const long FileTimeTicksPerMinute = 600_000_000L;

    /// <summary>
    /// FILETIME을 분 경계로 내린다(초·밀리초 제거).
    /// 해시에 쓰는 시각과 UserChoice 키의 LastWrite 시각은 분 단위로 일치해야 Windows가 수용한다.
    /// </summary>
    public static long FloorToMinute(long fileTimeUtc) =>
        fileTimeUtc - (fileTimeUtc % FileTimeTicksPerMinute);

    /// <summary>
    /// UserChoice 해시(Base64)를 만든다.
    /// </summary>
    /// <param name="assoc">확장자(".mp4") 또는 프로토콜("https").</param>
    /// <param name="userSid">현재 사용자 SID 문자열("S-1-5-21-...").</param>
    /// <param name="progId">지정할 ProgId(레지스트리에 쓸 값과 반드시 동일해야 함).</param>
    /// <param name="fileTimeUtc">UserChoice 키 시각(FILETIME, UTC) — 초·밀리초 0이어야 함(FloorToMinute).</param>
    public static string Generate(string assoc, string userSid, string progId, long fileTimeUtc)
    {
        uint hi = (uint)(fileTimeUtc >> 32);
        uint lo = (uint)(fileTimeUtc & 0xFFFFFFFF);
        // Windows 형식: {assoc}{sid}{progId}{hi:x8}{lo:x8}{experience}, 전체 소문자.
        string input = $"{assoc}{userSid}{progId}{hi:x8}{lo:x8}{UserExperience}".ToLowerInvariant();
        return HashString(input);
    }

    private static uint WordSwap(uint v) => (v >> 16) | (v << 16);

    /// <summary>
    /// 문자열(UTF-16LE + 널 종료자)에 대한 UserChoice 해시.
    /// MD5의 앞 두 DWORD를 곱셈 상수로 쓰는 2중 체크섬 스크램블 후 8바이트를 Base64로.
    /// </summary>
    private static string HashString(string input)
    {
        // C++ (lstrlenW+1)*sizeof(wchar_t) 와 동일: UTF-16LE에 2바이트 널 종료자 포함.
        byte[] bytes = Encoding.Unicode.GetBytes(input + "\0");
        int byteCount = bytes.Length;

        const int blockSize = sizeof(uint) * 2; // 8바이트(2 DWORD)
        int blockCount = byteCount / blockSize; // 불완전한 마지막 블록은 버린다
        if (blockCount == 0)
            return string.Empty;

        // MD5[0], MD5[1]을 스크램블 곱셈 상수로 사용.
        byte[] md5 = MD5.HashData(bytes);
        uint md50 = BitConverter.ToUInt32(md5, 0);
        uint md51 = BitConverter.ToUInt32(md5, 4);

        // 블록 내 DWORD별(j=0,1) 상수 세트. c0=스크램블0, c1=스크램블1.
        uint[] c0a = [md50 | 1u, 0xCF98B111u, 0x87085B9Fu, 0x12CEB96Du, 0x257E1D83u];
        uint[] c0b = [md51 | 1u, 0xA27416F5u, 0xD38396FFu, 0x7C932B89u, 0xBFA49F69u];
        uint[] c1a = [md50 | 1u, 0xEF0569FBu, 0x689B6B9Fu, 0x79F8A395u, 0xC3EFEA97u];
        uint[] c1b = [md51 | 1u, 0xC31713DBu, 0xDDCD1F0Fu, 0x59C3AF2Du, 0x35BD1EC9u];

        uint h0 = 0, h1 = 0, h0Acc = 0, h1Acc = 0;

        unchecked
        {
            for (int i = 0; i < blockCount; i++)
            {
                for (int j = 0; j < 2; j++)
                {
                    uint[] c0 = j == 0 ? c0a : c0b;
                    uint[] c1 = j == 0 ? c1a : c1b;
                    uint input32 = BitConverter.ToUInt32(bytes, (i * 2 + j) * sizeof(uint));

                    h0 += input32;
                    h0 *= c0[0];
                    h0 = WordSwap(h0) * c0[1];
                    h0 = WordSwap(h0) * c0[2];
                    h0 = WordSwap(h0) * c0[3];
                    h0 = WordSwap(h0) * c0[4];
                    h0Acc += h0;

                    h1 += input32;
                    h1 = WordSwap(h1) * c1[1] + h1 * c1[0];
                    h1 = (h1 >> 16) * c1[2] + h1 * c1[3];
                    h1 = WordSwap(h1) * c1[4] + h1;
                    h1Acc += h1;
                }
            }
        }

        byte[] outBytes = new byte[8];
        BitConverter.GetBytes(h0 ^ h1).CopyTo(outBytes, 0);
        BitConverter.GetBytes(h0Acc ^ h1Acc).CopyTo(outBytes, 4);
        return Convert.ToBase64String(outBytes);
    }
}
