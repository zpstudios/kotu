using System.Diagnostics;

namespace KOTU.Module.Document;

// A401: 첫 일반 요청부터 고정 50ms 창으로 합친다. 유휴 뒤에도 즉시 실행하지 않으며,
// 후속 요청은 실행 기한을 늦추지 않는다. 대기는 워커에서 한 번만 수행한다.
// Request/RenderNow/Suspend/Resume은 UI 소유자만 호출하고, 지연 콜백만 워커에서 들어온다.
internal sealed class EditorDecorScheduler : IDisposable
{
    private readonly object _gate = new();
    private readonly Func<long> _clock;
    private readonly long _frequency;
    private readonly int _intervalMilliseconds;
    private readonly Func<Action, int, IDisposable> _delay;
    private Func<Action, bool>? _enqueue;
    private Action<TextPerfEvent>? _render;
    private IDisposable? _waiting;
    private long _generation;
    private long _requestedAt;
    private bool _hasRendered;
    private bool _active = true;
    private bool _requested;
    private bool _queued;
    private bool _rendering;
    private TextPerfEvent _trigger;

    internal EditorDecorScheduler(Func<Action, bool> enqueue, Action<TextPerfEvent> render,
        int intervalMilliseconds, Func<long>? clock = null, long frequency = 0,
        Func<Action, int, IDisposable>? delay = null)
    {
        _enqueue = enqueue;
        _render = render;
        _intervalMilliseconds = intervalMilliseconds;
        _clock = clock ?? Stopwatch.GetTimestamp;
        _frequency = frequency == 0 ? Stopwatch.Frequency : frequency;
        _delay = delay ?? ((callback, milliseconds) =>
            new System.Threading.Timer(_ => callback(), null, milliseconds, Timeout.Infinite));
    }

    internal void Request(TextPerfEvent trigger)
    {
        var first = false;
        lock (_gate)
        {
            if (!_active || _render is null) return;
            if (!_requested) _requestedAt = _clock();
            _requested = true;
            _trigger = trigger;
            if (_rendering || _queued || _waiting is not null) return;
            first = !_hasRendered;
            if (!first) ScheduleCore();
        }
        // 첫 표시를 타이머만큼 늦추지 않는다. 이후 요청은 항상 큐에서 함께 소비한다.
        if (first) RenderNow(trigger);
    }

    internal void RenderNow(TextPerfEvent trigger)
    {
        long generation;
        lock (_gate)
        {
            if (!_active || _render is null) return;
            CancelCore();
            _requestedAt = _clock();
            _requested = true;
            _trigger = trigger;
            // 렌더 도중 들어온 변경은 재귀 렌더 대신 완료 뒤 최신 상태로 한 번 반영한다.
            if (_rendering) return;
            _queued = true;
            generation = _generation;
        }
        RunPending(generation);
    }

    private int RemainingMilliseconds()
    {
        var elapsed = (_clock() - _requestedAt) * 1000.0 / _frequency;
        return (int)Math.Ceiling(Math.Max(0, _intervalMilliseconds - elapsed));
    }

    private void ScheduleCore()
    {
        if (!_active || _render is null || !_requested || _rendering || _queued || _waiting is not null) return;
        var generation = ++_generation;
        var remaining = RemainingMilliseconds();
        if (remaining == 0) QueueCore(generation);
        else _waiting = _delay(() => DelayElapsed(generation), remaining);
    }

    private void DelayElapsed(long generation)
    {
        lock (_gate)
        {
            if (!_active || generation != _generation || _render is null) return;
            _waiting?.Dispose();
            _waiting = null;
            // 타이머가 조금 일찍 돌아와도 최소 간격을 지킨다. 새 입력은 기한을 되감지 않는다.
            ScheduleCore();
        }
    }

    private void QueueCore(long generation)
    {
        _queued = true;
        try
        {
            if (_enqueue?.Invoke(() => RunPending(generation)) == true) return;
        }
        catch { /* 닫히는 dispatcher의 실패가 문서 입력을 막지 않게 한다. */ }
        _queued = false;
        _requested = false;
    }

    private void RunPending(long generation)
    {
        Action<TextPerfEvent> render;
        TextPerfEvent trigger;
        lock (_gate)
        {
            if (!_active || generation != _generation || !_queued || _render is null) return;
            _queued = false;
            if (!_requested) return;
            _requested = false;
            _rendering = true;
            _hasRendered = true;
            render = _render;
            trigger = _trigger;
        }
        try { render(trigger); }
        finally
        {
            lock (_gate)
            {
                _rendering = false;
                ScheduleCore();
            }
        }
    }

    private void CancelCore()
    {
        _generation++;
        _waiting?.Dispose();
        _waiting = null;
        _queued = false;
        _requested = false;
    }

    internal void Suspend()
    {
        lock (_gate)
        {
            _active = false;
            CancelCore();
        }
    }

    internal void Resume()
    {
        lock (_gate)
        {
            if (_render is null || _active) return;
            _active = true;
            // 재부착의 첫 패스는 이전 화면의 간격에 묶지 않는다.
            _hasRendered = false;
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            _active = false;
            CancelCore();
            // 취소할 수 없는 dispatcher 콜백이 남아도 뷰·큐를 붙들거나 호출하지 않는다.
            _render = null;
            _enqueue = null;
        }
    }
}
