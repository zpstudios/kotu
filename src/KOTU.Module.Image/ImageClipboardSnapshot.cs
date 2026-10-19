using System.Buffers.Binary;
using Windows.Graphics.Imaging;

namespace KOTU.Module.Image;

/// <summary>표시 배율과 무관한 원본 픽셀의 첫 프레임. EXIF는 화면의 합산 회전에 이미 포함되어 있다.</summary>
internal static class ImageClipboardSnapshot
{
    public static async Task<byte[]> EncodePngAsync(byte[] source, int rotation) =>
        (await CreateAsync(source, rotation)).Png;

    public static async Task<ImageClipboardPayload> CreateAsync(byte[] source, int rotation)
    {
        var turn = rotation switch
        {
            0 => BitmapRotation.None,
            90 => BitmapRotation.Clockwise90Degrees,
            180 => BitmapRotation.Clockwise180Degrees,
            270 => BitmapRotation.Clockwise270Degrees,
            _ => throw new ArgumentOutOfRangeException(nameof(rotation)),
        };
        using var input = new MemoryStream(source, writable: false);
        var decoder = await BitmapDecoder.CreateAsync(input.AsRandomAccessStream());
        var swapped = rotation is 90 or 270;
        var width = checked((int)(swapped ? decoder.PixelHeight : decoder.PixelWidth));
        var height = checked((int)(swapped ? decoder.PixelWidth : decoder.PixelHeight));
        ValidatePixelSize(width, height);
        var pixels = await decoder.GetPixelDataAsync(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Straight,
            new BitmapTransform { Rotation = turn }, ExifOrientationMode.IgnoreExifOrientation,
            ColorManagementMode.ColorManageToSRgb);
        var bgra = pixels.DetachPixelData();
        var dibV5 = CreateDibV5(width, height, bgra);
        var dib = CreateDib(width, height, bgra);
        using var output = new MemoryStream();
        var encoder = await BitmapEncoder.CreateAsync(BitmapEncoder.PngEncoderId, output.AsRandomAccessStream());
        encoder.SetPixelData(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Straight,
            (uint)width, (uint)height, 96, 96, bgra);
        await encoder.FlushAsync();
        return new ImageClipboardPayload(output.ToArray(), dibV5, dib);
    }

    private static int ValidatePixelSize(int width, int height)
    {
        if (width <= 0 || height <= 0) throw new ArgumentOutOfRangeException(nameof(width));
        return checked(width * height * 4);
    }

    internal static byte[] CreateDibV5(int width, int height, byte[] bgra)
    {
        var pixelSize = ValidatePixelSize(width, height);
        if (bgra.Length != pixelSize) throw new ArgumentException("Invalid pixel buffer", nameof(bgra));
        // 상단부터 내려가는 32비트 BGRA. 파일 헤더 없이 V5 헤더 바로 뒤에 픽셀을 둔다.
        var dib = new byte[checked(124 + pixelSize)];
        WriteHeader(dib, 124, width, -height, 32, pixelSize);
        Write32(dib, 16, 3); // BI_BITFIELDS
        Write32(dib, 40, 0x00ff0000);
        Write32(dib, 44, 0x0000ff00);
        Write32(dib, 48, 0x000000ff);
        Write32(dib, 52, 0xff000000);
        Write32(dib, 56, 0x73524742); // LCS_sRGB
        Write32(dib, 108, 4); // LCS_GM_IMAGES
        bgra.CopyTo(dib, 124);
        return dib;
    }

    internal static byte[] CreateDib(int width, int height, byte[] bgra)
    {
        if (bgra.Length != ValidatePixelSize(width, height))
            throw new ArgumentException("Invalid pixel buffer", nameof(bgra));
        var stride = checked((width * 3 + 3) & ~3);
        var pixelSize = checked(stride * height);
        var dib = new byte[checked(40 + pixelSize)];
        WriteHeader(dib, 40, width, height, 24, pixelSize);
        // 알파를 모르는 구형 소비자에는 흰 배경 합성 BGR을 아래 행부터 준다.
        // PNG와 DIBV5는 원래의 투명도를 그대로 보존한다.
        for (var y = 0; y < height; y++)
        {
            var source = y * width * 4;
            var target = 40 + (height - 1 - y) * stride;
            for (var x = 0; x < width; x++, source += 4, target += 3)
            {
                var alpha = bgra[source + 3];
                for (var channel = 0; channel < 3; channel++)
                    dib[target + channel] = (byte)((bgra[source + channel] * alpha + 255 * (255 - alpha) + 127) / 255);
            }
        }
        return dib;
    }

    private static void WriteHeader(byte[] dib, int size, int width, int height, ushort bits, int pixelSize)
    {
        Write32(dib, 0, (uint)size);
        Write32(dib, 4, (uint)width);
        BinaryPrimitives.WriteInt32LittleEndian(dib.AsSpan(8), height);
        BinaryPrimitives.WriteUInt16LittleEndian(dib.AsSpan(12), 1);
        BinaryPrimitives.WriteUInt16LittleEndian(dib.AsSpan(14), bits);
        Write32(dib, 20, (uint)pixelSize);
        Write32(dib, 24, 3780);
        Write32(dib, 28, 3780);
    }

    private static void Write32(byte[] data, int offset, uint value) =>
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(offset), value);
}

internal sealed record ImageClipboardPayload(byte[] Png, byte[] DibV5, byte[] Dib);

/// <summary>이미지 전환 후 늦게 도착한 복사 결과와 겹친 키 입력을 버린다. UI 스레드 전용.</summary>
internal sealed class ImageCopyGate
{
    private long _revision;
    private long? _pending;

    internal bool IsPending => _pending is not null;

    internal bool TryBegin(out long request)
    {
        request = _revision;
        if (_pending is not null) return false;
        request = ++_revision;
        _pending = request;
        return true;
    }

    internal void Invalidate() => _revision++;
    internal bool IsCurrent(long request) => _pending == request && _revision == request;
    internal void Finish(long request)
    {
        if (_pending == request) _pending = null;
    }
}
