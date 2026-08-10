namespace KOTU.Module.Audio;

/// <summary>
/// 재생 시간 표시 포맷. UI 비의존 — 단위 테스트 대상.
/// 비디오 모듈의 TimeText와 동일 구현 — 모듈 간 직접 참조 금지 규칙에 따라 사본 유지(A10).
/// </summary>
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
