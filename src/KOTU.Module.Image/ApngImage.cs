using System.Buffers.Binary;
using Windows.Graphics.Imaging;

namespace KOTU.Module.Image;

/// <summary>Bounded APNG data. Compressed frames are retained; decoded canvases belong to one playback.</summary>
internal sealed class ApngImage
{
    internal sealed record Frame(int Width, int Height, int X, int Y, TimeSpan Delay, byte Dispose, byte Blend,
        IReadOnlyList<ReadOnlyMemory<byte>> Data);

    internal const int MaxPixels = 16_777_216;
    private readonly byte[] _header;
    private readonly IReadOnlyList<ReadOnlyMemory<byte>> _properties;
    internal int Width { get; }
    internal int Height { get; }
    internal int DisplayWidth => Orientation >= 5 ? Height : Width;
    internal int DisplayHeight => Orientation >= 5 ? Width : Height;
    internal ushort Orientation { get; }
    internal uint Plays { get; }
    internal IReadOnlyList<Frame> Frames { get; }

    private ApngImage(byte[] header, IReadOnlyList<ReadOnlyMemory<byte>> properties, int width, int height,
        ushort orientation, uint plays, IReadOnlyList<Frame> frames)
    {
        _header = header; _properties = properties; Width = width; Height = height;
        Orientation = orientation; Plays = plays; Frames = frames;
    }

    internal static ApngImage? Parse(byte[] source)
    {
        if (source.Length < 33 || source[0] != 137 || !source.AsSpan(1, 7).SequenceEqual("PNG\r\n\x1a\n"u8))
            return null;
        var chunks = new List<(int Offset, int Length, string Type)>();
        var animated = false;
        for (var offset = 8; offset <= source.Length - 12;)
        {
            var length = BinaryPrimitives.ReadUInt32BigEndian(source.AsSpan(offset));
            if (length > source.Length - offset - 12) break;
            var type = System.Text.Encoding.ASCII.GetString(source, offset + 4, 4);
            animated |= type == "acTL";
            chunks.Add((offset, (int)length, type));
            if (chunks.Count > 65_536) throw Invalid("Too many PNG chunks.");
            offset += (int)length + 12;
            if (type == "IEND") break;
        }
        if (!animated) return null;
        if (source.Length > 128 * 1024 * 1024 || chunks.Count == 0 || chunks[0].Type != "IHDR" ||
            chunks[0].Length != 13 || chunks[^1].Type != "IEND" || chunks[^1].Length != 0 ||
            chunks[^1].Offset + 12 != source.Length) throw Invalid("Invalid or oversized APNG.");
        var header = source.AsSpan(16, 13).ToArray();
        var width = BinaryPrimitives.ReadInt32BigEndian(header);
        var height = BinaryPrimitives.ReadInt32BigEndian(header.AsSpan(4));
        if (width <= 0 || height <= 0 || (long)width * height > MaxPixels)
            throw Invalid("APNG canvas exceeds the pixel limit.");
        var properties = new List<ReadOnlyMemory<byte>>();
        var frames = new List<Frame>();
        List<ReadOnlyMemory<byte>>? data = null;
        uint expectedFrames = 0, plays = 0, sequence = 0;
        var hasIdat = false;
        var idatEnded = false;
        var firstUsesIdat = false;
        foreach (var (offset, length, type) in chunks)
        {
            var bytes = source.AsSpan(offset + 8, length);
            if (PngExifOrientation.Crc32(source.AsSpan(offset + 4, length + 4)) !=
                BinaryPrimitives.ReadUInt32BigEndian(source.AsSpan(offset + 8 + length)))
                throw Invalid("Invalid APNG checksum.");
            if (hasIdat && type != "IDAT") idatEnded = true;
            switch (type)
            {
                case "IHDR":
                    if (offset != 8) throw Invalid("Duplicate PNG header.");
                    break;
                case "acTL":
                    if (expectedFrames != 0 || hasIdat || length != 8) throw Invalid("Invalid animation control.");
                    expectedFrames = BinaryPrimitives.ReadUInt32BigEndian(bytes);
                    plays = BinaryPrimitives.ReadUInt32BigEndian(bytes[4..]);
                    if (expectedFrames is 0 or > 4096) throw Invalid("Invalid APNG frame count.");
                    break;
                case "fcTL":
                    if (expectedFrames == 0 || length != 26 || frames.Count >= expectedFrames ||
                        (data is not null && data.Count == 0)) throw Invalid("Invalid frame control.");
                    CheckSequence(bytes, ref sequence);
                    var fw = BinaryPrimitives.ReadInt32BigEndian(bytes[4..]);
                    var fh = BinaryPrimitives.ReadInt32BigEndian(bytes[8..]);
                    var x = BinaryPrimitives.ReadInt32BigEndian(bytes[12..]);
                    var y = BinaryPrimitives.ReadInt32BigEndian(bytes[16..]);
                    if (fw <= 0 || fh <= 0 || x < 0 || y < 0 || (long)x + fw > width || (long)y + fh > height ||
                        bytes[24] > 2 || bytes[25] > 1) throw Invalid("Invalid APNG frame bounds or operation.");
                    if (frames.Count == 0)
                    {
                        firstUsesIdat = !hasIdat;
                        if (firstUsesIdat && (fw != width || fh != height || x != 0 || y != 0))
                            throw Invalid("The default animation frame must fill the canvas.");
                    }
                    var numerator = BinaryPrimitives.ReadUInt16BigEndian(bytes[20..]);
                    var denominator = BinaryPrimitives.ReadUInt16BigEndian(bytes[22..]);
                    var delay = TimeSpan.FromSeconds(Math.Max(0.01, (double)numerator / (denominator == 0 ? 100 : denominator)));
                    data = [];
                    frames.Add(new(fw, fh, x, y, delay, bytes[24], bytes[25], data));
                    break;
                case "IDAT":
                    if (idatEnded || expectedFrames == 0 || frames.Count > 1) throw Invalid("Misplaced image data.");
                    hasIdat = true;
                    if (firstUsesIdat && length > 0) data!.Add(source.AsMemory(offset + 8, length));
                    break;
                case "fdAT":
                    if (!hasIdat || data is null || (frames.Count == 1 && firstUsesIdat) || length < 4)
                        throw Invalid("Misplaced animation data.");
                    CheckSequence(bytes, ref sequence);
                    if (length > 4) data.Add(source.AsMemory(offset + 12, length - 4));
                    break;
                case "IEND": break;
                case "eXIf": break; // Orientation is applied to the composed canvas exactly once.
                default:
                    if (!hasIdat && type is "PLTE" or "tRNS" or "iCCP" or "sRGB" or "gAMA" or "cHRM" or
                        "cICP" or "mDCV" or "cLLI" or "sBIT" or "pHYs")
                        properties.Add(source.AsMemory(offset, length + 12));
                    else if ((source[offset + 4] & 32) == 0) throw Invalid("Unsupported critical PNG chunk.");
                    break;
            }
        }
        if (!hasIdat || frames.Count != expectedFrames || data is null || data.Count == 0)
            throw Invalid("Incomplete APNG frames.");
        return new(header, properties, width, height, PngExifOrientation.ReadOrientation(source), plays, frames);
    }

