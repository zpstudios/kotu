using System.Diagnostics;
using KOTU.DocumentModel;

namespace KOTU.Module.Document;

// A401: UI 형식·본문·경로를 모르는 고정 크기 계측 모델. 시험에서도 같은 소스를 링크한다.
internal enum TextPerfPhase { Initial, Settling, Stable }
internal enum TextPerfMetric
{
    OpenWorker, TextSet, TextGet, NormalizeUi, NormalizeWorker, LineIndexBuild,
    LineIndexReuse, CountLines, DecorPass, RectCall, FirstRendering,
    ProbeDelay, ProbeInterval, Count
}
internal enum TextPerfEvent
{
    TextChanged, SelectionChanged, SizeChanged, LayoutUpdated, ScrollIntermediate,
    ScrollFinal, PointerWheel, Zoom, DecorLayout, DecorScrollFinal, DecorScrollTimer,
    DecorVisibility, DecorMode, DecorTheme, DecorDiagnostic, DecorDisabled, ScrollOffsetChanged, Count
}

internal sealed record TextPerfOptions(bool Wrap, int Decor, int Seconds)
{
    // Decor: -1 = 제품 정책, 0 = 완전 억제, 1 = 상한 자동 억제만 우회하는 실험.
    public static TextPerfOptions? Parse(bool standalone, string? enabled, string? wrap,
        string? decor, string? seconds)
    {
        if (standalone || enabled != "1") return null;
        return new(wrap != "off", decor == "off" ? 0 : decor == "on" ? 1 : -1,
            int.TryParse(seconds, out var value) ? Math.Clamp(value, 5, 120) : 30);
    }
}

internal readonly record struct TextPerfState(int Characters, int Lines, int ZoomPercent,
    bool Wrap, bool Guides, bool Marks, bool ViewSuppressed, bool DecorDisabled,
    bool Capped, bool Truncated, double Width, double Height);

internal readonly record struct TextPerfScrollState(bool Hooked, double HorizontalOffset, double VerticalOffset,
    double ViewportWidth, double ViewportHeight, double ExtentWidth, double ExtentHeight);

internal sealed record TextPerfMetricSummary(long Count, double TotalMs, double MaxMs,
    double P50UpperMs, double P95UpperMs, long AllocatedBytes, long HistogramSamples);

internal sealed class TextPerfDistribution
{
    // 1 μs부터 1.25배 간격의 상계 128개. 표본을 보관하지 않아 세션 길이와 무관하게 유계다.
    private readonly long[] _buckets = new long[128];
    private long _count;
    private long _samples;
    private double _totalMs;
    private double _maxMs;
    private long _allocated;

    public void Add(double ms, long allocated = 0)
    {
        AddBatch(1, ms, ms, allocated);
        var bucket = ms <= 0.001 ? 0 : Math.Min(127, (int)Math.Ceiling(Math.Log(ms / 0.001, 1.25)));
        _buckets[bucket]++;
        _samples++;
    }

    public void AddBatch(long count, double totalMs, double maxMs, long allocated = 0)
    {
        _count += count;
        _totalMs += totalMs;
        _maxMs = Math.Max(_maxMs, maxMs);
        _allocated += Math.Max(0, allocated);
    }

    public TextPerfMetricSummary Snapshot() => new(_count, _totalMs, _maxMs,
        Percentile(0.50), Percentile(0.95), _allocated, _samples);

    private double Percentile(double fraction)
    {
        if (_samples == 0) return 0;
        var rank = (long)Math.Ceiling(_samples * fraction);
        long cumulative = 0;
        for (var i = 0; i < _buckets.Length; i++)
            if ((cumulative += _buckets[i]) >= rank)
                return Math.Min(_maxMs, 0.001 * Math.Pow(1.25, i));
        return _maxMs;
    }
}

internal sealed record TextPerfPhaseSummary(string Phase, double DurationMs,
    Dictionary<string, TextPerfMetricSummary> Metrics, Dictionary<string, long> Events,
    long UiAllocatedBytes, long MemorySamples, long MaxWorkingSetBytes, long MaxPrivateBytes);

