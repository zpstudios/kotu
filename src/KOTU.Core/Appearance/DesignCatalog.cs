namespace KOTU.Core.Appearance;

/// <summary>The single source of appearance tokens for live controls and all twelve previews.</summary>
public static class DesignCatalog
{
    public const string DefaultId = "original";
    private const string Sans = "Segoe UI";
    private const string Nunito = "ms-appx:///Assets/Fonts/NunitoSans-Regular.ttf#Nunito Sans, Malgun Gothic, Segoe UI";
    private const string NunitoHeading = "ms-appx:///Assets/Fonts/NunitoSans-SemiBold.ttf#Nunito Sans, Malgun Gothic, Segoe UI";
    private const string Barlow = "ms-appx:///Assets/Fonts/BarlowSemiCondensed-Regular.ttf#Barlow Semi Condensed, Malgun Gothic, Segoe UI";
    private const string BarlowHeading = "ms-appx:///Assets/Fonts/BarlowSemiCondensed-SemiBold.ttf#Barlow Semi Condensed, Malgun Gothic, Segoe UI";
    private const string Coding = "ms-appx:///Assets/Fonts/NanumGothicCoding-Regular.ttf#NanumGothicCoding, Consolas, Malgun Gothic, Segoe UI";
    private const string CodingHeading = "ms-appx:///Assets/Fonts/NanumGothicCoding-Bold.ttf#NanumGothicCoding, Consolas, Malgun Gothic, Segoe UI";
    private const string Pixel = "ms-appx:///Assets/Fonts/Galmuri11.ttf#Galmuri11, Consolas, Malgun Gothic, Segoe UI";
    private const string PixelHeading = "ms-appx:///Assets/Fonts/Galmuri11-Bold.ttf#Galmuri11, Consolas, Malgun Gothic, Segoe UI";
    private const string Ink = "ms-appx:///Assets/Fonts/Gaegu-Regular.ttf#Gaegu, Malgun Gothic, Segoe Print, Segoe UI";
    private const string InkHeading = "ms-appx:///Assets/Fonts/Gaegu-Bold.ttf#Gaegu, Malgun Gothic, Segoe Print, Segoe UI";

    private static readonly DesignPalette PrecisionLight = P(0xf9fafb, 0xf0f2f5, 0xffffff, 0x242c37, 0x626d7b, 0xd0d7df, 0x28637a, 0xe1edf2, 0xffffff);
    private static readonly DesignPalette PrecisionDark = P(0x1b2028, 0x242a34, 0x272f3b, 0xedf1f7, 0xb3becd, 0x4b5665, 0x86cbe0, 0x304954, 0x182933);

