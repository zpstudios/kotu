using System.Text;

namespace WinUtil.Module.Video;

/// <summary>
/// 자막 파일 한글 인코딩 자동 처리 (설계 4.3). UI 비의존 — 단위 테스트 대상.
/// libvlc는 UTF-8이 아닌 자막(국내에 흔한 CP949 srt·smi)을 깨뜨리므로,
/// UTF-8이 아닌 파일은 UTF-8 사본을 만들어 그 경로를 재생기에 넘긴다.
/// </summary>
public static class SubtitleCharset
{
    /// <summary>바이트열이 온전한 UTF-8인지 (BOM 유무 무관).</summary>
    public static bool IsValidUtf8(byte[] bytes)
    {
        try
        {
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true)
                .GetString(bytes);
            return true;
        }
        catch (DecoderFallbackException)
        {
            return false;
        }
    }

    /// <summary>UTF-8 사본 생성이 필요한지. UTF-8(BOM 유무 무관)이면 false.</summary>
    public static bool NeedsConversion(byte[] bytes)
    {
        if (HasUtf8Bom(bytes)) return false;
        if (HasUtf16Bom(bytes)) return true;
        return !IsValidUtf8(bytes);
    }

    /// <summary>BOM → UTF-8 유효성 → CP949 순서로 판단해 텍스트로 디코드한다.</summary>
    public static string DecodeAuto(byte[] bytes)
    {
        if (HasUtf8Bom(bytes))
            return Encoding.UTF8.GetString(bytes, 3, bytes.Length - 3);
        if (bytes.Length >= 2 && bytes[0] == 0xFF && bytes[1] == 0xFE)
            return Encoding.Unicode.GetString(bytes, 2, bytes.Length - 2);
        if (bytes.Length >= 2 && bytes[0] == 0xFE && bytes[1] == 0xFF)
            return Encoding.BigEndianUnicode.GetString(bytes, 2, bytes.Length - 2);
        if (IsValidUtf8(bytes))
            return new UTF8Encoding(false).GetString(bytes);

        // UTF-8이 아니면 국내 자막의 사실상 표준인 CP949로 해석한다.
        var cp949 = CodePagesEncodingProvider.Instance.GetEncoding(949)
            ?? throw new NotSupportedException("CP949 인코딩을 로드할 수 없습니다.");
        return cp949.GetString(bytes);
    }

    /// <summary>
    /// libvlc가 읽을 수 있는 UTF-8 자막 경로를 보장한다.
    /// 이미 UTF-8이면 원본 경로 그대로, 아니면 UTF-8 사본을 만들어 그 경로를 반환.
    /// </summary>
    /// <param name="path">원본 자막 경로.</param>
    /// <param name="readBytes">파일 읽기 (테스트 주입용, 기본: File.ReadAllBytes).</param>
    /// <param name="writeUtf8Copy">(원본 경로, 디코드된 텍스트) → 사본 경로 (테스트 주입용, 기본: 임시 폴더).</param>
    public static string EnsureUtf8File(
        string path,
        Func<string, byte[]>? readBytes = null,
        Func<string, string, string>? writeUtf8Copy = null)
    {
        readBytes ??= File.ReadAllBytes;
        writeUtf8Copy ??= WriteTempUtf8Copy;

        var bytes = readBytes(path);
        if (!NeedsConversion(bytes)) return path;
        return writeUtf8Copy(path, DecodeAuto(bytes));
    }

    private static bool HasUtf8Bom(byte[] bytes) =>
        bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF;

    private static bool HasUtf16Bom(byte[] bytes) =>
        bytes.Length >= 2 &&
        ((bytes[0] == 0xFF && bytes[1] == 0xFE) || (bytes[0] == 0xFE && bytes[1] == 0xFF));

    /// <summary>%TEMP%\WinUtil\subtitles\ 아래에 원본별 결정적 이름으로 사본 저장(재사용 시 덮어씀).</summary>
    private static string WriteTempUtf8Copy(string sourcePath, string text)
    {
        var dir = Path.Combine(Path.GetTempPath(), "WinUtil", "subtitles");
        Directory.CreateDirectory(dir);
        var name = $"{(uint)StringComparer.OrdinalIgnoreCase.GetHashCode(sourcePath):x8}_{Path.GetFileName(sourcePath)}";
        var target = Path.Combine(dir, name);
        File.WriteAllText(target, text, new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
        return target;
    }
}
