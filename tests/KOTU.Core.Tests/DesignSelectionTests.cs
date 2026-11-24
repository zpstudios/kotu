using KOTU.Core.Appearance;
using KOTU.Core.Settings;
using Xunit;

namespace KOTU.Core.Tests;

public sealed class DesignSelectionTests
{
    [Fact]
    public void CatalogHasStableIdsAndDifferentConstructionRules()
    {
        Assert.Equal(new[] { "original", "precision", "humanist", "solid", "editorial", "terminal", "mechanical", "frame", "paper", "isometric", "pixel", "ink" },
            DesignCatalog.All.Select(d => d.Id));
        Assert.Equal(12, DesignCatalog.All.Select(d => d.Decoration).Distinct().Count());
        Assert.All(DesignCatalog.All, d =>
        {
            Assert.InRange(d.FontSize, 12, 18);
            Assert.InRange(d.IconStrokeWidth, 1, 3);
            Assert.NotEqual(d.Light.Foreground, d.Light.Background);
            Assert.NotEqual(d.Dark.Foreground, d.Dark.Background);
            Assert.Equal(0xffu, d.Light.Accent >> 24);
            Assert.Equal(0xffu, d.Dark.Accent >> 24);
        });
    }

    [Fact]
    public void ApplyPersistsOnlyNewKeyAndNotifiesOtherViewsOnce()
    {
        var settings = new TrackingSettings();
        settings.Set("display.thinControls", true);
        settings.Set("document.font", "Consolas");
        settings.Writes = 0;
        var preferences = new DesignPreferences(settings);
        var observedId = preferences.Current.Id;
        var notifications = 0;
        preferences.Changed += () => { notifications++; observedId = preferences.Current.Id; };
        Assert.True(preferences.Apply("pixel"));
        Assert.False(preferences.Apply("pixel"));
        Assert.Equal("pixel", settings.Get(DesignPreferences.SettingKey, ""));
        Assert.Equal("pixel", observedId);
        Assert.True(settings.Get("display.thinControls", false));
        Assert.Equal("Consolas", settings.Get("document.font", ""));
        Assert.Equal(1, notifications);
        Assert.Equal(1, settings.Writes);
        Assert.Equal(1, settings.Saves);
    }

    public static IEnumerable<object[]> PersistedDesignIds =>
        DesignCatalog.All.Select(design => new object[] { design.Id });

    [Theory]
    [MemberData(nameof(PersistedDesignIds))]
    public async Task EveryDesignRestoresFromJsonAndPreservesUnrelatedSettings(string designId)
    {
        var directory = Path.Combine(Path.GetTempPath(), $"kotu-design-test-{Guid.NewGuid():N}");
        var path = Path.Combine(directory, "settings.json");
        try
        {
            var seed = new JsonSettingsService(path);
            seed.Set("display.thinControls", true);
            seed.Set("document.font", "Consolas");
            await seed.SaveAsync();

            var settings = new JsonSettingsService(path);
            var preferences = new DesignPreferences(settings);
            Assert.True(preferences.Apply(designId));
            await settings.SaveAsync();

            var restoredSettings = new JsonSettingsService(path);
            var restoredPreferences = new DesignPreferences(restoredSettings);
            Assert.Equal(designId, restoredSettings.Get(DesignPreferences.SettingKey, ""));
            Assert.Equal(designId, restoredPreferences.Current.Id);
            Assert.True(restoredSettings.Get("display.thinControls", false));
            Assert.Equal("Consolas", restoredSettings.Get("document.font", ""));
            Assert.False(File.Exists(path + ".tmp"));
        }
        finally
        {
            if (Directory.Exists(directory))
            {
                File.Delete(path);
                File.Delete(path + ".tmp");
                Directory.Delete(directory);
            }
        }
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("future-design")]
    public void MissingOrUnknownSettingFallsBackWithoutWritingUntilExplicitApply(string? saved)
    {
        var settings = new TrackingSettings();
        if (saved is not null) settings.Set(DesignPreferences.SettingKey, saved);
        settings.Writes = 0;
        var preferences = new DesignPreferences(settings);
        Assert.Equal("original", preferences.Current.Id);
        Assert.Equal(0, settings.Writes);
        preferences.Apply("original");
        Assert.Equal("original", settings.Get(DesignPreferences.SettingKey, ""));
        Assert.Equal(1, settings.Saves);
    }

    [Fact]
    public void InvalidChoiceCannotOverwriteSavedDesign()
    {
        var settings = new TrackingSettings();
        var preferences = new DesignPreferences(settings);
        Assert.Throws<ArgumentException>(() => preferences.Apply("missing"));
        Assert.Equal(0, settings.Writes);
    }

    [Fact]
    public void SharedRuntimeBroadcastsOnlyTheCurrentlyBoundPreferenceStore()
    {
        var first = new DesignPreferences(new MemorySettingsService());
        var second = new DesignPreferences(new MemorySettingsService());
        var notifications = 0;
        void Changed() => notifications++;
        DesignRuntime.Initialize(first);
        DesignRuntime.Changed += Changed;
        try
        {
            first.Apply("humanist");
            Assert.Equal("humanist", DesignRuntime.Current.Id);
            DesignRuntime.Initialize(second);
            first.Apply("ink");
            Assert.Equal("original", DesignRuntime.Current.Id);
            second.Apply("terminal");
            Assert.Equal("terminal", DesignRuntime.Current.Id);
            Assert.Equal(3, notifications);
        }
        finally
        {
            DesignRuntime.Changed -= Changed;
            DesignRuntime.Initialize(new DesignPreferences(new MemorySettingsService()));
        }
    }

