using KOTU.Core.Settings;
using Xunit;

namespace KOTU.Core.Tests;

public sealed class MemorySettingsServiceTests
{
    [Fact]
    public void SettingsAreIsolatedAndSaveCannotCreateAFile()
    {
        var first = new MemorySettingsService();
        var second = new MemorySettingsService();
        first.Set("resume", new[] { 12, 34 });
        first.Save();
        Assert.Equal(new[] { 12, 34 }, first.Get("resume", Array.Empty<int>()));
        Assert.Empty(second.Get("resume", Array.Empty<int>()));
        Assert.False(File.Exists(first.FilePath));
    }

    [Fact]
    public void MutableValuesAreSnapshotsAndIncompatibleTypesUseDefault()
    {
        var settings = new MemorySettingsService();
        var source = new List<string> { "original" };
        settings.Set("value", source);
        source.Add("later");
        var read = settings.Get("value", new List<string>());
        read.Add("changed");
        Assert.Equal(new[] { "original" }, settings.Get("value", new List<string>()));
        Assert.Equal(7, settings.Get("value", 7));
    }

    [Fact]
    public void ConcurrentSessionsAndResumeUpdatesRetainCompleteValues()
    {
        var settings = new MemorySettingsService();
        Parallel.For(0, 1000, i =>
        {
            settings.Set("file." + i, new[] { i, i + 1 });
            settings.Save();
            Assert.Equal(new[] { i, i + 1 }, settings.Get("file." + i, Array.Empty<int>()));
        });
    }
}
