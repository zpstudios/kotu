using KOTU.Core.Threading;
using Xunit;

namespace KOTU.Core.Tests;

public class ThumbnailRetryTests
{
    private sealed class Clock : TimeProvider
    {
        private DateTimeOffset _now = DateTimeOffset.UnixEpoch;
        public override DateTimeOffset GetUtcNow() => _now;
        public void Advance(int seconds) => _now += TimeSpan.FromSeconds(seconds);
    }
    private sealed class Scene
    {
        public readonly Clock Clock = new();
        public readonly ThumbnailRetryRegistry Registry;
        public ThumbnailRetryCandidate Candidate;
        public int Calls, Automatic;
        public Scene() { Registry = new(clock: Clock); Candidate = NewVm(); }
        public ThumbnailRetryCandidate NewVm() => new(Registry, "C:\\test.pdf", Clock);
        public void Initial(ThumbnailOutcome outcome, bool audioFailed = false)
        {
            using var attempt = Registry.Begin("C:\\test.pdf", false);
            Calls++;
            attempt?.Complete(outcome, audioFailed);
            Candidate.Observe(outcome, audioFailed);
        }
        public bool Run(ThumbnailOutcome outcome, bool visible = true, bool editing = false, bool current = true)
        {
            if (Candidate.Remaining(visible, editing, current) != TimeSpan.Zero) return false;
            using var attempt = Registry.Begin("c:\\TEST.pdf", true);
            if (attempt is null) return false;
            Calls++;
            Automatic++;
            attempt.Complete(outcome);
            Candidate.Observe(outcome);
            return true;
        }
    }

    [Theory]
    [InlineData(ThumbnailOutcome.CacheMiss)]
    [InlineData(ThumbnailOutcome.IconFallback)]
    public void Completed_miss_recovers_without_entry_or_file_event(ThumbnailOutcome miss)
    {
        var scene = new Scene();
        scene.Initial(miss);
        scene.Clock.Advance(1);
        Assert.False(scene.Run(ThumbnailOutcome.Available));
        scene.Clock.Advance(1);
        Assert.True(scene.Run(ThumbnailOutcome.Available));
        scene.Clock.Advance(100);
        Assert.False(scene.Run(ThumbnailOutcome.Available));
        Assert.Equal((2, 1), (scene.Calls, scene.Automatic));
    }

    [Fact]
    public void Persistent_miss_is_exactly_three_requests_with_two_and_eight_second_delays()
    {
        var scene = new Scene();
        scene.Initial(ThumbnailOutcome.CacheMiss);
        scene.Clock.Advance(2);
        Assert.True(scene.Run(ThumbnailOutcome.CacheMiss));
        scene.Clock.Advance(7);
        Assert.False(scene.Run(ThumbnailOutcome.CacheMiss));
        scene.Clock.Advance(1);
        Assert.True(scene.Run(ThumbnailOutcome.CacheMiss));
        scene.Clock.Advance(100);
        Assert.False(scene.Run(ThumbnailOutcome.CacheMiss));
        Assert.Equal((3, 2), (scene.Calls, scene.Automatic));
    }

    [Theory]
    [InlineData(ThumbnailOutcome.InvalidOrOversized)]
    [InlineData(ThumbnailOutcome.AccessDeniedOrMissing)]
    [InlineData(ThumbnailOutcome.OtherFailure)]
    [InlineData(ThumbnailOutcome.Timeout)]
    [InlineData(ThumbnailOutcome.Cancelled)]
    [InlineData(ThumbnailOutcome.Available)]
    public void Terminal_results_do_not_schedule(ThumbnailOutcome outcome)
    {
        var scene = new Scene();
        scene.Initial(outcome);
        scene.Clock.Advance(100);
        Assert.False(scene.Run(ThumbnailOutcome.Available));
        Assert.Equal(1, scene.Calls);
    }

    [Theory]
    [InlineData(ThumbnailOutcome.Timeout, false)]
    [InlineData(ThumbnailOutcome.Cancelled, false)]
    [InlineData(ThumbnailOutcome.CacheMiss, true)]
    public void Native_hazard_is_not_cleared_by_success_refresh_or_replacement(ThumbnailOutcome outcome, bool audioFailed)
    {
        var scene = new Scene();
        scene.Initial(outcome, audioFailed);
        for (var generation = 0; generation < 100; generation++)
        {
            scene.Candidate = scene.NewVm();
            scene.Initial(ThumbnailOutcome.Available);
            scene.Initial(ThumbnailOutcome.CacheMiss);
            scene.Clock.Advance(100);
            Assert.False(scene.Run(ThumbnailOutcome.Available));
        }
        Assert.True(scene.Registry.Inspect("c:\\TEST.pdf")!.Value.Blocked);
        Assert.Equal(0, scene.Automatic);
    }

    [Fact]
    public void Offscreen_and_editing_pause_without_consuming_or_blocking()
    {
        var scene = new Scene();
        scene.Initial(ThumbnailOutcome.CacheMiss);
        scene.Clock.Advance(10);
        Assert.False(scene.Run(ThumbnailOutcome.Available, visible: false));
        Assert.False(scene.Run(ThumbnailOutcome.Available, editing: true));
        Assert.Equal((0, 0, false), scene.Registry.Inspect("C:\\test.pdf"));
        Assert.True(scene.Run(ThumbnailOutcome.Available));
    }

