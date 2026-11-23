#if KOTU_DESIGN_TEST_HARNESS
using System.Diagnostics;
using KOTU.Core.Appearance;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Media;
using NativePath=Microsoft.UI.Xaml.Shapes.Path;
using Point=Windows.Foundation.Point;
using Rect=Windows.Foundation.Rect;

namespace KOTU.App.Integration;

// Read the real factory's native layout without changing its geometry or child sizing.
// This window is never registered with LiveDesign and owns no runtime subscriptions.
internal sealed class DesignIconProbe
{
    private static readonly double[] Sizes=[13,14,18,24,32];
    private static readonly string[] Symbols=["Check","Mixed"];
    internal const int ObservationsPerCase=10;
    internal Grid Root { get; }=new() { Tag=DesignRuntime.PreviewScopeMarker,Padding=new Thickness(16),RowSpacing=16,ColumnSpacing=16 };
    internal Window Window { get; }
    internal sealed record CaseResult(string Design,string Theme,bool Passed,List<string> Failures,List<object> Observations,object Readiness);
    private sealed record Sample(string Symbol,double Size,Grid Face,IconSourceElement Icon);
    private sealed record ElementReadiness(string Type,bool IsLoaded,double ActualWidth,double ActualHeight);
    private sealed record SampleReadiness(string Symbol,double RequestedSize,ElementReadiness Face,ElementReadiness Icon,ElementReadiness[] NativeChildren)
    {
        public bool Ready=>Face.IsLoaded && Icon.IsLoaded && Face.ActualWidth>0 && Icon.ActualWidth>0 &&
            NativeChildren.Any(child=>child.IsLoaded && child.ActualWidth>0 && child.ActualHeight>0);
    }
    private sealed record ReadinessSnapshot(double ElapsedMilliseconds,ElementReadiness Root,SampleReadiness[] Samples)
    {
        public bool Ready=>Root.IsLoaded && Samples.All(sample=>sample.Ready);
    }

    internal DesignIconProbe()
    {
        for(var i=0;i<Symbols.Length;i++) Root.RowDefinitions.Add(new RowDefinition());
        for(var i=0;i<Sizes.Length;i++) Root.ColumnDefinitions.Add(new ColumnDefinition());
        Window=new Window { Title="KOTU A427 validation — icon geometry",Content=Root };
        Window.AppWindow.Resize(new Windows.Graphics.SizeInt32(720,320));
    }

    internal async Task<CaseResult> Observe(DesignDefinition design,ElementTheme theme)
    {
        var failures=new List<string>();
        var observations=new List<object>();
        var samples=new List<Sample>();
        Root.Children.Clear();Root.RequestedTheme=theme;
        for(var row=0;row<Symbols.Length;row++)
        for(var column=0;column<Sizes.Length;column++)
        {
            var size=Sizes[column];
            // Match the actual 13-in-17 preview and 14-in-20 native check hosts.
            // Larger samples keep a six-unit inset around the requested icon slot.
            var faceSize=size==13 ? 17 : size+6;
            var face=new Grid { Width=faceSize,Height=faceSize,HorizontalAlignment=HorizontalAlignment.Center,
                VerticalAlignment=VerticalAlignment.Center,Background=new SolidColorBrush(Microsoft.UI.Colors.DarkGreen) };
            var icon=KOTU.Ui.DesignIcons.Create(Symbols[row],design,size);
            icon.Foreground=new SolidColorBrush(Microsoft.UI.Colors.White);
            face.Children.Add(icon);
            var cell=new Grid();cell.Children.Add(face);
            cell.Children.Add(new TextBlock { Text=$"{Symbols[row]} {size}",FontSize=12,VerticalAlignment=VerticalAlignment.Bottom });
            Grid.SetRow(cell,row);Grid.SetColumn(cell,column);Root.Children.Add(cell);
            samples.Add(new Sample(Symbols[row],size,face,icon));
        }
        Window.Activate();
        var timer=Stopwatch.StartNew();
        var pollCount=0;var totalReadMilliseconds=0d;var maxReadMilliseconds=0d;
        ReadinessSnapshot Readiness()
        {
            var start=timer.Elapsed.TotalMilliseconds;
            // Capture every predicate, without All/Any short-circuiting away the
            // state needed to explain a false result. No layout is forced here.
            var root=ReadElementReadiness(Root);
            var states=samples.Select(sample=>ReadSampleReadiness(sample,design)).ToArray();
            var end=timer.Elapsed.TotalMilliseconds;var duration=end-start;
            pollCount++;totalReadMilliseconds+=duration;maxReadMilliseconds=Math.Max(maxReadMilliseconds,duration);
            return new ReadinessSnapshot(end,root,states);
        }
        var initialReadiness=Readiness();var lastReadiness=initialReadiness;
        while(!lastReadiness.Ready && timer.Elapsed.TotalMilliseconds<2000)
        {
            await Task.Delay(25);
            lastReadiness=Readiness();
        }
        var readyWithinDeadline=lastReadiness.Ready && lastReadiness.ElapsedMilliseconds<=2000;
        if (readyWithinDeadline) await Task.Delay(100);
        foreach(var sample in samples)
        {
            var label=$"{design.Id}: {theme} icon {sample.Symbol} {sample.Size}";
            try { observations.Add(ObserveSample(sample,design,readyWithinDeadline,failures,label)); }
            catch(Exception error)
            {
                failures.Add(label+": observation error "+error);
                observations.Add(new { sample.Symbol,RequestedSize=sample.Size,ConfigurationPassed=false,Error=error.ToString() });
            }
        }
        return new CaseResult(design.Id,theme.ToString(),failures.Count==0 && observations.Count==ObservationsPerCase,failures,observations,
            new { ReadyWithinDeadline=readyWithinDeadline,DeadlineMilliseconds=2000,PollCount=pollCount,
                TotalReadMilliseconds=totalReadMilliseconds,MaxReadMilliseconds=maxReadMilliseconds,
                Initial=initialReadiness,LastPoll=lastReadiness,AfterObservations=Readiness() });
    }

