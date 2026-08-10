namespace KOTU.Module.Video;

/// <summary>재생 시간 표시 포맷. UI 비의존 — 단위 테스트 대상.</summary>
public static class TimeText
{
    /// <summary>밀리초 → "m:ss" 또는 1시간 이상이면 "h:mm:ss". 음수는 0으로 처리.</summary>
    public static string Format(long milliseconds)
    {
        if (milliseconds < 0) milliseconds = 0;
        var t = TimeSpan.FromMilliseconds(milliseconds);
        return t.TotalHours >= 1
            ? $"{(int)t.TotalHours}:{t.Minutes:D2}:{t.Seconds:D2}"
            : $"{t.Minutes}:{t.Seconds:D2}";
    }
}
