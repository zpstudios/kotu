namespace KOTU.Core.Appearance;

/// <summary>Construction rules shared by the live interface and its previews.</summary>
public enum DesignDecoration
{
    Precision, Rounded, Solid, Editorial, Terminal, Mechanical, Frame, Paper, Isometric, Pixel, Ink,
}

/// <summary>Opaque ARGB colors. High contrast deliberately has no custom palette.</summary>
public sealed record DesignPalette(
    uint Background, uint Panel, uint Card, uint Foreground, uint Muted,
    uint Border, uint Accent, uint Selection, uint OnAccent);

/// <summary>UI roles only: never apply these fonts to document, code or subtitle content.</summary>
public sealed record DesignDefinition(
    string Id, string Name, string Description, DesignDecoration Decoration,
    string FontFamily, string HeadingFontFamily, double FontSize, ushort FontWeight,
    double ControlRadius, double PanelRadius, double CheckRadius,
    double BorderWidth, double IconStrokeWidth, double Depth,
    DesignPalette Light, DesignPalette Dark)
{
    public DesignPalette Palette(bool dark) => dark ? Dark : Light;
}
