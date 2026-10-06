using Windows.Graphics.Imaging;

namespace KOTU.App.Controls;

/// <summary>Worker-only WIC decode and area-prefilter of shell-produced bytes; never opens a file.</summary>
internal static class ThumbnailRaster
{
    internal const int MaxSide = 768;
    internal const int MaxEncodedBytes = 16 * 1024 * 1024;
    internal readonly record struct Target(int Width, int Height);
    internal sealed record Pixels(int Width, int Height, byte[] Bgra);

    internal static Target Viewport(double width, double height, double scale) => new(
        RasterSide(width, scale), RasterSide(height, scale));

    private static int RasterSide(double dip, double scale) =>
        !double.IsFinite(dip) || !double.IsFinite(scale) || dip <= 8 || scale <= 0 ? 0
        : (int)Math.Clamp(Math.Ceiling((dip - 8) * scale), 1, MaxSide);

    internal static Target Fit(uint width, uint height, Target target)
    {
        if (width == 0 || height == 0 || target.Width <= 0 || target.Height <= 0)
            throw new ArgumentOutOfRangeException(nameof(target));
        var ratio = Math.Min(1, Math.Min((double)target.Width / width, (double)target.Height / height));
        return new(Math.Max(1, (int)Math.Round(width * ratio)), Math.Max(1, (int)Math.Round(height * ratio)));
    }

    internal static Pixels Decode(byte[] source, Target target, CancellationToken cancellation)
    {
        cancellation.ThrowIfCancellationRequested();
        if (source.Length > MaxEncodedBytes) throw new InvalidDataException("Shell thumbnail is too large.");
        using var input = new MemoryStream(source, writable: false);
        var decoder = BitmapDecoder.CreateAsync(input.AsRandomAccessStream()).AsTask(cancellation).GetAwaiter().GetResult();
        // Bound decoder metadata too: the shell request is 768, but a handler can ignore it.
        if (decoder.PixelWidth > 8192 || decoder.PixelHeight > 8192)
            throw new InvalidDataException("Shell thumbnail dimensions are too large.");
        var size = Fit(decoder.PixelWidth, decoder.PixelHeight, target);
        var transform = new BitmapTransform
        {
            ScaledWidth = (uint)size.Width,
            ScaledHeight = (uint)size.Height,
            InterpolationMode = BitmapInterpolationMode.Fant,
        };
        var data = decoder.GetPixelDataAsync(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied,
            transform, ExifOrientationMode.IgnoreExifOrientation, ColorManagementMode.ColorManageToSRgb)
            .AsTask(cancellation).GetAwaiter().GetResult();
        cancellation.ThrowIfCancellationRequested();
        var pixels = data.DetachPixelData();
        if (pixels.Length != checked(size.Width * size.Height * 4))
            throw new InvalidDataException("Unexpected thumbnail pixel buffer.");
        return new(size.Width, size.Height, pixels);
    }
}
