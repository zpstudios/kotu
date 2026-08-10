using System.Text.Json;

namespace WinUtil.Core.Settings;

/// <summary>JSON 파일 기반 설정. 기본 위치: %AppData%\KOTU\settings.json (경로 주입 가능 — 테스트용).</summary>
public sealed class JsonSettingsService : ISettingsService
{
    private static readonly JsonSerializerOptions s_json = new() { WriteIndented = true };
    private readonly string _path;
    private readonly Dictionary<string, JsonElement> _values;
    private readonly object _lock = new();

    public JsonSettingsService(string? path = null)
    {
        _path = path ?? DefaultPath();
        _values = Load(_path);
    }

    /// <summary>
    /// 기본 경로: %AppData%\KOTU\settings.json.
    /// A46(v0.86.0) 리브랜딩 — 사용자 결정(2026-08-10)에 따라 구 폴더(%AppData%\ZP,
    /// 그 전 %AppData%\WinUtil)의 설정은 <b>이관하지 않는다</b>. 새 이름으로 기본값부터 시작한다.
    /// (구 폴더는 그대로 남으므로 사용자가 직접 지우거나 되돌릴 수 있다.)
    /// </summary>
    private static string DefaultPath() =>
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "KOTU", "settings.json");

    public T Get<T>(string key, T defaultValue)
    {
        lock (_lock)
        {
            if (!_values.TryGetValue(key, out var el)) return defaultValue;
            try { return el.Deserialize<T>() ?? defaultValue; }
            catch (JsonException) { return defaultValue; }
        }
    }

    public void Set<T>(string key, T value)
    {
        lock (_lock)
        {
            _values[key] = JsonSerializer.SerializeToElement(value);
        }
    }

    public void Save()
    {
        lock (_lock)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            File.WriteAllText(_path, JsonSerializer.Serialize(_values, s_json));
        }
    }

    private static Dictionary<string, JsonElement> Load(string path)
    {
        try
        {
            if (File.Exists(path))
                return JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(
                    File.ReadAllText(path)) ?? [];
        }
        catch (JsonException) { /* 손상된 파일은 초기화 */ }
        return [];
    }
}
