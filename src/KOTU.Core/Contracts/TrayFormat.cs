using System.Globalization;

namespace KOTU.Core.Contracts;

/// <summary>
/// 트레이 아이콘 한 줄(16px 폭)에 들어가는 초압축 표기 (A54, v0.118.0).
///
/// 기존 크기 헬퍼(<c>ExplorerListing.FormatSize</c>·<c>ArchiveEntryTree.FormatSize</c>)는
/// "1.2 MB"처럼 공백 + 두 글자 단위라 16px에 못 들어간다 — 그래서 별도 표기를 둔다:
/// 소수 1자리 + 단위 1글자("1.2M"·"340K"·"2.1G", 사용자 확정). 값이 10 이상이면
/// 소수를 버려 네 글자를 넘기지 않는다.
///
/// 순수 함수(UI·모듈 비의존)라 Core에 둔다 — 모든 모듈이 같은 표기를 쓴다.
/// </summary>
public static class TrayFormat
{
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    /// <summary>해상도 축약에 쓰는 표준 세로 픽셀. 이 값보다 6% 이내로 크면 같은 등급으로 본다.</summary>
    private static readonly int[] StandardHeights = [4320, 2160, 1440, 1080, 720, 576, 480, 360, 240];

    /// <summary>파일 용량 → "1.2M"·"340K"·"2.1G"(1024 단위). 1KB 미만은 "912B".</summary>
    public static string Size(long bytes)
    {
        if (bytes < 0) return TrayStatus.Unknown;
        if (bytes < 1024) return bytes.ToString(Inv) + "B";

        const string units = "KMGTP";
        double value = bytes;
        var index = -1;
        while (value >= 1024 && index < units.Length - 1)
        {
            value /= 1024;
            index++;
        }
        return Compact(value) + units[index];
    }

    /// <summary>비트레이트 → "4.2M"(1000 단위 — 미디어 관례). 0 이하면 "—".</summary>
    public static string Bitrate(double bitsPerSecond)
    {
        if (double.IsNaN(bitsPerSecond) || double.IsInfinity(bitsPerSecond) || bitsPerSecond <= 0)
            return TrayStatus.Unknown;

        const string units = "KMG";
        var value = bitsPerSecond;
        var index = -1;
        while (value >= 1000 && index < units.Length - 1)
        {
            value /= 1000;
            index++;
        }
        return index < 0 ? Compact(value) + "b" : Compact(value) + units[index];
    }

    /// <summary>파일 크기·재생 길이로 계산한 평균 비트레이트. 둘 중 하나라도 모르면 "—".</summary>
    public static string BitrateOf(long fileBytes, long durationMs) =>
        fileBytes <= 0 || durationMs <= 0
            ? TrayStatus.Unknown
            : Bitrate(fileBytes * 8.0 * 1000.0 / durationMs);

    /// <summary>세로 픽셀 → "1080p". 표준 등급에 6% 이내로 걸치면 그 등급으로 맞춘다(1088 → 1080p).</summary>
    public static string Resolution(int heightPx)
    {
        if (heightPx <= 0) return TrayStatus.Unknown;
        foreach (var standard in StandardHeights)
        {
            if (heightPx >= standard && heightPx <= standard * 106 / 100)
                return standard.ToString(Inv) + "p";
        }
        return heightPx.ToString(Inv) + "p";
    }

    /// <summary>확장자 → 점 없는 대문자 3~4자("PNG"·"JPEG"). 더 길면 4자로 자른다.</summary>
    public static string Extension(string? path)
    {
        if (string.IsNullOrEmpty(path)) return TrayStatus.Unknown;
        var ext = Path.GetExtension(path);
        if (ext.Length <= 1) return TrayStatus.Unknown;
        ext = ext[1..].ToUpperInvariant();
        return ext.Length <= 4 ? ext : ext[..4];
    }

    /// <summary>압축률(압축 후 / 원본) → "0.42". 원본 크기를 모르면 "—".</summary>
    public static string Ratio(long packedBytes, long rawBytes)
    {
        if (packedBytes < 0 || rawBytes <= 0) return TrayStatus.Unknown;
        return Math.Clamp((double)packedBytes / rawBytes, 0, 1).ToString("0.00", Inv);
    }

    /// <summary>진행률(0~1) → "45%".</summary>
    public static string Percent(double fraction) =>
        (Math.Clamp(fraction, 0, 1) * 100).ToString("0", Inv) + "%";

    /// <summary>10 미만은 소수 1자리, 그 이상은 정수 — 단위 글자를 붙여도 네 글자를 넘기지 않는다.</summary>
    private static string Compact(double value) =>
        value < 10 ? value.ToString("0.0", Inv) : value.ToString("0", Inv);
}
