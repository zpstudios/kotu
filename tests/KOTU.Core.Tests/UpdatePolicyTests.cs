using KOTU.Core.Content;
using KOTU.Core.Updates;
using Xunit;

namespace KOTU.Core.Tests;

public sealed class UpdatePolicyTests
{
    [Theory]
    [InlineData("0.437.0", "0.438.1", true)]
    [InlineData("0.438.0", "0.438.1", false)]
    [InlineData("0.438.0", "0.438.0", false)]
    [InlineData("0.438.0", "0.437.99", false)]
    [InlineData("0.438.0", "1.0.0", false)]
    [InlineData("0.438.0", "1.439.0", true)]
    [InlineData("1.438.0", "0.439.0", false)]
    public void Prompt_requires_newer_stable_and_larger_middle_component(string installed, string target, bool expected)
    {
        Assert.True(UpdateVersion.TryParse(installed, out var current));
        Assert.True(UpdateVersion.TryParse(target, out var candidate));
        Assert.Equal(expected, candidate.ShouldPromptOver(current));
    }

    [Theory]
    [InlineData(null)] [InlineData("")] [InlineData("0.438")] [InlineData("0.438.0.0")]
    [InlineData("0.438.0-beta")] [InlineData("0.438.0+build")] [InlineData("00.438.0")]
    [InlineData("0.-438.0")] [InlineData("0.2147483648.0")] [InlineData(" 0.438.0")]
    [InlineData("0.４３８.0")] [InlineData("../438/0")]
    public void Invalid_or_prerelease_versions_cannot_be_marker_names(string? text)
        => Assert.False(UpdateVersion.TryParse(text, out _));

    [Fact]
    public void Installed_assembly_revision_is_not_the_release_patch()
        => Assert.Equal(new UpdateVersion(0, 438, 0), UpdateVersion.FromInstalled(new Version("0.438.0.0")));

    [Fact]
    public void First_check_is_immediate_then_completion_starts_the_24_hour_interval()
    {
        var clock = new Clock();
        using var schedule = new UpdateCheckSchedule(clock);
        Assert.True(schedule.TryBegin());
        clock.Advance(TimeSpan.FromHours(30));
        Assert.False(schedule.TryBegin()); // 오래 걸린 확인과 겹치지 않는다.
        Assert.True(schedule.Complete());
        Assert.Equal(UpdateCheckSchedule.Interval, schedule.Remaining);
        clock.Advance(TimeSpan.FromHours(24) - TimeSpan.FromTicks(1));
        Assert.False(schedule.TryBegin());
        clock.Advance(TimeSpan.FromTicks(1));
        Assert.True(schedule.TryBegin());
        Assert.True(schedule.Complete()); // 실패도 이 완료 경로를 쓴다.
        Assert.False(schedule.TryBegin());
        using var restarted = new UpdateCheckSchedule(clock);
        Assert.True(restarted.TryBegin()); // 저장 시각과 무관한 새 앱 수명.
    }

    [Fact]
    public void Wall_clock_rewind_does_not_delay_and_sleep_is_counted()
    {
        var clock = new Clock();
        using var schedule = new UpdateCheckSchedule(clock);
        Assert.True(schedule.TryBegin()); schedule.Complete();
        clock.Advance(TimeSpan.FromHours(24), TimeSpan.FromHours(-3));
        Assert.True(schedule.TryBegin()); schedule.Complete();
        clock.Advance(TimeSpan.Zero, TimeSpan.FromHours(24));
        Assert.True(schedule.TryBegin());
    }

    [Fact]
    public void Dispose_rejects_late_completion_and_future_checks()
    {
        var schedule = new UpdateCheckSchedule();
        Assert.True(schedule.TryBegin());
        schedule.Dispose();
        Assert.False(schedule.Complete());
        Assert.Null(schedule.CompletedAt);
        Assert.False(schedule.IsChecking);
        Assert.False(schedule.TryBegin());
    }

    [Fact]
    public async Task Refusal_is_exact_durable_across_instances_and_parallel_creates()
    {
        using var directory = new TempDirectory();
        var version = new UpdateVersion(0, 438, 0);
        var store = new UpdateStateStore(directory.Path);
        Assert.False(store.IsDeclined(version));
        await Task.WhenAll(Enumerable.Range(0, 12).Select(_ => Task.Run(() => new UpdateStateStore(directory.Path).Decline(version))));
        Assert.True(new UpdateStateStore(directory.Path).IsDeclined(version));
        Assert.False(store.IsDeclined(new(0, 438, 1)));
        Assert.True(new UpdateVersion(0, 438, 1).ShouldPromptOver(new(0, 437, 0)));
        File.WriteAllBytes(System.IO.Path.Combine(directory.Path, "declined", "0.439.0.declined"), []);
        Assert.True(store.IsDeclined(new(0, 439, 0))); // 빈 표식도 영구 거절이다.
    }

