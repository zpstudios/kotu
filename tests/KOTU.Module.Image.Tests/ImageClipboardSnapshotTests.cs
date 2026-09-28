using Windows.Graphics.Imaging;
using KOTU.Module.Image;
using Xunit;

namespace KOTU.Module.Image.Tests;

public sealed class ImageClipboardSnapshotTests
{
    // Two opaque colors followed by one transparent pixel; no file I/O or clipboard mutation.
    private static readonly byte[] Pixels = [0, 0, 255, 255, 0, 255, 0, 255, 255, 0, 0, 0];

    [Theory]
    [InlineData(0, 3, 1, false)]
    [InlineData(90, 1, 3, false)]
    [InlineData(180, 3, 1, true)]
    [InlineData(270, 1, 3, true)]
    public async Task CopyPreservesFullResolutionAlphaAndClockwisePixelOrder(int rotation, uint width, uint height, bool reversed)
    {
        var original = await EncodeSourceAsync();
        var result = await ImageClipboardSnapshot.EncodePngAsync(original, rotation);
        using var stream = new MemoryStream(result);
        var decoder = await BitmapDecoder.CreateAsync(stream.AsRandomAccessStream());
        Assert.Equal(width, decoder.PixelWidth);
        Assert.Equal(height, decoder.PixelHeight);
        Assert.Equal(1u, decoder.FrameCount);
        var pixels = (await decoder.GetPixelDataAsync(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Straight,
            new BitmapTransform(), ExifOrientationMode.IgnoreExifOrientation,
            ColorManagementMode.DoNotColorManage)).DetachPixelData();
        var redIndex = reversed ? 8 : 0;
        var transparentIndex = reversed ? 0 : 8;
        Assert.Equal(new byte[] { 0, 0, 255, 255 }, pixels[redIndex..(redIndex + 4)]);
        Assert.Equal(new byte[] { 0, 255, 0, 255 }, pixels[4..8]);
        Assert.Equal(0, pixels[transparentIndex + 3]);
    }

    [Fact]
    public async Task AnimatedImageCopiesFirstFrameAsStaticPng()
    {
        using var frames = new ImageMagick.MagickImageCollection();
        frames.Add(new ImageMagick.MagickImage(ImageMagick.MagickColors.Red, 3, 2));
        frames.Add(new ImageMagick.MagickImage(ImageMagick.MagickColors.Blue, 3, 2));
        var result = await ImageClipboardSnapshot.EncodePngAsync(frames.ToByteArray(ImageMagick.MagickFormat.Gif), 0);
        using var stream = new MemoryStream(result);
        var decoder = await BitmapDecoder.CreateAsync(stream.AsRandomAccessStream());
        Assert.Equal(1u, decoder.FrameCount);
        Assert.Equal(3u, decoder.PixelWidth);
        var pixels = (await decoder.GetPixelDataAsync()).DetachPixelData();
        Assert.Equal(new byte[] { 0, 0, 255, 255 }, pixels[..4]);
    }

    [Fact]
    public async Task BrokenSourceFailsWithoutProducingEmptySuccess()
    {
        await Assert.ThrowsAnyAsync<Exception>(() => ImageClipboardSnapshot.EncodePngAsync([1, 2, 3], 0));
    }

    [Fact]
    public async Task InvalidRotationIsRejected()
    {
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => ImageClipboardSnapshot.EncodePngAsync([], 45));
    }

    [Fact]
    public void OverlappingCopyIsRejectedUntilWorkerFinishes()
    {
        var gate = new ImageCopyGate();
        Assert.True(gate.TryBegin(out var first));
        Assert.False(gate.TryBegin(out _));
        Assert.True(gate.IsCurrent(first));
        gate.Finish(first);
        Assert.True(gate.TryBegin(out _));
    }

    [Fact]
    public void FocusOrImageChangePermanentlyInvalidatesPendingCopy()
    {
        var gate = new ImageCopyGate();
        Assert.True(gate.TryBegin(out var first));
        gate.Invalidate();
        gate.Invalidate(); // Leaving and coming back must not restore a pending request.
        Assert.False(gate.IsCurrent(first));
        Assert.False(gate.TryBegin(out _));
        gate.Finish(first);
        Assert.True(gate.TryBegin(out var next));
        Assert.True(gate.IsCurrent(next));
        gate.Finish(first); // A stale completion cannot finish the new request.
        Assert.True(gate.IsCurrent(next));
    }

    [Fact]
    public async Task ExifOrientationIsNotAppliedTwice()
    {
        using var input = new MemoryStream();
        var encoder = await BitmapEncoder.CreateAsync(BitmapEncoder.TiffEncoderId, input.AsRandomAccessStream());
        encoder.SetPixelData(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Straight, 3, 1, 96, 96, Pixels);
        await encoder.BitmapProperties.SetPropertiesAsync(new Dictionary<string, BitmapTypedValue>
        {
            ["/ifd/{ushort=274}"] = new((ushort)6, Windows.Foundation.PropertyType.UInt16),
        });
        await encoder.FlushAsync();
        var result = await ImageClipboardSnapshot.EncodePngAsync(input.ToArray(), 90);
        using var output = new MemoryStream(result);
        var decoder = await BitmapDecoder.CreateAsync(output.AsRandomAccessStream());
        Assert.Equal(1u, decoder.PixelWidth);
        Assert.Equal(3u, decoder.PixelHeight);
        var pixels = (await decoder.GetPixelDataAsync()).DetachPixelData();
        Assert.Equal(new byte[] { 0, 0, 255, 255 }, pixels[..4]);
    }

    [Fact]
    public void EveryCopyHasADistinctCompletionToken()
    {
        var gate = new ImageCopyGate();
        gate.TryBegin(out var first);
        gate.Finish(first);
        gate.TryBegin(out var second);
        Assert.NotEqual(first, second);
        gate.Finish(first);
        Assert.True(gate.IsCurrent(second));
    }

    private static async Task<byte[]> EncodeSourceAsync()
    {
        using var stream = new MemoryStream();
        var encoder = await BitmapEncoder.CreateAsync(BitmapEncoder.PngEncoderId, stream.AsRandomAccessStream());
        encoder.SetPixelData(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Straight, 3, 1, 96, 96, Pixels);
        await encoder.FlushAsync();
        return stream.ToArray();
    }
}