    [Fact]
    public void One_hundred_replacement_generations_share_two_automatic_attempts()
    {
        var scene = new Scene();
        for (var generation = 0; generation < 100; generation++)
        {
            scene.Candidate = scene.NewVm();
            scene.Initial(ThumbnailOutcome.CacheMiss);
            scene.Clock.Advance(10);
            scene.Run(ThumbnailOutcome.CacheMiss);
        }
        Assert.Equal(2, scene.Automatic);
        Assert.Equal(102, scene.Calls);
    }

    [Fact]
    public void Two_surfaces_cannot_overlap_automatic_attempts_and_late_cancel_blocks_both()
    {
        var scene = new Scene();
        scene.Initial(ThumbnailOutcome.CacheMiss);
        var other = scene.NewVm();
        other.Observe(ThumbnailOutcome.CacheMiss);
        scene.Clock.Advance(2);
        using var active = scene.Registry.Begin("C:\\test.pdf", true);
        Assert.NotNull(active);
        Assert.Null(scene.Registry.Begin("c:\\TEST.pdf", true));
        // 이전 표면이 내려간 뒤에도 활성 lease가 살아 있다. 늦은 취소만으로 새 VM을 막는다.
        scene.Candidate.Remaining(true, false, false);
        active.Complete(ThumbnailOutcome.Cancelled);
        Assert.Null(other.Remaining(true, false, true));
        Assert.Null(scene.Registry.Begin("c:\\test.pdf", true));
    }

    [Fact]
    public void Registry_capacity_does_not_evict_or_refund_and_ordinary_requests_remain_possible()
    {
        var clock = new Clock();
        var registry = new ThumbnailRetryRegistry(2, clock);
        registry.Begin("a", false)!.Complete(ThumbnailOutcome.CacheMiss);
        registry.Begin("b", false)!.Complete(ThumbnailOutcome.CacheMiss);
        Assert.Null(registry.Begin("c", false)); // 호출자는 일반 제공자를 계속 실행한다.
        Assert.Null(registry.Begin("c", true));
        clock.Advance(2);
        registry.Begin("a", true)!.Complete(ThumbnailOutcome.CacheMiss);
        clock.Advance(8);
        registry.Begin("a", true)!.Complete(ThumbnailOutcome.Available);
        for (var i = 0; i < 100; i++) Assert.Null(registry.Begin("new" + i, false));
        Assert.Null(registry.Begin("A", true));
        Assert.Equal(2, registry.Inspect("a")!.Value.Used);
    }

    [Theory]
    [InlineData(true, true, true, true, false)]
    [InlineData(true, false, true, true, false)]
    [InlineData(false, false, true, true, false)]
    [InlineData(false, true, true, false, true)]
    [InlineData(false, true, false, false, false)]
    public void Automatic_access_is_always_cached_and_never_audio(bool automatic, bool readable, bool audio,
        bool cached, bool readAudio) =>
        Assert.Equal(new ThumbnailAccessPlan(cached, readAudio), ThumbnailAccessPlan.Create(automatic, readable, audio));

    [Fact]
    public void Closed_timer_generation_cannot_claim_or_release_new_generation_queue()
    {
        var old = new ThumbnailRetryWakeup();
        Assert.True(old.TryQueue());
        old.Close();
        var current = new ThumbnailRetryWakeup();
        Assert.True(current.TryQueue());
        Assert.False(old.Consume());
        Assert.False(old.TryQueue());
        Assert.False(current.TryQueue());
        Assert.True(current.Consume());
        Assert.True(current.TryQueue());
    }

    [Fact]
    public void Other_surface_cannot_bypass_eight_second_global_cooldown()
    {
        var scene = new Scene();
        scene.Initial(ThumbnailOutcome.CacheMiss);
        var other = scene.NewVm();
        other.Observe(ThumbnailOutcome.CacheMiss);
        scene.Clock.Advance(2);
        Assert.True(scene.Run(ThumbnailOutcome.CacheMiss));
        Assert.Equal(TimeSpan.FromSeconds(8), other.Remaining(true, false, true));
        Assert.Null(scene.Registry.Begin("c:\\test.pdf", true));
        scene.Clock.Advance(7);
        Assert.Null(scene.Registry.Begin("c:\\test.pdf", true));
        scene.Clock.Advance(1);
        using var second = scene.Registry.Begin("c:\\test.pdf", true);
        Assert.NotNull(second);
        second.Complete(ThumbnailOutcome.Available);
        Assert.Null(scene.Candidate.Remaining(true, false, true));
    }

    [Fact]
    public void One_surface_success_preserves_other_surfaces_remaining_recovery_budget()
    {
        var scene = new Scene();
        scene.Initial(ThumbnailOutcome.CacheMiss);
        var other = scene.NewVm();
        other.Observe(ThumbnailOutcome.CacheMiss);
        scene.Clock.Advance(2);
        Assert.True(scene.Run(ThumbnailOutcome.Available));
        Assert.Null(scene.Candidate.Remaining(true, false, true));
        Assert.NotNull(other.Remaining(true, false, true));
        Assert.Null(scene.Registry.Begin("C:\\test.pdf", true));
        scene.Clock.Advance(8);
        using var second = scene.Registry.Begin("C:\\test.pdf", true);
        Assert.NotNull(second);
        second.Complete(ThumbnailOutcome.Available);
        other.Observe(ThumbnailOutcome.Available);
        Assert.Null(other.Remaining(true, false, true));
    }
}
