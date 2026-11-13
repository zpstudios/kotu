using System.Buffers.Binary;
using Windows.Graphics.Imaging;

namespace KOTU.Module.Image;

/// <summary>정적 PNG의 표준 eXIf 방향을 표시용 바이트에 한 번 반영한다. 원본은 수정하지 않는다.</summary>
internal static class PngExifOrientation
{
    private static readonly uint[] CrcTable = CreateCrcTable();

    internal static async Task<byte[]> NormalizeAsync(byte[] source)
    {
        if (!TryRead(source, out var orientation, out var offset, out var length)) return source;
        // 디코더가 앞으로 eXIf를 지원해도 이중 보정하지 않도록 입력 복사본에서만 방향 메타데이터를 뺀다.
        var untagged = new byte[source.Length - length];
        source.AsSpan(0, offset).CopyTo(untagged);
        source.AsSpan(offset + length).CopyTo(untagged.AsSpan(offset));
        return await TransformAsync(untagged, orientation);
    }

    internal static ushort ReadOrientation(byte[] source) =>
        TryRead(source, out var orientation, out _, out _, allowAnimation: true) ? orientation : (ushort)1;

    internal static byte[] WithoutExif(byte[] source, out ushort orientation)
    {
        orientation = 1;
        if (!TryRead(source, out var value, out var offset, out var length, allowAnimation: true)) return source;
        orientation = value;
        return [.. source.AsSpan(0, offset), .. source.AsSpan(offset + length)];
    }

    internal static BitmapTransform Transform(ushort orientation) => new()
    {
        Flip = orientation switch { 2 or 7 => BitmapFlip.Horizontal, 4 or 5 => BitmapFlip.Vertical, _ => BitmapFlip.None },
        Rotation = orientation switch
        {
            3 => BitmapRotation.Clockwise180Degrees,
            5 or 6 or 7 => BitmapRotation.Clockwise90Degrees,
            8 => BitmapRotation.Clockwise270Degrees,
            _ => BitmapRotation.None,
        },
    };

    internal static async Task<byte[]> TransformAsync(byte[] untagged, ushort orientation, CancellationToken cancellation = default)
    {
        using var input = new MemoryStream(untagged, writable: false);
        var decoder = await BitmapDecoder.CreateAsync(input.AsRandomAccessStream()).AsTask(cancellation);
        using var output = new MemoryStream();
        // 기존 픽셀 형식·색상 프로필·해상도를 복사하고 회전/반전만 적용한다. Q8 변환을 거치지 않는다.
        var encoder = await BitmapEncoder.CreateForTranscodingAsync(output.AsRandomAccessStream(), decoder).AsTask(cancellation);
        // WIC의 packed/indexed PNG 직접 변환은 홀수 폭에서 좌우 반전을 누락할 수 있다.
        // 팔레트·저비트 입력만 BGRA8로 펼치며 16비트 입력은 원래 정밀도를 유지한다.
        if (untagged[24] < 8 || untagged[25] == 3)
        {
            var pixels = (await decoder.GetPixelDataAsync(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Straight,
                new BitmapTransform(), ExifOrientationMode.IgnoreExifOrientation,
                ColorManagementMode.DoNotColorManage).AsTask(cancellation)).DetachPixelData();
            encoder.SetPixelData(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Straight,
                decoder.PixelWidth, decoder.PixelHeight, decoder.DpiX, decoder.DpiY, pixels);
        }
        var transform = Transform(orientation);
        encoder.BitmapTransform.Flip = transform.Flip;
        encoder.BitmapTransform.Rotation = transform.Rotation;
        await encoder.FlushAsync().AsTask(cancellation);
        return RestoreColorMetadata(untagged, output.ToArray());
    }

    // WinRT may replace iCCP with sRGB when SetPixelData expands indexed pixels. The samples above
    // are explicitly unmanaged; restore their original color description, not the encoder's default.
    internal static byte[] RestoreColorMetadata(byte[] source, byte[] encoded)
    {
        static bool IsColor(ReadOnlySpan<byte> type) => type.SequenceEqual("iCCP"u8) || type.SequenceEqual("sRGB"u8) ||
            type.SequenceEqual("gAMA"u8) || type.SequenceEqual("cHRM"u8) || type.SequenceEqual("cICP"u8) ||
            type.SequenceEqual("mDCV"u8) || type.SequenceEqual("cLLI"u8);
        using var colors = new MemoryStream();
        for (var offset = 8; offset <= source.Length - 12;)
        {
            var size = BinaryPrimitives.ReadUInt32BigEndian(source.AsSpan(offset));
            if (size > source.Length - offset - 12) break;
            var length = (int)size;
            if (IsColor(source.AsSpan(offset + 4, 4)) && ValidCrc(source.AsSpan(offset + 4, length + 4),
                    BinaryPrimitives.ReadUInt32BigEndian(source.AsSpan(offset + length + 8))))
                colors.Write(source.AsSpan(offset, length + 12));
            offset += length + 12;
        }
        if (colors.Length == 0) return encoded;
        using var result = new MemoryStream();
        result.Write(encoded.AsSpan(0, 33));
        colors.Position = 0; colors.CopyTo(result);
        for (var offset = 33; offset <= encoded.Length - 12;)
        {
            var length = BinaryPrimitives.ReadInt32BigEndian(encoded.AsSpan(offset));
            if (!IsColor(encoded.AsSpan(offset + 4, 4))) result.Write(encoded.AsSpan(offset, length + 12));
            offset += length + 12;
        }
        return result.ToArray();
    }

