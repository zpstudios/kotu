namespace KOTU.Core.Threading;

/// <summary>UI 소유 요청의 세대와 수명. 새 요청/해제는 대기 작업을 취소하고 실행 결과를 폐기한다.</summary>
public sealed class LatestRequest : IDisposable
{
    private CancellationTokenSource? _source;
    private bool _disposed;
    public long Generation { get; private set; }

    public CancellationToken Begin()
    {
        Cancel();
        if (_disposed) return new CancellationToken(canceled: true);
        _source = new CancellationTokenSource();
        return _source.Token;
    }

    public void Cancel()
    {
        Generation++;
        _source?.Cancel();
        _source?.Dispose();
        _source = null;
    }

    public void Dispose() { _disposed = true; Cancel(); }
}
