using ImageMagick;

namespace KOTU.Module.Image;

/// <summary>워커 전용 PSD 첫 합성 이미지 디코드. 방향이 보정된 PNG와 같은 축의 크기를 반환한다.</summary>
internal static class MagickImageDecoder
{
    internal static (byte[] Png, uint Width, uint Height, uint BitDepth) Read(string path)
    {
        using var magick = new MagickImage(path);
        // EXIF 회전·반전을 픽셀에 한 번 반영하고 방향 태그도 정방향으로 바꾼다.
        // PNG를 읽는 BitmapImage와 복사 디코더는 이 보정을 다시 적용하지 않는다.
        magick.AutoOrient();
        var bitDepth = (uint)(magick.Depth * magick.ChannelCount);
        return (magick.ToByteArray(MagickFormat.Png), magick.Width, magick.Height, bitDepth);
    }
}
