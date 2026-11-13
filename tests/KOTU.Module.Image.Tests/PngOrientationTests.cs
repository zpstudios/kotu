using System.Buffers.Binary;
using ImageMagick;
using KOTU.Module.Image;
using Windows.Graphics.Imaging;
using Xunit;

namespace KOTU.Module.Image.Tests;

public sealed class PngOrientationTests
{
    public static IEnumerable<object[]> Cases()
    {
        string[][] orders = [
            ["ABCDEF", "DAEBFC", "FEDCBA", "CFBEAD"],
            ["CBAFED", "FCEBDA", "DEFABC", "ADBECF"],
            ["FEDCBA", "CFBEAD", "ABCDEF", "DAEBFC"],
            ["DEFABC", "ADBECF", "CBAFED", "FCEBDA"],
            ["ADBECF", "CBAFED", "FCEBDA", "DEFABC"],
            ["DAEBFC", "FEDCBA", "CFBEAD", "ABCDEF"],
            ["FCEBDA", "DEFABC", "ADBECF", "CBAFED"],
            ["CFBEAD", "ABCDEF", "DAEBFC", "FEDCBA"]];
        for (ushort orientation = 1; orientation <= 8; orientation++)
            for (var turn = 0; turn < 4; turn++)
                foreach (var bigEndian in new[] { false, true })
                {
                    var swap = (orientation >= 5) ^ (turn % 2 == 1);
                    yield return [orientation, turn * 90, bigEndian, swap ? 2u : 3u, swap ? 3u : 2u, orders[orientation - 1][turn]];
                }
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public async Task StandardPngExifAndManualRotationAgreeInAllClipboardFormats(
        ushort orientation, int rotation, bool bigEndian, uint width, uint height, string order)
    {
        var source = await PngFixture.CreateAsync(orientation, bigEndian);
        var original = source.ToArray();
        using (var independent = new MagickImage(source))
            Assert.Equal(orientation, independent.GetExifProfile()!.GetValue(ExifTag.Orientation)!.Value);
        var payload = await ImageClipboardSnapshot.CreateAsync(source, rotation);
        foreach (var bytes in new[] { payload.Png, OrientationFixtures.BmpFromDib(payload.DibV5, true), OrientationFixtures.BmpFromDib(payload.Dib, false) })
        {
            var decoded = await OrientationFixtures.DecodeAsync(bytes);
            Assert.Equal(width, decoded.Width);
            Assert.Equal(height, decoded.Height);
            Assert.Equal(OrientationFixtures.Pixels(order), decoded.Pixels);
        }
        Assert.Equal(original, source);
    }
    [Theory]
    [InlineData(2, 3u, 2u, "CBAFED")]
    [InlineData(3, 3u, 2u, "FEDCBA")]
    [InlineData(4, 3u, 2u, "DEFABC")]
    [InlineData(5, 2u, 3u, "ADBECF")]
    [InlineData(6, 2u, 3u, "DAEBFC")]
    [InlineData(7, 2u, 3u, "FCEBDA")]
    [InlineData(8, 2u, 3u, "CFBEAD")]
    public async Task DisplayAndPrintBytesAreNormalizedOnceWithAlphaAndColorProfile(
        ushort orientation, uint width, uint height, string order)
    {
        using var image = new MagickImage(await PngFixture.CreateAsync(null, alpha: true));
        image.SetProfile(ColorProfiles.AdobeRGB1998);
        var source = PngFixture.InsertChunk(image.ToByteArray(MagickFormat.Png), "eXIf", PngFixture.Exif(orientation, false));
        var original = source.ToArray();
        var normalized = await PngExifOrientation.NormalizeAsync(source);
        Assert.NotSame(source, normalized);
        Assert.Same(normalized, await PngExifOrientation.NormalizeAsync(normalized));
        foreach (var mode in new[] { ExifOrientationMode.IgnoreExifOrientation, ExifOrientationMode.RespectExifOrientation })
        {
            var decoded = await OrientationFixtures.DecodeAsync(normalized, mode);
            Assert.Equal(width, decoded.Width);
            Assert.Equal(height, decoded.Height);
            Assert.Equal(OrientationFixtures.Pixels(order, alpha: true), decoded.Pixels);
        }
        using var result = new MagickImage(normalized);
        Assert.Equal(ColorProfiles.AdobeRGB1998.ToByteArray(), result.GetColorProfile()!.ToByteArray());
        Assert.Equal(original, source);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SixteenBitSamplesAndResolutionSurviveTranscoding(bool gray)
    {
        ushort[][] values = gray
            ? [[0x1234], [0x4567], [0x89ab], [0xcdef], [0x2345], [0x6789]]
            : [[0x1234, 0x4567, 0x89ab, 65535], [0x2345, 0x5678, 0x9abc, 0x7fff],
               [0x3456, 0x6789, 0xabcd, 65535], [0x4567, 0x789a, 0xbcde, 65535],
               [0x5678, 0x89ab, 0xcdef, 65535], [0x6789, 0x9abc, 0xdef0, 0x3fff]];
        var format = BitmapPixelFormat.Rgba16;
        var alpha = BitmapAlphaMode.Straight;
        var source = PngFixture.InsertChunk(PngFixture.SixteenBit(values, gray), "eXIf", PngFixture.Exif(6, false));
        var normalized = await PngExifOrientation.NormalizeAsync(source);
        Assert.Equal(16, normalized[24]);
        using var stream = new MemoryStream(normalized);
        var decoder = await BitmapDecoder.CreateAsync(stream.AsRandomAccessStream());
        Assert.Equal(2u, decoder.PixelWidth);
        Assert.Equal(3u, decoder.PixelHeight);
        Assert.InRange(decoder.DpiX, 143.9, 144.1);
        Assert.InRange(decoder.DpiY, 143.9, 144.1);
        var data = (await decoder.GetPixelDataAsync(format, alpha, new BitmapTransform(),
            ExifOrientationMode.IgnoreExifOrientation, ColorManagementMode.DoNotColorManage)).DetachPixelData();
        var actual = Enumerable.Range(0, data.Length / 2).Select(i => BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(i * 2))).ToArray();
        Assert.Equal(new[] { 3, 0, 4, 1, 5, 2 }.SelectMany(i => gray ? new ushort[] { values[i][0], values[i][0], values[i][0], 65535 } : values[i]), actual);
    }

    [Theory]
    [InlineData(null)]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(9)]
    [InlineData(65535)]
    public async Task AbsentIdentityAndInvalidOrientationKeepOriginalBytes(int? orientation)
    {
        var source = await PngFixture.CreateAsync(orientation.HasValue ? (ushort)orientation.Value : null);
        Assert.Same(source, await PngExifOrientation.NormalizeAsync(source));
    }

