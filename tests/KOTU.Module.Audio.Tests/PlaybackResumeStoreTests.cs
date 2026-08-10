using KOTU.Core.Settings;
using Xunit;

namespace KOTU.Module.Audio.Tests;

public class PlaybackResumeStoreTests : IDisposable
{
    private readonly string _settingsPath =
        Path.Combine(Path.GetTempPath(), $"winutil-test-{Guid.NewGuid():N}.json");

    private PlaybackResumeStore NewStore(int capacity = 300) =>
        new(new JsonSettingsService(_settingsPath), capacity);

    public void Dispose()
    {
        if (File.Exists(_settingsPath)) File.Delete(_settingsPath);
    }

    [Fact]
    public void 저장_키는_비디오와_분리돼_있다() =>
        Assert.Equal("audio.resume", PlaybackResumeStore.SettingsKey);

    [Fact]
    public void 중간까지_들은_위치는_저장된다()
    {
        var store = NewStore();
        store.Report(@"C:\m\song.mp3", 120_000, 3_600_000);

        Assert.Equal(120_000, store.GetResumePositionMs(@"C:\m\song.mp3"));
    }

    [Fact]
    public void 삼십초_미만_청취는_저장하지_않는다()
    {
        var store = NewStore();
        store.Report(@"C:\m\song.mp3", 10_000, 3_600_000);

        Assert.Null(store.GetResumePositionMs(@"C:\m\song.mp3"));
    }

    [Fact]
    public void 거의_끝까지_들으면_기록이_지워진다()
    {
        var store = NewStore();
        store.Report(@"C:\m\song.mp3", 120_000, 3_600_000);
        store.Report(@"C:\m\song.mp3", 3_550_000, 3_600_000); // 98.6% 지점

        Assert.Null(store.GetResumePositionMs(@"C:\m\song.mp3"));
    }

    [Fact]
    public void 길이를_모르면_무시한다()
    {
        var store = NewStore();
        store.Report(@"C:\m\song.mp3", 120_000, 0);

        Assert.Equal(0, store.Count);
    }

    [Fact]
    public void 경로_비교는_대소문자를_무시한다()
    {
        var store = NewStore();
        store.Report(@"C:\m\Song.MP3", 120_000, 3_600_000);

        Assert.Equal(120_000, store.GetResumePositionMs(@"c:\M\song.mp3"));
    }

    [Fact]
    public void 같은_파일은_최신_위치로_덮어쓴다()
    {
        var store = NewStore();
        store.Report(@"C:\m\song.mp3", 120_000, 3_600_000);
        store.Report(@"C:\m\song.mp3", 240_000, 3_600_000);

        Assert.Equal(240_000, store.GetResumePositionMs(@"C:\m\song.mp3"));
        Assert.Equal(1, store.Count);
    }

    [Fact]
    public void 용량_초과_시_오래된_기록부터_버린다()
    {
        var store = NewStore(capacity: 2);
        store.Report(@"C:\m\a.mp3", 60_000, 3_600_000);
        store.Report(@"C:\m\b.mp3", 60_000, 3_600_000);
        store.Report(@"C:\m\c.mp3", 60_000, 3_600_000);

        Assert.Null(store.GetResumePositionMs(@"C:\m\a.mp3"));
        Assert.NotNull(store.GetResumePositionMs(@"C:\m\b.mp3"));
        Assert.NotNull(store.GetResumePositionMs(@"C:\m\c.mp3"));
    }

    [Fact]
    public void Clear는_해당_파일_기록만_지운다()
    {
        var store = NewStore();
        store.Report(@"C:\m\a.mp3", 60_000, 3_600_000);
        store.Report(@"C:\m\b.mp3", 60_000, 3_600_000);
        store.Clear(@"C:\m\a.mp3");

        Assert.Null(store.GetResumePositionMs(@"C:\m\a.mp3"));
        Assert.Equal(60_000, store.GetResumePositionMs(@"C:\m\b.mp3"));
    }

    [Fact]
    public void 설정_파일을_거쳐_재시작해도_기록이_유지된다()
    {
        NewStore().Report(@"C:\m\song.mp3", 120_000, 3_600_000);

        // 새 설정 서비스 + 새 스토어 = 앱 재시작 시뮬레이션
        var reloaded = NewStore();
        Assert.Equal(120_000, reloaded.GetResumePositionMs(@"C:\m\song.mp3"));
    }

    [Fact]
    public void 확장자_목록에_음악_확장자만_있다()
    {
        Assert.Contains(".mp3", AudioModule.Extensions);
        Assert.Contains(".flac", AudioModule.Extensions);
        Assert.DoesNotContain(".mp4", AudioModule.Extensions);
        Assert.DoesNotContain(".mkv", AudioModule.Extensions);
    }
}
