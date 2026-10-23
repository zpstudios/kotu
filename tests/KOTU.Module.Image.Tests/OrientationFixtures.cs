using System.Buffers.Binary;
using ImageMagick;
using Windows.Graphics.Imaging;
using Xunit;

namespace KOTU.Module.Image.Tests;

internal static class OrientationFixtures
{
    // ABC / DEF: 비정사각형의 서로 다른 여섯 색. 기대 순서는 제품 변환식 없이 리터럴로 지정한다.
    internal static byte[] Pixels(string order, bool alpha = false) => order.SelectMany(label => label switch
    {
        'A' => new byte[] { 0, 0, 255, 255 },
        'B' => new byte[] { 0, 255, 0, alpha ? (byte)128 : (byte)255 },
        'C' => new byte[] { 255, 0, 0, 255 },
        'D' => new byte[] { 0, 255, 255, 255 },
        'E' => new byte[] { 255, 0, 255, 255 },
        'F' => new byte[] { 255, 255, 0, alpha ? (byte)64 : (byte)255 },
        _ => throw new ArgumentOutOfRangeException(nameof(order)),
    }).ToArray();

    internal static async Task<byte[]> TiffAsync(ushort? orientation, bool alpha = false)
    {
        using var stream = new MemoryStream();
        var encoder = await BitmapEncoder.CreateAsync(BitmapEncoder.TiffEncoderId, stream.AsRandomAccessStream());
        encoder.SetPixelData(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Straight, 3, 2, 96, 96, Pixels("ABCDEF", alpha));
        if (orientation.HasValue)
            await encoder.BitmapProperties.SetPropertiesAsync(new Dictionary<string, BitmapTypedValue>
            {
                ["/ifd/{ushort=274}"] = new(orientation.Value, Windows.Foundation.PropertyType.UInt16),
            });
        await encoder.FlushAsync();
        return stream.ToArray();
    }

    internal static async Task<byte[]> JpegAsync(ushort orientation)
    {
        var colors = Pixels("ABCDEF");
        var pixels = new byte[360 * 240 * 4];
        for (var y = 0; y < 240; y++)
            for (var x = 0; x < 360; x++)
                colors.AsSpan((y / 120 * 3 + x / 120) * 4, 4).CopyTo(pixels.AsSpan((y * 360 + x) * 4));
        using var stream = new MemoryStream();
        var encoder = await BitmapEncoder.CreateAsync(BitmapEncoder.JpegEncoderId, stream.AsRandomAccessStream());
        encoder.SetPixelData(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Straight, 360, 240, 96, 96, pixels);
        await encoder.BitmapProperties.SetPropertiesAsync(new Dictionary<string, BitmapTypedValue>
        {
            ["/app1/ifd/{ushort=274}"] = new(orientation, Windows.Foundation.PropertyType.UInt16),
        });
        await encoder.FlushAsync();
        return stream.ToArray();
    }

    internal static async Task<(uint Width, uint Height, byte[] Pixels)> DecodeAsync(byte[] bytes,
        ExifOrientationMode orientation = ExifOrientationMode.IgnoreExifOrientation)
    {
        using var stream = new MemoryStream(bytes);
        var decoder = await BitmapDecoder.CreateAsync(stream.AsRandomAccessStream());
        var respect = orientation == ExifOrientationMode.RespectExifOrientation;
        var pixels = await decoder.GetPixelDataAsync(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Straight,
            new BitmapTransform(), orientation, ColorManagementMode.DoNotColorManage);
        return (respect ? decoder.OrientedPixelWidth : decoder.PixelWidth,
            respect ? decoder.OrientedPixelHeight : decoder.PixelHeight, pixels.DetachPixelData());
    }

    internal static byte[] BmpFromDib(byte[] dib, bool v5)
    {
        var file = new byte[14 + dib.Length];
        file[0] = (byte)'B';
        file[1] = (byte)'M';
        BinaryPrimitives.WriteInt32LittleEndian(file.AsSpan(2), file.Length);
        BinaryPrimitives.WriteInt32LittleEndian(file.AsSpan(10), 14 + (v5 ? 124 : 40));
        dib.CopyTo(file, 14);
        return file;
    }

    internal static byte[] MalformedOrientation(byte[] tiff)
    {
        var result = (byte[])tiff.Clone();
        Assert.Equal(new byte[] { (byte)'I', (byte)'I' }, result[..2]);
        var ifd = checked((int)BinaryPrimitives.ReadUInt32LittleEndian(result.AsSpan(4)));
        var count = BinaryPrimitives.ReadUInt16LittleEndian(result.AsSpan(ifd));
        for (var index = 0; index < count; index++)
        {
            var entry = ifd + 2 + index * 12;
            if (BinaryPrimitives.ReadUInt16LittleEndian(result.AsSpan(entry)) != 274) continue;
            // 방향은 SHORT 하나여야 한다. 태그만 빈 값으로 손상시키고 픽셀과 나머지 IFD는 보존한다.
            BinaryPrimitives.WriteUInt32LittleEndian(result.AsSpan(entry + 4), 0);
            return result;
        }
        throw new InvalidOperationException("Fixture did not contain an orientation tag.");
    }

    internal static byte[] PsdWithExif(byte[] tiff, bool layered)
    {
        using var composite = new MagickImage(tiff);
        // sRGB는 PNG 인코더가 sRGB 청크로 대체할 수 있어 비표준 RGB 프로필로 보존을 확인한다.
        composite.SetProfile(ColorProfiles.AdobeRGB1998);
        byte[] psd;
        if (layered)
        {
            using var images = new MagickImageCollection();
            images.Add(composite.Clone());
            images.Add(new MagickImage(MagickColors.White, 3, 2));
            psd = images.ToByteArray(MagickFormat.Psd);
        }
        else psd = composite.ToByteArray(MagickFormat.Psd);

        // Adobe PSD 표준1058(EXIF data1) 리소스. Magick의 PSD 쓰기가 EXIF를 버려 직접 삽입한다.
        // 독립 TIFF의 IFD를 넣으므로 제품의 방향 보정 코드와 fixture 생성이 같은 함수를 쓰지 않는다.
        var resourceOffset = checked(30 + BinaryPrimitives.ReadInt32BigEndian(psd.AsSpan(26)));
        var resourceLength = BinaryPrimitives.ReadInt32BigEndian(psd.AsSpan(resourceOffset));
        var resource = new byte[12 + tiff.Length + (tiff.Length & 1)];
        "8BIM"u8.CopyTo(resource);
        BinaryPrimitives.WriteUInt16BigEndian(resource.AsSpan(4), 1058);
        BinaryPrimitives.WriteInt32BigEndian(resource.AsSpan(8), tiff.Length);
        tiff.CopyTo(resource, 12);
        var insertion = resourceOffset + 4 + resourceLength;
        var result = new byte[psd.Length + resource.Length];
        psd.AsSpan(0, insertion).CopyTo(result);
        resource.CopyTo(result, insertion);
        psd.AsSpan(insertion).CopyTo(result.AsSpan(insertion + resource.Length));
        BinaryPrimitives.WriteInt32BigEndian(result.AsSpan(resourceOffset), resourceLength + resource.Length);
        return result;
    }
}
