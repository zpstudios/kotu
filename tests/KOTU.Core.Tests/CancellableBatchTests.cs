using KOTU.Core.Threading;
using Xunit;

namespace KOTU.Core.Tests;

public class CancellableBatchTests
{
    [Fact]
    public async Task Restart_cancels_queued_batch_without_releasing_an_unowned_gate()
    {
        using var batch = new CancellableBatch();
        using var gate = new SemaphoreSlim(1);
        using var held = await CancellableBatch.EnterAsync(gate, batch.Token);
        var oldToken = batch.Token;
        var queued = CancellableBatch.EnterAsync(gate, oldToken);
        batch.Restart();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => queued);
        Assert.True(oldToken.IsCancellationRequested);
        Assert.False(batch.Token.IsCancellationRequested);
        Assert.Equal(0, gate.CurrentCount);
        held.Dispose();
        held.Dispose();
        Assert.Equal(1, gate.CurrentCount);
        using var next = await CancellableBatch.EnterAsync(gate, batch.Token);
        Assert.Equal(0, gate.CurrentCount);
    }

    [Fact]
    public async Task Unload_cancels_waiters_and_reload_has_a_new_token()
    {
        using var batch = new CancellableBatch();
        using var gate = new SemaphoreSlim(0, 1);
        var oldToken = batch.Token;
        var queued = CancellableBatch.EnterAsync(gate, oldToken);
        batch.Dispose();
        batch.Dispose();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => queued);
        Assert.True(batch.Token.IsCancellationRequested);
        batch.Restart();
        Assert.NotEqual(oldToken, batch.Token);
        Assert.False(batch.Token.IsCancellationRequested);
        Assert.Equal(0, gate.CurrentCount);
    }

    [Fact]
    public async Task Failed_work_returns_gate_to_the_next_batch()
    {
        using var batch = new CancellableBatch();
        using var gate = new SemaphoreSlim(1);
        await Assert.ThrowsAsync<IOException>(async () =>
        {
            using var held = await CancellableBatch.EnterAsync(gate, batch.Token);
            batch.Restart();
            throw new IOException();
        });
        using var next = await CancellableBatch.EnterAsync(gate, batch.Token);
        Assert.Equal(0, gate.CurrentCount);
    }
}
