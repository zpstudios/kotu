using Windows.Graphics.Imaging;

namespace KOTU.Module.Image;

/// <summary>표시 배율과 무관한 원본 픽셀의 첫 프레임. EXIF는 화면의 합산 회전에 이미 포함되어 있다.</summary>
internal static class ImageClipboardSnapshot
{
    public static async Task<byte[]> EncodePngAsync(byte[] source, int rotation)
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
        var pixels = await decoder.GetPixelDataAsync(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Straight,
            new BitmapTransform { Rotation = turn }, ExifOrientationMode.IgnoreExifOrientation,
            ColorManagementMode.ColorManageToSRgb);
        var swapped = rotation is 90 or 270;
        using var output = new MemoryStream();
        var encoder = await BitmapEncoder.CreateAsync(BitmapEncoder.PngEncoderId, output.AsRandomAccessStream());
        encoder.SetPixelData(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Straight,
            swapped ? decoder.PixelHeight : decoder.PixelWidth,
            swapped ? decoder.PixelWidth : decoder.PixelHeight,
            96, 96, pixels.DetachPixelData());
        await encoder.FlushAsync();
        return output.ToArray();
    }
}

/// <summary>포커스/이미지 전환 후 늦게 도착한 복사 결과와 겹친 키 입력을 버린다. UI 스레드 전용.</summary>
internal sealed class ImageCopyGate
{
    private long _revision;
    private long? _pending;

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
