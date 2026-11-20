using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace KOTU.Ui;

/// <summary>Each control owns its icon; its Foreground follows the native button state.</summary>
internal static class MediaIcons
{
    internal static Grid BuildPlayIcon() => ToolbarIcons.Build(ToolbarIconKind.Play);
    internal static Grid BuildPauseIcon() => ToolbarIcons.Build(ToolbarIconKind.Pause);
    internal static Grid BuildPreviousIcon() => ToolbarIcons.Build(ToolbarIconKind.Previous);
    internal static Grid BuildNextIcon() => ToolbarIcons.Build(ToolbarIconKind.Next);
    internal static Grid BuildSoundIcon(bool muted = false) => ToolbarIcons.Build(muted ? ToolbarIconKind.Muted : ToolbarIconKind.Sound);

    internal static FontIcon BuildEqualizerIcon() => Glyph("\uE000");
    internal static FontIcon BuildVisualizerIcon() => Glyph("\uE001");
    internal static FontIcon BuildExitFullScreenIcon() => Glyph("\uE003");
    internal static FontIcon BuildLoopOffIcon() => Glyph("\uE004");

    private static FontIcon Glyph(string glyph) => new()
    {
        Glyph = glyph,
        FontSize = 18,
        FontFamily = (FontFamily)Application.Current.Resources["KotuIconFontFamily"],
    };
}
