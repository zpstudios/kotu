using System.Text.Json;
using KOTU.Core.Settings;
using Xunit;

namespace KOTU.Module.Audio.Tests;

public sealed class ResumeCompatibilityTests : IDisposable
{
    private readonly string _path = Path.Combine(Path.GetTempPath(), $"kotu-resume-{Guid.NewGuid():N}.json");
    public void Dispose() { if (File.Exists(_path)) File.Delete(_path); }

    [Fact]
    public void Legacy_json_reads_and_writes_without_changing_other_module()
    {
        File.WriteAllText(_path, """
            {"audio.resume":[{"Path":"C:\\song.mp3","PositionMs":60000,"DurationMs":600000,"UpdatedAt":"2026-01-02T03:04:05+09:00"}],
             "video.resume":[{"Path":"C:\\movie.mp4","PositionMs":90000,"DurationMs":600000,"UpdatedAt":"2026-02-03T04:05:06+09:00"}]}
            """);
        var settings = new JsonSettingsService(_path);
        var store = new PlaybackResumeStore(settings);
        var original = settings.Get<List<ResumeEntry>>(PlaybackResumeStore.SettingsKey, []);
        Assert.Single(original);
        Assert.Equal(new DateTimeOffset(2026, 1, 2, 3, 4, 5, TimeSpan.FromHours(9)), original[0].UpdatedAt);
        Assert.Equal(60000L, store.GetResumePositionMs(@"C:\song.mp3"));
        store.Report(@"C:\song.mp3", 120_000, 600_000);
        using var json = JsonDocument.Parse(File.ReadAllText(_path));
        var entry = json.RootElement.GetProperty(PlaybackResumeStore.SettingsKey)[0];
        Assert.Equal(new[] { "DurationMs", "Path", "PositionMs", "UpdatedAt" },
            entry.EnumerateObject().Select(p => p.Name).OrderBy(n => n).ToArray());
        Assert.Equal(90000L, json.RootElement.GetProperty("video.resume")[0].GetProperty("PositionMs").GetInt64());
        var reloaded = new PlaybackResumeStore(new JsonSettingsService(_path));
        Assert.Equal(120_000, reloaded.GetResumePositionMs(@"C:\song.mp3"));
    }

    [Fact]
    public void Exact_thresholds_are_preserved()
    {
        var store = new PlaybackResumeStore(new JsonSettingsService(_path));
        store.Report("a", 29_999, 100_000);
        Assert.Null(store.GetResumePositionMs("a"));
        store.Report("a", 30_000, 100_000);
        Assert.Equal(30_000, store.GetResumePositionMs("a"));
        store.Report("a", 96_999, 100_000);
        Assert.Equal(96_999, store.GetResumePositionMs("a"));
        store.Report("a", 97_000, 100_000);
        Assert.Null(store.GetResumePositionMs("a"));
    }

    [Fact]
    public void Reports_refresh_lru_but_reads_do_not_and_capacity_has_minimum_one()
    {
        var store = new PlaybackResumeStore(new JsonSettingsService(_path), 2);
        store.Report("a", 30_000, 100_000);
        store.Report("b", 30_000, 100_000);
        store.Report("A", 40_000, 100_000);
        Assert.Equal(30_000, store.GetResumePositionMs("b"));
        store.Report("c", 30_000, 100_000);
        Assert.Null(store.GetResumePositionMs("b"));
        Assert.Equal(40_000, store.GetResumePositionMs("a"));
        var single = new PlaybackResumeStore(new JsonSettingsService(_path), 0);
        single.Report("d", 30_000, 100_000);
        Assert.Equal(1, single.Count);
        Assert.Equal(30_000, single.GetResumePositionMs("d"));
    }
}