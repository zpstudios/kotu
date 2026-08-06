using System.Text.Json;

namespace WinUtil.Core.Settings;

/// <summary>JSON 파일 기반 설정. 기본 위치: %AppData%\ZP\settings.json (경로 주입 가능 — 테스트용).</summary>
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
    /// 기본 경로: %AppData%\ZP\settings.json. 구 폴더(%AppData%\WinUtil)의 설정이 있고
    /// 새 폴더에 아직 없으면 1회 복사해 이관한다(v0.33.0 리브랜딩 — 볼륨·이어보기 기록 유지).
    /// </summary>
    private static string DefaultPath()
    {
        var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        var newPath = Path.Combine(appData, "ZP", "settings.json");
        try
        {
            var oldPath = Path.Combine(appData, "WinUtil", "settings.json");
            if (!File.Exists(newPath) && File.Exists(oldPath))
            {
                Directory.CreateDirectory(Path.GetDirectoryName(newPath)!);
                File.Copy(oldPath, newPath);
            }
        }
        catch
        {
            // 이관 실패는 새 설정으로 시작하면 그만이다.
        }
        return newPath;
    }

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
