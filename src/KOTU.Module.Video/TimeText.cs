namespace KOTU.Module.Video;

/// <summary>공개 이름을 유지하면서 공통 시간 표시 규칙을 사용한다.</summary>
public static class TimeText
{
    public static string Format(long milliseconds) => KOTU.Core.Media.TimeText.Format(milliseconds);
}