    [Fact]
    public void StandaloneMemorySettingsStillSupportSessionChanges()
    {
        var session = new MemorySettingsService();
        var preferences = new DesignPreferences(session);
        preferences.Apply("ink");
        Assert.Equal("ink", new DesignPreferences(session).Current.Id);
        Assert.Equal("original", new DesignPreferences(new MemorySettingsService()).Current.Id);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DirectPreSkinUpgradeRetainsOriginalControlsAndDoesNotMigrateSettings(bool thin)
    {
        var directory = Path.Combine(Path.GetTempPath(), $"kotu-design-upgrade-{Guid.NewGuid():N}");
        var path = Path.Combine(directory, "settings.json");
        try
        {
            Directory.CreateDirectory(directory);
            // f74a289 stored a flat JSON object, with the control preference and no design ID.
            // Fixture paths/content are synthetic; the shape and preference names are preserved.
            var before = System.Text.Json.JsonSerializer.Serialize(new Dictionary<string, object>
            {
                ["display.thinControls"] = thin, ["document.font"] = "Consolas",
                ["explorer.folder.v1.F14C636123D40F75F217BF50A2A687E63C92B1366409B10E2E964245655CB449.view"] = "preview",
                ["explorer.lastFolder"] = "C:\\KOTU test\\Documents", ["audio.volume"] = 80,
                ["player.autoNext"] = true, ["video.resume"] = Array.Empty<object>(),
                ["window.width"] = 1946, ["window.height"] = 1226,
            });
            await File.WriteAllTextAsync(path, before);
            var settings = new JsonSettingsService(path);
            var preferences = new DesignPreferences(settings);
            Assert.Equal("original", preferences.Current.Id);
            Assert.Equal(thin, preferences.Current.ThinControls);
            Assert.Equal(thin ? 1 : 1.4, preferences.Current.IconStrokeWidth);
            Assert.Contains(thin ? "Segoe UI Semilight" : "KotuFit.ttf", preferences.Current.FontFamily);
            Assert.Null(settings.Get<string?>(DesignPreferences.SettingKey, null));
            Assert.Equal(before, await File.ReadAllTextAsync(path));

            foreach (var design in DesignCatalog.All.Skip(1))
            {
                Assert.True(preferences.Apply(design.Id));
                await settings.SaveAsync();
                var reopened = new DesignPreferences(new JsonSettingsService(path));
                Assert.Same(design, reopened.Current);
                Assert.False(reopened.Current.ThinControls);
                Assert.Equal(thin, reopened.LegacyThinControls);
            }
            Assert.True(preferences.Apply("original"));
            await settings.SaveAsync();
            var restored = new JsonSettingsService(path);
            var original = new DesignPreferences(restored);
            Assert.Equal("original", original.Current.Id);
            Assert.Equal(thin, original.Current.ThinControls);
            Assert.False(original.Apply("original"));
            using var oldJson = System.Text.Json.JsonDocument.Parse(before);
            using var newJson = System.Text.Json.JsonDocument.Parse(await File.ReadAllTextAsync(path));
            Assert.Equal(oldJson.RootElement.EnumerateObject().Count() + 1, newJson.RootElement.EnumerateObject().Count());
            foreach (var pair in oldJson.RootElement.EnumerateObject())
                Assert.Equal(pair.Value.GetRawText(), newJson.RootElement.GetProperty(pair.Name).GetRawText());
        }
        finally
        {
            File.Delete(path); File.Delete(path + ".tmp");
            if (Directory.Exists(directory)) Directory.Delete(directory);
        }
    }

    [Theory]
    [InlineData(null)]
    [InlineData("invalid")]
    [InlineData("original")]
    public void OriginalFallbackHonorsLegacyThinButDoesNotInventAThirteenthDesign(string? id)
    {
        var settings = new TrackingSettings();
        settings.Set("display.thinControls", true);
        if (id is not null) settings.Set(DesignPreferences.SettingKey, id);
        settings.Writes = 0;
        var preferences = new DesignPreferences(settings);
        Assert.Equal("original", preferences.Current.Id);
        Assert.True(preferences.Current.ThinControls);
        Assert.Equal(0, settings.Writes);
        Assert.Equal(0, settings.Saves);
        Assert.Null(DesignCatalog.Find("original-thin"));
        Assert.Equal(12, DesignCatalog.All.Count);
    }

    private sealed class TrackingSettings : ISettingsService
    {
        private readonly MemorySettingsService _inner = new();
        public int Writes { get; set; }
        public int Saves { get; private set; }
        public string FilePath => "memory";
        public T Get<T>(string key, T defaultValue) => _inner.Get(key, defaultValue);
        public void Set<T>(string key, T value) { Writes++; _inner.Set(key, value); }
        public void Save() => Saves++;
        public Task SaveAsync() { Saves++; return Task.CompletedTask; }
    }
}
