using System.Text;

namespace KOTU.Module.Document;

/// <summary>
/// 텍스트 문서의 가벼운 인코딩 판정 (A199) — 탐색기 리스트의 상세 줄(인코딩 조각)용.
/// 셸(ExplorerPane)이 모듈의 public static을 직접 참조하는 선례(ArchiveQuickInfo·
/// AudioModule.Extensions)를 따른다. 판정 규칙은 DocumentView.ReadTextSmart와 같다:
/// BOM 우선, 없으면 엄격 UTF-8 시도, 깨질 때만 CP949로 분류. 단 표시 판정만 필요하므로
/// 전체 파일 대신 앞부분 상한(DetectBytes)만 읽는다 — 대용량 문서가 탐색기 워커 직렬 큐를
/// 막지 않게 하기 위함이고, ReadTextSmart도 4MB에서 자르는 같은 성격의 근사다.
/// 동기 메서드 — 호출자가 뷰 전용 ModuleWorker에서 돌린다(ArchiveQuickInfo와 같은 규칙, A42).
/// </summary>
public static class DocumentQuickInfo
{
    /// <summary>
    /// 판정에 읽는 앞부분 상한. BOM은 3바이트면 충분하고, BOM 없는 UTF-8/CP949 구별도
    /// 비ASCII가 나오는 첫 지점에서 판가름 나므로 64KB면 실용적으로 넉넉하다
    /// (앞 64KB가 전부 ASCII인 파일은 UTF-8로 분류된다 — ReadTextSmart의 결론과 같은 방향).
    /// </summary>
    private const int DetectBytes = 64 * 1024;

    /// <summary>
    /// 인코딩 판정의 짧은 표시 이름 — ReadTextSmart의 TextEncodingKind 다섯 갈래와 1:1이다
    /// ("UTF-8" · "UTF-8 BOM" · "UTF-16 LE" · "UTF-16 BE" · "CP949").
    /// 판정 불가·실패(빈 파일·잠김·삭제 경합 등)는 null = 호출부가 조각을 생략한다.
    /// CP949 분류는 ReadTextSmart처럼 "엄격 UTF-8이 아니다"의 소거법이라 실제 디코드가 없다 —
    /// 코드페이지 제공자 등록(Encoding.RegisterProvider) 없이 동작한다.
    /// </summary>
    public static string? TryGetEncodingName(string path)
    {
        try
        {
            using var stream = File.OpenRead(path);
            if (stream.Length == 0) return null; // 빈 파일 — 판정할 바이트가 없다
            var truncated = stream.Length > DetectBytes;
            var bytes = new byte[Math.Min(stream.Length, DetectBytes)];
            stream.ReadExactly(bytes);

            if (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF)
                return "UTF-8 BOM";
            if (bytes.Length >= 2 && bytes[0] == 0xFF && bytes[1] == 0xFE)
                return "UTF-16 LE";
            if (bytes.Length >= 2 && bytes[0] == 0xFE && bytes[1] == 0xFF)
                return "UTF-16 BE";

            // 상한에서 잘린 파일은 멀티바이트 문자 한가운데서 끊겼을 수 있다 — 꼬리의 불완전한
            // UTF-8 시퀀스를 떼고 검사해야 진짜 UTF-8이 CP949로 오판되지 않는다.
            // (전체를 읽은 파일의 불완전한 꼬리는 진짜 손상이므로 그대로 검사에 넣는다 —
            // ReadTextSmart와 같은 결론이 나오는 방향.)
            var length = truncated ? TrimIncompleteUtf8Tail(bytes) : bytes.Length;
            try
            {
                new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true)
                    .GetString(bytes, 0, length);
                return "UTF-8";
            }
            catch (DecoderFallbackException)
            {
                return "CP949"; // 레거시 한글 — ReadTextSmart와 같은 소거법 분류
            }
        }
        catch
        {
            return null; // 읽기 실패(잠김·권한·삭제 경합 등) = 조각 생략
        }
    }

    /// <summary>
    /// 버퍼 끝의 불완전한 UTF-8 시퀀스를 제외한 길이. 끝에서 최대 3바이트의 연속 바이트
    /// (상위 2비트 = 0x80)를 거슬러 리드 바이트를 찾고, 그 시퀀스의 기대 길이가 버퍼를 넘으면
    /// 리드 바이트 앞에서 자른다. 리드 바이트가 ASCII거나 형식 불량이면 자르지 않는다 —
    /// 엄격 디코드가 원래 규칙대로 판정한다.
    /// </summary>
    private static int TrimIncompleteUtf8Tail(byte[] bytes)
    {
        for (var i = bytes.Length - 1; i >= 0 && i >= bytes.Length - 4; i--)
        {
            if ((bytes[i] & 0xC0) == 0x80) continue; // 연속 바이트 — 더 거슬러 간다
            var expected = (bytes[i] & 0xE0) == 0xC0 ? 2
                : (bytes[i] & 0xF0) == 0xE0 ? 3
                : (bytes[i] & 0xF8) == 0xF0 ? 4
                : 1; // ASCII 또는 불량 리드 — 불완전으로 보지 않는다
            return bytes.Length - i < expected ? i : bytes.Length;
        }
        return bytes.Length; // 끝 4바이트가 전부 연속 바이트 — 불량이므로 그대로 검사
    }
}