    private static void CheckSequence(ReadOnlySpan<byte> data, ref uint sequence)
    {
        if (BinaryPrimitives.ReadUInt32BigEndian(data) != sequence++) throw Invalid("Invalid APNG sequence.");
    }

    internal byte[] FramePng(int index)
    {
        var frame = Frames[index];
        using var stream = new MemoryStream();
        stream.Write(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 });
        var header = _header.ToArray();
        BinaryPrimitives.WriteInt32BigEndian(header, frame.Width);
        BinaryPrimitives.WriteInt32BigEndian(header.AsSpan(4), frame.Height);
        WriteChunk(stream, "IHDR"u8, header);
        foreach (var property in _properties) stream.Write(property.Span);
        foreach (var data in frame.Data) WriteChunk(stream, "IDAT"u8, data.Span);
        WriteChunk(stream, "IEND"u8, []);
        return stream.ToArray();
    }

    private static void WriteChunk(Stream output, ReadOnlySpan<byte> type, ReadOnlySpan<byte> data)
    {
        var chunk = new byte[data.Length + 12];
        BinaryPrimitives.WriteInt32BigEndian(chunk, data.Length);
        type.CopyTo(chunk.AsSpan(4)); data.CopyTo(chunk.AsSpan(8));
        BinaryPrimitives.WriteUInt32BigEndian(chunk.AsSpan(data.Length + 8), PngExifOrientation.Crc32(chunk.AsSpan(4, data.Length + 4)));
        output.Write(chunk);
    }

    /// <summary>First animation frame for printing, clipboard and previews, retaining 16-bit samples and ICC.</summary>
    internal async Task<byte[]> FirstFrameAsync(CancellationToken cancellation = default)
    {
        cancellation.ThrowIfCancellationRequested();
        var png = FramePng(0);
        var first = Frames[0];
        if (first.Width != Width || first.Height != Height)
        {
            using var input = new MemoryStream(png);
            var decoder = await BitmapDecoder.CreateAsync(input.AsRandomAccessStream()).AsTask(cancellation);
            var wide = _header[8] == 16;
            var format = wide ? BitmapPixelFormat.Rgba16 : BitmapPixelFormat.Bgra8;
            var stride = wide ? 8 : 4;
            var pixels = (await decoder.GetPixelDataAsync(format, BitmapAlphaMode.Straight, new BitmapTransform(),
                ExifOrientationMode.IgnoreExifOrientation, ColorManagementMode.DoNotColorManage).AsTask(cancellation)).DetachPixelData();
            var canvas = new byte[checked(Width * Height * stride)];
            for (var row = 0; row < first.Height; row++)
                pixels.AsSpan(row * first.Width * stride, first.Width * stride).CopyTo(canvas.AsSpan(((row + first.Y) * Width + first.X) * stride));
            using var output = new MemoryStream();
            var encoder = await BitmapEncoder.CreateForTranscodingAsync(output.AsRandomAccessStream(), decoder).AsTask(cancellation);
            encoder.SetPixelData(format, BitmapAlphaMode.Straight, (uint)Width, (uint)Height, decoder.DpiX, decoder.DpiY, canvas);
            await encoder.FlushAsync().AsTask(cancellation);
            png = PngExifOrientation.RestoreColorMetadata(png, output.ToArray());
        }
        cancellation.ThrowIfCancellationRequested();
        return Orientation == 1 ? png : await PngExifOrientation.TransformAsync(png, Orientation, cancellation);
    }

    private static InvalidDataException Invalid(string message) => new(message);
}
