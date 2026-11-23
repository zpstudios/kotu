using KOTU.Core.Appearance;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Data;
using Windows.Foundation;

namespace KOTU.App.Appearance;

/// <summary>A paint-only layer. Its source owns layout, content, input and all state.</summary>
internal sealed class DesignOverlay : Panel, IDisposable
{
    private readonly FrameworkElement _source;
    private readonly Grid _host;
    private readonly bool _inside;
    private readonly bool _descendantHost;
    private readonly Border? _borderSource;
    private readonly List<(DependencyProperty Property,long Token)> _tokens=[];
    private Action<double,double>? _draw;
    private double _drawWidth=-1, _drawHeight=-1;
    private bool _drawQueued, _placementQueued, _disposed;
    internal DesignSurface Surface { get; }=new();
    internal FrameworkElement Source=>_source;

    internal DesignOverlay(FrameworkElement source, Grid host, bool above=false, bool descendantHost=false)
    {
        _source=source; _host=host;
        _descendantHost=descendantHost;
        _borderSource=source is Border border && ReferenceEquals(border.Child,host) ? border : null;
        _inside=ReferenceEquals(source,host) || _borderSource is not null || descendantHost;
        Tag=DesignRuntime.IconScopeMarker; IsHitTestVisible=false;
        HorizontalAlignment=HorizontalAlignment.Stretch; VerticalAlignment=VerticalAlignment.Stretch;
        Children.Add(Surface);
        Canvas.SetZIndex(this,_inside ? -1 : Canvas.GetZIndex(source));
        if (!_inside)
        {
            SetBinding(VisibilityProperty,new Binding { Source=source,Path=new PropertyPath("Visibility") });
            SetBinding(OpacityProperty,new Binding { Source=source,Path=new PropertyPath("Opacity") });
            foreach(var property in new[] { Grid.RowProperty,Grid.ColumnProperty,Grid.RowSpanProperty,Grid.ColumnSpanProperty,
                MarginProperty,HorizontalAlignmentProperty,VerticalAlignmentProperty })
                _tokens.Add((property,source.RegisterPropertyChangedCallback(property,(_,_)=>{ CopyCell(); InvalidateArrange(); })));
        }
        CopyCell();
        source.SizeChanged+=SourceSizeChanged;
        if (_descendantHost)
        {
            host.SizeChanged+=HostSizeChanged;
            Loaded+=OverlayLoaded;
        }
        // Keep the source's stacking position; a backdrop must not sink behind an existing video.
        if (_inside || above) host.Children.Add(this);
        else host.Children.Insert(Math.Max(0,host.Children.IndexOf(source)),this);
    }

    private void OverlayLoaded(object sender,RoutedEventArgs args)=>QueuePlacement();
    private void HostSizeChanged(object sender,SizeChangedEventArgs args)
    {
        InvalidateArrange();
        QueuePlacement();
    }
    private void QueuePlacement()
    {
        if (!_descendantHost || _placementQueued || _disposed) return;
        _placementQueued=true;
        DispatcherQueue.TryEnqueue(()=>
        {
            _placementQueued=false;
            if (!_disposed) InvalidateArrange();
        });
    }

    internal void Draw(Action<DesignSurface,double,double> draw)
    {
        _draw=(w,h)=>draw(Surface,w,h);
        _draw(_source.ActualWidth,_source.ActualHeight);
        _drawWidth=_source.ActualWidth; _drawHeight=_source.ActualHeight;
        InvalidateArrange();
    }

    private void CopyCell()
    {
        Grid.SetRow(this,_inside ? 0 : Grid.GetRow(_source));
        Grid.SetColumn(this,_inside ? 0 : Grid.GetColumn(_source));
        Grid.SetRowSpan(this,_inside ? Math.Max(1,_host.RowDefinitions.Count) : Grid.GetRowSpan(_source));
        Grid.SetColumnSpan(this,_inside ? Math.Max(1,_host.ColumnDefinitions.Count) : Grid.GetColumnSpan(_source));
    }

