using WinUtil.Core.Settings;
using Xunit;

namespace WinUtil.Core.Tests;

public class JsonSettingsServiceTests : IDisposable
{
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
