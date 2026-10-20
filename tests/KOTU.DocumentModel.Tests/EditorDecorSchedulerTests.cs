using KOTU.Module.Document;
using Xunit;

namespace KOTU.DocumentModel.Tests;

public sealed class EditorDecorSchedulerTests
{
    private sealed class Delay(Action callback, long due) : IDisposable
    {
        internal long Due { get; } = due;
        internal bool Disposed { get; private set; }
        internal void Fire() => callback(); // 취소 직후 이미 시작된 워커 콜백도 시험한다.
        public void Dispose() => Disposed = true;
    }

    private sealed class Harness : IDisposable
    {
        internal long Now;
        internal readonly Queue<Action> Queue = new();
        internal readonly List<Delay> Delays = [];
        internal readonly List<(long Time, TextPerfEvent Trigger)> Renders = [];
        internal Action? DuringRender;
        internal bool RejectQueue;
        internal bool ThrowOnQueue;
        internal EditorDecorScheduler Scheduler { get; }

        internal Harness()
        {
            Scheduler = new(action =>
            {
                if (ThrowOnQueue) throw new InvalidOperationException();
                if (RejectQueue) return false;
                Queue.Enqueue(action);
                return true;
            }, trigger =>
            {
                Renders.Add((Now, trigger));
                DuringRender?.Invoke();
            }, 50, () => Now, 1000, (callback, milliseconds) =>
            {
                var delay = new Delay(callback, Now + milliseconds);
                Delays.Add(delay);
                return delay;
            });
        }

        internal void Advance(long time)
        {
            Now = time;
            while (Delays.FirstOrDefault(delay => !delay.Disposed && delay.Due <= Now) is { } due)
                due.Fire();
        }

        internal void Drain()
        {
            while (Queue.TryDequeue(out var callback)) callback();
        }

        public void Dispose() => Scheduler.Dispose();
    }

    [Fact]
    public void InitialDisplayIsImmediateAndIdleLayoutFinalShareOneFixedWindow()
    {
        using var h = new Harness();
        h.Scheduler.Request(TextPerfEvent.DecorLayout);
        Assert.Single(h.Renders);
        Assert.Empty(h.Delays);
        h.Advance(300);
        h.Scheduler.Request(TextPerfEvent.DecorLayout);
        h.Advance(310);
        h.Scheduler.Request(TextPerfEvent.DecorScrollFinal);
        Assert.Single(h.Renders);
        Assert.Single(h.Delays);
        Assert.Equal(350, h.Delays[0].Due);
        h.Advance(349);
        h.Drain();
        Assert.Single(h.Renders);
        h.Advance(350);
        h.Drain();
        Assert.Equal(2, h.Renders.Count);
        Assert.Equal((350L, TextPerfEvent.DecorScrollFinal), h.Renders[1]);
    }

    [Fact]
    public void ContinuousRequestsMakeProgressWithoutMovingDeadline()
    {
        using var h = new Harness();
        h.Scheduler.Request(TextPerfEvent.DecorLayout);
        for (var time = 1; time <= 201; time++)
        {
            h.Advance(time);
            h.Scheduler.Request(TextPerfEvent.DecorScrollTimer);
            h.Drain();
        }
        Assert.Equal(new long[] { 0, 51, 102, 153 }, h.Renders.Select(render => render.Time));
        h.Advance(204);
        h.Drain();
        Assert.Equal(new long[] { 0, 51, 102, 153, 204 }, h.Renders.Select(render => render.Time));
        Assert.All(h.Delays, delay => Assert.True(delay.Disposed));
    }

    [Theory]
    [InlineData((int)TextPerfEvent.DecorScrollFinal)]
    [InlineData((int)TextPerfEvent.DecorScrollTimer)]
    public void LastScrollRunsWithoutLayoutOrAnotherScroll(int triggerValue)
    {
        using var h = new Harness();
        var trigger = (TextPerfEvent)triggerValue;
        h.Scheduler.Request(TextPerfEvent.DecorLayout);
        h.Advance(500);
        h.Scheduler.Request(trigger);
        h.Advance(550);
        h.Drain();
        Assert.Equal((550L, trigger), h.Renders[1]);
        Assert.Empty(h.Queue);
    }

    [Fact]
    public void QueuedPassReadsLatestRequestAndDoesNotAddAnotherReservation()
    {
        using var h = new Harness();
        h.Scheduler.Request(TextPerfEvent.DecorLayout);
        h.Advance(10);
        h.Scheduler.Request(TextPerfEvent.DecorLayout);
        h.Advance(60);
        Assert.Single(h.Queue);
        for (var i = 0; i < 1000; i++) h.Scheduler.Request(TextPerfEvent.DecorScrollFinal);
        Assert.Single(h.Queue);
        Assert.Single(h.Delays);
        h.Drain();
        Assert.Equal(TextPerfEvent.DecorScrollFinal, h.Renders[1].Trigger);
        h.Advance(1000);
        h.Drain();
        Assert.Equal(2, h.Renders.Count);
    }

