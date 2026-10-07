namespace KOTU.Core.Threading;

/// <summary>
/// UI 소유 요청 조정기. 호출과 await 후 반영은 같은 UI 컨텍스트에서 수행한다.
/// 실행 하나 + 최신 요청 bool 하나만 유지하며 수집 자체는 주입된 워커에서 수행한다.
/// 비활성화/해제는 대기와 늦은 결과만 취소한다. 진행 중 네이티브 호출은 강제 종료하지 않는다.
/// </summary>
public sealed class CoalescingRefresh<T>(Func<CancellationToken, Task<T>> collect, Action<T> publish) : IDisposable
{
    private bool _active;
    private bool _running;
    private bool _pending;
    private bool _disposed;
    private long _generation;
    private CancellationTokenSource? _current;
    public Task WhenIdle { get; private set; } = Task.CompletedTask;

    public void SetActive(bool active)
    {
        if (_disposed || _active == active) return;
        _active = active;
        ++_generation;
        if (active) Request();
        else
        {
            _pending = false;
            _current?.Cancel();
        }
    }

    public void Request()
    {
        if (!_active || _disposed) return;
        _pending = true;
        if (_running) return;
        _running = true;
        WhenIdle = DrainAsync();
    }

    private async Task DrainAsync()
    {
        try
        {
            while (_active && _pending && !_disposed)
            {
                _pending = false;
                var generation = _generation;
                using var cancellation = new CancellationTokenSource();
                _current = cancellation;
                try
                {
                    var result = await collect(cancellation.Token);
                    if (_active && !_pending && !_disposed && generation == _generation && !cancellation.IsCancellationRequested)
                        publish(result);
                }
                catch { /* 부가 표시의 조회 실패: 다음 요청은 정상 재시도한다. */ }
                finally { _current = null; }
            }
        }
        finally { _running = false; }
    }

    public void Dispose()
    {
        SetActive(false);
        _disposed = true;
    }
}
