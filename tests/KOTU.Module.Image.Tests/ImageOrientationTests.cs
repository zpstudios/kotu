using System.Buffers.Binary;
using ImageMagick;
using KOTU.Module.Image;
using Windows.Graphics.Imaging;
using Xunit;

namespace KOTU.Module.Image.Tests;

public sealed class ImageOrientationTests
{
    [Theory]
    [InlineData(1, 0, 3, 2, "ABCDEF")]
    [InlineData(1, 90, 2, 3, "DAEBFC")]
    [InlineData(1, 180, 3, 2, "FEDCBA")]
    [InlineData(1, 270, 2, 3, "CFBEAD")]
    [InlineData(2, 0, 3, 2, "CBAFED")]
    [InlineData(2, 90, 2, 3, "FCEBDA")]
    [InlineData(2, 180, 3, 2, "DEFABC")]
    [InlineData(2, 270, 2, 3, "ADBECF")]
    [InlineData(3, 0, 3, 2, "FEDCBA")]
    [InlineData(3, 90, 2, 3, "CFBEAD")]
    [InlineData(3, 180, 3, 2, "ABCDEF")]
    [InlineData(3, 270, 2, 3, "DAEBFC")]
    [InlineData(4, 0, 3, 2, "DEFABC")]
    [InlineData(4, 90, 2, 3, "ADBECF")]
    [InlineData(4, 180, 3, 2, "CBAFED")]
    [InlineData(4, 270, 2, 3, "FCEBDA")]
    [InlineData(5, 0, 2, 3, "ADBECF")]
    [InlineData(5, 90, 3, 2, "CBAFED")]
    [InlineData(5, 180, 2, 3, "FCEBDA")]
    [InlineData(5, 270, 3, 2, "DEFABC")]
    [InlineData(6, 0, 2, 3, "DAEBFC")]
    [InlineData(6, 90, 3, 2, "FEDCBA")]
    [InlineData(6, 180, 2, 3, "CFBEAD")]
    [InlineData(6, 270, 3, 2, "ABCDEF")]
    [InlineData(7, 0, 2, 3, "FCEBDA")]
    [InlineData(7, 90, 3, 2, "DEFABC")]
    [InlineData(7, 180, 2, 3, "ADBECF")]
    [InlineData(7, 270, 3, 2, "CBAFED")]
    [InlineData(8, 0, 2, 3, "CFBEAD")]
    [InlineData(8, 90, 3, 2, "ABCDEF")]
    [InlineData(8, 180, 2, 3, "DAEBFC")]
    [InlineData(8, 270, 3, 2, "FEDCBA")]
    public async Task AllOrientationsThenManualRotationHaveExactPixelsAndShapeInAllFormats(
        ushort orientation, int rotation, int width, int height, string expected)
    {
        var source = await OrientationFixtures.TiffAsync(orientation);
        var original = (byte[])source.Clone();
        var payload = await ImageClipboardSnapshot.CreateAsync(source, rotation);
        var expectedPixels = OrientationFixtures.Pixels(expected);
        foreach (var bytes in new[] { payload.Png, OrientationFixtures.BmpFromDib(payload.DibV5, true),
                     OrientationFixtures.BmpFromDib(payload.Dib, false) })
        {
            var result = await OrientationFixtures.DecodeAsync(bytes);
            Assert.Equal((uint)width, result.Width);
            Assert.Equal((uint)height, result.Height);
            Assert.Equal(expectedPixels, result.Pixels);
        }
        // 픽셀 수는 축을 잘못 바꿔도 같으므로 헤더의 부호와 크기도 따로 확인한다.
        Assert.Equal(width, BinaryPrimitives.ReadInt32LittleEndian(payload.DibV5.AsSpan(4)));
        Assert.Equal(-height, BinaryPrimitives.ReadInt32LittleEndian(payload.DibV5.AsSpan(8)));
        Assert.Equal(width, BinaryPrimitives.ReadInt32LittleEndian(payload.Dib.AsSpan(4)));
        Assert.Equal(height, BinaryPrimitives.ReadInt32LittleEndian(payload.Dib.AsSpan(8)));
        var respected = await OrientationFixtures.DecodeAsync(payload.Png, ExifOrientationMode.RespectExifOrientation);
        Assert.Equal((uint)width, respected.Width);
        Assert.Equal((uint)height, respected.Height);
        Assert.Equal(expectedPixels, respected.Pixels);
        Assert.Equal(original, source);
    }

