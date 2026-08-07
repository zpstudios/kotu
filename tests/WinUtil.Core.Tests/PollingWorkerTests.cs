using WinUtil.Core.Threading;
using Xunit;

namespace WinUtil.Core.Tests;

public sealed class PollingWorkerTests
{
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(5);

    [Fact]
    public void Idle_without_subscribers()
    {
        var polls = 0;
        using var worker = new PollingWorker<int>("idle", TimeSpan.FromMilliseconds(10),
            () => Interlocked.Increment(ref polls));
        Thread.Sleep(150);
        Assert.Equal(0, Volatile.Read(ref polls)); // 구독자 없으면 수집 비용 0
    }

    [Fact]
    public void First_subscribe_polls_promptly_and_delivers()
    {
        // 간격을 30초로 잡아 "간격을 기다리지 않고 즉시 1회"를 검증한다.
        using var worker = new PollingWorker<int>("first", TimeSpan.FromSeconds(30), () => 7);
        var got = new ManualResetEventSlim(false);
        var value = 0;
        using var sub = worker.Subscribe(v =>
        {
            value = v;
            got.Set();
        });
        Assert.True(got.Wait(Wait));
        Assert.Equal(7, value);
    }

    [Fact]
    public void Unsubscribe_pauses_delivery()
    {
        var delivered = 0;
        using var worker = new PollingWorker<int>("pause", TimeSpan.FromMilliseconds(10), () => 1);
        var first = new ManualResetEventSlim(false);
        var sub = worker.Subscribe(_ =>
        {
            Interlocked.Increment(ref delivered);
            first.Set();
        });
        Assert.True(first.Wait(Wait));
        sub.Dispose();
        var afterStop = Volatile.Read(ref delivered);
        Thread.Sleep(200);
        // 해지 순간 배달 중이던 1회까지는 허용, 그 뒤로는 멈춰야 한다.
        Assert.InRange(Volatile.Read(ref delivered), afterStop, afterStop + 1);
    }

    [Fact]
    public void Poke_shortcuts_interval()
    {
        var polls = 0;
        using var worker = new PollingWorker<int>("poke", TimeSpan.FromSeconds(30),
            () => Interlocked.Increment(ref polls));
        var second = new ManualResetEventSlim(false);
        using var sub = worker.Subscribe(v =>
        {
            if (v >= 2) second.Set();
        });
        Assert.True(SpinWait.SpinUntil(() => Volatile.Read(ref polls) >= 1, Wait));
        worker.Poke();
        Assert.True(second.Wait(Wait)); // 30초 간격을 기다리지 않고 두 번째 폴링이 와야 한다
    }

    [Fact]
    public void Single_loop_serves_multiple_subscribers()
    {
        var threads = new HashSet<int>();
        using var worker = new PollingWorker<int>("shared", TimeSpan.FromMilliseconds(10), () =>
        {
            lock (threads) threads.Add(Environment.CurrentManagedThreadId);
            return 0;
        });
        var a = new ManualResetEventSlim(false);
        var b = new ManualResetEventSlim(false);
        using var subA = worker.Subscribe(_ => a.Set());
        using var subB = worker.Subscribe(_ => b.Set());
        Assert.True(a.Wait(Wait));
        Assert.True(b.Wait(Wait));
        lock (threads) Assert.Single(threads); // 구독이 몇 개든 수집 스레드는 하나
    }

    [Fact]
    public void Poll_exception_does_not_kill_loop()
    {
        var calls = 0;
        using var worker = new PollingWorker<int>("flaky", TimeSpan.FromMilliseconds(10), () =>
        {
            var n = Interlocked.Increment(ref calls);
            return n % 2 == 1 ? throw new InvalidOperationException("flaky") : n;
        });
        var ok = new ManualResetEventSlim(false);
        using var sub = worker.Subscribe(_ => ok.Set());
        Assert.True(ok.Wait(Wait)); // 홀수 번째 poll이 던져도 짝수 번째 발행은 온다
    }
}