    private static bool TryRead(ReadOnlySpan<byte> source, out ushort orientation, out int exifOffset, out int exifLength,
        bool allowAnimation = false)
    {
        orientation = 0;
        exifOffset = exifLength = 0;
        if (source.Length < 33 || (source[0] != 0x89 || !source.Slice(1, 7).SequenceEqual("PNG\r\n\x1a\n"u8)) ||
            BinaryPrimitives.ReadUInt32BigEndian(source[8..]) != 13 || !source.Slice(12, 4).SequenceEqual("IHDR"u8)) return false;
        for (var position = 8; position <= source.Length - 12;)
        {
            var size = BinaryPrimitives.ReadUInt32BigEndian(source[position..]);
            if (size > (uint)(source.Length - position - 12)) return false;
            var count = (int)size;
            var type = source.Slice(position + 4, 4);
            // APNG는 프레임·타이밍을 보존한다. 이번 정적 PNG 보정으로 애니메이션을 평탄화하지 않는다.
            if (!allowAnimation && type.SequenceEqual("acTL"u8)) return false;
            if (type.SequenceEqual("eXIf"u8))
            {
                if (exifLength != 0 || !ValidCrc(source.Slice(position + 4, count + 4),
                        BinaryPrimitives.ReadUInt32BigEndian(source[(position + count + 8)..])) ||
                    !TryReadIfd(source.Slice(position + 8, count), out orientation)) return false;
                exifOffset = position;
                exifLength = count + 12;
            }
            if (type.SequenceEqual("IEND"u8)) return count == 0 && exifLength != 0 && orientation is >= 2 and <= 8;
            position += count + 12;
        }
        return false;
    }

    private static bool TryReadIfd(ReadOnlySpan<byte> data, out ushort orientation)
    {
        orientation = 0;
        if (data.Length < 8) return false;
        var little = data[..2].SequenceEqual("II"u8);
        if ((!little && !data[..2].SequenceEqual("MM"u8)) || Read16(data[2..], little) != 42) return false;
        var offset = Read32(data[4..], little);
        if (offset < 8 || offset > (uint)(data.Length - 2)) return false;
        var start = (int)offset;
        var count = Read16(data[start..], little);
        if ((long)start + 2 + count * 12L + 4 > data.Length) return false;
        // IFD0의 SHORT 하나만 읽는다. 하위 IFD/썸네일/순환 포인터는 따라가지 않는다.
        var found = false;
        for (var index = 0; index < count; index++)
        {
            var entry = data[(start + 2 + index * 12)..];
            if (Read16(entry, little) != 274) continue;
            if (found || Read16(entry[2..], little) != 3 || Read32(entry[4..], little) != 1) return false;
            found = true;
            orientation = Read16(entry[8..], little);
        }
        return found && orientation is >= 1 and <= 8;
    }

    private static ushort Read16(ReadOnlySpan<byte> data, bool little) => little
        ? BinaryPrimitives.ReadUInt16LittleEndian(data) : BinaryPrimitives.ReadUInt16BigEndian(data);
    private static uint Read32(ReadOnlySpan<byte> data, bool little) => little
        ? BinaryPrimitives.ReadUInt32LittleEndian(data) : BinaryPrimitives.ReadUInt32BigEndian(data);

    private static bool ValidCrc(ReadOnlySpan<byte> data, uint expected)
        => Crc32(data) == expected;

    internal static uint Crc32(ReadOnlySpan<byte> data)
    {
        var crc = uint.MaxValue;
        foreach (var value in data) crc = (crc >> 8) ^ CrcTable[(crc ^ value) & 255];
        return ~crc;
    }

    private static uint[] CreateCrcTable()
    {
        var table = new uint[256];
        for (uint index = 0; index < table.Length; index++)
        {
            var value = index;
            for (var bit = 0; bit < 8; bit++) value = (value >> 1) ^ ((value & 1) != 0 ? 0xedb88320u : 0);
            table[index] = value;
        }
        return table;
    }
}
