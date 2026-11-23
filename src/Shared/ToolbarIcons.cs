using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace KOTU.Ui;

/// <summary>Toolbar/menu compatibility entry points use the full design factory.</summary>
internal static class ToolbarIcons
{
    internal static Grid Build(ToolbarIconKind kind)
    {
        var size = ToolbarIconGeometry.ViewportSize(kind);
        var root = new Grid
        {
            Width = size, Height = size,
            HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
        };
        root.Children.Add(BuildMenu(kind));
        return root;
    }

    internal static IconSourceElement BuildMenu(ToolbarIconKind kind)
        => DesignIcons.CreateLive(Symbol(kind), ToolbarIconGeometry.ViewportSize(kind));

    private static string Symbol(ToolbarIconKind kind) => kind switch
    {
        ToolbarIconKind.Previous => "PreviousTrack",
        ToolbarIconKind.Next => "NextTrack",
        ToolbarIconKind.Sound => "Speaker",
        ToolbarIconKind.FitContain => "Fit",
        _ => kind.ToString(),
    };
}
