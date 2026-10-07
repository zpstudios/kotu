using System.Text.Json;
using KOTU.Core.Threading;

namespace KOTU.Core.Settings;

/// <summary>JSON 파일 기반 설정. 기본 위치: %AppData%\KOTU\settings.json (경로 주입 가능 — 테스트용).</summary>
public sealed class JsonSettingsService : ISettingsService
{
    private static readonly JsonSerializerOptions s_json = new() { WriteIndented = true };
    private readonly string _path;
    private readonly Dictionary<string, JsonElement> _values;
    private readonly object _lock = new();
    private readonly Action<string, string> _write;
    private TaskCompletionSource? _pendingSave;
    private bool _saving;

    public JsonSettingsService(string? path = null) : this(path, WriteFile) { }

    internal JsonSettingsService(string? path, Action<string, string> write)
    {
        if (KOTU.Core.Integration.DistributionPolicy.IsStandalone)
            throw new InvalidOperationException("Persistent settings are disabled in the standalone build.");
        _path = path ?? DefaultPath();
        _values = Load(_path);
        _write = write;
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

    /// <summary>실제 저장 경로 (A36) — 설정 화면 표기·직접 열기에 쓴다.</summary>
    public string FilePath => _path;

    public T Get<T>(string key, T defaultValue)
    {
        JsonElement el;
        lock (_lock)
            if (!_values.TryGetValue(key, out el)) return defaultValue;
        try { return el.Deserialize<T>() ?? defaultValue; }
        catch (JsonException) { return defaultValue; }
    }

    public void Set<T>(string key, T value)
    {
        var serialized = JsonSerializer.SerializeToElement(value);
        lock (_lock) _values[key] = serialized;
    }

    // 녹화 폴더 등 기존 워커 호출자는 실패를 직접 받아야 한다. UI 호출은 금지.
    public void Save() => SaveAsync().GetAwaiter().GetResult();

    public Task SaveAsync()
    {
        lock (_lock)
        {
            _pendingSave ??= new(TaskCreationOptions.RunContinuationsAsynchronously);
            var task = _pendingSave.Task;
            if (!_saving)
            {
                _saving = true;
                _ = DrainAsync();
            }
            return task;
        }
    }

    private async Task DrainAsync()
    {
        // 버스트마다 워커 하나. 실행 중 스냅샷 + 최신 대기 배치 하나만 유지한다.
        // 스냅샷은 실행 순서대로 취득하므로 오래된 값이 뒤늦게 파일을 덮지 않는다.
        using var worker = new ModuleWorker("KOTU settings persistence", ThreadPriority.BelowNormal);
        await worker.Run(_ =>
        {
            while (true)
            {
                TaskCompletionSource completion;
                Dictionary<string, JsonElement> snapshot;
                lock (_lock)
                {
                    if (_pendingSave is null) { _saving = false; return; }
                    completion = _pendingSave;
                    _pendingSave = null;
                    snapshot = new(_values);
                }
                try
                {
                    _write(_path, JsonSerializer.Serialize(snapshot, s_json));
                    completion.TrySetResult();
                }
                catch (Exception ex) { completion.TrySetException(ex); }
            }
        }).ConfigureAwait(false);
    }

    private static void WriteFile(string path, string json)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temp = path + ".tmp";
        try
        {
            File.WriteAllText(temp, json);
            File.Move(temp, path, overwrite: true);
        }
        finally { if (File.Exists(temp)) File.Delete(temp); }
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
