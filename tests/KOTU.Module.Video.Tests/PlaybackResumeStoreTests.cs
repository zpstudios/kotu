using KOTU.Core.Settings;
using Xunit;

namespace KOTU.Module.Video.Tests;

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
    public void 중간까지_본_위치는_저장된다()
    {
        var store = NewStore();
        store.Report(@"C:\v\movie.mp4", 120_000, 3_600_000);

        Assert.Equal(120_000, store.GetResumePositionMs(@"C:\v\movie.mp4"));
    }

    [Fact]
    public void 삼십초_미만_시청은_저장하지_않는다()
    {
        var store = NewStore();
        store.Report(@"C:\v\movie.mp4", 10_000, 3_600_000);

        Assert.Null(store.GetResumePositionMs(@"C:\v\movie.mp4"));
    }

    [Fact]
    public void 거의_끝까지_보면_기록이_지워진다()
    {
        var store = NewStore();
        store.Report(@"C:\v\movie.mp4", 120_000, 3_600_000);
        store.Report(@"C:\v\movie.mp4", 3_550_000, 3_600_000); // 98.6% 지점

        Assert.Null(store.GetResumePositionMs(@"C:\v\movie.mp4"));
    }

    [Fact]
    public void 길이를_모르면_무시한다()
    {
        var store = NewStore();
        store.Report(@"C:\v\movie.mp4", 120_000, 0);

        Assert.Equal(0, store.Count);
    }

    [Fact]
    public void 경로_비교는_대소문자를_무시한다()
    {
        var store = NewStore();
        store.Report(@"C:\v\Movie.MP4", 120_000, 3_600_000);

        Assert.Equal(120_000, store.GetResumePositionMs(@"c:\V\movie.mp4"));
    }

    [Fact]
    public void 같은_파일은_최신_위치로_덮어쓴다()
    {
        var store = NewStore();
        store.Report(@"C:\v\movie.mp4", 120_000, 3_600_000);
        store.Report(@"C:\v\movie.mp4", 240_000, 3_600_000);

        Assert.Equal(240_000, store.GetResumePositionMs(@"C:\v\movie.mp4"));
        Assert.Equal(1, store.Count);
    }

    [Fact]
    public void 용량_초과_시_오래된_기록부터_버린다()
    {
        var store = NewStore(capacity: 2);
        store.Report(@"C:\v\a.mp4", 60_000, 3_600_000);
        store.Report(@"C:\v\b.mp4", 60_000, 3_600_000);
        store.Report(@"C:\v\c.mp4", 60_000, 3_600_000);

        Assert.Null(store.GetResumePositionMs(@"C:\v\a.mp4"));
        Assert.NotNull(store.GetResumePositionMs(@"C:\v\b.mp4"));
        Assert.NotNull(store.GetResumePositionMs(@"C:\v\c.mp4"));
    }

    [Fact]
    public void Clear는_해당_파일_기록만_지운다()
    {
        var store = NewStore();
        store.Report(@"C:\v\a.mp4", 60_000, 3_600_000);
        store.Report(@"C:\v\b.mp4", 60_000, 3_600_000);
        store.Clear(@"C:\v\a.mp4");

        Assert.Null(store.GetResumePositionMs(@"C:\v\a.mp4"));
        Assert.Equal(60_000, store.GetResumePositionMs(@"C:\v\b.mp4"));
    }

    [Fact]
    public void 설정_파일을_거쳐_재시작해도_기록이_유지된다()
    {
        NewStore().Report(@"C:\v\movie.mp4", 120_000, 3_600_000);

        // 새 설정 서비스 + 새 스토어 = 앱 재시작 시뮬레이션
        var reloaded = NewStore();
        Assert.Equal(120_000, reloaded.GetResumePositionMs(@"C:\v\movie.mp4"));
    }
}