    private object ObserveSample(Sample sample,DesignDefinition design,bool readyWithinDeadline,List<string> failures,string label)
    {
        void Check(bool value,string detail) { if (!value) failures.Add(label+": "+detail); }
        var nodes=new[] { (DependencyObject)sample.Icon }.Concat(Descendants(sample.Icon)).ToArray();
        var paths=nodes.OfType<NativePath>().ToArray();
        var fonts=nodes.OfType<FontIcon>().ToArray();
        var drawing=DesignIconCatalog.Create(sample.Symbol,design);
        var terminal=drawing.Text is not null;
        var expectedScale=DesignScope.IconSize(sample.Size,design)/24;
        var rootBeforeLayout=ReadElementReadiness(Root);
        var readinessBeforeLayout=ReadSampleReadiness(sample,design,nodes);
        var iconToFace=sample.Icon.TransformToVisual(sample.Face);
        var slot=iconToFace.TransformBounds(new Rect(0,0,sample.Size,sample.Size));
        var tree=nodes.OfType<FrameworkElement>().Select(element=>Node(element,sample.Face,sample.Icon)).ToArray();
        var rootAtMeasurement=ReadElementReadiness(Root);
        var readinessAtMeasurement=ReadSampleReadiness(sample,design,nodes);
        var readiness=new { RootBeforeLayout=rootBeforeLayout,BeforeLayout=readinessBeforeLayout,
            RootAtMeasurement=rootAtMeasurement,AtMeasurement=readinessAtMeasurement };
        var ready=rootAtMeasurement.IsLoaded && readinessAtMeasurement.Ready;
        // A late-ready sample remains a timeout failure, but its current native
        // geometry is still measured instead of being discarded by a stale bool.
        Check(readyWithinDeadline,"factory icon and native child ready within 2s");
        Check(ready,"factory icon and native child ready at geometry observation");
        Check(sample.Icon.Width==sample.Size && sample.Icon.Height==sample.Size,
            "factory requested outer size retained without forcing native child size");
        if (terminal)
        {
            var valid=ready && paths.Length==0 && fonts.Length==1 && fonts[0].Glyph==drawing.Text &&
                Math.Abs(fonts[0].FontSize-sample.Size*drawing.TextScale)<.01;
            Check(valid,"terminal uses expected FontIcon branch, not Path geometry");
            return new { sample.Symbol,RequestedSize=sample.Size,Branch="Font",ConfigurationPassed=valid,Readiness=readiness,
                ExpectedText=drawing.Text,ExpectedFontSize=sample.Size*drawing.TextScale,
                Fonts=fonts.Select(font=>new { font.Glyph,font.FontSize,FontFamily=font.FontFamily.Source }).ToArray(),
                IntendedSlotInFace=Box(slot),Face=Node(sample.Face,sample.Face,sample.Icon),Nodes=tree };
        }
        var path=paths.FirstOrDefault();
        var geometry=path?.Data as PathGeometry;
        var supported=geometry is not null && geometry.Figures.All(figure=>figure.Segments.All(segment=>segment is LineSegment));
        var filled=geometry?.Figures.Where(figure=>figure.IsFilled).ToArray() ?? [];
        var configuration=ready && paths.Length==1 && paths[0].IsLoaded && fonts.Length==0 && supported &&
            path!.Stretch==Stretch.None && filled.Length==drawing.Contours.Length && filled.All(figure=>figure.IsClosed);
        Check(configuration,"one native Path with Stretch=None and readable filled line contours");
        if (!configuration)
            return new { sample.Symbol,RequestedSize=sample.Size,Branch="Path",ConfigurationPassed=false,Readiness=readiness,
                PathCount=paths.Length,FontCount=fonts.Length,Stretch=path?.Stretch.ToString(),GeometryType=path?.Data?.GetType().Name,
                FilledFigureCount=filled.Length,ExpectedContourCount=drawing.Contours.Length,Face=Node(sample.Face,sample.Face,sample.Icon),Nodes=tree };

