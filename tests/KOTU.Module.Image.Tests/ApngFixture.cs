using System.Buffers.Binary;
using System.IO.Compression;

namespace KOTU.Module.Image.Tests;

internal static class ApngFixture
{
    internal sealed record Frame(int Width, int Height, int X, int Y, byte[] Bgra,
        ushort Numerator = 1, ushort Denominator = 10, byte Dispose = 0, byte Blend = 0);

    internal static byte[] Build(ushort orientation = 6, Frame[]? frames = null, uint plays = 0,
        bool separatePoster = false, bool bigEndian = false, int scale = 1)
    {
        var width = 3 * scale; var height = 2 * scale;
        byte[] Cells(string order)
        {
            var colors = OrientationFixtures.Pixels(order);
            var bytes = new byte[width*height*4];
            for (var y=0;y<height;y++) for (var x=0;x<width;x++)
                colors.AsSpan((y/scale*3+x/scale)*4,4).CopyTo(bytes.AsSpan((y*width+x)*4));
            return bytes;
        }
        frames ??= [new(width,height,0,0,Cells("ABCDEF"),1,2), new(width,height,0,0,Cells("FEDCBA"),3,4)];
        var header = new byte[13];
        BinaryPrimitives.WriteInt32BigEndian(header,width);
        BinaryPrimitives.WriteInt32BigEndian(header.AsSpan(4),height);
        header[8]=8; header[9]=6;
        var result = PngFixture.InsertChunk([137,80,78,71,13,10,26,10],"IHDR",header,8);
        void Append(string name, byte[] bytes) => result = PngFixture.InsertChunk(result,name,bytes,result.Length);
        Append("eXIf",PngFixture.Exif(orientation,bigEndian));
        var actl = new byte[8];
        BinaryPrimitives.WriteInt32BigEndian(actl,frames.Length);
        BinaryPrimitives.WriteUInt32BigEndian(actl.AsSpan(4),plays);
        Append("acTL",actl);
        if (separatePoster) Append("IDAT",Compress(new(width,height,0,0,Cells("CCCCCC"))));
        uint sequence=0;
        for(var index=0;index<frames.Length;index++)
        {
            var frame=frames[index];
            var control=new byte[26];
            BinaryPrimitives.WriteUInt32BigEndian(control,sequence++);
            BinaryPrimitives.WriteInt32BigEndian(control.AsSpan(4),frame.Width);
            BinaryPrimitives.WriteInt32BigEndian(control.AsSpan(8),frame.Height);
            BinaryPrimitives.WriteInt32BigEndian(control.AsSpan(12),frame.X);
            BinaryPrimitives.WriteInt32BigEndian(control.AsSpan(16),frame.Y);
            BinaryPrimitives.WriteUInt16BigEndian(control.AsSpan(20),frame.Numerator);
            BinaryPrimitives.WriteUInt16BigEndian(control.AsSpan(22),frame.Denominator);
            control[24]=frame.Dispose; control[25]=frame.Blend;
            Append("fcTL",control);
            var compressed=Compress(frame);
            if(index==0 && !separatePoster) Append("IDAT",compressed);
            else
            {
                var data=new byte[compressed.Length+4];
                BinaryPrimitives.WriteUInt32BigEndian(data,sequence++);
                compressed.CopyTo(data,4); Append("fdAT",data);
            }
        }
        Append("IEND",[]);
        return result;
    }

    private static byte[] Compress(Frame frame)
    {
        using var buffer=new MemoryStream();
        using(var zlib=new ZLibStream(buffer,CompressionLevel.Optimal,true))
            for(var y=0;y<frame.Height;y++)
            {
                zlib.WriteByte(0);
                for(var x=0;x<frame.Width;x++)
                {
                    var i=(y*frame.Width+x)*4;
                    zlib.WriteByte(frame.Bgra[i+2]); zlib.WriteByte(frame.Bgra[i+1]);
                    zlib.WriteByte(frame.Bgra[i]); zlib.WriteByte(frame.Bgra[i+3]);
                }
            }
        return buffer.ToArray();
    }
}
