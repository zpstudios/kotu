using System.Runtime.CompilerServices;

namespace KOTU.Core.Appearance;

/// <summary>UI-independent, weak metadata shared by modules and the shell.</summary>
public enum DesignElementRole { Automatic, Content, Panel, Card, Heading, FileName, Toolbar, Selection, PreviewChoice, NeutralText }

public static class DesignScope
{
    private sealed class Entry { internal DesignElementRole Role; internal double? FontSize; }
    private static readonly ConditionalWeakTable<object, Entry> Entries = new();
    // Forward creation-time requests without retaining elements or depending on a UI framework.
    public static event Action<object>? ManagementRequested;
    public static void RequestManagement(object element) => ManagementRequested?.Invoke(element);
    public static void SetRole(object element, DesignElementRole role) => Entries.GetOrCreateValue(element).Role = role;
    public static DesignElementRole GetRole(object element) => Entries.TryGetValue(element, out var entry) ? entry.Role : DesignElementRole.Automatic;
    public static void ProtectContent(object element) => SetRole(element, DesignElementRole.Content);
    public static void SetOriginalFontSize(object element,double value)=>Entries.GetOrCreateValue(element).FontSize=value;
    public static double OriginalFontSize(object element,double fallback)=>Entries.TryGetValue(element,out var entry) ? entry.FontSize ?? fallback : fallback;
    public static double PixelCornerStep(double width,double height)=>Math.Max(1,Math.Min(4,Math.Floor(Math.Min(width,height)/3)));

    // Content boundaries do not include a view's toolbar: only the actual content subtree.
    public static bool IsContentBoundary(string name, string typeName) => name is
        "EditorBox" or "DecorLayer" or "RenderPane" or "RenderStack" or "TextList" or "PageHost" or
        "ImageLayoutHost" or "Vlc" or "VirtualEditor" or "EditorWebView" or "DocumentTextPreview" ||
        typeName is "WebView2" or "PdfPane" or "VirtualTextEditor" or "VirtualEditorPane" or "CompositionVideoView";

    public static bool IsFileName(string name) => name is
        "FileNameText" or "TileCaption" or "ExplorerItemName" or "ListItemName" or "ParentListCaption";

    public static double FontSize(double original, DesignDefinition design) =>
        design.Decoration == DesignDecoration.Pixel ? Math.Max(10, Math.Round(original * design.FontSize / 14)) :
        design.Decoration == DesignDecoration.Ink ? Math.Max(12, original * 1.18) : original;

    // The authored weight expresses emphasis relative to ordinary 400 text. Preserve that
    // difference above every design's baseline (including Solid/Frame's 600), within OpenType bounds.
    public static ushort FontWeight(ushort original,DesignDefinition design) =>
        (ushort)Math.Clamp(design.FontWeight+(int)original-400,100,900);

    // Keep the layout slot; snap only the pixel drawing to whole 1.5-unit source cells.
    public static double IconSize(double original, DesignDefinition design) =>
        design.Decoration == DesignDecoration.Pixel && original >= 16 ? (original >= 28 ? 32 : 16) : original;
}

/// <summary>Keeps canonical semantics while a terminal design temporarily displays text.</summary>
public sealed class DesignGlyphState
{
    public string Canonical { get; private set; }
    public string Displayed { get; private set; }
    public DesignGlyphState(string glyph) { Canonical = Displayed = glyph; }
    public void Observe(string glyph) { if (glyph != Displayed) Canonical = glyph; }
    public (string Glyph, double Scale, bool Known) Render(DesignDefinition design)
    {
        if (!DesignIconCatalog.TryResolveGlyph(Canonical, out var symbol)) return (Displayed = Canonical, 1, false);
        if (design.Decoration != DesignDecoration.Terminal) return (Displayed = Canonical, 1, true);
        var drawing = DesignIconCatalog.Create(symbol, design);
        return (Displayed = drawing.Text!, drawing.TextScale, true);
    }
}
