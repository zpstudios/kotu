using ImageMagick;
using KOTU.App.Controls;
using Xunit;

namespace KOTU.Module.Image.Tests;

public sealed class LocalImageThumbnailTests
{
    [Theory]
    [InlineData(1, "ABCDEF")]
    [InlineData(2, "CBAFED")]
    [InlineData(3, "FEDCBA")]
    [InlineData(4, "DEFABC")]
    [InlineData(5, "ADBECF")]
    [InlineData(6, "DAEBFC")]
    [InlineData(7, "FCEBDA")]
    [InlineData(8, "CFBEAD")]
    public async Task LocalPngAndTiffThumbnailsUseOrientedPixels(int orientation, string order)
    {
        foreach (var extension in new[] { ".png", ".tiff" })
        {
            var bytes = extension == ".png" ? await PngFixture.CreateAsync((ushort)orientation)
                : await OrientationFixtures.TiffAsync((ushort)orientation);
            var result = LocalImageThumbnail.Read("sample" + extension, false, false, new(30, 30), default,
                _ => FileAttributes.Normal, _ => new MemoryStream(bytes))!;
            Assert.Equal(orientation >= 5 ? 2 : 3, result.Width);
            Assert.Equal(orientation >= 5 ? 3 : 2, result.Height);
            Assert.Equal(OrientationFixtures.Pixels(order), result.Bgra);
        }
    }

    [Theory]
    [InlineData(2, "CBAFED")]
    [InlineData(3, "FEDCBA")]
    [InlineData(5, "ADBECF")]
    [InlineData(7, "FCEBDA")]
    public async Task PackedPaletteReflectionIsNotLost(int orientation, string order)
    {
        using var image = new MagickImage(await PngFixture.CreateAsync(null));
        var bytes = PngFixture.InsertChunk(image.ToByteArray(MagickFormat.Png), "eXIf", PngFixture.Exif((ushort)orientation, false));
        var result = LocalImageThumbnail.Read("sample.png", false, false, new(30,30), default,
            _=>FileAttributes.Normal, _=>new MemoryStream(bytes))!;
        Assert.Equal(OrientationFixtures.Pixels(order), result.Bgra);
    }

    [Theory]
    [InlineData(true, false, 0)]
    [InlineData(false, true, 0)]
    [InlineData(false, false, 0x40000)]
    [InlineData(false, false, 0x400000)]
    public void CachedOrCloudRequestsNeverOpenOriginals(bool listed, bool cached, int attributes)
    {
        var reads = 0;
        var result = LocalImageThumbnail.Read("photo.png", listed, cached, new(96,96), default,
            _=>(FileAttributes)attributes, _=> { reads++; throw new InvalidOperationException(); });
        Assert.Null(result);
        Assert.Equal(0, reads);
    }

    [Fact]
    public void MissingAttributesAndCancellationDoNotOpenOriginals()
    {
        Assert.Null(LocalImageThumbnail.Read("photo.tiff", false, false, new(96,96), default,
            _=>throw new IOException(), _=>throw new InvalidOperationException()));
        Assert.Throws<OperationCanceledException>(() => LocalImageThumbnail.Read("photo.png", false, false,
            new(96,96), new CancellationToken(true), _=>throw new InvalidOperationException(), _=>throw new InvalidOperationException()));
    }

    [Fact]
    public async Task PortraitThumbnailFitsNonsquareTargetAndRoundtripsForLegacyGrid()
    {
        var bytes = await PngFixture.CreateAsync(6, cellSize:120);
        var result = LocalImageThumbnail.Read("photo.png", false, false, new(40,80), default,
            _=>FileAttributes.Normal, _=>new MemoryStream(bytes))!;
        Assert.Equal(40,result.Width); Assert.Equal(60,result.Height);
        var decoded = await OrientationFixtures.DecodeAsync(LocalImageThumbnail.Encode(result));
        Assert.Equal(result.Bgra,decoded.Pixels);
    }
}
