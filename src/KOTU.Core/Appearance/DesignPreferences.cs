using KOTU.Core.Settings;

namespace KOTU.Core.Appearance;

/// <summary>Explicit application is the only operation that writes display.design.</summary>
public sealed class DesignPreferences(ISettingsService settings)
{
    public const string SettingKey = "display.design";
    private readonly object _gate = new();
    private DesignDefinition _current = DesignCatalog.Resolve(settings.Get<string?>(SettingKey, null), settings.Get("display.thinControls", false));
    public bool LegacyThinControls { get; } = settings.Get("display.thinControls", false);
    public DesignDefinition Current => Volatile.Read(ref _current);
    public event Action? Changed;

    public bool Apply(string id)
    {
        var design = DesignCatalog.WithLegacyControls(
            DesignCatalog.Find(id) ?? throw new ArgumentException("Unknown interface design.", nameof(id)), LegacyThinControls);
        lock (_gate)
        {
            if (settings.Get<string?>(SettingKey, null) == id) return false;
            // Keep legacy display.thinControls and every unrelated preference untouched.
            settings.Set(SettingKey, id);
            Volatile.Write(ref _current, design);
        }
        Changed?.Invoke();
        settings.RequestSave();
        return true;
    }
}

/// <summary>
/// One process-wide event in Core, not a copy in each assembly's linked Shared sources.
/// Subscribers read Current on their dispatcher, subscribe on Loaded and detach on Unloaded.
/// A change updates visuals in place; it must never reopen a module, document or player.
/// </summary>
public static class DesignRuntime
{
    /// <summary>Tag on preview roots: live appearance traversals must skip this complete subtree.</summary>
    public static object PreviewScopeMarker { get; } = new();
    /// <summary>Native factory icons already own their appearance; skip their generated visual children.</summary>
    public static object IconScopeMarker { get; } = new();
    private static DesignPreferences? s_preferences;
    public static DesignDefinition Current => s_preferences?.Current ?? DesignCatalog.Default;
    public static event Action? Changed;

    public static void Initialize(DesignPreferences preferences)
    {
        ArgumentNullException.ThrowIfNull(preferences);
        if (ReferenceEquals(s_preferences, preferences)) return;
        if (s_preferences is not null) s_preferences.Changed -= OnChanged;
        s_preferences = preferences;
        preferences.Changed += OnChanged;
        OnChanged();
    }

    private static void OnChanged() => Changed?.Invoke();
}
