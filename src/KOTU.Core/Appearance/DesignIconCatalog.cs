using System.Globalization;

namespace KOTU.Core.Appearance;

/// <summary>Original semantic drawings shared by live icons and explicit-profile previews.</summary>
public static class DesignIconCatalog
{
    private sealed record Symbol(char Glyph, string Name, IconFigure[] Figures);
    private static readonly Dictionary<string, Symbol> Symbols = ReadSymbols();
    private static readonly Dictionary<char, string> Glyphs = Symbols.Values.ToDictionary(s => s.Glyph, s => s.Name);
    public static IReadOnlyCollection<string> Names => Symbols.Keys;
    public static char GlyphFor(string symbol) => Symbols[symbol].Glyph;
    public static string FontId(DesignDefinition design) => design.Decoration == DesignDecoration.Original && design.ThinControls
        ? "original-thin" : design.Id;
    public static string FontFamily(DesignDefinition design) => design.Decoration == DesignDecoration.Terminal
        ? design.FontFamily
        : $"ms-appx:///Assets/Fonts/KotuDesign-{FontId(design)}.ttf#KOTU Icons {FontId(design)}";

    public static bool TryResolveGlyph(string? glyph, out string symbol)
    {
        symbol = string.Empty;
        return glyph is { Length: 1 } && Glyphs.TryGetValue(glyph[0], out symbol!);
    }

    public static DesignIconDrawing Create(string symbol, DesignDefinition design)
    {
        ArgumentNullException.ThrowIfNull(design);
        if (!Symbols.TryGetValue(symbol, out var source)) throw new ArgumentException("Unknown icon symbol.", nameof(symbol));
        var kind = design.Decoration;
        if (kind == DesignDecoration.Terminal)
        {
            var text = TerminalText(source.Name);
            return new(text, Math.Min(1.15, 1.55 / text.Length), []);
        }

        var figures = source.Figures;
        if (kind == DesignDecoration.Paper && source.Name == "Folder")
            figures = IconCenterlineParser.Parse("M3 19 V5 H9 L12 8 H19 V10 M3 19 L6.7 11 H22 L18.5 19 Z M19 10 L16 13 H21");
        var contours = new List<DesignIconPoint[]>();
        var radius = design.IconStrokeWidth / 2;
        var rounded = kind == DesignDecoration.Rounded || kind == DesignDecoration.Original && !design.ThinControls;
        var fill = kind == DesignDecoration.Solid && source.Name is
            "Folder" or "File" or "Document" or "Play" or "PreviousTrack" or "NextTrack" or "Video" or "Stop" or "StopLegacy" or "Filter" or "NewFolder" or "ZipFolder" or "Speaker" or "Muted";
        foreach (var figure in figures)
        {
            var points = figure.Points;
            if (kind == DesignDecoration.Ink)
                points = points.Select((p,i) => new DesignIconPoint(
                    p.X + .20*Math.Sin(i*1.9+p.Y), p.Y + .20*Math.Cos(i*1.7+p.X))).ToArray();
            if (fill && figure.Closed)
            {
                AddContour(contours, points);
                // Keep inside detail strokes visible as counters in filled files/folders.
                fill = false;
                continue;
            }
            if (kind == DesignDecoration.Pixel) RasterStroke(contours, points, figure.Closed);
            else Stroke(contours, points, figure.Closed, radius, rounded, kind == DesignDecoration.Original && design.ThinControls);
        }

        if (kind == DesignDecoration.Solid && source.Name is "Document" or "NewFolder" or "ZipFolder")
        {
            // Each counter is one non-overlapping contour, including the joined plus sign.
            contours.RemoveRange(1, contours.Count-1);
            if (source.Name == "Document")
            {
                Hole([new(8,11),new(16,11),new(16,13),new(8,13)]);
                Hole([new(8,15),new(16,15),new(16,17),new(8,17)]);
            }
            else if (source.Name == "NewFolder")
                Hole([new(11.5,10),new(13.5,10),new(13.5,12.5),new(16,12.5),new(16,14.5),new(13.5,14.5),
                    new(13.5,17),new(11.5,17),new(11.5,14.5),new(9,14.5),new(9,12.5),new(11.5,12.5)]);
            else for (var y = 9; y < 18; y += 3)
                Hole([new(14,y),new(16,y),new(16,y+2),new(14,y+2)]);
            void Hole(DesignIconPoint[] points) { AddContour(contours,points); Array.Reverse(contours[^1]); }
        }
        if (kind == DesignDecoration.Solid && source.Name is "AllFormats" or "Preview" or "Settings" or "Home" or "Print")
        {
            contours.Clear();
            if (source.Name is "AllFormats" or "Preview")
                foreach (var figure in figures) AddContour(contours,figure.Points);
            else if (source.Name == "Settings")
            {
                AddContour(contours,figures[0].Points);
                AddContour(contours,figures[1].Points);
                Array.Reverse(contours[^1]);
            }
            else if (source.Name == "Home")
                AddContour(contours,[new(2.5,11),new(12,2.8),new(21.5,11),new(19,11),new(19,21),new(14,21),
                    new(14,15),new(10,15),new(10,21),new(5,21),new(5,11)]);
            else
            {
                AddContour(contours,[new(6.5,2.5),new(17.5,2.5),new(17.5,8.5),new(21.5,8.5),new(21.5,17.5),
                    new(17.5,17.5),new(17.5,21.5),new(6.5,21.5),new(6.5,17.5),new(2.5,17.5),new(2.5,8.5),new(6.5,8.5)]);
                AddContour(contours,[new(8,15),new(16,15),new(16,20),new(8,20)]); Array.Reverse(contours[^1]);
                AddContour(contours,[new(17.5,10.5),new(19.5,10.5),new(19.5,12.5),new(17.5,12.5)]); Array.Reverse(contours[^1]);
            }
        }
        if (kind == DesignDecoration.Editorial)
            Transform(contours, p => new(12+(p.X-12)*.88, 12+(p.Y-12)*.88));
        if (kind == DesignDecoration.Mechanical)
        {
            // An offset engraved edge, kept inside the icon's own viewport.
            var edge = contours.Select(c => c.Select(p => new DesignIconPoint(p.X+.45,p.Y+.55)).ToArray()).ToArray();
            contours.AddRange(edge);
            Transform(contours, p => new(12+(p.X-12)*.94,12+(p.Y-12)*.94));
        }
        if (kind == DesignDecoration.Paper && source.Name is "File" or "Document")
        {
            Stroke(contours, [new(14,3),new(14,8),new(19,8)], false, .6, false);
            Stroke(contours, [new(7,22),new(21,22),new(21,9)], false, .5, false);
        }
        if (kind == DesignDecoration.Isometric && source.Name is not ("Check" or "Mixed" or "Down" or "Up" or "Next" or "Previous"))
        {
            Transform(contours, p => new(10.5+(p.X-12)*.58,14.5+(p.Y-12)*.58));
            Stroke(contours, [new(3,7),new(18,7),new(18,22),new(3,22)], true, .6, false);
            Stroke(contours, [new(3,7),new(8,2),new(23,2),new(18,7)], false, .6, false);
            Stroke(contours, [new(18,22),new(23,17),new(23,2)], false, .6, false);
            AddContour(contours, [new(19,8),new(22,5),new(22,16.5),new(19,19.5)]);
        }
        return new(null, 1, contours.ToArray());
    }

