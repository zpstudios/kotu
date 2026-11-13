using System.Buffers.Binary;
using ImageMagick;
using Windows.Graphics.Imaging;
using Xunit;

namespace KOTU.Module.Image.Tests;

public sealed class ApngTests
{
    [Theory]
    [MemberData(nameof(PngOrientationTests.Cases), MemberType = typeof(PngOrientationTests))]
    public async Task AnimatedSourceFirstFrameCopyAndManualRotationAgree(ushort orientation, int rotation,
        bool bigEndian, uint width, uint height, string order)
    {
        var source = ApngFixture.Build(orientation, bigEndian: bigEndian);
        var original = source.ToArray();
        var payload = await ImageClipboardSnapshot.CreateAsync(source, rotation);
        foreach (var bytes in new[] { payload.Png, OrientationFixtures.BmpFromDib(payload.DibV5, true), OrientationFixtures.BmpFromDib(payload.Dib, false) })
        {
            var decoded = await OrientationFixtures.DecodeAsync(bytes);
            Assert.Equal(width, decoded.Width); Assert.Equal(height, decoded.Height);
            Assert.Equal(OrientationFixtures.Pixels(order), decoded.Pixels);
        }
        Assert.Equal(original, source);
    }

    [Theory]
    [InlineData(1, "ABCDEF", "FEDCBA")]
    [InlineData(2, "CBAFED", "DEFABC")]
    [InlineData(3, "FEDCBA", "ABCDEF")]
    [InlineData(4, "DEFABC", "CBAFED")]
    [InlineData(5, "ADBECF", "FCEBDA")]
    [InlineData(6, "DAEBFC", "CFBEAD")]
    [InlineData(7, "FCEBDA", "ADBECF")]
    [InlineData(8, "CFBEAD", "DAEBFC")]
    public async Task EveryAnimatedFrameAndPreviewUseOneOrientation(int orientation, string first, string second)
    {
        foreach (var endian in new[] { false, true })
        {
            var source = ApngFixture.Build((ushort)orientation, bigEndian: endian, separatePoster: true);
            var image = ApngImage.Parse(source)!;
            Assert.Equal(orientation >= 5 ? 2 : 3, image.DisplayWidth);
            Assert.Equal(orientation >= 5 ? 3 : 2, image.DisplayHeight);
            Assert.Equal(TimeSpan.FromMilliseconds(500), image.Frames[0].Delay);
            Assert.Equal(TimeSpan.FromMilliseconds(750), image.Frames[1].Delay);
            var compositor = new ApngCompositor(image);
            Assert.Equal(OrientationFixtures.Pixels(first), await compositor.RenderAsync(0));
            Assert.Equal(OrientationFixtures.Pixels(second), await compositor.RenderAsync(1));
            Assert.Equal(OrientationFixtures.Pixels(first), await compositor.RenderAsync(0));
            var png = await image.FirstFrameAsync();
            Assert.Equal(OrientationFixtures.Pixels(first), (await OrientationFixtures.DecodeAsync(png)).Pixels);
            var preview = await ImagePreviewDecoder.DecodeAsync(source, 96, 96);
            Assert.Equal(OrientationFixtures.Pixels(first), preview.Bgra);
        }
    }

    [Theory]
    [InlineData(0, "AACDEF")]
    [InlineData(1, "A.CDEF")]
    [InlineData(2, "ABCDEF")]
    public async Task PartialFramesApplyDisposalBeforeNextFrame(int disposal, string final)
    {
        var frames = new ApngFixture.Frame[]
        {
            new(3,2,0,0,OrientationFixtures.Pixels("ABCDEF")),
            new(1,1,1,0,OrientationFixtures.Pixels("A"),Dispose:(byte)disposal),
            new(1,1,0,1,[0,0,0,0],Blend:1),
        };
        var image = ApngImage.Parse(ApngFixture.Build(1,frames))!;
        var compositor = new ApngCompositor(image);
        await compositor.RenderAsync(0);
        Assert.Equal(OrientationFixtures.Pixels("AACDEF"),await compositor.RenderAsync(1));
        var expected = new List<byte>();
        foreach (var cell in final) expected.AddRange(cell == '.' ? new byte[4] : OrientationFixtures.Pixels(cell.ToString()));
        Assert.Equal(expected,await compositor.RenderAsync(2));
    }

