namespace KOTU.Core.Routing;

/// <summary>Keeps the requested preview columns and scales overlays with narrow cells.</summary>
public readonly record struct ExplorerTileLayout(int Columns, double ItemWidth, double ItemHeight)
{
    // This is an overlay sizing reference, not a minimum cell width or a column breakpoint.
    public const double OverlayReferenceWidth = 96;

    public static ExplorerTileLayout? Measure(double availableWidth, int requestedColumns)
    {
        if (!double.IsFinite(availableWidth) || availableWidth <= 0) return null;
        var columns = Math.Max(1, requestedColumns);
        var share = availableWidth / columns;
        if (share <= 0) return null; // Underflow at an unrenderable, subnormal input.
        // Preserve floor(width / columns), including the original cell height. A sub-DIP
        // share stays fractional so extreme widths never require fewer columns or overflow.
        var width = share < 1 ? share : Math.Floor(share);
        return new(columns, width, Math.Max(width + 4, 64));
    }

    public static double MeasureOverlayScale(double tileWidth) =>
        !double.IsFinite(tileWidth) || tileWidth <= 0 ? 0 : Math.Min(1, tileWidth / OverlayReferenceWidth);
}
