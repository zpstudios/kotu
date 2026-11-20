using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using Windows.Foundation;

namespace KOTU.Module.Document;

internal static class PageViewport
{
    internal static double VisibleHeight(FrameworkElement viewport, double width, double height, FrameworkElement? overlay)
    {
        if (overlay is null || viewport.XamlRoot is null || overlay.XamlRoot != viewport.XamlRoot) return height;
        for (DependencyObject? node = overlay; node is not null; node = VisualTreeHelper.GetParent(node))
            if (node is UIElement element && element.Visibility != Visibility.Visible) return height;
        var bounds = overlay.TransformToVisual(viewport).TransformBounds(
            new Rect(0, 0, overlay.ActualWidth, overlay.ActualHeight));
        return PdfFitGeometry.VisibleHeight(width, height, bounds.X, bounds.Y, bounds.Width, bounds.Height);
    }
}
