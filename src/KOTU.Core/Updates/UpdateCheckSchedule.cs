namespace KOTU.Core.Updates;

/// <summary>앱 수명 첫 확인과 완료 후 24시간. 시계 되감기는 경과 시간, 절전은 UTC가 보완한다.</summary>
public sealed class UpdateCheckSchedule(TimeProvider? clock = null) : IDisposable
{
    public static readonly TimeSpan Interval = TimeSpan.FromHours(24);
    private readonly TimeProvider _clock = clock ?? TimeProvider.System;
    private readonly object _gate = new();
    private DateTimeOffset? _completedAt;
    private long _completedTimestamp;
    private bool _checking, _disposed;

    public DateTimeOffset? CompletedAt { get { lock (_gate) return _completedAt; } }
    public bool IsChecking { get { lock (_gate) return _checking; } }
    public TimeSpan Remaining
    {
        get
        {
            lock (_gate)
            {
                if (_completedAt is not { } completed) return TimeSpan.Zero;
                var elapsed = _clock.GetElapsedTime(_completedTimestamp);
                var wallElapsed = _clock.GetUtcNow() - completed;
                if (wallElapsed > elapsed) elapsed = wallElapsed;
                return elapsed >= Interval ? TimeSpan.Zero : Interval - elapsed;
            }
        }
    }

    public bool TryBegin()
    {
        lock (_gate)
        {
            if (_disposed || _checking || Remaining > TimeSpan.Zero) return false;
            _checking = true;
            return true;
        }
    }

    /// <summary>성공/실패 모두 같은 완료 기준을 쓴다. 해제 후 늦은 결과는 채택하지 않는다.</summary>
    public bool Complete()
    {
        lock (_gate)
        {
            if (!_checking || _disposed) return false;
            _checking = false;
            _completedAt = _clock.GetUtcNow();
            _completedTimestamp = _clock.GetTimestamp();
            return true;
        }
    }

    public void Dispose() { lock (_gate) { _disposed = true; _checking = false; } }
}
