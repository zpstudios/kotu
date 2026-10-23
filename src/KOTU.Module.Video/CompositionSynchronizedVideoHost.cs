using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.Foundation;

namespace KOTU.Module.Video;

// A412: 실제 크기 변경만 composition 뒤로 넘긴다. 플레이어와 네이티브 표면은 교체하지 않는다.
public sealed class CompositionSynchronizedVideoHost : Panel
{
    private CompositionVideoView? _view;
    private SwapChainPanel? _surface;
    private Size _arranged;
    private bool _active;
    private bool _arrangeQueued;
    private bool _hadPositiveArrangement;
    // 지연 중 첫 요청의 기한을 유지한다. 워커 타이머는 UI 큐에 한 번만 전달한다.
    private Timer? _deadline;
    private long _generation;
    private bool _expired;
    private bool _releaseNextArrange;
    private Size _expiredTarget;

    public CompositionSynchronizedVideoHost()
    {
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        SynchronizeChild();
        var desired = new Size();
        foreach (var child in Children)
        {
            child.Measure(availableSize);
            desired.Width = Math.Max(desired.Width, FiniteLength(child.DesiredSize.Width));
            desired.Height = Math.Max(desired.Height, FiniteLength(child.DesiredSize.Height));
        }
        // 무한 제약은 자식 측정에 허용하지만 Panel의 측정 결과는 유한한 실제 desired size다.
        return desired;
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        _arrangeQueued = false;
        SynchronizeChild();
        var requested = new Size(FiniteLength(finalSize.Width), FiniteLength(finalSize.Height));
        var readable = TryGetScales(out var target, out var actual);
        var matches = readable && VideoCompositionGeometry.ScalesMatch(target, actual);
        if (_releaseNextArrange)
        {
            _releaseNextArrange = false;
            _expiredTarget = target;
            _expired = readable && !matches;
        }
        else if (matches || (readable && _expired && !VideoCompositionGeometry.ScalesMatch(target, _expiredTarget)))
            _expired = false;
        // 최초 양수 배치는 표면 초기화를 허용하고, 그 뒤의 실제 0 크기도 유지할 수 있다.
        var hold = _hadPositiveArrangement && !_expired &&
            requested.Width > 0 && requested.Height > 0 && requested != _arranged &&
            readable && !matches;

        // 이전에 실제 배치한 사각형만 잠시 유지한다. 최신 finalSize는 다음 배치에서 다시 받는다.
        if (hold) ArmDeadline();
        else
        {
            DisarmDeadline();
            _arranged = requested;
            if (requested.Width > 0 && requested.Height > 0) _hadPositiveArrangement = true;
        }
        foreach (var child in Children)
            child.Arrange(new Rect(0, 0, _arranged.Width, _arranged.Height));
        return requested;
    }

    private static double FiniteLength(double value) => double.IsFinite(value) && value > 0 ? value : 0;

    private bool Owns(CompositionVideoView view) =>
        _active && Children.Count == 1 && ReferenceEquals(Children[0], view);

    private void OnLoaded(object sender, RoutedEventArgs args)
    {
        _active = true;
        SynchronizeChild();
    }

    private void OnUnloaded(object sender, RoutedEventArgs args)
    {
        _active = false;
        DetachChild();
    }

    private void SynchronizeChild()
    {
        var current = _active && Children.Count == 1 ? Children[0] as CompositionVideoView : null;
        if (ReferenceEquals(current, _view)) return;
        DetachChild();
        _view = current;
        if (current is null) return;
        current.TemplateApplied += OnTemplateApplied;
        current.Loaded += OnChildLoaded;
        current.Unloaded += OnChildUnloaded;
        if (current.IsLoaded) BindSurface();
    }

    private void DetachChild()
    {
        if (_view is { } view)
        {
            view.TemplateApplied -= OnTemplateApplied;
            view.Loaded -= OnChildLoaded;
            view.Unloaded -= OnChildUnloaded;
        }
        DetachSurface();
        _view = null;
    }

    private void DetachSurface()
    {
        DisarmDeadline();
        _hadPositiveArrangement = false;
        _expired = false;
        _releaseNextArrange = false;
        _expiredTarget = default;
        if (_surface is { } surface) surface.CompositionScaleChanged -= OnCompositionScaleChanged;
        _surface = null;
        _arranged = default;
        _arrangeQueued = false;
    }

