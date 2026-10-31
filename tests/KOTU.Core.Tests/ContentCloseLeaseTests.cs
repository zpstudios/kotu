using KOTU.Core.Content;
using KOTU.Core.Contracts;
using Xunit;

namespace KOTU.Core.Tests;

public sealed class ContentCloseLeaseTests
{
    private sealed class Guard : ICloseGuard
    {
        public bool HasUnsavedChanges => true;
        public event Action<bool>? UnsavedChanged { add { } remove { } }
        public bool Frozen { get; private set; }
        public int CancelCount { get; private set; }
        public Task<bool> ConfirmCloseAsync() { Frozen = true; return Task.FromResult(true); }
        public Func<Task> CaptureCloseCancellation() => () =>
        { Frozen = false; CancelCount++; return Task.CompletedTask; };
    }

    [Theory]
    [InlineData("window-close-veto")]
    [InlineData("file-target-reject")]
    [InlineData("exception-after-approval")]
    public async Task RemainingContentAlwaysResumesOnScopeExit(string reason)
    {
        var guard = new Guard();
        using var session = new ContentContractSession(guard, a => a(), () => true);
        async Task Caller()
        {
            await using var lease = session.CreateCloseLease();
            Assert.True(await session.ConfirmCloseAsync());
            Assert.True(guard.Frozen);
            if (reason == "exception-after-approval") throw new IOException();
        }
        if (reason == "exception-after-approval") await Assert.ThrowsAsync<IOException>(Caller);
        else await Caller();
        Assert.False(guard.Frozen);
        Assert.Equal(1, guard.CancelCount);
    }

    [Fact]
    public async Task RealDisposalDoesNotResumeDetachedContent()
    {
        var guard = new Guard();
        var session = new ContentContractSession(guard, a => a(), () => true);
        var lease = session.CreateCloseLease();
        Assert.True(await session.ConfirmCloseAsync());
        session.Dispose();
        await lease.DisposeAsync();
        await lease.DisposeAsync();
        Assert.Equal(0, guard.CancelCount);
    }

    [Fact]
    public async Task SameRootHostWithNewChildCannotReleaseNewChildFreeze()
    {
        var old = new Guard();
        var next = new Guard();
        var host = new ChildContentHost(a => a());
        host.Attach(old, "old");
        using var root = new ContentContractSession(host, a => a(), () => true);
        var lease = root.CreateCloseLease();
        Assert.True(await root.ConfirmCloseAsync());
        host.Detach(() => { });
        host.Attach(next, "next");
        Assert.True(await next.ConfirmCloseAsync());
        await lease.DisposeAsync();
        Assert.True(next.Frozen);
        Assert.Equal(0, next.CancelCount);
        Assert.Equal(0, old.CancelCount);
        var currentLease = root.CreateCloseLease();
        await currentLease.DisposeAsync();
        Assert.False(next.Frozen);
        Assert.Equal(1, next.CancelCount);
    }
}