        // Geometry.Transform belongs to Data, not to the Path's visual transform. Apply
        // each exactly once. Shape.Stretch=None is required above for this calculation.
        var localPoints=filled.SelectMany(FigurePoints).ToArray();
        var dataPoints=localPoints.Select(point=>geometry!.Transform?.TransformPoint(point) ?? point).ToArray();
        var pathToFace=path!.TransformToVisual(sample.Face);
        var pathToSlot=path.TransformToVisual(sample.Icon);
        var facePoints=dataPoints.Select(pathToFace.TransformPoint).ToArray();
        var slotPoints=dataPoints.Select(pathToSlot.TransformPoint).ToArray();
        var sourcePoints=drawing.Contours.SelectMany(contour=>contour).Select(point=>new Point(point.X,point.Y)).ToArray();
        // Use the measured outer icon origin/transform, including layout rounding and
        // pixel's render transform. The source-to-slot factor remains size/24.
        var expectedPoints=sourcePoints.Select(point=>iconToFace.TransformPoint(new Point(point.X*sample.Size/24,point.Y*sample.Size/24))).ToArray();
        var actual=Bounds(facePoints);var expected=Bounds(expectedPoints);var source=Bounds(sourcePoints);
        var within=Contains(slot,actual,.05);
        // The assertion uses the design contract, independently of the observed
        // outer RenderTransform, so an incorrect pixel transform cannot bless itself.
        var scaleMatches=Math.Abs(actual.Width-source.Width*expectedScale)<.02 && Math.Abs(actual.Height-source.Height*expectedScale)<.02;
        Check(within,"filled geometry lies inside intended rendered slot");
        Check(scaleMatches,"filled geometry dimensions match requested size/24 and pixel render scale once");
        return new { sample.Symbol,RequestedSize=sample.Size,Branch="Path",ConfigurationPassed=true,Readiness=readiness,
            PathStretch=path.Stretch.ToString(),ExpectedSourceToFaceScale=expectedScale,
            FactoryRenderScale=DesignScope.IconSize(sample.Size,design)/sample.Size,
            ObservedSourceToFaceScaleX=actual.Width/source.Width,ObservedSourceToFaceScaleY=actual.Height/source.Height,
            GlyphWithinIntendedSlot=within,GlyphScaleMatches=scaleMatches,
            ExpectedFilledWidth=source.Width*expectedScale,ExpectedFilledHeight=source.Height*expectedScale,
            SourceViewport=24,CanonicalFilledBounds=Box(source),NativeFilledBounds=Box(Bounds(localPoints)),
            NativeDataBounds=Box(geometry!.Bounds),NativeDataTransform=TransformInfo(geometry.Transform),
            NativeFillRule=geometry.FillRule.ToString(),FilledFigureCount=filled.Length,
            UnfilledFigureCount=geometry.Figures.Count-filled.Length,NativeFilledPointCount=localPoints.Length,CanonicalPointCount=sourcePoints.Length,
            FilledBoundsInIconCoordinates=Box(Bounds(slotPoints)),FilledBoundsInFace=Box(actual),
            ExpectedFilledBoundsInFace=Box(expected),IntendedSlotInFace=Box(slot),
            PathToFace=TransformInfo(pathToFace),PathToIcon=TransformInfo(pathToSlot),IconToFace=TransformInfo(iconToFace),
            Face=Node(sample.Face,sample.Face,sample.Icon),RasterizationScale=sample.Icon.XamlRoot.RasterizationScale,Nodes=tree };
    }

