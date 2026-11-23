using KOTU.Core.Appearance;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.Foundation;

namespace KOTU.Ui;

/// <summary>Native icon factory. Explicit designs stay isolated; live icons refresh in place.</summary>
internal static class DesignIcons
{
    internal static IconSourceElement Create(string symbol, DesignDefinition design, double size = 18)
    {
        var icon = new IconSourceElement
        {
            Width = size, Height = size,
            HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
            IsHitTestVisible = false, Tag = DesignRuntime.IconScopeMarker,
        };
        Apply(icon, symbol, design, size);
        return icon;
    }

    internal static IconSourceElement CreateLive(string symbol, double size = 18)
    {
        var icon = Create(symbol, DesignRuntime.Current, size);
        _ = new LiveIcon(icon, symbol, size);
        return icon;
    }

    internal static void Apply(IconSourceElement icon, string symbol, DesignDefinition design, double size = 18)
    {
        icon.IconSource = CreateSource(symbol, design, size);
        var scale = size > 0 ? DesignScope.IconSize(size,design)/size : 1;
        icon.RenderTransformOrigin = new Point(.5,.5);
        icon.RenderTransform = new ScaleTransform { ScaleX=scale, ScaleY=scale };
    }

    internal static IconSource CreateSource(string symbol, DesignDefinition design, double size = 18)
    {
        var drawing = DesignIconCatalog.Create(symbol, design);
        if (drawing.Text is { } text)
            return new FontIconSource
            {
                Glyph = text, FontFamily = new FontFamily(design.FontFamily), FontSize = size * drawing.TextScale,
                IsTextScaleFactorEnabled = false,
            };

        // PathIcon does not scale its native Path to the surrounding icon slot.
        // Keep the canonical 24-unit drawing and size this fresh geometry once.
        var coordinateScale = size / 24;
        var geometry = new PathGeometry { FillRule = FillRule.Nonzero };
        // This unpainted line fixes a common viewport without introducing visible ink.
        var viewport = new PathFigure { StartPoint = new(0,0), IsFilled = false, IsClosed = false };
        viewport.Segments.Add(new LineSegment { Point = new(size,size) });
        geometry.Figures.Add(viewport);
        foreach (var contour in drawing.Contours)
        {
            var figure = new PathFigure { StartPoint = new Point(contour[0].X * coordinateScale,contour[0].Y * coordinateScale), IsClosed = true };
            foreach (var point in contour.Skip(1)) figure.Segments.Add(new LineSegment { Point = new(point.X * coordinateScale,point.Y * coordinateScale) });
            geometry.Figures.Add(figure);
        }
        return new PathIconSource { Data = geometry };
    }

    private sealed class LiveIcon
    {
        private readonly IconSourceElement _icon;
        private readonly string _symbol;
        private readonly double _size;
        private bool _loaded;

        internal LiveIcon(IconSourceElement icon, string symbol, double size)
        {
            _icon = icon; _symbol = symbol; _size = size;
            icon.Loaded += OnLoaded;
            icon.Unloaded += OnUnloaded;
        }

        private void OnLoaded(object sender, RoutedEventArgs e)
        {
            _loaded = true;
            DesignRuntime.Changed -= OnChanged;
            DesignRuntime.Changed += OnChanged;
            Refresh();
        }

        private void OnUnloaded(object sender, RoutedEventArgs e)
        {
            _loaded = false;
            DesignRuntime.Changed -= OnChanged;
        }

        private void OnChanged()
        {
            if (_icon.DispatcherQueue.HasThreadAccess) Refresh();
            else _icon.DispatcherQueue.TryEnqueue(Refresh);
        }

        private void Refresh()
        {
            if (_loaded) Apply(_icon, _symbol, DesignRuntime.Current, _size);
        }
    }
}
