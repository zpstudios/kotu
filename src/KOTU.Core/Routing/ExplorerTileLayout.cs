namespace KOTU.Core.Routing;

/// <summary>Sizes preview cells without shrinking their native controls or changing the requested density.</summary>
public readonly record struct ExplorerTileLayout(int Columns, double ItemWidth, double ItemHeight)
{
    // A 20-DIP check occupies at most about one fifth of an ordinary cell. Below this width,
    // use fewer columns instead of shrinking the check target, caption, or status badges.
    public const double MinimumItemWidth = 96;

    public static ExplorerTileLayout? Measure(double availableWidth, int requestedColumns)
    {
        if (!double.IsFinite(availableWidth) || availableWidth <= 0) return null;
        var columns = (int)Math.Clamp(Math.Floor(availableWidth / MinimumItemWidth),
            1, Math.Max(1, requestedColumns));
        // A surface narrower than one cell still uses its actual width. Keep fractional
        // sub-DIP inputs positive without allowing the cell to extend past that surface.
        var width = Math.Min(availableWidth, Math.Max(1, Math.Floor(availableWidth / columns)));
        return new(columns, width, Math.Max(width + 4, 64));
    }
}