internal sealed record TextPerfSummary(int Schema, int Pid, int Session, string ModuleMvid,
    string ModuleVersion, TextPerfOptions Options, string EndReason, bool FirstRenderingObserved,
    bool StableWindowCompleted, bool ProbePendingAtStop, double PendingProbeAgeMs, double EndingProbeDelayMs,
    int ActiveProbesAfterStop, TextPerfState FirstState, TextPerfState LastState,
    long StableConfigurationChanges, TextPerfScrollState FirstScroll, TextPerfScrollState LastScroll,
    double MaxHorizontalMovement, double MaxVerticalMovement, TextPerfPhaseSummary[] Phases);

internal sealed class TextPerformanceData
{
    private readonly object _gate = new();
    private readonly Func<long> _clock;
    private readonly long _frequency;
    private readonly long _started;
    private readonly TextPerfDistribution[,] _metrics = new TextPerfDistribution[3, (int)TextPerfMetric.Count];
    private readonly long[,] _events = new long[3, (int)TextPerfEvent.Count];
    private readonly long[] _uiAllocated = new long[3];
    private readonly long[] _memorySamples = new long[3];
    private readonly long[] _workingSet = new long[3];
    private readonly long[] _privateBytes = new long[3];
    private long _firstRendering;
    private bool _hasFirstRendering;
    private long _ended;
    private bool _closed;
    private TextPerfState _firstState;
    private TextPerfState _lastState;
    private bool _hasState;
    private long _stableConfigurationChanges;
    private long _lastProbe;
    private long _lastUiAllocated;
    private bool _hasProbe;
    private TextPerfPhase _lastProbePhase;
    private TextPerfScrollState _firstScroll;
    private TextPerfScrollState _lastScroll;
    private double _maxHorizontalMovement;
    private double _maxVerticalMovement;
    private int _knownLines;

    public TextPerfOptions Options { get; }
    public bool IsActive { get { lock (_gate) return !_closed && !ExpiredAt(_clock()); } }
    public bool IsExpired { get { lock (_gate) return ExpiredAt(_clock()); } }
    public int KnownLines { get { lock (_gate) return _knownLines; } }

    public TextPerformanceData(TextPerfOptions options, Func<long>? clock = null, long frequency = 0)
    {
        Options = options;
        _clock = clock ?? Stopwatch.GetTimestamp;
        _frequency = frequency == 0 ? Stopwatch.Frequency : frequency;
        _started = _clock();
        for (var phase = 0; phase < 3; phase++)
            for (var metric = 0; metric < (int)TextPerfMetric.Count; metric++)
                _metrics[phase, metric] = new TextPerfDistribution();
    }

    private double Milliseconds(long ticks) => ticks * 1000.0 / _frequency;
    private long StableStart => _firstRendering + 2 * _frequency;
    private bool ExpiredAt(long now) => _hasFirstRendering
        ? now >= StableStart + Options.Seconds * _frequency
        : now >= _started + 180 * _frequency;
    private TextPerfPhase PhaseAt(long now) => !_hasFirstRendering || now < _firstRendering
        ? TextPerfPhase.Initial : now < StableStart ? TextPerfPhase.Settling : TextPerfPhase.Stable;

    public TextPerfMeasurement Measure(TextPerfMetric metric)
    {
        lock (_gate)
        {
            var now = _clock();
            return _closed || ExpiredAt(now) ? default : new(this, metric, PhaseAt(now), now,
                GC.GetAllocatedBytesForCurrentThread());
        }
    }

    internal void Complete(TextPerfMetric metric, TextPerfPhase phase, long start, long allocated)
    {
        var end = _clock();
        var bytes = GC.GetAllocatedBytesForCurrentThread() - allocated;
        lock (_gate)
            if (!_closed) _metrics[(int)phase, (int)metric].Add(Milliseconds(end - start), bytes);
    }

    public void RecordRectBatch(long count, long ticks, long maxTicks, TextPerfPhase phase)
    {
        lock (_gate)
            if (!_closed && count > 0)
                _metrics[(int)phase, (int)TextPerfMetric.RectCall].AddBatch(count, Milliseconds(ticks), Milliseconds(maxTicks));
    }

