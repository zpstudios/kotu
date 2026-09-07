namespace KOTU.Core.Threading;

/// <summary>UI 소유 배치. 전환 시 이전 요청을 취소하고, 게이트 소유권은 작업별로 관리한다.</summary>
public sealed class CancellableBatch : IDisposable
{
    private CancellationTokenSource? _source = new();

    public CancellationToken Token => _source?.Token ?? new CancellationToken(true);

    public void Restart()
    {
        var previous = _source;
        _source = new CancellationTokenSource();
        previous?.Cancel();
        previous?.Dispose();
    }

    public void Dispose()
    {
        var previous = _source;
        _source = null;
        previous?.Cancel();
        previous?.Dispose();
    }

    public static async Task<IDisposable> EnterAsync(SemaphoreSlim gate, CancellationToken cancellation)
    {
        await gate.WaitAsync(cancellation).ConfigureAwait(false);
        return new Lease(gate);
    }

    private sealed class Lease(SemaphoreSlim gate) : IDisposable
    {
        private SemaphoreSlim? _gate = gate;
        public void Dispose() => Interlocked.Exchange(ref _gate, null)?.Release();
    }
}