    private static Dictionary<string, Symbol> ReadSymbols()
    {
        using var stream = typeof(DesignIconCatalog).Assembly.GetManifestResourceStream("KOTU.Core.Appearance.IconCenterlines.txt")
            ?? throw new InvalidOperationException("Missing original icon centerlines.");
        using var reader = new StreamReader(stream);
        var symbols = new Dictionary<string, Symbol>(StringComparer.OrdinalIgnoreCase);
        while (reader.ReadLine() is { } line)
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            var pieces = line.Split(' ',3);
            var glyph = (char)int.Parse(pieces[0], NumberStyles.HexNumber, CultureInfo.InvariantCulture);
            symbols.Add(pieces[1], new(glyph,pieces[1],IconCenterlineParser.Parse(pieces[2])));
        }
        return symbols;
    }

    private static string TerminalText(string symbol) => symbol switch
    {
        "Folder" => "[D]", "File" => "[F]", "Document" => "[T]", "ZipFolder" => "[Z]",
        "Image" => "[I]", "Video" => "[V]", "Play" => ">", "Pause" => "II", "Stop" or "StopLegacy" => "[]",
        "PreviousTrack" => "|<", "NextTrack" => ">|", "Previous" or "Back" => "<", "Next" => ">",
        "Up" or "Parent" => "^", "Down" => "v", "Menu" or "List" => "=", "More" => "...", "Home" => "~",
        "AllFormats" or "Preview" => "#", "Filter" => "Y", "Speaker" => "))", "Muted" => "x))",
        "Music" => "♪", "Equalizer" => "EQ", "Analyzer" => "WAV", "RepeatOff" => "-/", "RepeatOne" => "R1",
        "RepeatList" => "RP", "OriginalRatio" => "1:1", "Fit" => "[]", "FitWidth" => "<>", "FitHeight" => "^v",
        "Fullscreen" => "[+]", "ExitFullscreen" => "[-]", "CollapseSidebars" => "|=|", "Print" => "P",
        "Settings" => "*", "Check" => "x", "Mixed" => "-", "Close" => "x", "Search" => "/",
        "Edit" => "E", "View" => "V", "Save" => "S", "Delete" => "DEL", "Rename" => "Aa",
        "Cut" => "X", "Copy" => "CP", "Paste" => "PS", "NewFolder" => "+D", "NewWindow" => "+W",
        "Refresh" => "R", "Sync" => "<>R", "Download" => "v_", "ExtractTo" => "Z>", "Open" => "D>",
        "Cloud" => "CLD", "Record" => "REC", "Microphone" => "MIC", "Camera" => "CAM", "Screen" => "MON",
        "AudioDevices" => "AUD", "Captions" => "CC", "Info" => "i", "Chip" => "CPU", "Guide" => "?|",
        "Pin" => "!", "Rotate" => "R>", "Windows" => "[W]", _ => throw new ArgumentException("Missing terminal symbol.", nameof(symbol)),
    };

    private static void Transform(List<DesignIconPoint[]> contours, Func<DesignIconPoint,DesignIconPoint> convert)
    {
        for (var i = 0; i < contours.Count; i++) contours[i] = contours[i].Select(convert).ToArray();
    }

    private static void Stroke(List<DesignIconPoint[]> contours, DesignIconPoint[] points, bool closed, double radius, bool rounded, bool flatCaps = false)
    {
        var count = closed ? points.Length : points.Length-1;
        for (var i = 0; i < count; i++)
        {
            var a = points[i]; var b = points[(i+1)%points.Length];
            var length = Math.Sqrt((b.X-a.X)*(b.X-a.X)+(b.Y-a.Y)*(b.Y-a.Y));
            if (length < .00001) continue;
            var x = (b.X-a.X)*radius/length; var y = (b.Y-a.Y)*radius/length;
            AddContour(contours, [new(a.X-y,a.Y+x),new(a.X+y,a.Y-x),new(b.X+y,b.Y-x),new(b.X-y,b.Y+x)]);
        }
        // Square joins are crisp, round joins/caps are circular. Curves remain smoothly sampled.
        foreach (var p in flatCaps && !closed ? points.Skip(1).SkipLast(1) : points)
        {
            if (rounded)
                AddContour(contours, Enumerable.Range(0,12).Select(i => new DesignIconPoint(
                    p.X+radius*Math.Cos(i*Math.PI/6),p.Y+radius*Math.Sin(i*Math.PI/6))).ToArray());
            else AddContour(contours, [new(p.X-radius,p.Y-radius),new(p.X+radius,p.Y-radius),new(p.X+radius,p.Y+radius),new(p.X-radius,p.Y+radius)]);
        }
    }

    private static void RasterStroke(List<DesignIconPoint[]> contours, DesignIconPoint[] points, bool closed)
    {
        const double cell = 1.5;
        var occupied = new HashSet<(int,int)>();
        for (var i = 0; i < (closed ? points.Length : points.Length-1); i++)
        {
            var a = points[i]; var b = points[(i+1)%points.Length];
            var steps = Math.Max(1,(int)Math.Ceiling(Math.Max(Math.Abs(b.X-a.X),Math.Abs(b.Y-a.Y))*2));
            for (var step = 0; step <= steps; step++)
                occupied.Add(((int)Math.Floor((a.X+(b.X-a.X)*step/steps)/cell),(int)Math.Floor((a.Y+(b.Y-a.Y)*step/steps)/cell)));
        }
        foreach (var (x,y) in occupied)
            AddContour(contours, [new(x*cell,y*cell),new((x+1)*cell,y*cell),new((x+1)*cell,(y+1)*cell),new(x*cell,(y+1)*cell)]);
    }

    private static void AddContour(List<DesignIconPoint[]> contours, DesignIconPoint[] points)
    {
        var copy = (DesignIconPoint[])points.Clone();
        var area = copy.Select((p,i) => p.X*copy[(i+1)%copy.Length].Y-p.Y*copy[(i+1)%copy.Length].X).Sum();
        if (Math.Abs(area) < .00001) return;
        if (area < 0) Array.Reverse(copy);
        contours.Add(copy);
    }
}
