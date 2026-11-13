using Windows.Graphics.Imaging;

namespace KOTU.Module.Image;

/// <summary>Worker-only first-frame decoder for local PNG/TIFF previews. Never opens a file.</summary>
public static class ImagePreviewDecoder
{
    public sealed record Pixels(int Width, int Height, byte[] Bgra);

    public static async Task<Pixels> DecodeAsync(byte[] source, int maxWidth, int maxHeight,
        CancellationToken cancellation = default)
    {
        cancellation.ThrowIfCancellationRequested();
        if (maxWidth is <= 0 or > 768 || maxHeight is <= 0 or > 768)
            throw new ArgumentOutOfRangeException(nameof(maxWidth));
        if (ApngImage.Parse(source) is { } animation) source = await animation.FirstFrameAsync(cancellation);
        source = PngExifOrientation.WithoutExif(source, out var orientation);
        using var input = new MemoryStream(source, writable: false);
        var decoder = await BitmapDecoder.CreateAsync(input.AsRandomAccessStream()).AsTask(cancellation);
        if ((long)decoder.PixelWidth * decoder.PixelHeight > 100_000_000)
            throw new InvalidDataException("Image dimensions exceed the preview limit.");
        var swapped = orientation >= 5;
        var width = swapped ? decoder.PixelHeight : decoder.OrientedPixelWidth;
        var height = swapped ? decoder.PixelWidth : decoder.OrientedPixelHeight;
        var ratio = Math.Min(1, Math.Min((double)maxWidth / width, (double)maxHeight / height));
        var resultWidth = Math.Max(1, (int)Math.Round(width * ratio));
        var resultHeight = Math.Max(1, (int)Math.Round(height * ratio));
        // Scale precedes EXIF/flip/rotation. TIFF's native orientation can also swap the axes.
        var nativeSwap = decoder.PixelWidth != decoder.OrientedPixelWidth;
        var transform = PngExifOrientation.Transform(orientation);
        transform.ScaledWidth = (uint)(swapped || nativeSwap ? resultHeight : resultWidth);
        transform.ScaledHeight = (uint)(swapped || nativeSwap ? resultWidth : resultHeight);
        transform.InterpolationMode = BitmapInterpolationMode.Fant;
        var data = await decoder.GetPixelDataAsync(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied,
            transform, ExifOrientationMode.RespectExifOrientation, ColorManagementMode.ColorManageToSRgb)
            .AsTask(cancellation);
        cancellation.ThrowIfCancellationRequested();
        var pixels = data.DetachPixelData();
        if (pixels.Length != checked(resultWidth * resultHeight * 4))
            throw new InvalidDataException("Unexpected image preview buffer.");
        return new(resultWidth, resultHeight, pixels);
    }
}
