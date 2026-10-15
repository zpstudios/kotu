namespace KOTU.Core.Threading;

/// <summary>표면 한 수명의 디스패처 병합 상태. 옛 콜백은 새 수명 상태를 만질 수 없다.</summary>
public sealed class ThumbnailRetryWakeup
{
    private readonly object _sync = new();
    private bool _closed, _queued;
    public bool TryQueue()
    {
        lock (_sync)
        {
            if (_closed || _queued) return false;
            return _queued = true;
        }
    }
    public bool Consume()
    {
        lock (_sync) { _queued = false; return !_closed; }
    }
    public void Close() { lock (_sync) { _closed = true; _queued = false; } }
}

public enum ThumbnailOutcome
{
    Available, CacheMiss, IconFallback, InvalidOrOversized, AccessDeniedOrMissing,
    OtherFailure, Timeout, Cancelled,
}

public readonly record struct ThumbnailAccessPlan(bool CachedOnly, bool ReadAudio)
{
    public static ThumbnailAccessPlan Create(bool automatic, bool canReadContent, bool wantAudio) =>
        new(automatic || !canReadContent, !automatic && canReadContent && wantAudio);
}

/// <summary>프로세스 경로별 자동 예산. 퇴출/환불/수명 재설정이 없어 VM 교체로 부활하지 않는다.</summary>
public sealed class ThumbnailRetryRegistry(int capacity = 4096, TimeProvider? clock = null)
{
    public static ThumbnailRetryRegistry Process { get; } = new();
    private readonly object _sync = new();
    private readonly Dictionary<string, State> _paths = new(StringComparer.OrdinalIgnoreCase);
    private readonly TimeProvider _clock = clock ?? TimeProvider.System;
    internal sealed class State
    {
        public int Active, Used;
        public bool Blocked, Eligible;
        public DateTimeOffset NextAllowed;
    }

    public (int Used, int Active, bool Blocked)? Inspect(string path)
    {
        lock (_sync) return _paths.TryGetValue(path, out var state) ? (state.Used, state.Active, state.Blocked) : null;
    }

    public TimeSpan? Delay(string path)
    {
        lock (_sync)
        {
            if (!_paths.TryGetValue(path, out var state) || state.Blocked || !state.Eligible || state.Used >= 2) return null;
            var remaining = state.NextAllowed - _clock.GetUtcNow();
            return remaining > TimeSpan.Zero ? remaining : TimeSpan.Zero;
        }
    }

    public Attempt? Begin(string path, bool automatic)
    {
        lock (_sync)
        {
            if (!_paths.TryGetValue(path, out var state))
            {
                if (automatic || _paths.Count >= capacity) return null;
                _paths.Add(path, state = new State());
            }
            if (automatic)
            {
                if (state.Active != 0 || state.Blocked || !state.Eligible || state.Used >= 2 ||
                    _clock.GetUtcNow() < state.NextAllowed) return null;
                state.Used++;
            }
            state.Active++;
            return new Attempt(this, state);
        }
    }

    public sealed class Attempt : IDisposable
    {
        private readonly ThumbnailRetryRegistry _owner;
        private State? _state;
        internal Attempt(ThumbnailRetryRegistry owner, State state) { _owner = owner; _state = state; }
        public void Complete(ThumbnailOutcome outcome, bool audioFailed = false)
        {
            lock (_owner._sync)
            {
                if (_state is not { } state) return;
                if (audioFailed || outcome is ThumbnailOutcome.Timeout or ThumbnailOutcome.Cancelled) state.Blocked = true;
                // 성공 픽셀은 표면끼리 공유하지 않는다. 한 표면의 성공이 다른 표면의 miss를 끝내지 않는다.
                if (!audioFailed && outcome is ThumbnailOutcome.CacheMiss or ThumbnailOutcome.IconFallback)
                {
                    state.Eligible = true;
                    state.NextAllowed = _owner._clock.GetUtcNow() + TimeSpan.FromSeconds(state.Used == 0 ? 2 : 8);
                }
                else if (state.Used > 0)
                    state.NextAllowed = _owner._clock.GetUtcNow() + TimeSpan.FromSeconds(8);
                state.Active--;
                _state = null;
            }
        }
        public void Dispose() => Complete(ThumbnailOutcome.Cancelled);
    }
}

/// <summary>한 VM의 예약 상태. UI 수명/가시성 입력만 받고 파일/셸에 접근하지 않는다.</summary>
public sealed class ThumbnailRetryCandidate(ThumbnailRetryRegistry registry, string path, TimeProvider? clock = null)
{
    private readonly TimeProvider _clock = clock ?? TimeProvider.System;
    private DateTimeOffset? _due;
    public void Observe(ThumbnailOutcome outcome, bool audioFailed = false)
    {
        _due = !audioFailed && (outcome is ThumbnailOutcome.CacheMiss or ThumbnailOutcome.IconFallback) &&
            registry.Delay(path) is { } delay ? _clock.GetUtcNow() + delay : null;
    }
    public void Stop() => _due = null;
    public TimeSpan? Remaining(bool visible, bool editing, bool current)
    {
        if (!current || registry.Delay(path) is not { } globalDelay) { Stop(); return null; }
        if (!visible || editing || _due is null) return null;
        var left = _due.Value - _clock.GetUtcNow();
        return left > globalDelay ? left : globalDelay;
    }
}
