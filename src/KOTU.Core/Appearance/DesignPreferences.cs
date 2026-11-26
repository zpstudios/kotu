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
/// Active native profile and the retained process-wide skin event. With skins inactive,
/// initialization reads only the independent thin-controls setting and never writes or
/// broadcasts the saved skin selection. No path reopens a module, document or player.
/// </summary>
public static class DesignRuntime
{
    // A439: keep the catalog and saved preference available without activating skins.
    // This is a product policy, not a user setting or an implicit settings migration.
    public static bool MultiSkinEnabled => false;
    /// <summary>Tag on preview roots: live appearance traversals must skip this complete subtree.</summary>
    public static object PreviewScopeMarker { get; } = new();
    /// <summary>Native factory icons already own their appearance; skip their generated visual children.</summary>
    public static object IconScopeMarker { get; } = new();
    private static DesignPreferences? s_preferences;
    private static DesignDefinition s_native = DesignCatalog.Default;
    public static DesignDefinition Current => MultiSkinEnabled ? s_preferences?.Current ?? DesignCatalog.Default : s_native;
    public static event Action? Changed;

    public static void Initialize(DesignPreferences preferences)
    {
        ArgumentNullException.ThrowIfNull(preferences);
        if (ReferenceEquals(s_preferences, preferences)) return;
        if (s_preferences is not null) s_preferences.Changed -= OnChanged;
        s_preferences = preferences;
        s_native = DesignCatalog.WithLegacyControls(DesignCatalog.Default, preferences.LegacyThinControls);
        if (!MultiSkinEnabled) return;
        preferences.Changed += OnChanged;
        OnChanged();
    }

    private static void OnChanged() => Changed?.Invoke();
}
