using KOTU.Core.Appearance;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.UI;

namespace KOTU.App.Appearance;

/// <summary>Reads native color tokens, never the brush keys owned by AppDesignService.</summary>
internal static class OriginalDesignPalette
{
    private static DesignPalette s_light = DesignCatalog.Default.Light;
    private static DesignPalette s_dark = DesignCatalog.Default.Dark;

    internal static DesignPalette Get(bool dark) => dark ? s_dark : s_light;

    internal static void Refresh(ResourceDictionary root)
    {
        var native = root.MergedDictionaries.OfType<XamlControlsResources>().FirstOrDefault();
        s_light = Read(root, native, "Light", DesignCatalog.Default.Light);
        s_dark = Read(root, native, "Default", DesignCatalog.Default.Dark);
    }

    private static DesignPalette Read(ResourceDictionary root, ResourceDictionary? native, string theme, DesignPalette fallback)
    {
        var colors = native?.ThemeDictionaries.TryGetValue(theme, out var value) == true ? value as ResourceDictionary : null;
        uint C(string key, uint otherwise, uint background) => colors?.TryGetValue(key, out var token) == true && token is Color color
            ? DesignPalette.Composite(ToArgb(color), background) : otherwise;
        var background = C("SolidBackgroundFillColorBase", fallback.Background, fallback.Background);
        // Native translucent washes are resolved over the opaque original shell. This keeps
        // menus continuous and prevents a previous design's face from leaking underneath.
        var accentKey = theme == "Light" ? "SystemAccentColorDark1" : "SystemAccentColorLight2";
        var accent = root.TryGetValue(accentKey, out var accentValue) && accentValue is Color accentColor
            ? ToArgb(accentColor) : fallback.Accent;
        return new(background,
            C("LayerFillColorDefault", fallback.Panel, background),
            C("ControlFillColorDefault", fallback.Card, background),
            C("TextFillColorPrimary", fallback.Foreground, background),
            C("TextFillColorSecondary", fallback.Muted, background),
            C("ControlStrokeColorDefault", fallback.Border, background), accent,
            C("SubtleFillColorSecondary", fallback.Selection, background),
            C("TextOnAccentFillColorPrimary", fallback.OnAccent, accent));
    }

    private static uint ToArgb(Color color) => (uint)(color.A << 24 | color.R << 16 | color.G << 8 | color.B);
}
