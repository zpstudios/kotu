using System.Diagnostics;
using Windows.Graphics.Imaging;

namespace KOTU.Module.Image;

/// <summary>One worker-owned canvas; applies APNG disposal/blend before EXIF orientation.</summary>
internal sealed class ApngCompositor(ApngImage image)
{
    private readonly byte[] _canvas = new byte[checked(image.Width * image.Height * 4)];
    private byte[]? _previous;
    private int _last = -1;

    internal async Task<byte[]> RenderAsync(int index, CancellationToken cancellation = default)
    {
        cancellation.ThrowIfCancellationRequested();
        if (index == 0)
        {
            Array.Clear(_canvas);
            _previous = null;
        }
        else
        {
            if (index != _last + 1) throw new InvalidOperationException("APNG frames must be composed in order.");
            var prior = image.Frames[_last];
            if (prior.Dispose == 2 && _previous is not null) _previous.CopyTo(_canvas, 0);
            else if (prior.Dispose == 1 || (prior.Dispose == 2 && _last == 0))
                for (var row = 0; row < prior.Height; row++)
                    Array.Clear(_canvas, ((row + prior.Y) * image.Width + prior.X) * 4, prior.Width * 4);
        }
        var frame = image.Frames[index];
        _previous = frame.Dispose == 2 ? _canvas.ToArray() : null;
        using var input = new MemoryStream(image.FramePng(index));
        var decoder = await BitmapDecoder.CreateAsync(input.AsRandomAccessStream()).AsTask(cancellation);
        var pixels = (await decoder.GetPixelDataAsync(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied,
            new BitmapTransform(), ExifOrientationMode.IgnoreExifOrientation, ColorManagementMode.ColorManageToSRgb)
            .AsTask(cancellation)).DetachPixelData();
        if (pixels.Length != checked(frame.Width * frame.Height * 4)) throw new InvalidDataException("Invalid frame pixels.");
        for (var y = 0; y < frame.Height; y++)
        {
            cancellation.ThrowIfCancellationRequested();
            for (var x = 0; x < frame.Width; x++)
            {
                var src = (y * frame.Width + x) * 4;
                var dst = ((y + frame.Y) * image.Width + x + frame.X) * 4;
                if (frame.Blend == 0) pixels.AsSpan(src, 4).CopyTo(_canvas.AsSpan(dst));
                else
                {
                    var inverseAlpha = 255 - pixels[src + 3];
                    for (var channel = 0; channel < 4; channel++)
                        _canvas[dst + channel] = (byte)Math.Min(255, pixels[src + channel] + (_canvas[dst + channel] * inverseAlpha + 127) / 255);
                }
            }
        }
        _last = index;
        return Orient(_canvas, image.Width, image.Height, image.Orientation, cancellation);
    }

    private static byte[] Orient(byte[] source, int width, int height, ushort orientation, CancellationToken cancellation)
    {
        if (orientation == 1) return source.ToArray();
        var result = new byte[source.Length];
        var targetWidth = orientation >= 5 ? height : width;
        for (var y = 0; y < height; y++)
        {
            cancellation.ThrowIfCancellationRequested();
            for (var x = 0; x < width; x++)
            {
                var (tx, ty) = orientation switch
                {
                    2 => (width - 1 - x, y),
                    3 => (width - 1 - x, height - 1 - y),
                    4 => (x, height - 1 - y),
                    5 => (y, x),
                    6 => (height - 1 - y, x),
                    7 => (height - 1 - y, width - 1 - x),
                    8 => (y, width - 1 - x),
                    _ => (x, y),
                };
                source.AsSpan((y * width + x) * 4, 4).CopyTo(result.AsSpan((ty * targetWidth + tx) * 4));
            }
        }
        return result;
    }
}

internal static class ApngPlayback
{
    // Decode, composition and delay all run on the worker. Only the awaited presentation enters the UI.
    internal static async Task RunAsync(ApngImage image, Func<byte[], CancellationToken, Task> present,
        CancellationToken cancellation, Func<TimeSpan, CancellationToken, Task>? delay = null,
        Func<TimeSpan>? elapsed = null)
    {
        var clock = Stopwatch.StartNew();
        elapsed ??= () => clock.Elapsed;
        delay ??= Task.Delay;
        var compositor = new ApngCompositor(image);
        for (ulong play = 0; image.Plays == 0 || play < image.Plays; play++)
            for (var index = 0; index < image.Frames.Count; index++)
            {
                cancellation.ThrowIfCancellationRequested();
                var started = elapsed();
                var pixels = await compositor.RenderAsync(index, cancellation).ConfigureAwait(false);
                await present(pixels, cancellation).ConfigureAwait(false);
                var remaining = image.Frames[index].Delay - (elapsed() - started);
                if (remaining > TimeSpan.Zero) await delay(remaining, cancellation).ConfigureAwait(false);
            }
    }
}
