using KOTU.Core.Settings;

namespace KOTU.Core.Media;

/// <summary>이어듣기 기록 한 건. 경로·위치·길이·갱신 시각.</summary>
public interface IResumeEntry { string Path { get; } long PositionMs { get; } }

/// <summary>
/// 이어듣기(마지막 재생 위치) 저장소. UI 비의존 — 단위 테스트 대상.
/// 모듈별 키와 기록 형식을 주입해 기존 설정 파일과 공개 계약을 유지한다.
/// 정책: 30초 미만 청취는 저장하지 않고, 97% 이상 들으면 다 들은 것으로 간주해 기록을 지운다.
/// 목록 순서가 곧 LRU(뒤가 최신)이며 용량 초과 시 오래된 것부터 버린다.
/// libvlc 이벤트 스레드와 UI 스레드 양쪽에서 호출되므로 내부 잠금으로 보호한다.
/// </summary>
public sealed class PlaybackResumeStore<TEntry> where TEntry : class, IResumeEntry
{
    private readonly string _settingsKey;
    public const long MinResumePositionMs = 30_000;
    public const double WatchedRatio = 0.97;

    private readonly Func<string, long, long, DateTimeOffset, TEntry> _create;
    private readonly ISettingsService _settings;
    private readonly int _capacity;
    private readonly List<TEntry> _entries;
    private readonly object _lock = new();

    public PlaybackResumeStore(ISettingsService settings, string settingsKey, Func<string, long, long, DateTimeOffset, TEntry> create, int capacity = 300)
    {
        _settings = settings;
        _settingsKey = settingsKey;
        _create = create;
        _capacity = Math.Max(1, capacity);
        _entries = settings.Get<List<TEntry>>(_settingsKey, []) ?? [];
    }

    /// <summary>저장된 이어듣기 위치(ms). 없으면 null.</summary>
    public long? GetResumePositionMs(string path)
    {
        lock (_lock)
        {
            return Find(path)?.PositionMs;
        }
    }

    /// <summary>
    /// 현재 재생 위치 보고. 정책에 따라 저장하거나(중간 지점),
    /// 기록을 지운다(초반이거나 거의 끝까지 들은 경우).
    /// </summary>
    public void Report(string path, long positionMs, long durationMs)
    {
        if (string.IsNullOrEmpty(path) || durationMs <= 0) return;

        lock (_lock)
        {
            var nearEnd = positionMs >= (long)(durationMs * WatchedRatio);
            if (positionMs < MinResumePositionMs || nearEnd)
            {
                if (RemoveNoPersist(path)) Persist();
                return;
            }

            RemoveNoPersist(path);
            _entries.Add(_create(path, positionMs, durationMs, DateTimeOffset.Now));
            if (_entries.Count > _capacity)
                _entries.RemoveRange(0, _entries.Count - _capacity);
            Persist();
        }
    }

    /// <summary>해당 파일의 이어듣기 기록 삭제.</summary>
    public void Clear(string path)
    {
        lock (_lock)
        {
            if (RemoveNoPersist(path)) Persist();
        }
    }

    /// <summary>저장된 기록 수 (테스트·진단용).</summary>
    public int Count
    {
        get { lock (_lock) return _entries.Count; }
    }

    private TEntry? Find(string path) =>
        _entries.FirstOrDefault(e => string.Equals(e.Path, path, StringComparison.OrdinalIgnoreCase));

    private bool RemoveNoPersist(string path) =>
        _entries.RemoveAll(e => string.Equals(e.Path, path, StringComparison.OrdinalIgnoreCase)) > 0;

    private void Persist()
    {
        _settings.Set(_settingsKey, _entries);
        _settings.Save();
    }
}
