namespace WinUtil.Core.Threading;

/// <summary>
/// 주기 폴링 전용 워커(A42): 전용 스레드의 장기 루프에서 poll을 반복 실행하고 스냅샷을
/// 구독자에게 발행한다. 하드웨어 WMI 폴링처럼 "창이 몇 개든 수집은 1회"가 목표인 곳에
/// 프로세스당 1개를 공유한다(창 여러 개 = 구독 여러 개, 수집 루프는 하나).
///
/// - 구독자가 없으면 루프는 휴면(수집 비용 0). 첫 구독이 루프를 깨워 즉시 1회 폴링한다.
/// - <see cref="Poke"/>는 대기 중인 간격을 건너뛰고 다음 폴링을 즉시 당긴다(수동 Refresh용).
/// - 우선순위 기본 BelowNormal — 배경 폴링이 재생·UI와 CPU를 다투지 않게(A42 스레드 예산).
/// - 간격은 "이전 폴링 종료 → 다음 폴링 시작" 기준: 수집이 간격보다 오래 걸려도 겹치지 않는다.
/// - 스냅샷 핸들러는 워커 스레드에서 호출된다 — UI 반영은 구독자가 디스패치할 책임.
/// - poll 예외는 삼키고 다음 주기에 재시도한다(일시적 WMI 실패 대응).
/// </summary>
public sealed class PollingWorker<T> : IDisposable
{
    private readonly Func<T> _poll;
    private TimeSpan _interval; // A29: 런타임 변경 가능 — _gate로 보호
    private readonly object _gate = new();
    private readonly List<Action<T>> _subscribers = [];
    private readonly ManualResetEventSlim _active = new(false); // 구독자 있음 → 루프 가동
    private readonly AutoResetEvent _wake = new(false);         // 간격 대기 중단(Poke/새 구독/종료)
    private volatile bool _disposed;

    public PollingWorker(string name, TimeSpan interval, Func<T> poll,
        ThreadPriority priority = ThreadPriority.BelowNormal)
    {
        _poll = poll;
        _interval = interval;
        var thread = new Thread(Loop) { IsBackground = true, Name = name, Priority = priority };
        thread.Start();
    }

    /// <summary>
    /// 스냅샷 구독. 첫 구독이면 휴면 중인 루프를 깨워 즉시 폴링하고, 이미 돌고 있으면
    /// 남은 간격을 건너뛰어 새 구독자가 오래 기다리지 않게 한다.
    /// 반환된 IDisposable을 버리면 해지 — 마지막 구독 해지 시 루프는 다시 휴면한다.
    /// </summary>
    public IDisposable Subscribe(Action<T> handler)
    {
        bool wasActive;
        lock (_gate)
        {
            wasActive = _subscribers.Count > 0;
            _subscribers.Add(handler);
            _active.Set();
        }
        if (wasActive) _wake.Set();
        return new Subscription(this, handler);
    }

    /// <summary>대기 중인 간격을 건너뛰고 즉시 다음 폴링을 당긴다(수동 새로고침).</summary>
    public void Poke() => _wake.Set();

    /// <summary>
    /// 폴링 간격(A29: 하드웨어 100/300/1000ms 선택). 바꾸면 대기 중인 이전 간격을
    /// 건너뛰고 즉시 1회 폴링한 뒤 새 간격으로 돈다 — 변경이 바로 체감되게.
    /// </summary>
    public TimeSpan Interval
    {
        get { lock (_gate) return _interval; }
        set
        {
            lock (_gate) _interval = value;
            _wake.Set();
        }
    }

    private void Unsubscribe(Action<T> handler)
    {
        lock (_gate)
        {
            _subscribers.Remove(handler);
            if (_subscribers.Count == 0) _active.Reset();
        }
    }

    private void Loop()
    {
        while (!_disposed)
        {
            _active.Wait(); // 구독자 생길 때까지 휴면
            if (_disposed) break;
            PollOnce();
            TimeSpan interval;
            lock (_gate) interval = _interval; // TimeSpan은 8바이트 — 찢긴 읽기 방지
            _wake.WaitOne(interval);
        }
        // 이벤트는 의도적으로 Dispose하지 않는다 — Dispose 스레드의 Set과 경합하면
        // ObjectDisposedException이 나기 때문. 이 워커는 프로세스 수명 공유 싱글턴 용도라
        // 핸들 2개는 프로세스 종료 때 정리되면 충분하다.
    }

    private void PollOnce()
    {
        T snapshot;
        try
        {
            snapshot = _poll();
        }
        catch
        {
            return; // 일시 실패 — 발행 없이 다음 주기에 재시도
        }

        Action<T>[] handlers;
        lock (_gate) handlers = [.. _subscribers];
        foreach (var handler in handlers)
        {
            try
            {
                handler(snapshot);
            }
            catch
            {
                // 구독자 예외가 폴링 루프를 죽이면 안 된다.
            }
        }
    }

    /// <summary>루프를 종료시킨다. 진행 중인 poll 1회는 끝까지 돌 수 있다(스레드는 IsBackground).</summary>
    public void Dispose()
    {
        _disposed = true;
        _active.Set();
        _wake.Set();
    }

    /// <summary>해지 토큰 — 중복 Dispose에 안전하고, 참조를 끊어 재해지를 막는다.</summary>
    private sealed class Subscription(PollingWorker<T> owner, Action<T> handler) : IDisposable
    {
        private Action<T>? _handler = handler;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _handler, null) is { } h) owner.Unsubscribe(h);
        }
    }
}