    [Theory]
    [InlineData(null)]
    [InlineData(0)]
    [InlineData(9)]
    [InlineData(65535)]
    public async Task MissingOrUndefinedOrientationPreservesPixelsAndAllowsManualRotation(int? orientation)
    {
        var source = await OrientationFixtures.TiffAsync(orientation.HasValue ? checked((ushort)orientation.Value) : null);
        var payload = await ImageClipboardSnapshot.CreateAsync(source, 90);
        var result = await OrientationFixtures.DecodeAsync(payload.Png);
        Assert.Equal(2u, result.Width);
        Assert.Equal(3u, result.Height);
        Assert.Equal(OrientationFixtures.Pixels("DAEBFC"), result.Pixels);
    }

    [Fact]
    public async Task DecodableImageWithEmptyTagCountUsesTheNativeRecoveredOrientation()
    {
        var source = OrientationFixtures.MalformedOrientation(await OrientationFixtures.TiffAsync(6));
        var decoded = await OrientationFixtures.DecodeAsync(source);
        Assert.Equal(OrientationFixtures.Pixels("ABCDEF"), decoded.Pixels);
        var payload = await ImageClipboardSnapshot.CreateAsync(source, 0);
        var result = await OrientationFixtures.DecodeAsync(payload.Png);
        // WIC는 count=0인 손상 태그에서도 남아 있는 SHORT6을 복구한다. 복사도 같은 계약을 따른다.
        Assert.Equal(2u, result.Width);
        Assert.Equal(3u, result.Height);
        Assert.Equal(OrientationFixtures.Pixels("DAEBFC"), result.Pixels);
    }

    [Theory]
    [InlineData(5, "ADBECF")]
    [InlineData(6, "DAEBFC")]
    [InlineData(7, "FCEBDA")]
    public async Task JpegMetadataIsPresentAndReflectedPixelsFollowTheSameContract(ushort orientation, string expected)
    {
        var source = await OrientationFixtures.JpegAsync(orientation);
        using var stream = new MemoryStream(source);
        var decoder = await BitmapDecoder.CreateAsync(stream.AsRandomAccessStream());
        var metadata = await decoder.BitmapProperties.GetPropertiesAsync(new[] { "System.Photo.Orientation" });
        Assert.Equal(orientation, Assert.IsType<ushort>(metadata["System.Photo.Orientation"].Value));
        Assert.Equal(360u, decoder.PixelWidth);
        Assert.Equal(240u, decoder.PixelHeight);
        var payload = await ImageClipboardSnapshot.CreateAsync(source, 0);
        var result = await OrientationFixtures.DecodeAsync(payload.Png);
        Assert.Equal(240u, result.Width);
        Assert.Equal(360u, result.Height);
        var colors = OrientationFixtures.Pixels(expected);
        for (var cell = 0; cell < 6; cell++)
        {
            var offset = ((cell / 2 * 120 + 60) * 240 + cell % 2 * 120 + 60) * 4;
            for (var channel = 0; channel < 3; channel++)
                Assert.InRange(Math.Abs(result.Pixels[offset + channel] - colors[cell * 4 + channel]), 0, 3);
            Assert.Equal(255, result.Pixels[offset + 3]);
        }
    }