    public TextPerfPhase CurrentPhase { get { lock (_gate) return PhaseAt(_clock()); } }

    public void Event(TextPerfEvent kind)
    {
        lock (_gate)
        {
            var now = _clock();
            if (!_closed && !ExpiredAt(now)) _events[(int)PhaseAt(now), (int)kind]++;
        }
    }

    public void UpdateState(TextPerfState state)
    {
        lock (_gate)
        {
            if (_closed || ExpiredAt(_clock())) return;
            if (_hasState && _lastState != state && PhaseAt(_clock()) == TextPerfPhase.Stable)
                _stableConfigurationChanges++;
            if (!_hasState) { _firstState = state; _hasState = true; }
            _lastState = state;
        }
    }

    public void UpdateLineIndex(TextLineIndex index, string text)
    {
        if (!IsActive) { index.Update(text); return; }
        // 재사용과 새 색인을 구분하되 같은 실제 Update를 한 번만 부른다.
        var start = _clock();
        var bytes = GC.GetAllocatedBytesForCurrentThread();
        var phase = CurrentPhase;
        var rebuilt = index.Update(text);
        Complete(rebuilt ? TextPerfMetric.LineIndexBuild : TextPerfMetric.LineIndexReuse, phase, start, bytes);
        lock (_gate) _knownLines = index.Count;
    }

    public void Scroll(TextPerfScrollState state)
    {
        lock (_gate)
        {
            var now = _clock();
            if (_closed || ExpiredAt(now) || !state.Hooked) return;
            if (!_firstScroll.Hooked) _firstScroll = state;
            if (_lastScroll.Hooked && (state.HorizontalOffset != _lastScroll.HorizontalOffset
                || state.VerticalOffset != _lastScroll.VerticalOffset))
                _events[(int)PhaseAt(now), (int)TextPerfEvent.ScrollOffsetChanged]++;
            _maxHorizontalMovement = Math.Max(_maxHorizontalMovement, Math.Abs(state.HorizontalOffset - _firstScroll.HorizontalOffset));
            _maxVerticalMovement = Math.Max(_maxVerticalMovement, Math.Abs(state.VerticalOffset - _firstScroll.VerticalOffset));
            _lastScroll = state;
        }
    }

    public void FirstRendering()
    {
        lock (_gate)
        {
            if (_closed || _hasFirstRendering) return;
            _firstRendering = _clock();
            _hasFirstRendering = true;
            _metrics[0, (int)TextPerfMetric.FirstRendering].Add(Milliseconds(_firstRendering - _started));
        }
    }

    public void Probe(long requested, long acknowledged, long allocated)
    {
        lock (_gate)
        {
            if (_closed || ExpiredAt(acknowledged)) return;
            var phase = PhaseAt(acknowledged);
            // 경계를 가로지르는 공백을 안정 스크롤 표본에 섞지 않는다.
            if (PhaseAt(requested) == phase)
                _metrics[(int)phase, (int)TextPerfMetric.ProbeDelay].Add(Milliseconds(acknowledged - requested));
            if (_hasProbe && _lastProbePhase == phase)
            {
                _metrics[(int)phase, (int)TextPerfMetric.ProbeInterval].Add(Milliseconds(acknowledged - _lastProbe));
                _uiAllocated[(int)phase] += Math.Max(0, allocated - _lastUiAllocated);
            }
            _lastProbe = acknowledged;
            _lastProbePhase = phase;
            _lastUiAllocated = allocated;
            _hasProbe = true;
        }
    }

    public void Memory(long workingSet, long privateBytes)
    {
        lock (_gate)
        {
            var now = _clock();
            if (_closed || ExpiredAt(now)) return;
            var phase = (int)PhaseAt(now);
            _memorySamples[phase]++;
            _workingSet[phase] = Math.Max(_workingSet[phase], workingSet);
            _privateBytes[phase] = Math.Max(_privateBytes[phase], privateBytes);
        }
    }

    public bool Stop()
    {
        lock (_gate)
        {
            if (_closed) return false;
            _closed = true;
            _ended = _clock();
            return true;
        }
    }

