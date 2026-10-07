using KOTU.Core.Settings;
using Xunit;

namespace KOTU.Core.Tests;

public class JsonSettingsServiceTests : IDisposable
{
    [Fact]
    public async Task Slow_write_does_not_block_memory_and_burst_saves_coalesce_in_order()
    {
        using var started = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        var writes = new List<string>();
        var settings = new JsonSettingsService(_path, (_, json) =>
        {
            writes.Add(json);
            if (writes.Count == 1) { started.Set(); Assert.True(release.Wait(TimeSpan.FromSeconds(10))); }
        });
        settings.Set("value", 1);
        var first = settings.SaveAsync();
        Assert.True(started.Wait(TimeSpan.FromSeconds(5)));
        try
        {
            var memory = Task.Run(() =>
            {
                for (var i = 2; i <= 200; i++) settings.Set("value", i);
                return settings.Get("value", 0);
            });
            Assert.Equal(200, await memory.WaitAsync(TimeSpan.FromSeconds(3)));
            var latest = settings.SaveAsync();
            for (var i = 0; i < 100; i++) Assert.Same(latest, settings.SaveAsync());
            Assert.False(latest.IsCompleted);
            release.Set();
            await Task.WhenAll(first, latest).WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(2, writes.Count);
            Assert.Contains("200", writes[1]);
        }
        finally { release.Set(); }
    }

    [Fact]
    public async Task Failed_save_is_observable_and_a_later_flush_retries_latest_values()
    {
        var count = 0;
        var settings = new JsonSettingsService(_path, (_, json) =>
        {
            if (Interlocked.Increment(ref count) == 1) throw new IOException("injected failure");
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            File.WriteAllText(_path, json);
        });
        settings.Set("value", 1);
        await Assert.ThrowsAsync<IOException>(() => settings.SaveAsync());
        settings.Set("value", 2);
        await settings.SaveAsync();
        Assert.Equal(2, new JsonSettingsService(_path).Get("value", 0));
    }

    [Fact]
    public async Task Async_flush_includes_changes_captured_during_module_release()
    {
        var settings = new JsonSettingsService(_path);
        settings.Set("document.zoom", 110);
        var earlier = settings.SaveAsync();
        settings.Set("document.zoom", 125);
        settings.Set("audio.volume", 77);
        await settings.SaveAsync();
        await earlier;
        var restored = new JsonSettingsService(_path);
        Assert.Equal(125, restored.Get("document.zoom", 0));
        Assert.Equal(77, restored.Get("audio.volume", 0));
        Assert.False(File.Exists(_path + ".tmp"));
    }

    private readonly string _path = Path.Combine(
        Path.GetTempPath(), $"winutil-test-{Guid.NewGuid():N}", "settings.json");

    [Fact]
    public void 저장_후_다시_로드하면_값이_유지된다()
    {
        var s1 = new JsonSettingsService(_path);
        s1.Set("video.volume", 80);
        s1.Set("image.lastFolder", @"C:\pics");
        s1.Save();

        var s2 = new JsonSettingsService(_path);
        Assert.Equal(80, s2.Get("video.volume", 0));
        Assert.Equal(@"C:\pics", s2.Get("image.lastFolder", ""));
    }

    [Fact]
    public void 없는_키는_기본값을_돌려준다()
    {
        var s = new JsonSettingsService(_path);
        Assert.Equal(1.0, s.Get("video.speed", 1.0));
    }

    [Fact]
    public void 손상된_설정파일은_초기화하고_계속_동작한다()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        File.WriteAllText(_path, "{ not valid json !!");

        var s = new JsonSettingsService(_path);
        Assert.Equal(42, s.Get("any", 42));
    }

    public void Dispose()
    {
        var dir = Path.GetDirectoryName(_path)!;
        if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
    }
}