    [Fact]
    public void Store_failure_is_not_reported_as_a_missing_refusal()
    {
        using var directory = new TempDirectory();
        File.WriteAllText(System.IO.Path.Combine(directory.Path, "declined"), "not a directory");
        var store = new UpdateStateStore(directory.Path);
        Assert.ThrowsAny<IOException>(() => store.Decline(new(0, 438, 0)));
        Assert.ThrowsAny<IOException>(() => store.IsDeclined(new(0, 438, 0)));
        // 잠긴 표식은 읽을 수 없어도 존재한다. 접근 불가를 부재로 변환하는 File.Exists는 쓰지 않는다.
        using var second = new TempDirectory();
        var other = new UpdateStateStore(second.Path);
        other.Decline(new(0, 438, 0));
        using var locked = File.Open(System.IO.Path.Combine(second.Path, "declined", "0.438.0.declined"), FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        Assert.True(other.IsDeclined(new(0, 438, 0)));
        Assert.ThrowsAny<IOException>(() => other.Decline(new(0, 438, 0)));
    }

    [Fact]
    public void File_lease_excludes_other_owner_and_allows_takeover_after_release()
    {
        using var directory = new TempDirectory();
        var first = new UpdateStateStore(directory.Path);
        var second = new UpdateStateStore(directory.Path);
        var owner = first.TryAcquireLease("automatic");
        Assert.NotNull(owner);
        Assert.Null(second.TryAcquireLease("automatic"));
        using var operation = second.TryAcquireLease("operation");
        Assert.NotNull(operation);
        Assert.Null(first.TryAcquireLease("operation"));
        owner.Dispose();
        using var takeover = second.TryAcquireLease("automatic");
        Assert.NotNull(takeover);
    }

    [Fact]
    public void Unreadable_refusal_is_not_treated_as_absent()
    {
        using var directory = new TempDirectory();
        Directory.CreateDirectory(System.IO.Path.Combine(directory.Path, "declined"));
        var store = new UpdateStateStore(directory.Path, path => path.EndsWith(".declined", StringComparison.Ordinal)
            ? throw new UnauthorizedAccessException("denied") : File.GetAttributes(path));
        Assert.Throws<UnauthorizedAccessException>(() => store.IsDeclined(new(0, 438, 0)));
    }

    [Fact]
    public async Task Cancelled_window_rolls_back_preceding_windows_in_reverse_order()
    {
        var rolledBack = new List<int>();
        var result = await UpdateRestartTransaction.PrepareAsync([
            () => Task.FromResult<IAsyncDisposable?>(Lease(1)),
            () => Task.FromResult<IAsyncDisposable?>(Lease(2)),
            () => Task.FromResult<IAsyncDisposable?>(null)]);
        Assert.Null(result);
        Assert.Equal(new[] { 2, 1 }, rolledBack);
        ContentCloseLease Lease(int id) => new(() => { rolledBack.Add(id); return Task.CompletedTask; });
    }

    [Theory]
    [InlineData("flush")] [InlineData("apply")]
    public async Task Failed_install_phase_releases_every_approved_window_once(string phase)
    {
        var released = 0;
        var transaction = await UpdateRestartTransaction.PrepareAsync([
            () => Task.FromResult<IAsyncDisposable?>(new ContentCloseLease(() => { released++; return Task.CompletedTask; }))]);
        await Assert.ThrowsAsync<IOException>(async () =>
        {
            await using (transaction!) { await Task.Yield(); throw new IOException(phase); }
        });
        await transaction!.DisposeAsync();
        Assert.Equal(1, released);
    }

    [Fact]
    public async Task Failed_rollback_still_releases_remaining_windows()
    {
        var released = false;
        var transaction = await UpdateRestartTransaction.PrepareAsync([
            () => Task.FromResult<IAsyncDisposable?>(new ContentCloseLease(() => { released = true; return Task.CompletedTask; })),
            () => Task.FromResult<IAsyncDisposable?>(new ContentCloseLease(() => throw new IOException()))]);
        await Assert.ThrowsAsync<AggregateException>(() => transaction!.DisposeAsync().AsTask());
        Assert.True(released);
    }

    private sealed class Clock : TimeProvider
    {
        private long _ticks;
        private DateTimeOffset _utc = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override long GetTimestamp() => _ticks;
        public override DateTimeOffset GetUtcNow() => _utc;
        public void Advance(TimeSpan elapsed, TimeSpan? wall = null) { _ticks += elapsed.Ticks; _utc += wall ?? elapsed; }
    }

    private sealed class TempDirectory : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "KOTU-update-tests-" + Guid.NewGuid().ToString("N"));
        public TempDirectory() => Directory.CreateDirectory(Path);
        public void Dispose() => Directory.Delete(Path, recursive: true);
    }
}