    [Theory]
    [InlineData(0, 0, 128, 128)]
    [InlineData(1, 127, 128, 255)]
    public async Task AlphaBlendUsesPremultipliedSourceOverOrReplacement(int blend, int green, int red, int alpha)
    {
        ApngFixture.Frame[] frames = [new(3,2,0,0,OrientationFixtures.Pixels("BBBBBB")),
            new(1,1,1,0,[0,0,255,128],Blend:(byte)blend)];
        var compositor = new ApngCompositor(ApngImage.Parse(ApngFixture.Build(1,frames))!);
        await compositor.RenderAsync(0);
        var result = await compositor.RenderAsync(1);
        Assert.Equal(new byte[] {0,(byte)green,(byte)red,(byte)alpha},result.AsSpan(4,4).ToArray());
        Assert.Equal(OrientationFixtures.Pixels("B"),result.AsSpan(0,4).ToArray());
    }

    [Fact]
    public async Task SeparatePosterDoesNotLeakIntoTransparentFirstFrameAndLoopReset()
    {
        ApngFixture.Frame[] frames = [new(1,1,1,0,OrientationFixtures.Pixels("A"),Dispose:2),
            new(1,1,2,1,OrientationFixtures.Pixels("B"),Blend:1)];
        var image = ApngImage.Parse(ApngFixture.Build(6,frames,separatePoster:true))!;
        var first = new byte[24]; OrientationFixtures.Pixels("A").CopyTo(first,12);
        var second = new byte[24]; OrientationFixtures.Pixels("B").CopyTo(second,16);
        var compositor = new ApngCompositor(image);
        Assert.Equal(first,await compositor.RenderAsync(0));
        Assert.Equal(first,(await OrientationFixtures.DecodeAsync(await image.FirstFrameAsync())).Pixels);
        Assert.Equal(second,await compositor.RenderAsync(1));
        Assert.Equal(first,await compositor.RenderAsync(0));
    }

    [Fact]
    public async Task FinitePlaybackHonorsDelaysAndCountsWithoutAddingDecodeOrUiTime()
    {
        var image = ApngImage.Parse(ApngFixture.Build(1,plays:2))!;
        var now = TimeSpan.Zero;
        var delays = new List<TimeSpan>(); var frames = new List<byte[]>();
        await ApngPlayback.RunAsync(image,(pixels,_) =>
        {
            frames.Add(pixels); now += TimeSpan.FromMilliseconds(120); return Task.CompletedTask;
        },default,(duration,_) => { delays.Add(duration); now += duration; return Task.CompletedTask; },()=>now);
        Assert.Equal(4,frames.Count);
        Assert.Equal(new[] {380,630,380,630},delays.Select(t=>(int)t.TotalMilliseconds));
        Assert.Equal(frames[0],frames[2]); Assert.Equal(frames[1],frames[3]);
    }