    [Theory]
    [InlineData("crc")]
    [InlineData("duplicate")]
    [InlineData("type")]
    [InlineData("count")]
    [InlineData("ifd")]
    [InlineData("truncated-ifd")]
    [InlineData("duplicate-tag")]
    [InlineData("chunk-overflow")]
    [InlineData("missing-iend")]
    [InlineData("animation")]
    public async Task AmbiguousOrMalformedMetadataAndAnimationAreNotReinterpreted(string fault)
    {
        var png = await PngFixture.CreateAsync(null);
        var exif = PngFixture.Exif(6, false);
        switch (fault)
        {
            case "type": exif[12] = 4; break;
            case "count": exif[14] = 0; break;
            case "ifd": BinaryPrimitives.WriteUInt32LittleEndian(exif.AsSpan(4), uint.MaxValue); break;
            case "truncated-ifd": exif = exif[..20]; break;
            case "duplicate-tag":
                exif = [.. exif.AsSpan(0, 22), .. exif.AsSpan(10, 12), 0, 0, 0, 0];
                exif[8] = 2;
                break;
        }
        var source = PngFixture.InsertChunk(png, "eXIf", exif);
        switch (fault)
        {
            case "crc": source[33 + 8 + exif.Length] ^= 1; break;
            case "duplicate": source = PngFixture.InsertChunk(source, "eXIf", exif); break;
            case "chunk-overflow": BinaryPrimitives.WriteUInt32BigEndian(source.AsSpan(33), uint.MaxValue); break;
            case "missing-iend": source = source[..^12]; break;
            case "animation": source = PngFixture.InsertChunk(source, "acTL", [0, 0, 0, 1, 0, 0, 0, 0]); break;
        }
        Assert.Same(source, await PngExifOrientation.NormalizeAsync(source));
    }

    [Fact]
    public async Task NonPngSourcesRemainUntouched()
    {
        foreach (var source in new[] { await OrientationFixtures.JpegAsync(6), await OrientationFixtures.TiffAsync(6), Array.Empty<byte>(), "GIF89a"u8.ToArray() })
            Assert.Same(source, await PngExifOrientation.NormalizeAsync(source));
    }
}

