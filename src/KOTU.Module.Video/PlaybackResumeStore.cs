using KOTU.Core.Settings;

namespace KOTU.Module.Video;

/// <summary>기존 공개 기록 형식과 JSON 필드를 유지한다.</summary>
public sealed record ResumeEntry(string Path, long PositionMs, long DurationMs, DateTimeOffset UpdatedAt)
    : KOTU.Core.Media.IResumeEntry;

/// <summary>모듈별 설정 키를 공통 재생 위치 정책에 연결한다.</summary>
public sealed class PlaybackResumeStore
{
    public const string SettingsKey = "video.resume";
    public const long MinResumePositionMs = KOTU.Core.Media.PlaybackResumeStore<ResumeEntry>.MinResumePositionMs;
    public const double WatchedRatio = KOTU.Core.Media.PlaybackResumeStore<ResumeEntry>.WatchedRatio;
    private readonly KOTU.Core.Media.PlaybackResumeStore<ResumeEntry> _store;

    public PlaybackResumeStore(ISettingsService settings, int capacity = 300)
        => _store = new(settings, SettingsKey, (path, position, duration, updated) =>
            new ResumeEntry(path, position, duration, updated), capacity);

    public long? GetResumePositionMs(string path) => _store.GetResumePositionMs(path);
    public void Report(string path, long positionMs, long durationMs) => _store.Report(path, positionMs, durationMs);
    public void Clear(string path) => _store.Clear(path);
    public int Count => _store.Count;
}
