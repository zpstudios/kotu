using KOTU.App.Controls;
using Windows.Graphics.Imaging;
using Xunit;

namespace KOTU.Module.Image.Tests;

public sealed class ThumbnailRasterTests
{
    private static async Task<byte[]> Png(uint width, uint height, byte[] pixels)
    {
        using var stream = new MemoryStream();
        var encoder = await BitmapEncoder.CreateAsync(BitmapEncoder.PngEncoderId, stream.AsRandomAccessStream());
        encoder.SetPixelData(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Straight, width, height, 96, 96, pixels);
        await encoder.FlushAsync();
        return stream.ToArray();
    }

    [Fact]
    public async Task ThinAlternatingLinesAverageInsteadOfAliasing()
    {
        var pixels = new byte[64 * 8 * 4];
        for (var y = 0; y < 8; y++)
            for (var x = 0; x < 64; x++)
            {
                var i = (y * 64 + x) * 4;
                pixels[i] = pixels[i + 1] = pixels[i + 2] = (byte)(x % 2 * 255);
                pixels[i + 3] = 255;
            }
        var source = await Png(64, 8, pixels);
        var raster = await Task.Run(() => ThumbnailRaster.Decode(source, new(8, 8), default));
        Assert.Equal(8, raster.Width);
        Assert.Equal(1, raster.Height);
        for (var x = 0; x < 8; x++)
        {
            Assert.InRange(raster.Bgra[x * 4], 120, 135);
            Assert.Equal(255, raster.Bgra[x * 4 + 3]);
        }
    }

    [Fact]
    public async Task TransparentPixelsDoNotIntroduceColorFringes()
    {
        // Transparent blue next to opaque red must average premultiplied channels.
        var source = await Png(2, 1, [255, 0, 0, 0, 0, 0, 255, 255]);
        var raster = await Task.Run(() => ThumbnailRaster.Decode(source, new(1, 1), default));
        Assert.InRange(raster.Bgra[0], 0, 1);
        Assert.InRange(raster.Bgra[2], 120, 135);
        Assert.InRange(raster.Bgra[3], 120, 135);
    }

    [Fact]
    public async Task SmallShellPreviewIsNotUpsampledAndAlphaIsPreserved()
    {
        var source = await Png(1, 1, [100, 80, 60, 128]);
        var raster = await Task.Run(() => ThumbnailRaster.Decode(source, new(400, 400), default));
        Assert.Equal(1, raster.Width);
        Assert.Equal(1, raster.Height);
        Assert.Equal(128, raster.Bgra[3]);
        Assert.InRange(raster.Bgra[0], 49, 51);
    }

    [Fact]
    public void ViewportIncludesBothMarginsAndDpiWithBoundedMemory()
    {
        Assert.Equal(new ThumbnailRaster.Target(184, 144), ThumbnailRaster.Viewport(100, 80, 2));
        Assert.Equal(new ThumbnailRaster.Target(768, 768), ThumbnailRaster.Viewport(10000, 10000, 4));
        Assert.Equal(new ThumbnailRaster.Target(0, 0), ThumbnailRaster.Viewport(0, 0, 1));
        Assert.Equal(new ThumbnailRaster.Target(100, 50), ThumbnailRaster.Fit(800, 400, new(100, 100)));
    }

    [Fact]
    public void CancelledAndMalformedResultsCannotReachUi()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        Assert.Throws<OperationCanceledException>(() => ThumbnailRaster.Decode([], new(10, 10), cancellation.Token));
        Assert.ThrowsAny<Exception>(() => ThumbnailRaster.Decode([1, 2, 3], new(10, 10), default));
    }
}
