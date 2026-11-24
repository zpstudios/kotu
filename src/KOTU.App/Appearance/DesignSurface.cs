using KOTU.Core.Appearance;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using Windows.Foundation;
using Windows.UI;

namespace KOTU.App.Appearance;

/// <summary>Small explicit-profile ornament, shared by specimen surfaces and live chrome.</summary>
internal sealed class DesignSurface : Canvas
{
    internal DesignSurface()
    {
        IsHitTestVisible = false;
        Tag = DesignRuntime.IconScopeMarker;
        HorizontalAlignment = HorizontalAlignment.Stretch;
        VerticalAlignment = VerticalAlignment.Stretch;
    }

    // Only the explorer's file-item surface uses this composition. Internal preview cards,
    // native high-contrast states and the accepted Paper composition keep their own policy.
    internal void DrawFileTile(DesignDefinition design, DesignPalette p, double width, double height,
        bool selected, bool checkedItem, bool hot, bool pressed, bool enabled)
    {
        var active = selected || checkedItem || hot || pressed;
        if (!enabled) p = p with { Foreground=p.Muted, Border=p.Muted, Accent=p.Muted };
        if (active || design.Decoration == DesignDecoration.Frame)
        {
            var fill = !enabled ? p.Card : selected && design.Decoration == DesignDecoration.Solid ? p.Accent :
                selected || hot || pressed ? p.Selection : checkedItem ? p.Panel : p.Background;
            Draw(design,p,width,height,false,pressed,selected:selected,fill:fill);
            return;
        }
        Children.Clear();
        if (width > 2 && height > 2 && (design.Decoration is DesignDecoration.Editorial or DesignDecoration.Terminal))
            Line([new(0,height-1),new(width-1,height-1)],p.Border,1);
    }

    internal void Draw(DesignDefinition design, DesignPalette p, double width, double height,
        bool highContrast, bool pressed = false, bool panel = false, bool selected = false, uint? fill = null, double? radius = null)
    {
        Children.Clear();
        if (width <= 2 || height <= 2) return;
        var w = width - 1; var h = height - 1;
        var line = selected ? p.Accent : p.Border;
        var kind = design.Decoration;
        if (fill is { } face)
        {
            if (!highContrast && kind is DesignDecoration.Paper or DesignDecoration.Pixel or DesignDecoration.Ink or DesignDecoration.Isometric)
                Polygon(Outline(kind,width,height,panel),face);
            else Children.Add(new Border { Width = width, Height = height, Background = Fill(design,p,face,pressed,panel,highContrast),
                CornerRadius = new CornerRadius(highContrast ? 0 : radius ?? (panel ? design.PanelRadius : design.ControlRadius)) });
        }
        if (highContrast)
        {
            Box(.5, .5, w, h, 0, line, 1);
            return;
        }
        switch (kind)
        {
            case DesignDecoration.Precision:
                Box(.5,.5,w,h,radius ?? design.ControlRadius,line,1); break;
            case DesignDecoration.Rounded:
                Box(.5,.5,w,h,radius ?? (panel ? design.PanelRadius : design.ControlRadius),line,selected ? 2 : 1); break;
            case DesignDecoration.Solid:
                if (selected) Box(.5,.5,w,h,2,p.Accent,2); break;
            case DesignDecoration.Editorial:
                Line([new(0,h),new(w,h)],line,selected ? 2 : 1); break;
            case DesignDecoration.Terminal:
                var bracket = Math.Min(5, w/4);
                Line([new(bracket,1),new(1,1),new(1,h-1),new(bracket,h-1)],line,1);
                Line([new(w-bracket,1),new(w-1,1),new(w-1,h-1),new(w-bracket,h-1)],line,1); break;
            case DesignDecoration.Mechanical:
                Box(.5,.5,w,h,design.ControlRadius,line,1);
                Line([new(2,h-2),new(2,2),new(w-2,2)],pressed || panel ? line : p.Card,2);
                Line([new(w-2,2),new(w-2,h-2),new(2,h-2)],pressed || panel ? p.Card : line,2); break;
            case DesignDecoration.Frame:
                Box(1,1,Math.Max(1,w-3),Math.Max(1,h-3),0,selected ? line : p.Foreground,2);
                Line([new(4,h-1),new(w-1,h-1),new(w-1,4)],selected ? line : p.Foreground,2); break;
            case DesignDecoration.Paper:
                var fold = Math.Min(height <= 22 ? 3 : panel ? 12 : 7, Math.Min(w,h)/3);
                Polygon([new(w-fold,1),new(w-1,fold),new(w-fold,fold)],p.Border);
                Line([new(1,h-2),new(1,1),new(w-fold,1),new(w-1,fold),new(w-1,h-2),new(1,h-2)],line,1);
                Line([new(4,h),new(w,h)],line,1); break;
            case DesignDecoration.Isometric:
                var depth = Math.Min(height <= 22 ? 2 : 4,Math.Min(w,h)/5);
                Polygon([new(1,depth),new(depth,1),new(w-1,1),new(w-depth,depth)],p.Selection);
                Polygon([new(w-depth,depth),new(w-1,1),new(w-1,h-depth),new(w-depth,h-1)],p.Border);
                Line([new(1,depth),new(w-depth,depth),new(w-depth,h-1),new(1,h-1),new(1,depth)],line,1); break;
            case DesignDecoration.Pixel:
                Line(Outline(kind,w,h),selected ? line : p.Foreground,2); break;
            case DesignDecoration.Ink:
                Line(Outline(kind,w,h),selected ? line : p.Foreground,1.5);
                Line([new(4,h-2),new(w*.51,h-1),new(w-4,h-3)],selected ? line : p.Foreground,.6); break;
        }
        if (pressed) Box(2,2,Math.Max(1,w-4),Math.Max(1,h-4),radius ?? (panel ? design.PanelRadius : design.ControlRadius),p.Foreground,1);
    }

