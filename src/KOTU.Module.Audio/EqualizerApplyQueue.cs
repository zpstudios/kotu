namespace KOTU.Module.Audio;

internal enum EqualizerApplyResult { Applied, DisabledAfterFailure, Failed, Superseded }

/// <summary>
/// A437: UI 소유 실행 하나 + 최신 대기 하나. 실행은 주입된 Audio Worker에만 맡긴다.
/// Invalidate는 플레이어 교체·해제 때 세대를 바꿔 이미 큐에 들어간 요청과 완료 통지도 무효화한다.
/// </summary>
internal sealed class EqualizerApplyQueue<TTarget> where TTarget : class
{
    private readonly Func<TTarget, EqualizerSnapshot?, Func<bool>, Task<EqualizerApplyResult>> _apply;
    private readonly Action<EqualizerApplyResult> _completed;
    private Request? _pending;
    private bool _running;
    private long _generation;
    internal Task Completion { get; private set; } = Task.CompletedTask;

    internal EqualizerApplyQueue(Func<TTarget, EqualizerSnapshot?, Func<bool>, Task<EqualizerApplyResult>> apply,
        Action<EqualizerApplyResult> completed)
    {
        _apply = apply;
        _completed = completed;
    }

    internal void Enqueue(TTarget target, EqualizerSnapshot? values)
    {
        _pending = new Request(target, values, Interlocked.Increment(ref _generation));
        if (!_running) Completion = DrainAsync();
    }

    internal void Invalidate()
    {
        Interlocked.Increment(ref _generation);
        _pending = null;
    }

    private async Task DrainAsync()
    {
        _running = true;
        try
        {
            while (_pending is { } request)
            {
                _pending = null;
                bool Current() => request.Generation == Interlocked.Read(ref _generation);
                EqualizerApplyResult result;
                try { result = await _apply(request.Target, request.Values, Current); }
                catch (OperationCanceledException) { result = EqualizerApplyResult.Superseded; }
                catch { result = EqualizerApplyResult.Failed; }
                if (Current() && result != EqualizerApplyResult.Superseded) _completed(result);
            }
        }
        finally { _running = false; }
    }

    private sealed record Request(TTarget Target, EqualizerSnapshot? Values, long Generation);
}
