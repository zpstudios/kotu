using KOTU.Core.Appearance;
using KOTU.Core.Settings;
using Xunit;

namespace KOTU.Core.Tests;

public sealed class DesignSelectionTests
{
    [Fact]
    public void CatalogHasStableIdsAndDifferentConstructionRules()
    {
        Assert.Equal(new[] { "precision", "humanist", "solid", "editorial", "terminal", "mechanical", "frame", "paper", "isometric", "pixel", "ink" },
            DesignCatalog.All.Select(d => d.Id));
        Assert.Equal(11, DesignCatalog.All.Select(d => d.Decoration).Distinct().Count());
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
    public void BrowsingAndChangingViewsNeverAppliesOrPersists()
    {
        var settings = new TrackingSettings();
        var preferences = new DesignPreferences(settings);
        var selection = new DesignSelection(preferences.Current.Id);
        selection.Move(-1);
        Assert.Equal("ink", selection.Preview.Id);
        selection.Mode = DesignPickerMode.Gallery;
        selection.Select("paper");
        selection.Mode = DesignPickerMode.Single;
        Assert.Equal("paper", selection.Preview.Id);
        Assert.Equal("precision", preferences.Current.Id);
        Assert.True(selection.HasPendingChange);
        Assert.Equal(0, settings.Writes);
        Assert.Equal(0, settings.Saves);
    }

    [Fact]
    public void ApplyPersistsOnlyNewKeyAndNotifiesOtherViewsOnce()
    {
        var settings = new TrackingSettings();
        settings.Set("display.thinControls", true);
        settings.Set("document.font", "Consolas");
        settings.Writes = 0;
        var preferences = new DesignPreferences(settings);
        var first = new DesignSelection(preferences.Current.Id);
        var second = new DesignSelection(preferences.Current.Id);
        var notifications = 0;
        preferences.Changed += () => { notifications++; second.SynchronizeApplied(preferences.Current.Id); };
        first.Select("pixel");
        first.Apply(preferences);
        first.Apply(preferences);
        Assert.Equal("pixel", settings.Get(DesignPreferences.SettingKey, ""));
        Assert.Equal("pixel", second.Preview.Id);
        Assert.False(first.HasPendingChange);
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

    [Fact]
    public void OtherWindowApplyPreservesLocalUnappliedPreview()
    {
        var selection = new DesignSelection("precision");
        selection.Select("terminal");
        selection.SynchronizeApplied("humanist");
        Assert.Equal("terminal", selection.Preview.Id);
        Assert.Equal("humanist", selection.Applied.Id);
        Assert.True(selection.HasPendingChange);
        selection.SynchronizeApplied("terminal");
        Assert.False(selection.HasPendingChange);
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
        Assert.Equal("precision", preferences.Current.Id);
        Assert.Equal(0, settings.Writes);
        preferences.Apply("precision");
        Assert.Equal("precision", settings.Get(DesignPreferences.SettingKey, ""));
        Assert.Equal(1, settings.Saves);
    }

    [Fact]
    public void InvalidChoiceCannotOverwriteSavedDesign()
    {
        var settings = new TrackingSettings();
        var preferences = new DesignPreferences(settings);
        Assert.Throws<ArgumentException>(() => preferences.Apply("missing"));
        Assert.Throws<ArgumentException>(() => new DesignSelection("precision").Select("missing"));
        Assert.Equal(0, settings.Writes);
    }

    [Fact]
    public void NavigationWrapsInBothDirectionsIncludingLargeOffsets()
    {
        var selection = new DesignSelection("ink");
        selection.Move(1);
        Assert.Equal("precision", selection.Preview.Id);
        selection.Move(-1);
        Assert.Equal("ink", selection.Preview.Id);
        selection.Move(int.MaxValue);
        Assert.InRange(selection.PreviewIndex, 0, 10);
        selection.Move(int.MinValue);
        Assert.Equal("pixel", selection.Preview.Id);
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
            Assert.Equal("precision", DesignRuntime.Current.Id);
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
        Assert.Equal("precision", new DesignPreferences(new MemorySettingsService()).Current.Id);
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
