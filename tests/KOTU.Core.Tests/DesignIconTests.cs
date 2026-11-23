using System.Text.Json;
using KOTU.Core.Appearance;
using Xunit;

namespace KOTU.Core.Tests;

public class DesignIconTests
{
    [Fact]
    public void EverySemanticIconHasFiniteBoundedDrawingsForEveryDesign()
    {
        Assert.True(DesignIconCatalog.Names.Count >= 70);
        foreach (var design in DesignCatalog.All)
        foreach (var name in DesignIconCatalog.Names)
        {
            var drawing = DesignIconCatalog.Create(name,design);
            Assert.True(DesignIconCatalog.TryResolveGlyph(DesignIconCatalog.GlyphFor(name).ToString(),out var resolved));
            Assert.Equal(name,resolved);
            if (design.Decoration == DesignDecoration.Terminal)
            {
                Assert.False(string.IsNullOrEmpty(drawing.Text));
                Assert.All(drawing.Text!, c => Assert.False(char.IsControl(c)));
                Assert.InRange(drawing.TextScale,.3,1.2);
                continue;
            }
            Assert.NotEmpty(drawing.Contours);
            foreach (var contour in drawing.Contours)
            {
                Assert.True(contour.Length >= 3);
                Assert.All(contour,p =>
                {
                    Assert.True(double.IsFinite(p.X) && double.IsFinite(p.Y));
                    Assert.True(p.X >= 0 && p.X <= 24 && p.Y >= 0 && p.Y <= 24,
                        $"Outside viewport: {design.Id}/{name}: ({p.X},{p.Y})");
                });
            }
        }
    }

    [Fact]
    public void AllElevenConstructionsDifferBeyondColorAndEachCallOwnsItsData()
    {
        var signatures = DesignCatalog.All.Select(d => JsonSerializer.Serialize(DesignIconCatalog.Create("Folder",d))).ToArray();
        Assert.Equal(11,signatures.Distinct().Count());
        var first = DesignIconCatalog.Create("Folder",DesignCatalog.Default);
        var second = DesignIconCatalog.Create("Folder",DesignCatalog.Default);
        first.Contours[0][0] = new(99,99);
        Assert.NotEqual(first.Contours[0][0],second.Contours[0][0]);
    }

    [Fact]
    public void PixelContoursHaveOnlyGridAlignedEdgesAndSolidDetailCountersKeepTheirMeaning()
    {
        foreach (var name in DesignIconCatalog.Names)
        foreach (var contour in DesignIconCatalog.Create(name,DesignCatalog.Resolve("pixel")).Contours)
        {
            Assert.Equal(4,contour.Length);
            Assert.All(contour,p => { Assert.Equal(0,p.X%1.5); Assert.Equal(0,p.Y%1.5); });
        }
        foreach (var name in new[] { "Document","NewFolder","ZipFolder" })
        {
            var contours = DesignIconCatalog.Create(name,DesignCatalog.Resolve("solid")).Contours;
            Assert.True(Area(contours[0]) > 0);
            Assert.All(contours.Skip(1),c => Assert.True(Area(c) < 0));
        }
        var folder = DesignIconCatalog.Create("NewFolder",DesignCatalog.Resolve("solid")).Contours;
        foreach (var point in new[] { new DesignIconPoint(12.5,13.5),new(12.5,11),new(12.5,16),new(10,13.5),new(15,13.5) })
            Assert.Equal(0,Winding(folder,point));
        Assert.NotEqual(0,Winding(folder,new(5,12)));
        var document = DesignIconCatalog.Create("Document",DesignCatalog.Resolve("solid")).Contours;
        Assert.Equal(0,Winding(document,new(12,12)));
        Assert.Equal(0,Winding(document,new(12,16)));
        Assert.NotEqual(0,Winding(document,new(12,14)));
        var gear = DesignIconCatalog.Create("Settings",DesignCatalog.Resolve("solid")).Contours;
        Assert.Equal(0,Winding(gear,new(12,12)));
        Assert.NotEqual(0,Winding(gear,new(12,5)));
    }