internal static class PngFixture
{
    internal static async Task<byte[]> CreateAsync(ushort? orientation, bool bigEndian = false, bool alpha = false, int cellSize = 1)
    {
        var width = 3 * cellSize;
        var height = 2 * cellSize;
        var pixels = new byte[width * height * 4];
        var colors = OrientationFixtures.Pixels("ABCDEF", alpha);
        for (var y = 0; y < height; y++)
            for (var x = 0; x < width; x++)
                colors.AsSpan((y / cellSize * 3 + x / cellSize) * 4, 4).CopyTo(pixels.AsSpan((y * width + x) * 4));
        using var output = new MemoryStream();
        var encoder = await BitmapEncoder.CreateAsync(BitmapEncoder.PngEncoderId, output.AsRandomAccessStream());
        encoder.SetPixelData(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Straight, (uint)width, (uint)height, 96, 96, pixels);
        await encoder.FlushAsync();
        return orientation.HasValue ? InsertChunk(output.ToArray(), "eXIf", Exif(orientation.Value, bigEndian)) : output.ToArray();
    }

    internal static byte[] SixteenBit(ushort[][] pixels, bool gray)
    {
        // PNG의 네트워크 바이트 순서·무필터 행을 직접 작성해 WIC 인코더 지원 형식에 의존하지 않는다.
        var header = new byte[13];
        BinaryPrimitives.WriteUInt32BigEndian(header, 3);
        BinaryPrimitives.WriteUInt32BigEndian(header.AsSpan(4), 2);
        header[8] = 16;
        header[9] = gray ? (byte)0 : (byte)6;
        var png = InsertChunk([137, 80, 78, 71, 13, 10, 26, 10], "IHDR", header, 8);
        var resolution = new byte[9];
        BinaryPrimitives.WriteUInt32BigEndian(resolution, 5669);
        BinaryPrimitives.WriteUInt32BigEndian(resolution.AsSpan(4), 5669);
        resolution[8] = 1;
        png = InsertChunk(png, "pHYs", resolution, png.Length);
        using var compressed = new MemoryStream();
        using (var zlib = new System.IO.Compression.ZLibStream(compressed, System.IO.Compression.CompressionLevel.Optimal, true))
            for (var y = 0; y < 2; y++)
            {
                zlib.WriteByte(0);
                foreach (var value in pixels.Skip(y * 3).Take(3).SelectMany(pixel => pixel))
                {
                    zlib.WriteByte((byte)(value >> 8));
                    zlib.WriteByte((byte)value);
                }
            }
        png = InsertChunk(png, "IDAT", compressed.ToArray(), png.Length);
        return InsertChunk(png, "IEND", [], png.Length);
    }

    internal static byte[] Exif(ushort orientation, bool bigEndian)
    {
        var data = new byte[26];
        data[0] = data[1] = bigEndian ? (byte)'M' : (byte)'I';
        void Word(int offset, ushort value)
        {
            if (bigEndian) BinaryPrimitives.WriteUInt16BigEndian(data.AsSpan(offset), value);
            else BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(offset), value);
        }
        void Dword(int offset, uint value)
        {
            if (bigEndian) BinaryPrimitives.WriteUInt32BigEndian(data.AsSpan(offset), value);
            else BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(offset), value);
        }
        Word(2, 42); Dword(4, 8); Word(8, 1);
        Word(10, 274); Word(12, 3); Dword(14, 1); Word(18, orientation);
        return data;
    }

    internal static byte[] InsertChunk(byte[] png, string name, byte[] data, int offset = 33)
    {
        var chunk = new byte[data.Length + 12];
        BinaryPrimitives.WriteInt32BigEndian(chunk, data.Length);
        System.Text.Encoding.ASCII.GetBytes(name).CopyTo(chunk, 4);
        data.CopyTo(chunk, 8);
        uint crc = uint.MaxValue;
        foreach (var value in chunk.AsSpan(4, data.Length + 4))
        {
            crc ^= value;
            for (var bit = 0; bit < 8; bit++) crc = (crc >> 1) ^ ((crc & 1) != 0 ? 0xedb88320u : 0);
        }
        BinaryPrimitives.WriteUInt32BigEndian(chunk.AsSpan(data.Length + 8), ~crc);
        return [.. png.AsSpan(0, offset), .. chunk, .. png.AsSpan(offset)];
    }
}