    [Theory]
    [InlineData(1, 3, 2, "ABCDEF")]
    [InlineData(2, 3, 2, "CBAFED")]
    [InlineData(3, 3, 2, "FEDCBA")]
    [InlineData(4, 3, 2, "DEFABC")]
    [InlineData(5, 2, 3, "ADBECF")]
    [InlineData(6, 2, 3, "DAEBFC")]
    [InlineData(7, 2, 3, "FCEBDA")]
    [InlineData(8, 2, 3, "CFBEAD")]
    public async Task ActualPsdLoaderNormalizesAllExifStatesAndPreservesAlphaAndProfile(
        ushort orientation, uint width, uint height, string expected)
    {
        var psd = OrientationFixtures.PsdWithExif(await OrientationFixtures.TiffAsync(orientation, alpha: true), layered: false);
        using var tagged = new MagickImage(psd);
        Assert.Equal(orientation, tagged.GetExifProfile()!.GetValue(ExifTag.Orientation)!.Value);
        Assert.Equal((OrientationType)orientation, tagged.Orientation);
        var profile = tagged.GetColorProfile()!.ToByteArray();
        var path = Path.Combine(Path.GetTempPath(), $"kotu-exif-{Guid.NewGuid():N}.psd");
        try
        {
            await File.WriteAllBytesAsync(path, psd);
            var decoded = MagickImageDecoder.Read(path);
            Assert.Equal(width, decoded.Width);
            Assert.Equal(height, decoded.Height);
            foreach (var mode in new[] { ExifOrientationMode.IgnoreExifOrientation, ExifOrientationMode.RespectExifOrientation })
            {
                var result = await OrientationFixtures.DecodeAsync(decoded.Png, mode);
                Assert.Equal(width, result.Width);
                Assert.Equal(height, result.Height);
                Assert.Equal(OrientationFixtures.Pixels(expected, alpha: true), result.Pixels);
            }
            using var normalized = new MagickImage(decoded.Png);
            Assert.Equal(profile, normalized.GetColorProfile()!.ToByteArray());
            Assert.Equal(psd, await File.ReadAllBytesAsync(path));
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public async Task PsdLoaderKeepsTheFirstCompositeInsteadOfTakingALayer()
    {
        var psd = OrientationFixtures.PsdWithExif(await OrientationFixtures.TiffAsync(5), layered: true);
        using var frames = new MagickImageCollection(psd);
        Assert.True(frames.Count >= 2);
        var path = Path.Combine(Path.GetTempPath(), $"kotu-composite-{Guid.NewGuid():N}.psd");
        try
        {
            await File.WriteAllBytesAsync(path, psd);
            var result = await OrientationFixtures.DecodeAsync(MagickImageDecoder.Read(path).Png);
            Assert.Equal(2u, result.Width);
            Assert.Equal(3u, result.Height);
            Assert.Equal(OrientationFixtures.Pixels("ADBECF"), result.Pixels);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public async Task UntaggedPsdKeepsItsPixelOrderAndAlpha()
    {
        using var image = new MagickImage(await OrientationFixtures.TiffAsync(null, alpha: true));
        image.RemoveProfile("exif");
        image.Orientation = OrientationType.Undefined;
        var psd = image.ToByteArray(MagickFormat.Psd);
        using var untagged = new MagickImage(psd);
        Assert.Equal(OrientationType.Undefined, untagged.Orientation);
        var path = Path.Combine(Path.GetTempPath(), $"kotu-untagged-{Guid.NewGuid():N}.psd");
        try
        {
            await File.WriteAllBytesAsync(path, psd);
            var result = await OrientationFixtures.DecodeAsync(MagickImageDecoder.Read(path).Png);
            Assert.Equal(3u, result.Width);
            Assert.Equal(2u, result.Height);
            Assert.Equal(OrientationFixtures.Pixels("ABCDEF", alpha: true), result.Pixels);
            Assert.Equal(psd, await File.ReadAllBytesAsync(path));
        }
        finally { File.Delete(path); }
    }
}
