namespace KOTU.Core.Appearance;

/// <summary>UI-independent filled contours in a 24-unit square. Every drawing owns its arrays.</summary>
public readonly record struct DesignIconPoint(double X, double Y);
public sealed record DesignIconDrawing(string? Text, double TextScale, DesignIconPoint[][] Contours);
