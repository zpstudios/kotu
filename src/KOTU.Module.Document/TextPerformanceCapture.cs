using System.Diagnostics;
using System.Text.Json;
using KOTU.Core.Integration;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;

namespace KOTU.Module.Document;

/// <summary>A401 개발용 관찰자. 기본 off, 주기는 워커, UI에는 미완료 probe 한 개뿐이다.</summary>
internal sealed class TextPerformanceCapture : IDisposable
{
    private static int _nextSession;
    private readonly int _session = Interlocked.Increment(ref _nextSession);
    private readonly TextBox _editor;
    private readonly Func<TextPerfState> _readState;
    private readonly TextPerformanceData _data;
    private readonly TextPerformanceProbe _probe;
    private readonly System.Threading.Timer _timer;
    private readonly PointerEventHandler _wheelHandler;
    private ScrollViewer? _scroll;
    private bool _renderingSubscribed;
    private bool _applied;
    private int _disposed;
    private int _tickActive;
    private int _ticks;

    public TextPerformanceData? Data => Volatile.Read(ref _disposed) == 0 ? _data : null;
    public TextPerfOptions Options => _data.Options;
    public static bool IsEnabled => !DistributionPolicy.IsStandalone
        && Environment.GetEnvironmentVariable("KOTU_TEXT_PERF") == "1";

    public static TextPerformanceCapture? Create(TextBox editor, Func<TextPerfState> readState)
    {
        // Standalone은 환경변수로도 디스크 금지를 우회할 수 없다.
        if (DistributionPolicy.IsStandalone) return null;
        var options = TextPerfOptions.Parse(false, Environment.GetEnvironmentVariable("KOTU_TEXT_PERF"),
            Environment.GetEnvironmentVariable("KOTU_TEXT_PERF_WRAP"),
            Environment.GetEnvironmentVariable("KOTU_TEXT_PERF_DECOR"),
            Environment.GetEnvironmentVariable("KOTU_TEXT_PERF_SECONDS"));
        return options is null ? null : new(editor, readState, options);
    }

    private TextPerformanceCapture(TextBox editor, Func<TextPerfState> readState, TextPerfOptions options)
    {
        _editor = editor;
        _readState = readState;
        _data = new(options);
        var queue = editor.DispatcherQueue;
        _probe = new(action => queue.TryEnqueue(() => action()), OnProbe);
        _wheelHandler = OnWheel;
        editor.TextChanged += OnTextChanged;
        editor.SelectionChanged += OnSelectionChanged;
        editor.SizeChanged += OnSizeChanged;
        editor.LayoutUpdated += OnLayoutUpdated;
        editor.AddHandler(UIElement.PointerWheelChangedEvent, _wheelHandler, handledEventsToo: true);
        _timer = new System.Threading.Timer(OnWorkerTick, null, 50, 50);
    }

    public void Applied()
    {
        if (Volatile.Read(ref _disposed) != 0 || _renderingSubscribed) return;
        _applied = true;
        _data.UpdateState(_readState());
        HookScroll();
        _renderingSubscribed = true;
        CompositionTarget.Rendering += OnFirstRendering;
    }

    private void OnFirstRendering(object? sender, object e)
    {
        CompositionTarget.Rendering -= OnFirstRendering;
        _renderingSubscribed = false;
        if (Volatile.Read(ref _disposed) == 0) _data.FirstRendering();
    }