    [Fact]
    public void PackagedFontsMatchEveryLiveContourAndPreserveGlyphBearings()
    {
        var folder = new DirectoryInfo(AppContext.BaseDirectory);
        while (folder is not null && !File.Exists(Path.Combine(folder.FullName,"Directory.Build.props"))) folder = folder.Parent;
        Assert.NotNull(folder);
        foreach (var design in DesignCatalog.All.Where(d => d.Decoration != DesignDecoration.Terminal))
        {
            var font = File.ReadAllBytes(Path.Combine(folder.FullName,"src","KOTU.App","Assets","Fonts",$"KotuDesign-{design.Id}.ttf"));
            int U16(int at) => (font[at]<<8)|font[at+1];
            int I16(int at) => (short)U16(at);
            int U32(int at) => (U16(at)<<16)|U16(at+2);
            var tables = Enumerable.Range(0,U16(4)).ToDictionary(i => System.Text.Encoding.ASCII.GetString(font,12+i*16,4),i => U32(20+i*16));
            var cmap = tables["cmap"]+20;
            var segments = U16(cmap+6)/2;
            foreach (var name in DesignIconCatalog.Names)
            {
                var code = (int)DesignIconCatalog.GlyphFor(name);
                var segment = Enumerable.Range(0,segments).First(i => U16(cmap+14+i*2) == code);
                Assert.Equal(code,U16(cmap+16+segments*2+segment*2));
                var index = (code+U16(cmap+16+segments*4+segment*2))&0xffff;
                var start = tables["glyf"]+U32(tables["loca"]+index*4);
                var expected = DesignIconCatalog.Create(name,design).Contours;
                Assert.Equal(expected.Length,I16(start));
                var points = U16(start+10+(expected.Length-1)*2)+1;
                var offset = start+12+expected.Length*2;
                Assert.Equal(0,U16(offset-2));
                Assert.All(font.AsSpan(offset,points).ToArray(),flag => Assert.Equal(1,flag));
                offset += points;
                var coordinates = new int[2][];
                for (var axis = 0; axis < 2; axis++)
                {
                    var previous = 0;
                    coordinates[axis] = new int[points];
                    for (var i = 0; i < points; i++,offset+=2) coordinates[axis][i] = previous += I16(offset);
                }
                var flattened = expected.SelectMany(c=>c).ToArray();
                Assert.Equal(flattened.Length,points);
                for (var i = 0; i < points; i++)
                {
                    Assert.Equal((int)Math.Round(flattened[i].X*1000/24),coordinates[0][i]);
                    Assert.Equal((int)Math.Round(850-flattened[i].Y*1000/24),coordinates[1][i]);
                }
                Assert.Equal(coordinates[0].Min(),I16(tables["hmtx"]+index*4+2));
            }
        }
    }

    [Fact]
    public void FontPackExportUsesTheSameLiveAndPreviewContours()
    {
        if (Environment.GetEnvironmentVariable("KOTU_DESIGN_ICON_EXPORT") is not { Length: > 0 } path) return;
        var export = DesignCatalog.All.Where(d => d.Decoration != DesignDecoration.Terminal).Select(d => new
        {
            d.Id, Family = $"KOTU Icons {d.Id}",
            Icons = DesignIconCatalog.Names.Select(n => new
            {
                Name = n, Codepoint = (int)DesignIconCatalog.GlyphFor(n), Drawing = DesignIconCatalog.Create(n,d),
            }),
        });
        File.WriteAllText(path,JsonSerializer.Serialize(export));
    }

    private static double Area(DesignIconPoint[] points) => points.Select((p,i) =>
        p.X*points[(i+1)%points.Length].Y-p.Y*points[(i+1)%points.Length].X).Sum()/2;

    private static int Winding(DesignIconPoint[][] contours, DesignIconPoint point)
    {
        var winding = 0;
        foreach (var contour in contours)
        for (var i = 0; i < contour.Length; i++)
        {
            var a = contour[i]; var b = contour[(i+1)%contour.Length];
            var side = (b.X-a.X)*(point.Y-a.Y)-(point.X-a.X)*(b.Y-a.Y);
            if (a.Y <= point.Y && b.Y > point.Y && side > 0) winding++;
            else if (a.Y > point.Y && b.Y <= point.Y && side < 0) winding--;
        }
        return winding;
    }
}
