using System.Text.Json;
using KOTU.Module.Document;
using Xunit;

namespace KOTU.DocumentModel.Tests;

public sealed class TextPerformanceTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("0")]
    [InlineData("true")]
    public void OnlyExplicitOneEnablesCapture(string? enabled) =>
        Assert.Null(TextPerfOptions.Parse(false, enabled, "off", "on", "30"));

    [Fact]
    public void StandaloneCannotBeEnabledByAnyOverrides() =>
        Assert.Null(TextPerfOptions.Parse(true, "1", "off", "on", "120"));

    [Theory]
    [InlineData(null, 30)]
    [InlineData("invalid", 30)]
    [InlineData("1", 5)]
    [InlineData("999", 120)]
    public void WindowIsBoundedAndOptionsDoNotNeedSettings(string? seconds, int expected)
    {
        var options = Assert.IsType<TextPerfOptions>(TextPerfOptions.Parse(false, "1", "off", "on", seconds));
        Assert.False(options.Wrap);
        Assert.Equal(1, options.Decor);
        Assert.Equal(expected, options.Seconds);
        Assert.Equal(new TextPerfOptions(true, -1, 30), TextPerfOptions.Parse(false, "1", null, null, null));
    }

    [Fact]
    public void DisabledParsingAllocatesNothingAfterWarmup()
    {
        _ = TextPerfOptions.Parse(false, null, null, null, null);
        var before = GC.GetAllocatedBytesForCurrentThread();
        for (var i = 0; i < 1000; i++) _ = TextPerfOptions.Parse(false, null, null, null, null);
        Assert.Equal(before, GC.GetAllocatedBytesForCurrentThread());
    }

    [Fact]
    public void HistogramQuantilesAreUpperBoundsAndTotalsRemainExact()
    {
        var distribution = new TextPerfDistribution();
        for (var ms = 1; ms <= 100; ms++) distribution.Add(ms, 2);
        var summary = distribution.Snapshot();
        Assert.Equal(100, summary.Count);
        Assert.Equal(5050, summary.TotalMs);
        Assert.Equal(100, summary.MaxMs);
        Assert.InRange(summary.P50UpperMs, 50, 62.5);
        Assert.InRange(summary.P95UpperMs, 95, 100);
        Assert.Equal(200, summary.AllocatedBytes);
    }

    [Fact]
    public void RectBatchesHaveExactCountsAndNoInventedQuantiles()
    {
        var distribution = new TextPerfDistribution();
        distribution.AddBatch(3, 15, 7);
        distribution.AddBatch(2, 21, 20);
        var summary = distribution.Snapshot();
        Assert.Equal(5, summary.Count);
        Assert.Equal(36, summary.TotalMs);
        Assert.Equal(20, summary.MaxMs);
        Assert.Equal(0, summary.HistogramSamples);
        Assert.Equal(0, summary.P95UpperMs);
    }

    [Fact]
    public void PhasesAndBoundaryCrossingProbesStaySeparate()
    {
        long now = 1000;
        var data = new TextPerformanceData(new(true, -1, 5), () => now, 1000);
        using (data.Measure(TextPerfMetric.TextSet)) now = 1100;
        data.Event(TextPerfEvent.TextChanged);
        now = 1200;
        data.FirstRendering();
        data.FirstRendering();
        data.Probe(1190, 1210, 100); // 초기→정착 경계: delay에서 제외한다.
        now = 1250;
        data.Probe(1240, 1250, 120);
        data.Event(TextPerfEvent.PointerWheel);
        now = 3200;
        data.Probe(3190, 3200, 200); // 정착→안정 경계: delay·interval에서 제외한다.
        now = 3300;
        data.Probe(3290, 3300, 250);
        data.Event(TextPerfEvent.ScrollFinal);
        now = 8200;
        Assert.True(data.IsExpired);
        Assert.False(data.IsActive);
        data.Event(TextPerfEvent.PointerWheel); // 기한 뒤 입력은 제외한다.
        data.Stop();
        var summary = data.Snapshot(1, "window-ended", false, 0);
        Assert.True(summary.StableWindowCompleted);
        Assert.Equal(200, summary.Phases[0].DurationMs);
        Assert.Equal(2000, summary.Phases[1].DurationMs);
        Assert.Equal(5000, summary.Phases[2].DurationMs);
        Assert.Equal(100, summary.Phases[0].Metrics["TextSet"].TotalMs);
        Assert.Equal(1, summary.Phases[0].Metrics["FirstRendering"].Count);
        Assert.Equal(1, summary.Phases[1].Metrics["ProbeDelay"].Count);
        Assert.Equal(1, summary.Phases[2].Metrics["ProbeDelay"].Count);
        Assert.Equal(100, summary.Phases[2].Metrics["ProbeInterval"].MaxMs);
        Assert.Equal(50, summary.Phases[2].UiAllocatedBytes);
        Assert.Equal(0, summary.Phases[2].Events["PointerWheel"]);
    }

    [Fact]
    public void EarlyStopWithoutRenderingReportsInitialDurationAndRejectsLateWork()
    {
        long now = 1000;
        var data = new TextPerformanceData(new(true, -1, 5), () => now, 1000);
        var late = data.Measure(TextPerfMetric.OpenWorker);
        now = 20000;
        Assert.True(data.Stop());
        Assert.False(data.Stop());
        late.Dispose();
        data.FirstRendering();
        data.Event(TextPerfEvent.TextChanged);
        data.Probe(19000, 20000, 100);
        data.Memory(100, 200);
        var summary = data.Snapshot(1, "view-ended", true, 300);
        Assert.Equal(19000, summary.Phases[0].DurationMs);
        Assert.Equal(0, summary.Phases[1].DurationMs);
        Assert.False(summary.FirstRenderingObserved);
        Assert.False(summary.StableWindowCompleted);
        Assert.True(summary.ProbePendingAtStop);
        Assert.Equal(300, summary.PendingProbeAgeMs);
        Assert.Equal(0, summary.ActiveProbesAfterStop);
        Assert.All(summary.Phases.SelectMany(p => p.Metrics.Values), m => Assert.Equal(0, m.Count));
    }

    [Fact]
    public void SharedIndexRebuildAndReuseAreMeasuredWithoutExposingText()
    {
        var data = new TextPerformanceData(new(true, 0, 5));
        var index = new TextLineIndex();
        const string synthetic = "private-synthetic-canary\r\n한글😀\r\n";
        data.UpdateLineIndex(index, synthetic);
        data.UpdateLineIndex(index, synthetic);
        Assert.Equal(3, data.KnownLines);
        data.Stop();
        var summary = data.Snapshot(1, "view-ended", false, 0);
        Assert.Equal(1, summary.Phases[0].Metrics["LineIndexBuild"].Count);
        Assert.Equal(1, summary.Phases[0].Metrics["LineIndexReuse"].Count);
        var json = JsonSerializer.Serialize(summary);
        Assert.DoesNotContain("canary", json);
        Assert.DoesNotContain("한글", json);
        Assert.True(json.Length < 128 * 1024);
    }

    [Fact]
    public void ScrollMovementDistinguishesWheelFromActualMotion()
    {
        long now = 1000;
        var data = new TextPerformanceData(new(false, 0, 5), () => now, 1000);
        data.FirstRendering();
        now = 3100;
        data.Scroll(new(true, 0, 0, 100, 100, 10000, 100));
        data.Event(TextPerfEvent.PointerWheel);
        data.Scroll(new(true, 0, 0, 100, 100, 10000, 100));
        data.Scroll(new(true, 50, 0, 100, 100, 10000, 100));
        data.Scroll(new(true, 0, 0, 100, 100, 10000, 100));
        data.Stop();
        var summary = data.Snapshot(1, "view-ended", false, 0);
        Assert.Equal(1, summary.Phases[2].Events["PointerWheel"]);
        Assert.Equal(2, summary.Phases[2].Events["ScrollOffsetChanged"]);
        Assert.Equal(50, summary.MaxHorizontalMovement);
        Assert.Equal(0, summary.MaxVerticalMovement);
        Assert.Equal(10000, summary.FirstScroll.ExtentWidth);
    }

    [Fact]
    public void PendingProbeIsBoundedAndDisposedCallbackCannotTouchTarget()
    {
        var queued = new List<Action>();
        var acknowledged = 0;
        using var probe = new TextPerformanceProbe(action => { queued.Add(action); return true; }, (_, _) => acknowledged++);
        Parallel.For(0, 1000, _ => probe.Request());
        Assert.Single(queued);
        Assert.True(probe.Pending);
        queued[0]();
        Assert.False(probe.Pending);
        Assert.Equal(1, acknowledged);
        probe.Request();
        Assert.Equal(2, queued.Count);
        probe.Dispose();
        queued[1]();
        probe.Request();
        Assert.Equal(1, acknowledged);
        Assert.Equal(2, queued.Count);
        Assert.False(probe.Pending);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void RejectedOrThrowingQueueDoesNotLeavePendingReservation(bool throws)
    {
        var requests = 0;
        using var probe = new TextPerformanceProbe(_ =>
        {
            requests++;
            if (throws) throw new InvalidOperationException();
            return false;
        }, (_, _) => Assert.Fail("A rejected request cannot acknowledge."));
        probe.Request();
        probe.Request();
        Assert.Equal(2, requests);
        Assert.False(probe.Pending);
    }

    [Fact]
    public void OldQueuedCallbackCannotAffectReplacementCapture()
    {
        Action? oldCallback = null;
        using var old = new TextPerformanceProbe(action => { oldCallback = action; return true; }, (_, _) => Assert.Fail("Stale callback"));
        old.Request();
        old.Dispose();
        Action? currentCallback = null;
        var acknowledgements = 0;
        using var current = new TextPerformanceProbe(action => { currentCallback = action; return true; }, (_, _) => acknowledgements++);
        current.Request();
        oldCallback!();
        Assert.True(current.Pending);
        Assert.Equal(0, acknowledgements);
        currentCallback!();
        Assert.False(current.Pending);
        Assert.Equal(1, acknowledgements);
    }
}