    private void OnTemplateApplied(object? sender, EventArgs args)
    {
        if (_view is { } view && ReferenceEquals(sender, view) && Owns(view)) BindSurface();
    }

    private void OnChildLoaded(object sender, RoutedEventArgs args)
    {
        if (_view is { } view && ReferenceEquals(sender, view) && Owns(view)) BindSurface();
    }

    private void OnChildUnloaded(object sender, RoutedEventArgs args)
    {
        if (!ReferenceEquals(sender, _view)) return;
        DetachSurface();
        QueueArrange();
    }

    private void BindSurface()
    {
        DetachSurface();
        try
        {
            if (_view is { IsLoaded: true } view && Owns(view))
                _surface = FindSingleSurface(view);
            if (_surface is { } surface) surface.CompositionScaleChanged += OnCompositionScaleChanged;
        }
        catch (Exception)
        {
            // 템플릿 해제 중 접근 불가도 정상 배치로 복귀한다. 발견 재시도나 영구 스캔은 없다.
            DetachSurface();
        }
        QueueArrange();
    }

    private static SwapChainPanel? FindSingleSurface(DependencyObject root)
    {
        var remaining = new Stack<DependencyObject>();
        remaining.Push(root);
        SwapChainPanel? found = null;
        var count = 0;
        while (remaining.TryPop(out var node))
        {
            if (++count > 128) return null;
            if (node is SwapChainPanel panel)
            {
                if (found is not null) return null;
                found = panel;
            }
            var children = VisualTreeHelper.GetChildrenCount(node);
            if (children > 128 - count - remaining.Count) return null;
            for (var i = 0; i < children; i++) remaining.Push(VisualTreeHelper.GetChild(node, i));
        }
        return found;
    }

    private bool TryGetScales(out Size target, out Size actual)
    {
        target = actual = default;
        try
        {
            if (_view is not { IsLoaded: true } view || !Owns(view) ||
                _surface is not { IsLoaded: true } surface || XamlRoot is not { Content: UIElement root } xamlRoot)
                return false;
            var transform = TransformToVisual(root);
            actual = new Size(surface.CompositionScaleX, surface.CompositionScaleY);
            return VideoCompositionGeometry.TryGetTargetScale(
                transform.TransformPoint(new Point(0, 0)),
                transform.TransformPoint(new Point(VideoCompositionGeometry.BasisLength, 0)),
                transform.TransformPoint(new Point(0, VideoCompositionGeometry.BasisLength)),
                xamlRoot.RasterizationScale, out target) &&
                VideoCompositionGeometry.IsPositiveFinite(actual);
        }
        catch (Exception)
        {
            // 해제 중이거나 지원하지 않는 루트이면 실제 최종 크기를 즉시 배치한다.
            return false;
        }
    }

    private void OnCompositionScaleChanged(SwapChainPanel sender, object args)
    {
        if (ReferenceEquals(sender, _surface) && _view is { IsLoaded: true } view && Owns(view))
        {
            if (TryGetScales(out var target, out var actual) && VideoCompositionGeometry.ScalesMatch(target, actual))
            {
                DisarmDeadline();
                _expired = false;
            }
            QueueArrange();
        }
    }

    private void ArmDeadline()
    {
        if (_deadline is not null || _expired || _surface is not { } surface || _view is not { } view) return;
        var generation = ++_generation;
        var dispatcher = DispatcherQueue;
        Timer? deadline = null;
        deadline = new Timer(_ =>
        {
            if (!dispatcher.TryEnqueue(() =>
            {
                if (generation != _generation || !_active || !IsLoaded || !view.IsLoaded ||
                    !surface.IsLoaded || !ReferenceEquals(view, _view) || !ReferenceEquals(surface, _surface) || !Owns(view))
                {
                    return;
                }
                DisarmDeadline();
                _expired = true;
                _releaseNextArrange = true;
                QueueArrange();
            })) deadline?.Dispose();
        }, null, Timeout.Infinite, Timeout.Infinite);
        _deadline = deadline;
        deadline.Change(100, Timeout.Infinite);
    }

    private void DisarmDeadline()
    {
        ++_generation;
        if (_deadline is null) return;
        _deadline.Dispose();
        _deadline = null;
    }

    private void QueueArrange()
    {
        if (!_active || _arrangeQueued) return;
        _arrangeQueued = true;
        InvalidateArrange();
    }
}