    public TextPerfSummary Snapshot(int session, string reason, bool pending, double pendingAgeMs, double endingProbeDelayMs = 0)
    {
        lock (_gate)
        {
            if (!_closed) throw new InvalidOperationException("Stop before taking a summary.");
            var phases = new TextPerfPhaseSummary[3];
            for (var p = 0; p < 3; p++)
            {
                var metrics = new Dictionary<string, TextPerfMetricSummary>();
                var events = new Dictionary<string, long>();
                for (var m = 0; m < (int)TextPerfMetric.Count; m++)
                    metrics.Add(((TextPerfMetric)m).ToString(), _metrics[p, m].Snapshot());
                for (var e = 0; e < (int)TextPerfEvent.Count; e++)
                    events.Add(((TextPerfEvent)e).ToString(), _events[p, e]);
                var start = p == 0 ? _started : p == 1 ? _firstRendering : StableStart;
                var end = p == 0 ? (_hasFirstRendering ? _firstRendering : _ended) : p == 1
                    ? Math.Min(_ended, StableStart) : Math.Min(_ended, StableStart + Options.Seconds * _frequency);
                var duration = p > 0 && !_hasFirstRendering ? 0 : Math.Max(0, Milliseconds(end - start));
                phases[p] = new(((TextPerfPhase)p).ToString(), duration, metrics, events,
                    _uiAllocated[p], _memorySamples[p], _workingSet[p], _privateBytes[p]);
            }
            var assembly = typeof(TextPerformanceData).Assembly;
            return new(1, Environment.ProcessId, session, assembly.ManifestModule.ModuleVersionId.ToString(),
                assembly.GetName().Version?.ToString() ?? "", Options, reason, _hasFirstRendering,
                _hasFirstRendering && _ended >= StableStart + Options.Seconds * _frequency,
                pending, pendingAgeMs, endingProbeDelayMs, 0, _firstState, _lastState, _stableConfigurationChanges,
                _firstScroll, _lastScroll, _maxHorizontalMovement, _maxVerticalMovement, phases);
        }
    }
}

internal readonly struct TextPerfMeasurement : IDisposable
{
    private readonly TextPerformanceData? _owner;
    private readonly TextPerfMetric _metric;
    private readonly TextPerfPhase _phase;
    private readonly long _start;
    private readonly long _allocated;
    public TextPerfMeasurement(TextPerformanceData owner, TextPerfMetric metric, TextPerfPhase phase,
        long start, long allocated) => (_owner, _metric, _phase, _start, _allocated) = (owner, metric, phase, start, allocated);
    public void Dispose() => _owner?.Complete(_metric, _phase, _start, _allocated);
}

// DispatcherQueue 예약을 취소할 수 없으므로 해제 뒤 남은 콜백은 어떤 대상도 호출하지 않는다.
internal sealed class TextPerformanceProbe : IDisposable
{
    private readonly object _gate = new();
    private Func<Action, bool>? _enqueue;
    private Action<long, long>? _acknowledge;
    private readonly Func<long> _clock;
    private readonly Action _callback;
    private bool _pending;
    private long _requested;
    public TextPerformanceProbe(Func<Action, bool> enqueue, Action<long, long> acknowledge, Func<long>? clock = null)
    {
        _enqueue = enqueue;
        _acknowledge = acknowledge;
        _clock = clock ?? Stopwatch.GetTimestamp;
        _callback = Acknowledge;
    }
    public bool Pending { get { lock (_gate) return _pending; } }
    public long Requested { get { lock (_gate) return _requested; } }
    public void Request()
    {
        lock (_gate)
        {
            if (_enqueue is null || _pending) return;
            _pending = true;
            _requested = _clock();
            try { if (!_enqueue(_callback)) _pending = false; }
            catch { _pending = false; }
        }
    }
    private void Acknowledge()
    {
        lock (_gate)
        {
            if (!_pending || _acknowledge is null) return;
            _pending = false;
            _acknowledge(_requested, _clock());
        }
    }
    public void Dispose()
    {
        lock (_gate)
        {
            _pending = false;
            _enqueue = null;
            _acknowledge = null;
        }
    }
}