    private static Point[] Outline(DesignDecoration kind, double w, double h, bool panel = false) => kind switch
    {
        DesignDecoration.Paper => [new(0,0),new(w-PaperFold(w,h,panel),0),new(w,PaperFold(w,h,panel)),new(w,h),new(0,h)],
        DesignDecoration.Isometric => [new(0,IsoDepth(w,h)),new(IsoDepth(w,h),0),new(w,0),new(w,h-IsoDepth(w,h)),new(w-IsoDepth(w,h),h),new(0,h)],
        DesignDecoration.Pixel => PixelOutline(w,h),
        _ => [new(3,2),new(w*.48,1),new(w-3,3),new(w-1,h*.48),new(w-3,h-2),new(w*.54,h-1),new(2,h-3),new(1,h*.45),new(3,2)],
    };
    private static double PaperFold(double w,double h,bool panel) => Math.Min(h<=22 ? 3 : panel ? 12 : 7,Math.Min(w,h)/3);
    private static double IsoDepth(double w,double h) => Math.Min(h<=22 ? 2 : 4,Math.Min(w,h)/5);
    private static Point[] PixelOutline(double w,double h)
    {
        var step=DesignScope.PixelCornerStep(w,h);
        return [new(step,1),new(w-step,1),new(w-step,step),new(w-1,step),new(w-1,h-step),new(w-step,h-step),new(w-step,h-1),new(step,h-1),new(step,h-step),new(1,h-step),new(1,step),new(step,step),new(step,1)];
    }

    internal static Brush Fill(DesignDefinition d, DesignPalette p, uint normal, bool pressed, bool panel, bool highContrast)
    {
        if (highContrast || d.Decoration != DesignDecoration.Mechanical) return Brush(normal);
        var gradient = new LinearGradientBrush { StartPoint = new Point(0,0), EndPoint = new Point(1,1) };
        var recessed = pressed || panel;
        gradient.GradientStops.Add(new GradientStop { Offset = 0, Color = Color(recessed ? p.Border : p.Card) });
        gradient.GradientStops.Add(new GradientStop { Offset = .12, Color = Color(normal) });
        gradient.GradientStops.Add(new GradientStop { Offset = .83, Color = Color(normal) });
        gradient.GradientStops.Add(new GradientStop { Offset = 1, Color = Color(recessed ? p.Card : p.Border) });
        return gradient;
    }

    internal static SolidColorBrush Brush(uint color) => new(Color(color));
    internal static Color Color(uint value) => Windows.UI.Color.FromArgb((byte)(value>>24),(byte)(value>>16),(byte)(value>>8),(byte)value);
    private void Box(double x,double y,double w,double h,double radius,uint color,double stroke)
    {
        var box = new Border { Width = w, Height = h, BorderBrush = Brush(color), BorderThickness = new Thickness(stroke), CornerRadius = new CornerRadius(radius) };
        SetLeft(box,x); SetTop(box,y); Children.Add(box);
    }
    private void Line(Point[] points,uint color,double stroke)
    {
        var line = new Polyline { Stroke = Brush(color), StrokeThickness = stroke, Points = new PointCollection(), StrokeLineJoin = PenLineJoin.Round };
        foreach(var point in points) line.Points.Add(point);
        Children.Add(line);
    }
    private void Polygon(Point[] points,uint color)
    {
        var polygon = new Polygon { Fill = Brush(color), Points = new PointCollection() };
        foreach(var point in points) polygon.Points.Add(point);
        Children.Add(polygon);
    }
}
