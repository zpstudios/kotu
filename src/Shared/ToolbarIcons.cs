using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.Foundation;

namespace KOTU.Ui;

/// <summary>같은 좌표 원본을 버튼과 메뉴의 새 UI 인스턴스로 만든다.</summary>
internal static class ToolbarIcons
{
    internal static Grid Build(ToolbarIconKind kind)
    {
        var sentinel = new TextBlock { Width = 0, Height = 0 };
        var shape = new Microsoft.UI.Xaml.Shapes.Path
        {
            Data = Geometry(kind), Stretch = Stretch.None, Fill = sentinel.Foreground,
        };
        var root = new Grid
        {
            Width = ToolbarIconGeometry.Size, Height = ToolbarIconGeometry.Size,
            HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
        };
        // Canvas는 좌표 원점을 옮기지 않는다. Path의 내용 자동 맞춤으로 여백이 사라지는 것을 막는다.
        var canvas = new Canvas { Width = ToolbarIconGeometry.Size, Height = ToolbarIconGeometry.Size };
        canvas.Children.Add(shape);
        root.Children.Add(sentinel);
        root.Children.Add(canvas);
        sentinel.RegisterPropertyChangedCallback(TextBlock.ForegroundProperty,
            (sender, _) => shape.Fill = ((TextBlock)sender).Foreground);
        root.Loaded += (_, _) => shape.Fill = sentinel.Foreground;
        return root;
    }

    internal static PathIcon BuildMenu(ToolbarIconKind kind) => new()
    {
        Width = ToolbarIconGeometry.Size, Height = ToolbarIconGeometry.Size,
        Data = Geometry(kind),
    };

    private static PathGeometry Geometry(ToolbarIconKind kind)
    {
        var geometry = new PathGeometry { FillRule = FillRule.EvenOdd };
        // 채우지 않는 대각선은 잉크 없이 18×18 경계를 확정한다. PathIcon이 실제 잉크 범위만
        // 확대해 메뉴의 1:1 상자를 늘리지 않도록 버튼과 같은 원점·배율을 보존한다.
        var viewport = new PathFigure { StartPoint = new Point(0, 0), IsFilled = false, IsClosed = false };
        viewport.Segments.Add(new LineSegment { Point = new Point(ToolbarIconGeometry.Size, ToolbarIconGeometry.Size) });
        geometry.Figures.Add(viewport);
        foreach (var contour in ToolbarIconGeometry.Create(kind))
        {
            var figure = new PathFigure { StartPoint = new Point(contour[0].X, contour[0].Y), IsClosed = true };
            foreach (var point in contour.Skip(1)) figure.Segments.Add(new LineSegment { Point = new Point(point.X, point.Y) });
            geometry.Figures.Add(figure);
        }
        return geometry;
    }
}
