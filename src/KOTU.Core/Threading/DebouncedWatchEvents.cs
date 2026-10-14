namespace KOTU.Core.Threading;

/// <summary>
/// 워커에서 마지막 통지 뒤 정숙 시간을 재고 UI 콜백을 최대 하나만 예약한다.
/// UI가 밀리면 소비 시점에도 정숙 시간을 다시 확인한다. 해제된 세대의 콜백은 무동작이다.
/// </summary>
public sealed class DebouncedWatchEvents : IDisposable
{
    private readonly object _sync = new();
    private readonly TimeProvider _clock;
    private readonly TimeSpan _delay;
    private readonly ITimer _timer;
    private readonly Func<Action, bool> _enqueue;
    private readonly Action<bool> _apply;
    private long _lastEvent;
    private bool _dirty, _error, _queued, _disposed;

    public DebouncedWatchEvents(TimeSpan delay, Func<Action, bool> enqueue, Action<bool> apply,
        TimeProvider? clock = null)
    {
        if (delay <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(delay));
        _clock = clock ?? TimeProvider.System;
        _delay = delay;
        _enqueue = enqueue;
        _apply = apply;
        _timer = _clock.CreateTimer(_ => OnTimer(), null, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
    }

    public void Signal(bool error = false)
    {
        lock (_sync)
        {
            if (_disposed) return;
            _lastEvent = _clock.GetTimestamp();
            _dirty = true;
            _error |= error;
            if (!_queued) _timer.Change(_delay, Timeout.InfiniteTimeSpan);
        }
    }

    private TimeSpan Remaining() => _delay - _clock.GetElapsedTime(_lastEvent);

    private void OnTimer()
    {
        lock (_sync)
        {
            if (_disposed || !_dirty || _queued) return;
            var remaining = Remaining();
            if (remaining > TimeSpan.Zero)
            {
                _timer.Change(remaining, Timeout.InfiniteTimeSpan);
                return;
            }
            _queued = true;
        }
        // UI 큐 종료 시 타이머와 남은 통지도 멈춘다.
        if (!_enqueue(Drain)) Dispose();
    }

    private void Drain()
    {
        bool error;
        lock (_sync)
        {
            if (_disposed) return;
            _queued = false;
            var remaining = Remaining();
            if (remaining > TimeSpan.Zero)
            {
                _timer.Change(remaining, Timeout.InfiniteTimeSpan);
                return;
            }
            error = _error;
            _dirty = _error = false;
        }
        _apply(error);
    }

    public void Dispose()
    {
        lock (_sync)
        {
            if (_disposed) return;
            _disposed = true;
            _dirty = _error = false;
            _timer.Dispose();
        }
    }
}