    [Fact]
    public async Task InfinitePlaybackCancellationPreventsAnyLaterFrame()
    {
        using var cancellation = new CancellationTokenSource();
        var count = 0;
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => ApngPlayback.RunAsync(ApngImage.Parse(ApngFixture.Build())!,
            (_,_) => { if (++count == 3) cancellation.Cancel(); return Task.CompletedTask; },cancellation.Token,
            (_,token) => { token.ThrowIfCancellationRequested(); return Task.CompletedTask; }));
        Assert.Equal(3,count);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new ApngCompositor(ApngImage.Parse(ApngFixture.Build())!)
            .RenderAsync(0,new CancellationToken(true)));
    }

    [Theory]
    [InlineData(0, 0, 10)]
    [InlineData(5, 0, 50)]
    [InlineData(1, 4, 250)]
    public void ZeroDenominatorAndMinimumDelayFollowPngRules(int numerator,int denominator,int milliseconds)
    {
        ApngFixture.Frame[] frames = [new(3,2,0,0,OrientationFixtures.Pixels("ABCDEF"),(ushort)numerator,(ushort)denominator)];
        Assert.Equal(TimeSpan.FromMilliseconds(milliseconds),ApngImage.Parse(ApngFixture.Build(1,frames))!.Frames[0].Delay);
    }

    [Theory]
    [InlineData("sequence")]
    [InlineData("bounds")]
    [InlineData("frame-count")]
    [InlineData("dispose")]
    [InlineData("blend")]
    [InlineData("crc")]
    [InlineData("truncated")]
    [InlineData("canvas-limit")]
    public void MalformedAnimationFailsBeforeAllocatingOrPlaying(string fault)
    {
        var source = ApngFixture.Build();
        if(fault == "truncated") source = source[..^1];
        else
        {
            var type = fault switch { "frame-count"=>"acTL", "canvas-limit"=>"IHDR", _=>"fcTL" };
            source = Rewrite(source,type,data =>
            {
                switch(fault)
                {
                    case "sequence": data[3]=3; break;
                    case "bounds": data[15]=3; break;
                    case "frame-count": data[3]=3; break;
                    case "dispose": data[24]=3; break;
                    case "blend": data[25]=2; break;
                    case "canvas-limit": BinaryPrimitives.WriteInt32BigEndian(data, int.MaxValue); break;
                    case "crc": data[3]=4; break;
                }
            });
            if(fault == "crc") source[41] ^= 1;
        }
        Assert.Throws<InvalidDataException>(()=>ApngImage.Parse(source));
    }

    [Fact]
    public async Task StaticPngAndGifAreNotAnimationCandidates()
    {
        Assert.Null(ApngImage.Parse(await PngFixture.CreateAsync(6)));
        Assert.Null(ApngImage.Parse("GIF89a"u8.ToArray()));
        Assert.Contains(".apng",ImageFolderNavigator.SupportedExtensions);
    }

    [Fact]
    public async Task FirstFramePreservesSixteenBitSamplesResolutionAndColorProfile()
    {
        ushort[][] values = [[0x1234,0x4567,0x89ab,65535],[0x2345,0x5678,0x9abc,0x7fff],
            [0x3456,0x6789,0xabcd,65535],[0x4567,0x789a,0xbcde,65535],
            [0x5678,0x89ab,0xcdef,65535],[0x6789,0x9abc,0xdef0,0x3fff]];
        var source = PngFixture.InsertChunk(PngFixture.SixteenBit(values,false),"eXIf",PngFixture.Exif(6,false));
        var expected = await PngExifOrientation.NormalizeAsync(source);
        var actual = await ApngImage.Parse(AnimatePng(source))!.FirstFrameAsync();
        Assert.Equal(16,actual[24]);
        using var a = new MemoryStream(actual); using var b = new MemoryStream(expected);
        var ad = await BitmapDecoder.CreateAsync(a.AsRandomAccessStream());
        var bd = await BitmapDecoder.CreateAsync(b.AsRandomAccessStream());
        var ap = (await ad.GetPixelDataAsync(BitmapPixelFormat.Rgba16,BitmapAlphaMode.Straight,new(),ExifOrientationMode.IgnoreExifOrientation,ColorManagementMode.DoNotColorManage)).DetachPixelData();
        var bp = (await bd.GetPixelDataAsync(BitmapPixelFormat.Rgba16,BitmapAlphaMode.Straight,new(),ExifOrientationMode.IgnoreExifOrientation,ColorManagementMode.DoNotColorManage)).DetachPixelData();
        Assert.Equal(bp,ap); Assert.Equal(bd.DpiX,ad.DpiX); Assert.Equal(bd.DpiY,ad.DpiY);
        using var profiled = new MagickImage(await PngFixture.CreateAsync(null,alpha:true));
        profiled.SetProfile(ColorProfiles.AdobeRGB1998);
        var tagged = PngFixture.InsertChunk(profiled.ToByteArray(MagickFormat.Png),"eXIf",PngFixture.Exif(7,false));
        var animation = ApngImage.Parse(AnimatePng(tagged))!;
        using var raw = new MagickImage(animation.FramePng(0));
        Assert.NotNull(raw.GetColorProfile());
        using var result = new MagickImage(await animation.FirstFrameAsync());
        Assert.NotNull(result.GetColorProfile());
        Assert.Equal(ColorProfiles.AdobeRGB1998.ToByteArray(),result.GetColorProfile()!.ToByteArray());
        var preview = await ImagePreviewDecoder.DecodeAsync(await animation.FirstFrameAsync(),96,96);
        var animated = await new ApngCompositor(animation).RenderAsync(0);
        Assert.Equal(preview.Bgra,animated);
    }

    private static byte[] AnimatePng(byte[] source)
    {
        var control = new byte[26];
        source.AsSpan(16,8).CopyTo(control.AsSpan(4)); control[21]=1; control[23]=10;
        var offset = 8;
        while(!source.AsSpan(offset+4,4).SequenceEqual("IDAT"u8)) offset+=BinaryPrimitives.ReadInt32BigEndian(source.AsSpan(offset))+12;
        source = PngFixture.InsertChunk(source,"fcTL",control,offset);
        return PngFixture.InsertChunk(source,"acTL",[0,0,0,1,0,0,0,1],33);
    }

    private static byte[] Rewrite(byte[] source,string type,Action<byte[]> change)
    {
        for(var offset=8;offset<source.Length;)
        {
            var length=BinaryPrimitives.ReadInt32BigEndian(source.AsSpan(offset));
            if(System.Text.Encoding.ASCII.GetString(source,offset+4,4)==type)
            {
                var bytes=source.AsSpan(offset+8,length).ToArray(); change(bytes);
                return PngFixture.InsertChunk([..source.AsSpan(0,offset),..source.AsSpan(offset+length+12)],type,bytes,offset);
            }
            offset+=length+12;
        }
        throw new InvalidOperationException(type);
    }
}
