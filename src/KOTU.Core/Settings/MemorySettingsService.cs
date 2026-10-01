using System.Text.Json;

namespace KOTU.Core.Settings;

/// <summary>Thread-safe session settings with the same serialization semantics as JSON settings.</summary>
public sealed class MemorySettingsService : ISettingsService
{
    private readonly Dictionary<string, JsonElement> _values = [];
    private readonly object _gate = new();
    public string FilePath => "In memory only (standalone build)";

    public T Get<T>(string key, T defaultValue)
    {
        lock (_gate)
        {
            if (!_values.TryGetValue(key, out var value)) return defaultValue;
            try { return value.Deserialize<T>() ?? defaultValue; }
            catch (JsonException) { return defaultValue; }
        }
    }

    public void Set<T>(string key, T value)
    {
        lock (_gate) _values[key] = JsonSerializer.SerializeToElement(value);
    }

    public void Save() { }
}
