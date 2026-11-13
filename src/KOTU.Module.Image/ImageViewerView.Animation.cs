using System.Diagnostics;
using System.Runtime.InteropServices.WindowsRuntime;
using Microsoft.UI.Xaml.Media.Imaging;

namespace KOTU.Module.Image;

public sealed partial class ImageViewerView
{
    private CancellationTokenSource? _animation;

    private void StopAnimation()
    {
        _animation?.Cancel();
        _animation = null;
    }

    private void StartAnimation(ApngImage image, string path, int sequence)
    {
        var lifetime = new CancellationTokenSource();
        _animation = lifetime;
        var cancellation = lifetime.Token;
        WriteableBitmap? bitmap = null;
        _ = Task.Run(async () =>
        {
            try
            {
                await ApngPlayback.RunAsync(image, async (pixels, token) =>
                {
                    var shown = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                    if (!DispatcherQueue.TryEnqueue(() =>
                    {
                        try
                        {
                            if (token.IsCancellationRequested || sequence != _loadSequence || _navigator?.Current != path)
                            {
                                shown.TrySetCanceled(token);
                                return;
                            }
                            var first = bitmap is null;
                            bitmap ??= new WriteableBitmap(image.DisplayWidth, image.DisplayHeight);
                            using (var buffer = bitmap.PixelBuffer.AsStream()) buffer.Write(pixels);
                            bitmap.Invalidate();
                            if (first)
                            {
                                ImageControl.Source = bitmap;
                                UpdateFit();
                            }
                            shown.TrySetResult();
                        }
                        catch (Exception ex) { shown.TrySetException(ex); }
                    })) throw new OperationCanceledException(token);
                    // At most one frame is queued, and navigation can cancel even if the dispatcher has closed.
                    await shown.Task.WaitAsync(token).ConfigureAwait(false);
                }, cancellation).ConfigureAwait(false);
            }
            catch (OperationCanceledException) { }
            catch (Exception ex) { Trace.TraceError($"APNG playback stopped: {ex.Message}"); }
            finally
            {
                // The UI owns the field; completion of an old worker cannot clear a newer playback.
                if (!DispatcherQueue.TryEnqueue(() =>
                {
                    if (ReferenceEquals(_animation, lifetime)) _animation = null;
                    lifetime.Dispose();
                })) lifetime.Dispose();
            }
        });
    }
}