    private void SourceSizeChanged(object sender,SizeChangedEventArgs args)
    {
        InvalidateArrange();
        QueuePlacement();
        if (_drawQueued || _disposed) return;
        _drawQueued=true;
        DispatcherQueue.TryEnqueue(()=>
        {
            _drawQueued=false;
            if (_disposed) return;
            var width=_source.ActualWidth; var height=_source.ActualHeight;
            if (width==_drawWidth && height==_drawHeight) return;
            _drawWidth=width; _drawHeight=height;
            _draw?.Invoke(width,height);
        });
    }
    protected override Size MeasureOverride(Size availableSize)
    {
        Surface.Measure(new Size(_source.ActualWidth,_source.ActualHeight));
        return new Size(0,0);
    }
    protected override Size ArrangeOverride(Size finalSize)
    {
        var width=_source.ActualWidth; var height=_source.ActualHeight;
        if (_descendantHost)
        {
            // The native item presenter owns padding, check space, clipping and content layout.
            // This layer's own offset is not committed during its first ArrangeOverride. The
            // host is already arranging us across its whole content area (row/column span), so
            // map to that host and subtract its explicit content inset instead of using `this`.
            var origin=_source.TransformToVisual(_host).TransformPoint(new Point(0,0));
            Surface.Arrange(new Rect(origin.X-_host.Padding.Left-_host.BorderThickness.Left,
                origin.Y-_host.Padding.Top-_host.BorderThickness.Top,width,height));
            return finalSize;
        }
        var margin=_inside ? new Thickness(0) : _source.Margin;
        var availableWidth=Math.Max(0,finalSize.Width-margin.Left-margin.Right);
        var availableHeight=Math.Max(0,finalSize.Height-margin.Top-margin.Bottom);
        var x=margin.Left+(_inside ? -_host.Padding.Left-_host.BorderThickness.Left : _source.HorizontalAlignment==HorizontalAlignment.Left ? 0 :
            _source.HorizontalAlignment==HorizontalAlignment.Right ? availableWidth-width : (availableWidth-width)/2);
        var y=margin.Top+(_inside ? -_host.Padding.Top-_host.BorderThickness.Top : _source.VerticalAlignment==VerticalAlignment.Top ? 0 :
            _source.VerticalAlignment==VerticalAlignment.Bottom ? availableHeight-height : (availableHeight-height)/2);
        if (_borderSource is { } border)
        {
            // The Grid starts inside the Border's padding, stroke and its own layout slot.
            // Paint the original Border bounds, without asking this decoration for more space.
            var innerWidth=Math.Max(0,width-border.Padding.Left-border.Padding.Right-border.BorderThickness.Left-border.BorderThickness.Right-_host.Margin.Left-_host.Margin.Right);
            var innerHeight=Math.Max(0,height-border.Padding.Top-border.Padding.Bottom-border.BorderThickness.Top-border.BorderThickness.Bottom-_host.Margin.Top-_host.Margin.Bottom);
            x-=border.Padding.Left+border.BorderThickness.Left+_host.Margin.Left+
                (_host.HorizontalAlignment==HorizontalAlignment.Left ? 0 : _host.HorizontalAlignment==HorizontalAlignment.Right ? innerWidth-_host.ActualWidth : (innerWidth-_host.ActualWidth)/2);
            y-=border.Padding.Top+border.BorderThickness.Top+_host.Margin.Top+
                (_host.VerticalAlignment==VerticalAlignment.Top ? 0 : _host.VerticalAlignment==VerticalAlignment.Bottom ? innerHeight-_host.ActualHeight : (innerHeight-_host.ActualHeight)/2);
        }
        Surface.Arrange(new Rect(x,y,width,height));
        return finalSize;
    }
    public void Dispose()
    {
        _disposed=true;
        _source.SizeChanged-=SourceSizeChanged;
        if (_descendantHost)
        {
            _host.SizeChanged-=HostSizeChanged;
            Loaded-=OverlayLoaded;
        }
        foreach(var (property,token) in _tokens) _source.UnregisterPropertyChangedCallback(property,token);
        _tokens.Clear();
        if (_host.Children.Contains(this)) _host.Children.Remove(this);
        _draw=null;
    }
}
