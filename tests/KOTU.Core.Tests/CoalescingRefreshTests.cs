using System.Threading.Channels;
using KOTU.Core.Threading;
using Xunit;

namespace KOTU.Core.Tests;

public sealed class CoalescingRefreshTests
{
    private sealed class Collector
    {
        public readonly Channel<(TaskCompletionSource<int> Result, CancellationToken Token)> Started =
            Channel.CreateUnbounded<(TaskCompletionSource<int>, CancellationToken)>();
        public int Calls;
        public int Concurrent;
        public int Maximum;

        public async Task<int> Read(CancellationToken cancellation)
        {
            Calls++;
            Maximum = Math.Max(Maximum, ++Concurrent);
            var result = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
            Started.Writer.TryWrite((result, cancellation));
            try { return await result.Task; } // 취소 불가능한 네이티브 작업처럼 끝까지 대기한다.
            finally { Concurrent--; }
        }

        public async Task<(TaskCompletionSource<int> Result, CancellationToken Token)> Next() =>
            await Started.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task Hundred_ticks_during_slow_query_schedule_only_one_followup()
    {
        var collector = new Collector();
        var shown = new List<int>();
        using var refresh = new CoalescingRefresh<int>(collector.Read, shown.Add);
        refresh.SetActive(true);
        var first = await collector.Next();
        for (var i = 0; i < 100; i++) refresh.Request();
        Assert.Equal(1, collector.Calls);
        first.Result.SetResult(1);
        var next = await collector.Next();
        next.Result.SetResult(2);
        await refresh.WhenIdle.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(2, collector.Calls);
        Assert.Equal(1, collector.Maximum);
        Assert.Equal(new[] { 2 }, shown); // 더 최신 요청이 왔으면 옛 결과 표시는 종전 _seq 규칙처럼 생략한다.
    }

    [Fact]
    public async Task Hide_and_reshow_wait_for_old_query_then_publish_only_fresh_result()
    {
        var collector = new Collector();
        var shown = new List<int>();
        using var refresh = new CoalescingRefresh<int>(collector.Read, shown.Add);
        refresh.SetActive(true);
        var old = await collector.Next();
        refresh.Request();
        refresh.SetActive(false);
        Assert.True(old.Token.IsCancellationRequested);
        refresh.SetActive(true);
        for (var i = 0; i < 100; i++) refresh.Request();
        Assert.Equal(1, collector.Calls);
        old.Result.SetResult(1);
        var current = await collector.Next();
        current.Result.SetResult(2);
        await refresh.WhenIdle.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(new[] { 2 }, shown);
        Assert.Equal(2, collector.Calls);
        Assert.Equal(1, collector.Maximum);
    }

    [Fact]
    public async Task Failure_releases_running_state_and_dispose_discards_pending_and_late_completion()
    {
        var collector = new Collector();
        var shown = new List<int>();
        using var refresh = new CoalescingRefresh<int>(collector.Read, shown.Add);
        refresh.SetActive(true);
        var failed = await collector.Next();
        failed.Result.SetException(new IOException("offline drive"));
        await refresh.WhenIdle.WaitAsync(TimeSpan.FromSeconds(5));
        refresh.Request();
        var retry = await collector.Next();
        refresh.Request();
        refresh.Dispose();
        Assert.True(retry.Token.IsCancellationRequested);
        refresh.SetActive(true);
        refresh.Request();
        retry.Result.SetResult(3);
        await refresh.WhenIdle.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(2, collector.Calls);
        Assert.Empty(shown);
    }

    [Fact]
    public async Task Hidden_query_failure_clears_old_pending_request_and_reactivation_can_retry()
    {
        var collector = new Collector();
        var shown = new List<int>();
        using var refresh = new CoalescingRefresh<int>(collector.Read, shown.Add);
        refresh.SetActive(true);
        var old = await collector.Next();
        refresh.Request();
        refresh.SetActive(false);
        old.Result.SetCanceled();
        await refresh.WhenIdle.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(1, collector.Calls);
        refresh.SetActive(true);
        var fresh = await collector.Next();
        fresh.Result.SetResult(4);
        await refresh.WhenIdle.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(new[] { 4 }, shown);
    }
}
