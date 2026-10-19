using System.Buffers.Binary;
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
        Assert.False(gate.IsPending);
        Assert.True(gate.TryBegin(out var first));
        Assert.True(gate.IsPending);
        Assert.False(gate.TryBegin(out _));
        Assert.True(gate.IsCurrent(first));
        gate.Finish(first);
        Assert.False(gate.IsPending);
        Assert.True(gate.TryBegin(out _));
    }

    [Fact]
    public void ImageChangePermanentlyInvalidatesPendingCopy()
    {
        var gate = new ImageCopyGate();
        Assert.True(gate.TryBegin(out var first));
        gate.Invalidate();
        gate.Invalidate(); // Returning to the same image must not restore a pending request.
        Assert.False(gate.IsCurrent(first));
        Assert.True(gate.IsPending);
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

    [Theory]
    [InlineData(0, 3, 1, false)]
    [InlineData(90, 1, 3, false)]
    [InlineData(180, 3, 1, true)]
    [InlineData(270, 1, 3, true)]
    public async Task NativePayloadHasFullSizeOrientedPixelsAndExplicitAlpha(int rotation, int width, int height, bool reversed)
    {
        var payload = await ImageClipboardSnapshot.CreateAsync(await EncodeSourceAsync(), rotation);
        Assert.Equal(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 }, payload.Png[..8]);
        Assert.Equal(124 + width * height * 4, payload.DibV5.Length);
        Assert.Equal(124, Read32(payload.DibV5, 0));
        Assert.Equal(width, Read32(payload.DibV5, 4));
        Assert.Equal(-height, Read32(payload.DibV5, 8));
        Assert.Equal(32, BinaryPrimitives.ReadUInt16LittleEndian(payload.DibV5.AsSpan(14)));
        Assert.Equal(3, Read32(payload.DibV5, 16));
        Assert.Equal(0x00ff0000, Read32(payload.DibV5, 40));
        Assert.Equal(0x0000ff00, Read32(payload.DibV5, 44));
        Assert.Equal(0x000000ff, Read32(payload.DibV5, 48));
        Assert.Equal(0xff000000u, BinaryPrimitives.ReadUInt32LittleEndian(payload.DibV5.AsSpan(52)));
        Assert.Equal(0x73524742, Read32(payload.DibV5, 56));
        var red = 124 + (reversed ? 8 : 0);
        Assert.Equal(new byte[] { 0, 0, 255, 255 }, payload.DibV5[red..(red + 4)]);
        Assert.Equal(0, payload.DibV5[124 + (reversed ? 0 : 8) + 3]);
        Assert.Equal(width, Read32(payload.Dib, 4));
        Assert.Equal(height, Read32(payload.Dib, 8));
        Assert.Equal(24, BinaryPrimitives.ReadUInt16LittleEndian(payload.Dib.AsSpan(14)));
        Assert.Equal(40 + ((width * 3 + 3) & ~3) * height, payload.Dib.Length);
    }

    [Fact]
    public void LegacyDibUsesBottomUpPaddedRowsAndCompositesOnlyItsOwnFallback()
    {
        byte[] pixels = [0, 0, 255, 255, 255, 0, 0, 128, 0, 255, 0, 255, 50, 60, 70, 0];
        var dib = ImageClipboardSnapshot.CreateDib(2, 2, pixels);
        Assert.Equal(new byte[] { 0, 255, 0, 255, 255, 255, 0, 0 }, dib[40..48]);
        Assert.Equal(new byte[] { 0, 0, 255, 255, 127, 127, 0, 0 }, dib[48..56]);
        var dibV5 = ImageClipboardSnapshot.CreateDibV5(2, 2, pixels);
        Assert.Equal(pixels, dibV5[124..]);
        Assert.Equal(16, Read32(dibV5, 20));
    }

    [Fact]
    public void InvalidDimensionsOrOverflowCannotProduceNativePayload()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => ImageClipboardSnapshot.CreateDibV5(0, 1, []));
        Assert.Throws<ArgumentOutOfRangeException>(() => ImageClipboardSnapshot.CreateDib(-1, 1, []));
        Assert.Throws<OverflowException>(() => ImageClipboardSnapshot.CreateDibV5(int.MaxValue, 2, []));
        Assert.Throws<ArgumentException>(() => ImageClipboardSnapshot.CreateDib(2, 2, [1, 2, 3]));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task IndependentWicDecoderReadsNativeHeadersRowsAndPartialAlpha(bool v5)
    {
        // 홀수 폭의 행 패딩과 서로 다른 두 행을 실제 BMP 디코더로 확인한다.
        byte[] pixels = [0, 0, 255, 255, 255, 0, 0, 128, 70, 60, 50, 0,
                         0, 255, 0, 255, 0, 0, 0, 255, 255, 255, 255, 255];
        var dib = v5 ? ImageClipboardSnapshot.CreateDibV5(3, 2, pixels) : ImageClipboardSnapshot.CreateDib(3, 2, pixels);
        var file = new byte[14 + dib.Length];
        file[0] = (byte)'B';
        file[1] = (byte)'M';
        BinaryPrimitives.WriteInt32LittleEndian(file.AsSpan(2), file.Length);
        BinaryPrimitives.WriteInt32LittleEndian(file.AsSpan(10), 14 + (v5 ? 124 : 40));
        dib.CopyTo(file, 14);
        using var stream = new MemoryStream(file);
        var decoder = await BitmapDecoder.CreateAsync(stream.AsRandomAccessStream());
        Assert.Equal(3u, decoder.PixelWidth);
        Assert.Equal(2u, decoder.PixelHeight);
        var decoded = (await decoder.GetPixelDataAsync(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Straight,
            new BitmapTransform(), ExifOrientationMode.IgnoreExifOrientation,
            ColorManagementMode.DoNotColorManage)).DetachPixelData();
        Assert.Equal(new byte[] { 0, 0, 255, 255 }, decoded[..4]);
        Assert.Equal(v5 ? new byte[] { 255, 0, 0, 128 } : new byte[] { 255, 127, 127, 255 }, decoded[4..8]);
        Assert.Equal(v5 ? (byte)0 : (byte)255, decoded[11]);
        Assert.Equal(pixels[12..], decoded[12..]);
    }

    private static int Read32(byte[] bytes, int offset) => BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(offset));

    private static async Task<byte[]> EncodeSourceAsync()
    {
        using var stream = new MemoryStream();
        var encoder = await BitmapEncoder.CreateAsync(BitmapEncoder.PngEncoderId, stream.AsRandomAccessStream());
        encoder.SetPixelData(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Straight, 3, 1, 96, 96, Pixels);
        await encoder.FlushAsync();
        return stream.ToArray();
    }
}
