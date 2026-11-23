#if KOTU_DESIGN_LIVE_PROBE
using System.Diagnostics;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using KOTU.App.Appearance;
using KOTU.Core.Appearance;
using KOTU.Core.Integration;
using KOTU.Module.Hardware;
using KOTU.Module.Record;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.Foundation;

namespace KOTU.App.Integration;

// Opt-in observation of the real module path, separate from the assertion harness.
// No Loaded handlers, style/template reads, item enumeration or LiveDesign registration.
// All native reads happen at one-second late samples, never inside Render/discovery callbacks.
internal sealed class DesignLiveModuleProbe : IDisposable
{
    private const int MaximumSamples = 180, MaximumNodesPerGrid = 512, MaximumIconsPerGrid = 32;
    private static readonly BindingFlags PrivateInstance = BindingFlags.Instance | BindingFlags.NonPublic;
    private static readonly string[] RecordFields = ["_mode", "_source", "_camera", "_windows", "_microphone", "_output"];
    private static readonly Lazy<ProbeSink?> Sink = new(CreateSink);
    private static int s_nextSession;
    private readonly int _session = ++s_nextSession;
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(1) };
    private readonly ProbeSink _sink;
    private FrameworkElement? _view, _windowRoot;
    private int _samples;

    private DesignLiveModuleProbe(FrameworkElement view, FrameworkElement windowRoot, ProbeSink sink)
    {
        _view = view;
        _windowRoot = windowRoot;
        _sink = sink;
        _timer.Tick += OnTick;
        _sink.Write(new { Event = "session-start", Session = _session, Module = view.GetType().Name,
            MaximumSamples, IntervalMilliseconds = 1000, Timestamp = Stopwatch.GetTimestamp() });
        _timer.Start();
    }

    internal static DesignLiveModuleProbe? TryStart(UIElement view, FrameworkElement windowRoot)
    {
        if (!DistributionPolicy.IsTestBuild || view is not (HardwareView or RecordView) || Sink.Value is not { } sink) return null;
        return new DesignLiveModuleProbe((FrameworkElement)view, windowRoot, sink);
    }

    private static ProbeSink? CreateSink()
    {
        var path = Environment.GetEnvironmentVariable("KOTU_DESIGN_LIVE_PROBE_OUTPUT");
        if (string.IsNullOrWhiteSpace(path) || !System.IO.Path.IsPathFullyQualified(path)) return null;
        try { return new ProbeSink(path); }
        catch (Exception error)
        {
            // Refuse existing/missing-parent/unwritable output; never overwrite evidence or fail the app.
            Debug.WriteLine($"Live module probe disabled: {error.GetType().Name}");
            return null;
        }
    }

    private void OnTick(object? sender, object args)
    {
        if (_view is not { } view || _windowRoot is not { } windowRoot) return;
        try
        {
            _samples++;
            var started = Stopwatch.GetTimestamp();
            var observation = view switch
            {
                HardwareView hardware => CaptureHardware(hardware),
                RecordView record => CaptureRecord(record, windowRoot),
                _ => null,
            };
            _sink.Write(new { Event = "sample", Session = _session, Sample = _samples, Timestamp = started,
                CapturedUtc = DateTime.UtcNow, Design = DesignRuntime.Current.Id, Module = view.GetType().Name,
                view.IsLoaded, WindowRootLoaded = windowRoot.IsLoaded,
                CaptureMilliseconds = Stopwatch.GetElapsedTime(started).TotalMilliseconds, Observation = observation });
            if (_samples >= MaximumSamples) Stop("sample-limit");
        }
        catch (Exception error)
        {
            // Do not persist exception messages containing user data or paths.
            _sink.Write(new { Event = "capture-error", Session = _session, Sample = _samples, ErrorType = error.GetType().FullName });
            Stop("capture-error");
        }
    }

    private object CaptureHardware(HardwareView view)
    {
        var type = typeof(HardwareView);
        var root = type.GetField("Root", PrivateInstance)?.GetValue(view) as StackPanel
            ?? throw new MissingFieldException("Hardware specification root");
        var signature = type.GetField("_dataSignature", PrivateInstance)?.GetValue(view) as string
            ?? throw new MissingFieldException("Hardware specification signature");
        var title = root.Children.OfType<TextBlock>().FirstOrDefault();
        var row = root.Children.OfType<Grid>().FirstOrDefault();
        var labels = row?.Children.OfType<TextBlock>().ToArray() ?? [];
        var renderCount = type.GetProperty("DiagnosticRenderCount", PrivateInstance)?.GetValue(view) as int?
            ?? throw new MissingMemberException("Hardware Render count");
        var renderTimestamp = type.GetProperty("DiagnosticRenderTimestamp", PrivateInstance)?.GetValue(view) as long?
            ?? throw new MissingMemberException("Hardware Render timestamp");
        return new { RenderCount = renderCount, RenderTimestamp = renderTimestamp,
            SignatureLength = signature.Length, SignatureDigest = _sink.Digest(signature),
            RootChildren = root.Children.Count, Root = Geometry(root),
            Title = title is null ? null : CaptureText(title, false),
            Label = labels.Length < 1 ? null : CaptureText(labels[0], false),
            Value = labels.Length < 2 ? null : CaptureText(labels[1], false),
            Scope = "First actual specification title/row; one-second samples can skip Render generations." };
    }

    private object CaptureRecord(RecordView view, FrameworkElement windowRoot)
    {
        return RecordFields.Select(field =>
        {
            var grid = typeof(RecordView).GetField(field, PrivateInstance)?.GetValue(view) as GridView
                ?? throw new MissingFieldException("Record source grid " + field);
            var found = Descendants(grid, MaximumNodesPerGrid, out var truncated);
            var icons = found.OfType<FontIcon>().Take(MaximumIconsPerGrid).Select(icon =>
            {
                var descendants = Descendants(icon, 16, out var innerTruncated);
                var container = FindContainer(icon, grid);
                return new { Icon = CaptureIcon(icon),
                    InWindowBounds = Intersects(icon, windowRoot), InGridBounds = Intersects(icon, grid),
                    ContainerIdentity = container is null ? null : Identity(container), Selected = container?.IsSelected,
                    InnerTraversalTruncated = innerTruncated,
                    InnerTexts = descendants.OfType<TextBlock>().Select(text => CaptureText(text, true)).ToArray() };
            }).ToArray();
            return new { Grid = field, Geometry = Geometry(grid), TraversalTruncated = truncated,
                IconsTruncated = found.OfType<FontIcon>().Skip(MaximumIconsPerGrid).Any(), Icons = icons };
        }).ToArray();
    }

    private static object CaptureIcon(FontIcon icon) => new { Identity = Identity(icon), Geometry = Geometry(icon),
        FontFamily = icon.FontFamily.Source, icon.FontSize, GlyphCodeUnits = CodeUnits(icon.Glyph),
        DesignState = LiveDesign.CapturePassiveDesignState(icon) };

    private static object CaptureText(TextBlock text, bool glyphText) => new { Identity = Identity(text), Geometry = Geometry(text),
        FontFamily = text.FontFamily.Source, text.FontSize, FontWeight = text.FontWeight.Weight,
        TextLength = text.Text.Length, GlyphCodeUnits = glyphText ? CodeUnits(text.Text) : null,
        DesignState = LiveDesign.CapturePassiveDesignState(text) };

    private static string[] CodeUnits(string value) => value.Take(16).Select(unit => $"U+{(int)unit:X4}").ToArray();
    private static string Identity(object value) => $"managed:{RuntimeHelpers.GetHashCode(value):X8}";
    private static object Geometry(FrameworkElement element) => new { element.IsLoaded, element.ActualWidth,
        element.ActualHeight, Visibility = element.Visibility.ToString(), element.Opacity };

    private static bool? Intersects(FrameworkElement element, FrameworkElement reference)
    {
        try
        {
            var bounds = element.TransformToVisual(reference).TransformBounds(new Rect(0, 0, element.ActualWidth, element.ActualHeight));
            return bounds.Width > 0 && bounds.Height > 0 && bounds.Right > 0 && bounds.Bottom > 0 &&
                bounds.Left < reference.ActualWidth && bounds.Top < reference.ActualHeight;
        }
        catch { return null; }
    }

    private static GridViewItem? FindContainer(DependencyObject element, GridView grid)
    {
        for (var depth = 0; depth < 32 && !ReferenceEquals(element, grid); depth++)
        {
            if (element is GridViewItem item) return item;
            if (VisualTreeHelper.GetParent(element) is not { } parent) break;
            element = parent;
        }
        return null;
    }

    private static List<DependencyObject> Descendants(DependencyObject root, int limit, out bool truncated)
    {
        var found = new List<DependencyObject>();
        var pending = new Stack<DependencyObject>();
        pending.Push(root);
        while (pending.Count > 0 && found.Count < limit)
        {
            var current = pending.Pop();
            found.Add(current);
            for (var index = VisualTreeHelper.GetChildrenCount(current) - 1; index >= 0; index--)
                pending.Push(VisualTreeHelper.GetChild(current, index));
        }
        truncated = pending.Count > 0;
        return found;
    }

    private void Stop(string reason)
    {
        if (_view is null) return;
        _timer.Stop();
        _timer.Tick -= OnTick;
        _view = null;
        _windowRoot = null;
        _sink.Write(new { Event = "session-end", Session = _session, Samples = _samples, Reason = reason,
            Timestamp = Stopwatch.GetTimestamp() });
    }

    public void Dispose() => Stop("module-detach-or-window-close");

    private sealed class ProbeSink
    {
        private readonly string _path;
        private readonly byte[] _salt = Guid.NewGuid().ToByteArray();
        private Task _writes = Task.CompletedTask;
        internal ProbeSink(string path)
        {
            _path = path;
            using var created = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.Read);
            using var writer = new StreamWriter(created, new UTF8Encoding(false));
            writer.WriteLine(JsonSerializer.Serialize(new { Event = "probe-start", SchemaVersion = 1,
                ProcessId = Environment.ProcessId, StartedUtc = DateTime.UtcNow, StopwatchFrequency = Stopwatch.Frequency,
                Scope = "Passive actual-module samples; no assertions, font raster proof or native harness pass.",
                Limits = "First Hardware title/row; realized Record grid visuals only, bounds do not prove complete clipping/visibility.",
                Privacy = "No labels, window titles, device names or specification values; salted signature equality only." }));
            AppDomain.CurrentDomain.ProcessExit += (_, _) =>
            {
                // The sink retains no window. Normal process exit gets a bounded drain for its end records.
                try { _writes.Wait(TimeSpan.FromSeconds(2)); }
                catch { }
            };
        }
        internal string Digest(string value) => Convert.ToHexString(SHA256.HashData(_salt.Concat(Encoding.UTF8.GetBytes(value)).ToArray()));
        internal void Write(object value)
        {
            // DTOs contain only primitive values/arrays; no native element escapes the UI sample.
            // Serial worker writes preserve sample order and do not perform file I/O on timer callbacks.
            _writes = _writes.ContinueWith(_ =>
            {
                try { File.AppendAllText(_path, JsonSerializer.Serialize(value) + Environment.NewLine, new UTF8Encoding(false)); }
                catch (Exception error) { Debug.WriteLine($"Live module probe write failed: {error.GetType().Name}"); }
            }, CancellationToken.None, TaskContinuationOptions.None, TaskScheduler.Default);
        }
    }
}
#endif
