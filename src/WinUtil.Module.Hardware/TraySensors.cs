using WinUtil.Core.Settings;

namespace WinUtil.Module.Hardware;

/// <summary>
/// 트레이 아이콘에 표시할 센서 선택 모델(A18). 최대 2개, 기본 CPU 온도/전력(사용자 확정).
///
/// - 변경(Toggle/Clear)은 UI 스레드에서만 한다 — 이 앱은 모든 창이 한 UI 스레드를 쓴다.
///   구독자(뷰의 핀 표시, 앱의 SensorTray)도 같은 스레드에서 동기로 통지받는다.
/// - <see cref="Selected"/>는 불변 스냅샷 배열을 돌려준다 — 폴러 워커 스레드가
///   열거 중일 때 UI에서 선택이 바뀌어도 안전하다.
/// - 이미 2개일 때 셋째를 고르면 가장 오래된 선택이 밀려난다(대화상자 없이 즉시).
/// - 저장 키 "hardware.traySensors" = 채널 ID 콤마 목록. 미지의 ID는 로드 시 버린다.
/// </summary>
public static class TraySensors
{
    public const int MaxCount = 2;
    private const string SettingKey = "hardware.traySensors";
    private const string DefaultSelection = "cpuTemp,cpuPower";

    private static readonly List<string> _selected = [];
    private static volatile string[] _snapshot = [];
    private static ISettingsService? _settings;

    /// <summary>선택이 바뀔 때(UI 스레드, 동기). 트레이 아이콘·카드 핀 갱신용.</summary>
    public static event Action? Changed;

    /// <summary>현재 선택(선택한 순서). 불변 스냅샷 — 어느 스레드에서 읽어도 안전.</summary>
    public static IReadOnlyList<string> Selected => _snapshot;

    public static bool IsSelected(string id) => Array.IndexOf(_snapshot, id) >= 0;

    /// <summary>앱 시작 시 1회(HardwareModule 생성) — 저장된 선택을 복원한다.</summary>
    public static void Initialize(ISettingsService settings)
    {
        _settings = settings;
        _selected.Clear();
        var raw = settings.Get(SettingKey, DefaultSelection);
        foreach (var id in raw.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            if (SensorChannels.ById(id) is not null && !_selected.Contains(id) && _selected.Count < MaxCount)
                _selected.Add(id);
        _snapshot = [.. _selected];
        // Initialize는 구독자가 붙기 전이라 Changed를 쏘지 않는다 — SensorTray가 생성 시 직접 읽는다.
    }

    /// <summary>카드 클릭 토글. 켤 때 이미 가득이면 가장 오래된 선택을 밀어낸다.</summary>
    public static void Toggle(string id)
    {
        if (SensorChannels.ById(id) is null) return;
        if (!_selected.Remove(id))
        {
            while (_selected.Count >= MaxCount) _selected.RemoveAt(0);
            _selected.Add(id);
        }
        Commit();
    }

    /// <summary>전부 해제(트레이 메뉴 "Hide tray sensors").</summary>
    public static void Clear()
    {
        if (_selected.Count == 0) return;
        _selected.Clear();
        Commit();
    }

    private static void Commit()
    {
        _snapshot = [.. _selected];
        if (_settings is not null)
        {
            _settings.Set(SettingKey, string.Join(',', _selected));
            _settings.Save();
        }
        Changed?.Invoke();
    }
}