    [Fact]
    public void RequestDuringRenderGetsOneFollowupWithoutRecursion()
    {
        using var h = new Harness();
        h.DuringRender = () =>
        {
            h.DuringRender = null;
            h.Now = 10;
            for (var i = 0; i < 10; i++) h.Scheduler.Request(TextPerfEvent.DecorLayout);
        };
        h.Scheduler.Request(TextPerfEvent.DecorScrollFinal);
        Assert.Single(h.Renders);
        Assert.Single(h.Delays);
        Assert.Equal(60, h.Delays[0].Due);
        h.Advance(60);
        h.Drain();
        Assert.Equal(2, h.Renders.Count);
    }

    [Fact]
    public void ImmediateChangeConsumesWaitingAndQueuedWork()
    {
        using var h = new Harness();
        h.Scheduler.Request(TextPerfEvent.DecorLayout);
        h.Advance(10);
        h.Scheduler.Request(TextPerfEvent.DecorLayout);
        var oldDelay = h.Delays[0];
        h.Advance(20);
        h.Scheduler.RenderNow(TextPerfEvent.DecorVisibility);
        oldDelay.Fire();
        h.Advance(30);
        h.Scheduler.Request(TextPerfEvent.DecorScrollFinal);
        h.Advance(80);
        Assert.Single(h.Queue);
        h.Scheduler.RenderNow(TextPerfEvent.DecorMode);
        h.Drain();
        h.Advance(1000);
        h.Drain();
        Assert.Equal(new[] { TextPerfEvent.DecorLayout, TextPerfEvent.DecorVisibility, TextPerfEvent.DecorMode },
            h.Renders.Select(render => render.Trigger));
    }

    [Fact]
    public void SuspendedTimerCannotRenderOrConsumeReattachedRequest()
    {
        using var h = new Harness();
        h.Scheduler.Request(TextPerfEvent.DecorLayout);
        h.Advance(10);
        h.Scheduler.Request(TextPerfEvent.DecorScrollFinal);
        var oldDelay = h.Delays[0];
        h.Scheduler.Suspend();
        h.Scheduler.Request(TextPerfEvent.DecorLayout);
        h.Scheduler.RenderNow(TextPerfEvent.DecorTheme);
        Assert.Single(h.Renders);
        h.Scheduler.Resume();
        h.Scheduler.Request(TextPerfEvent.DecorLayout);
        h.Advance(20);
        h.Scheduler.Request(TextPerfEvent.DecorScrollFinal);
        oldDelay.Fire();
        Assert.Equal(2, h.Renders.Count);
        h.Advance(70);
        h.Drain();
        Assert.Equal(3, h.Renders.Count);
    }

    [Fact]
    public void SuspendedQueuedCallbackCannotClearNewGeneration()
    {
        using var h = new Harness();
        h.Scheduler.Request(TextPerfEvent.DecorLayout);
        h.Advance(10);
        h.Scheduler.Request(TextPerfEvent.DecorLayout);
        h.Advance(60);
        var oldCallback = h.Queue.Dequeue();
        h.Scheduler.Suspend();
        h.Scheduler.Resume();
        h.Scheduler.Request(TextPerfEvent.DecorLayout);
        h.Advance(70);
        h.Scheduler.Request(TextPerfEvent.DecorScrollFinal);
        h.Advance(120);
        oldCallback();
        Assert.Single(h.Queue);
        h.Drain();
        Assert.Equal(new long[] { 0, 60, 120 }, h.Renders.Select(render => render.Time));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void DisposePermanentlyDisconnectsWaitingOrQueuedCallback(bool queued)
    {
        using var h = new Harness();
        h.Scheduler.Request(TextPerfEvent.DecorLayout);
        h.Advance(10);
        h.Scheduler.Request(TextPerfEvent.DecorLayout);
        var delay = h.Delays[0];
        if (queued) h.Advance(60);
        h.Scheduler.Dispose();
        delay.Fire();
        h.Drain();
        h.Scheduler.Resume();
        h.Scheduler.Request(TextPerfEvent.DecorScrollFinal);
        h.Scheduler.RenderNow(TextPerfEvent.DecorVisibility);
        Assert.Single(h.Renders);
        Assert.True(delay.Disposed);
        Assert.Empty(h.Queue);
    }

    [Fact]
    public void EarlyTimerDoesNotViolateWindow()
    {
        using var h = new Harness();
        h.Scheduler.Request(TextPerfEvent.DecorLayout);
        h.Advance(10);
        h.Scheduler.Request(TextPerfEvent.DecorLayout);
        h.Now = 59;
        h.Delays[0].Fire();
        Assert.Empty(h.Queue);
        Assert.Equal(60, h.Delays[1].Due);
        h.Advance(60);
        h.Drain();
        Assert.Equal(2, h.Renders.Count);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void RejectedQueueDoesNotLatchFutureRequest(bool throws)
    {
        using var h = new Harness();
        h.Scheduler.Request(TextPerfEvent.DecorLayout);
        h.ThrowOnQueue = throws;
        h.RejectQueue = !throws;
        h.Advance(10);
        h.Scheduler.Request(TextPerfEvent.DecorScrollFinal);
        h.Advance(60);
        h.ThrowOnQueue = h.RejectQueue = false;
        h.Scheduler.Request(TextPerfEvent.DecorScrollFinal);
        h.Advance(110);
        h.Drain();
        Assert.Equal(new long[] { 0, 110 }, h.Renders.Select(render => render.Time));
    }
}
