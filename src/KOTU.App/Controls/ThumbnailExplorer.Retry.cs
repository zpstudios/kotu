using KOTU.Core.Threading;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using System.Runtime.InteropServices.WindowsRuntime;
using Windows.Foundation;

namespace KOTU.App.Controls;

public sealed partial class ThumbnailExplorer
{
    private bool _retrySurfaceLive;
    private ThumbnailRetryWakeup? _retryWakeup;
    private System.Threading.Timer? _retryTimer;
    private ScrollViewer? _retryScroll;
    private FrameworkElement? _retryViewport;
    private readonly HashSet<ExplorerEntryVm> _retryCandidates = [];
    private readonly Dictionary<ExplorerEntryVm, TilePreview> _retryReady = [];
    // 예약별 타이머가 아니다. 실제 발사된 최대3개의 취소 수명만 따로 둔다.
    private readonly Dictionary<ExplorerEntryVm, CancellationTokenSource> _retryRequests = [];

    private void StartRetrySurface()
    {
        _retrySurfaceLive = true;
        _retryWakeup?.Close();
        var wakeup = _retryWakeup = new ThumbnailRetryWakeup();
        _retryTimer?.Dispose();
        _retryTimer = new System.Threading.Timer(_ => PostRetryCheck(wakeup), null, Timeout.Infinite, Timeout.Infinite);
        LayoutUpdated -= OnRetryLayout;
        LayoutUpdated += OnRetryLayout;
        ExplorerRenameBox.EditEnded -= QueueRetryCheck;
        ExplorerRenameBox.EditEnded += QueueRetryCheck;
        QueueRetryCheck();
    }

    private void StopRetrySurface()
    {
        _retrySurfaceLive = false;
        _retryWakeup?.Close();
        _retryWakeup = null;
        _retryTimer?.Dispose();
        _retryTimer = null;
        LayoutUpdated -= OnRetryLayout;
        if (_retryScroll is not null) _retryScroll.ViewChanged -= OnRetryScroll;
        _retryScroll = null;
        _retryViewport = null;
        ExplorerRenameBox.EditEnded -= QueueRetryCheck;
        foreach (var request in _retryRequests.Values) request.Cancel();
        _retryCandidates.Clear();
        _retryReady.Clear();
    }

