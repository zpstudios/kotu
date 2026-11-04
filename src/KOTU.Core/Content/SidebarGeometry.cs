namespace KOTU.Core.Content;

/// <summary>Geometry follows rendered panels, including panels that finish opening asynchronously.</summary>
public readonly record struct SidebarGeometry(double LeftPercent, double RightPercent, bool Floating)
{
    public double CenterPercent => 100 - LeftPercent - RightPercent;
    public int VisibleCount => (LeftPercent > 0 ? 1 : 0) + (RightPercent > 0 ? 1 : 0);
    public int ExplorerColumns => 8 - 2 * VisibleCount;
    public double ContentLeftPercent => Floating ? 0 : LeftPercent;
    public double ContentRightPercent => Floating ? 0 : RightPercent;
    public double ContentCenterPercent => 100 - ContentLeftPercent - ContentRightPercent;
    public int DockCount => Floating ? 0 : VisibleCount;
    public int ContentColumns => 8 - 2 * DockCount;

    public static SidebarGeometry FromVisiblePanels(bool left, bool right, bool floating, double panelPercent)
        => new(left ? panelPercent : 0, right ? panelPercent : 0, floating);
}
