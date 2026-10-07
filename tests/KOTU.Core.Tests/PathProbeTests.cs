using KOTU.Core.Routing;
using KOTU.Core.Threading;
using Xunit;

namespace KOTU.Core.Tests;

public sealed class PathProbeTests
{
    [Fact]
    public void Missing_folder_uses_nearest_surviving_ancestor()
    {
        var root = Path.GetPathRoot(Path.GetFullPath("."))!;
        var parent = Path.Combine(root, "parent");
        var child = Path.Combine(parent, "missing", "child");
        Assert.Equal(parent, PathProbe.ExistingAncestor(child, default, path => path == parent));
        Assert.Equal(child, PathProbe.ExistingAncestor(child, default, _ => false));
    }

    [Fact]
    public async Task Slow_lookup_is_discarded_after_navigation_and_queued_obsolete_query_never_runs()
    {
        using var worker = new ModuleWorker("slow path test");
        using var request = new LatestRequest();
        using var started = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        var first = request.Begin();
        var work = worker.Run(_ =>
        {
            started.Set();
            Assert.True(release.Wait(TimeSpan.FromSeconds(10)));
            return "old folder";
        }, first);
        Assert.True(started.Wait(TimeSpan.FromSeconds(5)));
        try
        {
            var second = request.Begin();
            var ran = false;
            var obsolete = worker.Run(_ => { ran = true; return "obsolete"; }, second);
            var current = request.Begin();
            Assert.True(first.IsCancellationRequested);
            release.Set();
            Assert.Equal("old folder", await work);
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => obsolete);
            Assert.False(ran);
            Assert.False(current.IsCancellationRequested);
            request.Dispose();
            Assert.True(current.IsCancellationRequested);
            Assert.True(request.Begin().IsCancellationRequested);
        }
        finally { release.Set(); }
    }

    [Fact]
    public void Cancellation_stops_ancestor_walk_after_uninterruptible_probe_returns()
    {
        using var cancellation = new CancellationTokenSource();
        var calls = 0;
        Assert.Throws<OperationCanceledException>(() => PathProbe.ExistingAncestor(
            Path.Combine(Path.GetFullPath("."), "missing"), cancellation.Token, _ =>
            {
                calls++;
                cancellation.Cancel();
                return false;
            }));
        Assert.Equal(1, calls);
    }
}