    private void OnRetryLayout(object? sender, object args)
    {
        if (_retryScroll is null && FindRetryDescendant<ScrollViewer>(TileGrid) is { } scroll)
        {
            _retryScroll = scroll;
            scroll.ViewChanged += OnRetryScroll;
        }
        QueueRetryCheck();
    }
    private void OnRetryScroll(object? sender, ScrollViewerViewChangedEventArgs args) => QueueRetryCheck();
    private void QueueRetryCheck()
    {
        if (_retrySurfaceLive && _retryWakeup is { } wakeup &&
            (_retryCandidates.Count > 0 || _retryRequests.Count > 0 || _retryReady.Count > 0)) PostRetryCheck(wakeup);
    }
    private void PostRetryCheck(ThumbnailRetryWakeup wakeup)
    {
        if (!wakeup.TryQueue()) return;
        // 편집 종료는 Auto 캡션 행의 최종 배치보다 앞선다. 중간 높이로 resize를 발사하지 않는다.
        if (!DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () =>
        {
            if (!wakeup.Consume() || !ReferenceEquals(wakeup, _retryWakeup)) return;
            CheckRetries();
        })) wakeup.Consume();
    }

    private void ConsiderRetry(ExplorerEntryVm vm)
    {
        if (vm.Retry.Remaining(true, false, !vm.Requests.Token.IsCancellationRequested) is not null &&
            (_retryCandidates.Contains(vm) || _retryCandidates.Count < 128)) _retryCandidates.Add(vm);
        QueueRetryCheck();
    }
    private void ObserveRetry(ExplorerEntryVm vm, TilePreview result)
    {
        vm.Retry.Observe(result.Outcome, result.AudioFailed);
        if (vm.Retry.Remaining(true, false, true) is null) _retryCandidates.Remove(vm);
        else ConsiderRetry(vm);
    }

    private void CheckRetries()
    {
        if (!_retrySurfaceLive) return;
        // 비가시 후보가 상한을 차지해도 현재 보이는 후보를 굶기지 않는다.
        // 밀려난 VM의 due/경로 예산은 그대로 남고 다시 실체화되면 복귀한다.
        if (TileGrid.ItemsPanelRoot is Panel panel)
            foreach (var item in panel.Children.OfType<GridViewItem>())
                if (VmOf(item) is { } vm && !_retryCandidates.Contains(vm) && IsRetryVisible(vm) &&
                    vm.Retry.Remaining(true, false, true) is not null)
                {
                    if (_retryCandidates.Count >= 128 && _retryCandidates.FirstOrDefault(candidate => !IsRetryVisible(candidate)) is { } hidden)
                        _retryCandidates.Remove(hidden);
                    if (_retryCandidates.Count < 128) _retryCandidates.Add(vm);
                }
        ApplyReadyPreviews();
        foreach (var pair in _retryRequests)
            if (!IsRetryVisible(pair.Key) || ExplorerRenameBox.IsEditing) pair.Value.Cancel();
        TimeSpan? next = null;
        foreach (var vm in _retryCandidates.ToArray())
        {
            var current = !vm.Requests.Token.IsCancellationRequested;
            if (!current || vm.Retry.Remaining(true, false, current) is null)
            {
                _retryCandidates.Remove(vm);
                continue;
            }
            var remaining = vm.Retry.Remaining(IsRetryVisible(vm), ExplorerRenameBox.IsEditing, current);
            if (remaining is null || _retryRequests.ContainsKey(vm) || vm.PreviewInFlight) continue;
            var delay = remaining.Value;
            if (_retryRequests.Count >= 3 || ThumbnailRetryRegistry.Process.Inspect(vm.Path) is { Active: > 0 })
                delay = TimeSpan.FromMilliseconds(Math.Max(500, delay.TotalMilliseconds));
            if (delay <= TimeSpan.Zero)
            {
                var source = new CancellationTokenSource();
                _retryRequests.Add(vm, source);
                _ = RunAutomaticPreviewAsync(vm, _showSeq, source);
            }
            else if (next is null || delay < next) next = delay;
        }
        _retryTimer?.Change(next ?? Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
    }

    private async Task RunAutomaticPreviewAsync(ExplorerEntryVm vm, int seq, CancellationTokenSource source)
    {
        try { await FillShellThumbnailAsync(vm, seq, automatic: true, source.Token); }
        catch (OperationCanceledException) { }
        catch (Exception ex) { System.Diagnostics.Debug.WriteLine("retry dispatch failure " + ex.GetType().Name); vm.Retry.Stop(); }
        finally
        {
            _retryRequests.Remove(vm);
            source.Dispose();
            QueueRetryCheck();
        }
    }

    private void DeferReadyPreview(ExplorerEntryVm vm, TilePreview result)
    {
        // 보이는 순간 반영할 성공 픽셀은 최대3개(약7MiB). 넘으면 일반 재실체화로 회복한다.
        if (_retryReady.Count >= 3 && !_retryReady.ContainsKey(vm)) _retryReady.Remove(_retryReady.Keys.First());
        _retryReady[vm] = result;
        QueueRetryCheck();
    }
    private void ApplyReadyPreviews()
    {
        foreach (var pair in _retryReady.ToArray())
        {
            var vm = pair.Key;
            if (vm.Requests.Token.IsCancellationRequested) { _retryReady.Remove(vm); continue; }
            if (!IsRetryVisible(vm) || ExplorerRenameBox.IsEditing || LivePreviewHostOf(vm) is not { } host) continue;
            _retryReady.Remove(vm);
            if (pair.Value.Pixels is not { } pixels) continue;
            try
            {
                var bitmap = new WriteableBitmap(pixels.Width, pixels.Height);
                using (var output = bitmap.PixelBuffer.AsStream()) output.Write(pixels.Bgra);
                bitmap.Invalidate();
                host.Children.Clear();
                host.Tag = pair.Value.Target;
                host.Children.Add(MakePreviewImage(bitmap));
                var info = pair.Value.Info ?? vm.AudioInfo;
                if (info is not null) vm.AudioInfo = info;
                if (info is not null) host.Children.Add(MakeAudioInfoBand(info));
                if (PreviewTarget(host) != pair.Value.Target) QueuePreviewResize(host);
            }
            catch { vm.PreviewKnownEmpty = true; }
        }
    }

    private bool IsRetryVisible(ExplorerEntryVm vm)
    {
        if (!_retrySurfaceLive || vm.Requests.Token.IsCancellationRequested || LivePreviewHostOf(vm) is not { } host)
            return false;
        for (DependencyObject? node = host; node is not null; node = VisualTreeHelper.GetParent(node))
            if (node is UIElement element && element.Visibility != Visibility.Visible) return false;
        try
        {
            var viewport = _retryViewport ??= (FrameworkElement?)FindRetryDescendant<ScrollContentPresenter>(TileGrid) ?? TileGrid;
            var bounds = host.TransformToVisual(viewport).TransformBounds(new Rect(0, 0, host.ActualWidth, host.ActualHeight));
            return bounds.Width > 8 && bounds.Height > 8 && viewport.ActualWidth > 0 && viewport.ActualHeight > 0 &&
                bounds.Right > 0 && bounds.Bottom > 0 && bounds.Left < viewport.ActualWidth && bounds.Top < viewport.ActualHeight;
        }
        catch { return false; }
    }
    private static T? FindRetryDescendant<T>(DependencyObject root) where T : DependencyObject
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is T match) return match;
            if (FindRetryDescendant<T>(child) is { } nested) return nested;
        }
        return null;
    }
}