    private void OnTextChanged(object sender, TextChangedEventArgs e) => _data.Event(TextPerfEvent.TextChanged);
    private void OnSelectionChanged(object sender, RoutedEventArgs e) => _data.Event(TextPerfEvent.SelectionChanged);
    private void OnSizeChanged(object sender, SizeChangedEventArgs e) => _data.Event(TextPerfEvent.SizeChanged);
    private void OnWheel(object sender, PointerRoutedEventArgs e) => _data.Event(TextPerfEvent.PointerWheel);
    private void OnLayoutUpdated(object? sender, object e)
    {
        _data.Event(TextPerfEvent.LayoutUpdated);
        HookScroll();
    }
    private void HookScroll()
    {
        if (_scroll is not null || _editor.Visibility != Visibility.Visible) return;
        if (FindScroll(_editor) is not { } scroll) return;
        _scroll = scroll;
        scroll.ViewChanged += OnViewChanged;
    }
    private static ScrollViewer? FindScroll(DependencyObject parent)
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            if (child is ScrollViewer found) return found;
            if (FindScroll(child) is { } nested) return nested;
        }
        return null;
    }
    private void OnViewChanged(object? sender, ScrollViewerViewChangedEventArgs e)
    {
        _data.Event(e.IsIntermediate ? TextPerfEvent.ScrollIntermediate : TextPerfEvent.ScrollFinal);
        RecordScroll();
    }

    private void RecordScroll()
    {
        if (_scroll is { } scroll)
            _data.Scroll(new(true, scroll.HorizontalOffset, scroll.VerticalOffset,
                scroll.ViewportWidth, scroll.ViewportHeight, scroll.ExtentWidth, scroll.ExtentHeight));
    }

    private void OnWorkerTick(object? state)
    {
        if (Volatile.Read(ref _disposed) != 0 || Interlocked.Exchange(ref _tickActive, 1) != 0) return;
        try
        {
            _probe.Request();
            if (_ticks++ % 20 == 0)
            {
                using var process = Process.GetCurrentProcess();
                _data.Memory(process.WorkingSet64, process.PrivateMemorySize64);
            }
        }
        catch { /* 계측 실패를 제품 동작으로 전파하지 않는다. */ }
        finally { Volatile.Write(ref _tickActive, 0); }
    }

    private void OnProbe(long requested, long acknowledged)
    {
        if (Volatile.Read(ref _disposed) != 0) return;
        if (_data.IsExpired)
        {
            // 마지막 probe가 기한을 넘겨 돌아오면 지연을 버리지 않고 별도 종료 하한으로 남긴다.
            Stop("window-ended", Math.Max(0, (acknowledged - requested) * 1000.0 / Stopwatch.Frequency));
            return;
        }
        _data.Probe(requested, acknowledged, GC.GetAllocatedBytesForCurrentThread());
        if (_applied) _data.UpdateState(_readState());
        RecordScroll();
    }

    public void Dispose() => Stop("view-ended");

    public void Stop(string reason, double endingProbeAgeMs = 0)
    {
        // 호출자는 UI 스레드. 워커 타이머를 기다리지 않고, 남은 콜백의 대상을 먼저 무효화한다.
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        var pending = _probe.Pending;
        var pendingAge = pending
            ? Math.Max(0, (Stopwatch.GetTimestamp() - _probe.Requested) * 1000.0 / Stopwatch.Frequency)
            : 0;
        _probe.Dispose();
        _timer.Dispose();
        _data.Stop();
        _editor.TextChanged -= OnTextChanged;
        _editor.SelectionChanged -= OnSelectionChanged;
        _editor.SizeChanged -= OnSizeChanged;
        _editor.LayoutUpdated -= OnLayoutUpdated;
        _editor.RemoveHandler(UIElement.PointerWheelChangedEvent, _wheelHandler);
        if (_scroll is { } scroll) scroll.ViewChanged -= OnViewChanged;
        _scroll = null;
        if (_renderingSubscribed) CompositionTarget.Rendering -= OnFirstRendering;
        _renderingSubscribed = false;
        // 파일 I/O·직렬화·요약 객체 생성은 종료를 통지한 UI가 아니라 워커에서만 한다.
        var data = _data;
        var session = _session;
        SummaryWriter.Enqueue(new(data, session, reason, pending, pendingAge, endingProbeAgeMs));
    }

    private sealed record WriteRequest(TextPerformanceData Data, int Session, string Reason,
        bool Pending, double PendingAge, double EndingProbeAge);

    private static class SummaryWriter
    {
        private static readonly object Gate = new();
        private static readonly Queue<WriteRequest> Pending = new(8);
        private static bool _running;

        public static void Enqueue(WriteRequest request)
        {
            if (DistributionPolicy.IsStandalone) return;
            lock (Gate)
            {
                if (Pending.Count == 8) Pending.Dequeue();
                Pending.Enqueue(request);
                if (_running) return;
                _running = true;
                _ = Task.Run(Drain);
            }
        }

        private static void Drain()
        {
            while (true)
            {
                WriteRequest request;
                lock (Gate)
                {
                    if (Pending.Count == 0) { _running = false; return; }
                    request = Pending.Dequeue();
                }
                try
                {
                    var summary = request.Data.Snapshot(request.Session, request.Reason, request.Pending,
                        request.PendingAge, request.EndingProbeAge);
                    var directory = Path.Combine(Path.GetTempPath(), "KOTU", "text-performance");
                    Directory.CreateDirectory(directory);
                    var json = JsonSerializer.Serialize(summary, new JsonSerializerOptions { WriteIndented = true });
                    // 고정 enum과 숫자만 담는다. 슬롯 8개·각 128 KiB 이내로 결과 파일도 제한한다.
                    if (json.Length > 128 * 1024) continue;
                    File.WriteAllText(Path.Combine(directory, $"session-{(summary.Session - 1) % 8}.json"), json);
                }
                catch { /* 읽기 전용 임시 폴더·종료 경합도 문서 작업을 막지 않는다. */ }
            }
        }
    }
}