    public static IReadOnlyList<DesignDefinition> All { get; } = Array.AsReadOnly(new[]
    {
        new DesignDefinition("original", "Original", "The original KOTU lettering, geometric symbols and Windows surfaces.",
            DesignDecoration.Original, "ms-appx:///Assets/KotuFit.ttf#KOTU Fit, Segoe UI", "ms-appx:///Assets/KotuFit.ttf#KOTU Fit, Segoe UI",
            14, 400, 4, 0, 0, 1, 1.4, 0,
            // Opaque fallbacks composited from WinUI 1.8 generic.xaml's native color tokens.
            // The app resolves those original tokens and the current system accent at runtime.
            P(0xf3f3f3,0xf9f9f9,0xfbfbfb,0x1a1a1a,0x5c5c5c,0xe5e5e5,0x005fb8,0xeaeaea,0xffffff),
            P(0x202020,0x282828,0x2d2d2d,0xffffff,0xcccccc,0x303030,0x60cdff,0x2d2d2d,0x000000)),
        new DesignDefinition("precision", "Precision line", "Fine geometric lines, restrained frames and clear spacing.",
            DesignDecoration.Precision, Sans, Sans, 14, 400, 4, 4, 2, 1, 1.6, 0, PrecisionLight, PrecisionDark),
        new DesignDefinition("humanist", "Rounded humanist", "Round-ended icons, soft panels and generous curved controls.",
            DesignDecoration.Rounded, Nunito, NunitoHeading, 14, 400, 16, 20, 6, 1, 2, 0,
            P(0xfcfaf6,0xf1f1e8,0xffffff,0x30382d,0x626c5d,0xd5dccb,0x497344,0xe1edda,0xffffff),
            P(0x242622,0x30352d,0x393e34,0xf0f3e9,0xbec8b4,0x55614c,0xaad399,0x3c5234,0x20301b)),
        new DesignDefinition("solid", "Solid pictograms", "Filled symbols, compact typography and strong inverted bars.",
            DesignDecoration.Solid, BarlowHeading, BarlowHeading, 15, 600, 2, 2, 2, 1, 2.5, 0,
            PrecisionLight with { Accent=0xff275bc0, Selection=0xffe1e9fc },
            PrecisionDark with { Accent=0xffa0bafb, Selection=0xff2d3d62, OnAccent=0xff152952 }),
        new DesignDefinition("editorial", "Editorial typography", "Serif headings, hairline rules and quiet typographic hierarchy.",
            DesignDecoration.Editorial, Sans, "Georgia, ms-appx:///Assets/Fonts/NanumMyeongjo-Bold.ttf#NanumMyeongjo, Malgun Gothic, Segoe UI", 14, 400, 0, 0, 0, 1, 1.25, 0,
            P(0xfffdf8,0xf8f5ed,0xfffdf8,0x262b29,0x696c64,0xcfcec1,0x85513b,0xf2e7dd,0xffffff),
            P(0x242320,0x2c2b26,0x302f29,0xefeee5,0xbebfb1,0x57584c,0xe1b19a,0x49392f,0x2a1e15)),
        new DesignDefinition("terminal", "Terminal console", "Monospaced labels, bracket controls and textual symbols.",
            DesignDecoration.Terminal, Coding, CodingHeading, 14, 400, 0, 0, 0, 1, 1, 0,
            P(0xf2f6f3,0xe5eee7,0xf2f6f3,0x224c35,0x4e705a,0xa2b8a8,0x1b6134,0xd1e4d4,0xffffff),
            P(0x101b16,0x17271d,0x1b2d22,0xbde5bb,0x9bb79d,0x3b5d45,0xa6e49d,0x2c4932,0x142b16)),
        new DesignDefinition("mechanical", "Mechanical controls", "Recessed panels, beveled controls and instrument-like labels.",
            DesignDecoration.Mechanical, Barlow, BarlowHeading, 15, 400, 5, 4, 2, 1, 1.8, 2,
            P(0xe6e5df,0xd6d6ce,0xf0f0e9,0x333e40,0x56615d,0xa8ada4,0x416752,0xcbd9cd,0xffffff),
            P(0x252a2c,0x343c3e,0x455053,0xe7efea,0xb6c7bd,0x64736c,0xa7cfb4,0x2c4338,0x163325)),
        new DesignDefinition("frame", "Bold frames", "Heavy square outlines, bold labels and crisp offset edges.",
            DesignDecoration.Frame, "Arial, Segoe UI", "Arial, Segoe UI", 14, 600, 0, 0, 0, 2, 2.8, 2,
            P(0xfffef6,0xf0eee2,0xfffef6,0x22241f,0x5f6357,0x22241f,0x515d17,0xe5ebbd,0xffffff),
            P(0x20201e,0x30302a,0x3b3b33,0xf2f3dd,0xbfc4ac,0xf2f3dd,0xd6e393,0x454e25,0x242d0b)),
        new DesignDefinition("paper", "Folded paper", "Folded corners, paper layers and warm ruled surfaces.",
            DesignDecoration.Paper, "Trebuchet MS, Segoe UI", "Trebuchet MS, Segoe UI", 14, 400, 0, 0, 0, 1, 1.65, 3,
            P(0xeee9df,0xe1d9c9,0xfffdf6,0x494237,0x756b5c,0xc1b7a5,0x946139,0xf2dec3,0xffffff),
            P(0x292822,0x3b372e,0x484236,0xeee5d5,0xc4b9a5,0x736753,0xe6b17c,0x5b422c,0x30200e)),
        new DesignDefinition("isometric", "Dimensional tiles", "Raised tiles, layered edges and dimensional icon plates.",
            DesignDecoration.Isometric, Sans, Sans, 14, 400, 5, 5, 1, 1, 1.7, 4,
            P(0xf3f2f8,0xe3dfee,0xffffff,0x393149,0x6b6079,0xb7aaca,0x75539c,0xeaddf7,0xffffff),
            P(0x25232e,0x373044,0x463e56,0xf0eaf8,0xc6b7d7,0x756785,0xd1b1f3,0x513b68,0x332244)),
        new DesignDefinition("pixel", "Pixel grid", "Stepped symbols, square controls and a pixel-built typeface.",
            DesignDecoration.Pixel, Pixel, PixelHeading, 13, 400, 0, 0, 0, 2, 2, 2,
            P(0xf5f4eb,0xe2e3d4,0xfffee9,0x2f3e48,0x536771,0x8b9c9d,0x356a78,0xd1e8e0,0xffffff),
            P(0x202735,0x313f50,0x3e5064,0xe1ecd6,0xb2c5bf,0x6f8c9b,0xb1d8cf,0x3e5c60,0x223e40)),
        new DesignDefinition("ink", "Hand-drawn ink", "Handwritten labels, lively contours and uneven ink frames.",
            DesignDecoration.Ink, Ink, InkHeading, 18, 400, 3, 3, 2, 1.6, 1.7, 0,
            P(0xfffdf6,0xf4f0e4,0xfffdf6,0x3e4545,0x696e65,0xa9afa0,0x52764f,0xe1edcc,0xffffff),
            P(0x292823,0x353329,0x3f3c30,0xede9d8,0xc5c2ae,0x7b806b,0xb0cb92,0x465536,0x28341e)),
    });

    public static DesignDefinition Default => All[0];
    public static DesignDefinition? Find(string? id) => All.FirstOrDefault(d => StringComparer.Ordinal.Equals(d.Id, id));
    public static DesignDefinition Resolve(string? id, bool thinControls = false) => WithLegacyControls(Find(id) ?? Default, thinControls);
    public static DesignDefinition WithLegacyControls(DesignDefinition design, bool thinControls) =>
        design.Decoration != DesignDecoration.Original ? design : thinControls
            ? design with { FontFamily="Segoe UI Semilight, Segoe UI", HeadingFontFamily="Segoe UI Semilight, Segoe UI", IconStrokeWidth=1, ThinControls=true }
            : Default;
    private static DesignPalette P(uint bg, uint panel, uint card, uint ink, uint muted, uint line, uint accent, uint wash, uint on)
        => new(bg|0xff000000, panel|0xff000000, card|0xff000000, ink|0xff000000, muted|0xff000000,
            line|0xff000000, accent|0xff000000, wash|0xff000000, on|0xff000000);
}