    private static ElementReadiness ReadElementReadiness(FrameworkElement element)=>
        new(element.GetType().FullName!,element.IsLoaded,element.ActualWidth,element.ActualHeight);
    private static SampleReadiness ReadSampleReadiness(Sample sample,DesignDefinition design,IEnumerable<DependencyObject>? nodes=null)
    {
        var children=(nodes ?? Descendants(sample.Icon)).OfType<FrameworkElement>()
            .Where(child=>design.Decoration==DesignDecoration.Terminal ? child is FontIcon : child is NativePath)
            .Select(ReadElementReadiness).ToArray();
        return new(sample.Symbol,sample.Size,ReadElementReadiness(sample.Face),ReadElementReadiness(sample.Icon),children);
    }

    private static object Node(FrameworkElement element,FrameworkElement face,FrameworkElement icon)=>new
    {
        Type=element.GetType().FullName,element.Name,element.IsLoaded,
        NativeIdentity=element is WinRT.IWinRTObject native ? $"0x{native.NativeObject.ThisPtr:X}" : null,
        Width=Finite(element.Width),Height=Finite(element.Height),
        DesiredWidth=Finite(element.DesiredSize.Width),DesiredHeight=Finite(element.DesiredSize.Height),
        ActualWidth=Finite(element.ActualWidth),ActualHeight=Finite(element.ActualHeight),
        LayoutSlotInParent=Box(LayoutInformation.GetLayoutSlot(element)),
        RenderBoundsInFace=Box(element.TransformToVisual(face).TransformBounds(new Rect(0,0,element.ActualWidth,element.ActualHeight))),
        TransformToFace=TransformInfo(element.TransformToVisual(face)),TransformToIcon=TransformInfo(element.TransformToVisual(icon)),
        element.HorizontalAlignment,element.VerticalAlignment,element.UseLayoutRounding,
        RenderTransform=TransformInfo(element.RenderTransform),RenderTransformOrigin=new { element.RenderTransformOrigin.X,element.RenderTransformOrigin.Y },
        ExplicitClip=element.Clip is { } clip ? new { Bounds=Box(clip.Rect),Transform=TransformInfo(clip.Transform) } : null
    };
    private static object? TransformInfo(GeneralTransform? transform)=>transform is null ? null : new
    {
        Type=transform.GetType().FullName,Origin=PointInfo(transform.TransformPoint(new Point(0,0))),
        UnitX=PointInfo(transform.TransformPoint(new Point(1,0))),UnitY=PointInfo(transform.TransformPoint(new Point(0,1)))
    };
    private static object PointInfo(Point point)=>new { X=Finite(point.X),Y=Finite(point.Y) };
    private static object Box(Rect rect)=>new { X=Finite(rect.X),Y=Finite(rect.Y),Width=Finite(rect.Width),Height=Finite(rect.Height) };
    private static double? Finite(double value)=>double.IsFinite(value) ? value : null;
    private static IEnumerable<Point> FigurePoints(PathFigure figure)
    {
        yield return figure.StartPoint;
        foreach(var segment in figure.Segments.Cast<LineSegment>()) yield return segment.Point;
    }
    private static Rect Bounds(Point[] points)
    {
        if (points.Length==0 || points.Any(point=>!double.IsFinite(point.X) || !double.IsFinite(point.Y)))
            throw new InvalidOperationException("No finite filled geometry points.");
        var left=points.Min(point=>point.X);var top=points.Min(point=>point.Y);
        return new Rect(left,top,points.Max(point=>point.X)-left,points.Max(point=>point.Y)-top);
    }
    private static bool Contains(Rect outer,Rect inner,double tolerance)=>inner.Left>=outer.Left-tolerance && inner.Top>=outer.Top-tolerance &&
        inner.Right<=outer.Right+tolerance && inner.Bottom<=outer.Bottom+tolerance;
    private static IEnumerable<DependencyObject> Descendants(DependencyObject root)
    {
        for(var i=0;i<VisualTreeHelper.GetChildrenCount(root);i++)
        {
            var child=VisualTreeHelper.GetChild(root,i);yield return child;
            foreach(var descendant in Descendants(child)) yield return descendant;
        }
    }
}
#endif
