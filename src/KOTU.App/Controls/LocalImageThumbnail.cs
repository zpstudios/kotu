using KOTU.Core.IO;
using KOTU.Core.Routing;
using KOTU.Module.Image;
using Windows.Graphics.Imaging;

namespace KOTU.App.Controls;

/// <summary>Bounded worker-only local decode; cached-only requests never open original content.</summary>
internal static class LocalImageThumbnail
{
    internal const int MaxSourceBytes = 64 * 1024 * 1024;

    internal static bool Supports(string path) => Path.GetExtension(path).ToLowerInvariant() is
        ".png" or ".apng" or ".tif" or ".tiff";

    internal static ThumbnailRaster.Pixels? Read(string path, bool listedPlaceholder, bool cachedOnly,
        ThumbnailRaster.Target target, CancellationToken cancellation,
        Func<string, FileAttributes>? readAttributes = null, Func<string, Stream>? open = null)
    {
        cancellation.ThrowIfCancellationRequested();
        if (cachedOnly || !Supports(path)) return null;
        return CloudFileAccess.ReadLocal(path, listedPlaceholder, cancellation, () =>
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
            timeout.CancelAfter(TimeSpan.FromSeconds(5));
            using var stream = open is null ? SharedRead.Open(path) : open(path);
            if (stream.Length is <= 0 or > MaxSourceBytes)
                throw new InvalidDataException("Image exceeds the preview source limit.");
            var bytes = new byte[checked((int)stream.Length)];
            stream.ReadExactlyAsync(bytes, timeout.Token).AsTask().GetAwaiter().GetResult();
            var pixels = ImagePreviewDecoder.DecodeAsync(bytes, target.Width, target.Height, timeout.Token)
                .GetAwaiter().GetResult();
            return new ThumbnailRaster.Pixels(pixels.Width, pixels.Height, pixels.Bgra);
        }, readAttributes);
    }

    internal static byte[] Encode(ThumbnailRaster.Pixels pixels)
    {
        using var stream = new MemoryStream();
        var encoder = BitmapEncoder.CreateAsync(BitmapEncoder.PngEncoderId, stream.AsRandomAccessStream())
            .AsTask().GetAwaiter().GetResult();
        encoder.SetPixelData(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied,
            (uint)pixels.Width, (uint)pixels.Height, 96, 96, pixels.Bgra);
        encoder.FlushAsync().AsTask().GetAwaiter().GetResult();
        return stream.ToArray();
    }
}
