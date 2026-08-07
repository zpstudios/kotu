using WinUtil.Core.Threading;
using Xunit;

namespace WinUtil.Core.Tests;

public sealed class ModuleWorkerTests
{
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(5);

    [Fact]
    public async Task Run_executes_on_named_dedicated_thread()
    {
        using var worker = new ModuleWorker("test worker");
        var (name, id) = await worker
            .Run(_ => (Thread.CurrentThread.Name, Environment.CurrentManagedThreadId))
            .WaitAsync(Wait);
        Assert.Equal("test worker", name);
        Assert.NotEqual(Environment.CurrentManagedThreadId, id);
    }

    [Fact]
    public async Task Run_is_serial_and_fifo()
    {
        using var worker = new ModuleWorker("serial");
        var order = new List<int>();
        var gate = new ManualResetEventSlim(false);
        var first = worker.Run(_ =>
        {
            gate.Wait(Wait);
            lock (order) order.Add(1);
            return 0;
        });
        var second = worker.Run(_ =>
        {
            lock (order) order.Add(2);
            return 0;
        });
        Assert.False(second.IsCompleted); // 첫 작업이 워커를 잡고 있는 동안 두 번째는 시작 못 한다
        gate.Set();
        await Task.WhenAll(first, second).WaitAsync(Wait);
        Assert.Equal(new[] { 1, 2 }, order);
    }

    [Fact]
    public async Task Faulted_work_surfaces_exception()
    {
        using var worker = new ModuleWorker("faulty");
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => worker.Run<int>(_ => throw new InvalidOperationException("boom")).WaitAsync(Wait));
    }

    [Fact]
    public async Task Cancelled_before_start_never_runs()
    {
        using var worker = new ModuleWorker("cancel");
        var gate = new ManualResetEventSlim(false);
        var ran = false;
        var blocker = worker.Run(_ =>
        {
            gate.Wait(Wait);
            return 0;
        });
        using var cts = new CancellationTokenSource();
        var victim = worker.Run(_ =>
        {
            ran = true;
            return 0;
        }, cts.Token);
        cts.Cancel();  // 아직 blocker가 워커를 잡고 있으므로 victim은 시작 전
        gate.Set();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => victim.WaitAsync(Wait));
        await blocker.WaitAsync(Wait);
        Assert.False(ran);
    }

    [Fact]
    public async Task Cancellation_during_work_marks_task_canceled()
    {
        using var worker = new ModuleWorker("coop");
        using var cts = new CancellationTokenSource();
        var started = new ManualResetEventSlim(false);
        var task = worker.Run<int>(ctx =>
        {
            started.Set();
            while (true)
            {
                ctx.ThrowIfCancelled();
                Thread.Sleep(10);
            }
        }, cts.Token);
        Assert.True(started.Wait(Wait));
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => task.WaitAsync(Wait));
        Assert.True(task.IsCanceled);
    }

    [Fact]
    public async Task Progress_reaches_caller_supplied_sink()
    {
        using var worker = new ModuleWorker("progress");
        var values = new List<double>();
        var sink = new InlineProgress(v =>
        {
            lock (values) values.Add(v);
        });
        await worker.Run(ctx =>
        {
            ctx.Progress.Report(0.25);
            ctx.Progress.Report(1.0);
            return 0;
        }, progress: sink).WaitAsync(Wait);
        lock (values) Assert.Equal(new[] { 0.25, 1.0 }, values);
    }

    [Fact]
    public async Task Progress_defaults_to_noop_sink()
    {
        using var worker = new ModuleWorker("noop");
        // progress를 안 넘겨도 Report가 던지지 않아야 백엔드가 null 검사 없이 쓸 수 있다.
        await worker.Run(ctx =>
        {
            ctx.Progress.Report(0.5);
            return 0;
        }).WaitAsync(Wait);
    }

    [Fact]
    public async Task Dispose_drains_pending_work_and_rejects_new()
    {
        var worker = new ModuleWorker("drain");
        var gate = new ManualResetEventSlim(false);
        var blocker = worker.Run(_ =>
        {
            gate.Wait(Wait);
            return 0;
        });
        var queued = worker.Run(_ => 42);
        worker.Dispose();
        var late = worker.Run(_ => 0);
        worker.Post(() => { }); // Dispose 후에도 던지지 않고 무시돼야 한다
        gate.Set();
        Assert.Equal(42, await queued.WaitAsync(Wait)); // 이미 큐에 있던 작업은 끝까지 실행
        await blocker.WaitAsync(Wait);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => late.WaitAsync(Wait));
    }

    private sealed class InlineProgress(Action<double> report) : IProgress<double>
    {
        public void Report(double value) => report(value);
    }
}
