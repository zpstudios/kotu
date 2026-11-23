using Microsoft.UI.Xaml.Controls;

namespace KOTU.Ui;

/// <summary>Each control owns its icon; its Foreground follows the native button state.</summary>
internal static class MediaIcons
{
    internal static Grid BuildPlayIcon() => ToolbarIcons.Build(ToolbarIconKind.Play);
    internal static Grid BuildPauseIcon() => ToolbarIcons.Build(ToolbarIconKind.Pause);
    internal static Grid BuildPreviousIcon() => ToolbarIcons.Build(ToolbarIconKind.Previous);
    internal static Grid BuildNextIcon() => ToolbarIcons.Build(ToolbarIconKind.Next);
    internal static Grid BuildSoundIcon(bool muted = false) => ToolbarIcons.Build(muted ? ToolbarIconKind.Muted : ToolbarIconKind.Sound);

    internal static IconSourceElement BuildEqualizerIcon() => DesignIcons.CreateLive("Equalizer");
    internal static IconSourceElement BuildVisualizerIcon() => DesignIcons.CreateLive("Analyzer");
    internal static IconSourceElement BuildExitFullScreenIcon() => DesignIcons.CreateLive("ExitFullscreen");
    internal static IconSourceElement BuildLoopOffIcon() => DesignIcons.CreateLive("RepeatOff");
}
