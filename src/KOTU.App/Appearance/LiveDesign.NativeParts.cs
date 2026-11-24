using KOTU.Core.Appearance;
using KOTU.Ui;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Data;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using Windows.Foundation;

namespace KOTU.App.Appearance;

public sealed partial class LiveDesign
{
    private sealed partial class ElementState
    {
        private Grid? _checkVisual;
        private Grid? _checkHost;
        private IconSourceElement? _arrowVisual;
        private AnimatedIcon? _arrowOriginal;
        private object? _arrowTag;
        private readonly Dictionary<UIElement,RectangleGeometry?> _hiddenParts = [];
        private readonly List<Border> _thumbVisuals = [];
        private readonly List<(FrameworkElement Original, DesignSurface Visual, bool On, bool Knob)> _switchParts = [];
        private VisualStateGroup? _switchCommonStates;
        private FrameworkElement? _menuCheckOriginal;
        private object? _menuCheckTag;
        private Grid? _menuCheckVisual;
        private DesignSurface? _progressTrack;
        private readonly List<(Grid Host,Brush Brush)> _hitSurfaces=[];
        private ContentPresenter? _treePresenter;
        private readonly List<(TextBlock Original,IconSourceElement Visual,long TextToken,long SizeToken)> _treeGlyphs=[];

        private void ApplyTree(TreeViewItem tree,DesignDefinition d)
        {
            // Only the owner's native template parts qualify, never a nested content control.
            if (VisualTreeHelper.GetChildrenCount(tree)==0 || VisualTreeHelper.GetChild(tree,0) is not Grid { Name:"ContentPresenterGrid" } root ||
                root.Children.OfType<Grid>().FirstOrDefault(g=>g.Name=="MultiSelectGrid") is not { } row) return;
            if (_treePresenter is null && row.Children.OfType<ContentPresenter>().FirstOrDefault(p=>p.Name=="ContentPresenter") is { } presenter &&
                presenter.ReadLocalValue(ContentPresenter.VerticalContentAlignmentProperty)==DependencyProperty.UnsetValue)
            {
                // The native presenter centers its 20-DIP box but leaves the shorter label at its top.
                // Center inside that box too, retaining the compact row's height and authored bindings.
                presenter.VerticalContentAlignment=VerticalAlignment.Center;
                _treePresenter=presenter;
            }
            if (row.Children.OfType<Grid>().FirstOrDefault(g=>g.Name=="ExpandCollapseChevron") is not { } host) return;
            foreach(var original in host.Children.OfType<TextBlock>().Where(t=>t.Name is "CollapsedGlyph" or "ExpandedGlyph").ToArray())
            {
                var symbol=original.Name=="CollapsedGlyph" ? "Next" : "Down";
                var entry=_treeGlyphs.FirstOrDefault(pair=>ReferenceEquals(pair.Original,original));
                if (entry.Visual is null)
                {
                    // The native 12x12 TextBlock has Segoe-specific Padding=2. A centered icon peer
                    // uses the existing design geometry instead of inheriting that font's line box.
                    var visual=DesignIcons.Create(symbol,d,original.FontSize);
                    CopySlot(original,visual);
                    Bind(visual,FrameworkElement.WidthProperty,original,"Width");
                    Bind(visual,FrameworkElement.HeightProperty,original,"Height");
                    Bind(visual,IconElement.ForegroundProperty,original,"Foreground");
                    HideDrawing(original);
                    host.Children.Insert(host.Children.IndexOf(original)+1,visual);
                    var textToken=original.RegisterPropertyChangedCallback(TextBlock.TextProperty,(_,_)=>QueuePaint());
                    var sizeToken=original.RegisterPropertyChangedCallback(TextBlock.FontSizeProperty,(_,_)=>QueuePaint());
                    entry=(original,visual,textToken,sizeToken);
                    _treeGlyphs.Add(entry);
                }
                var glyph=CanonicalNativeGlyph(original.Text);
                if (DesignIconCatalog.TryResolveGlyph(glyph,out var currentSymbol)) symbol=currentSymbol;
                DesignIcons.Apply(entry.Visual,symbol,d,original.FontSize);
            }
        }

        private void EnsureHitSurface(Grid host)
        {
            // Drawing clips must not remove the last hit-testable pixel of an empty control.
            // Do not replace an existing binding/background owned by the native template.
            if (host.Background is not null || host.ReadLocalValue(Panel.BackgroundProperty)!=DependencyProperty.UnsetValue) return;
            var clear=DesignSurface.Brush(0);
            host.Background=clear; _hitSurfaces.Add((host,clear));
        }

        private void ApplySwitch(ToggleSwitch control, DesignDefinition d, DesignPalette p)
        {
            control.Foreground = DesignSurface.Brush(control.IsEnabled ? p.Foreground : p.Muted);
            control.CornerRadius = Radius(d,false);
            if (_switchParts.Count == 0)
            {
                var parts = Descendants(control).OfType<FrameworkElement>().ToArray();
                foreach (var original in parts.Where(e => e.Name is "OuterBorder" or "SwitchKnobBounds" or "SwitchKnobOff" or "SwitchKnobOn"))
                {
                    if (VisualTreeHelper.GetParent(original) is not Grid host) continue;
                    // The native SwitchKnob parent still performs drag/reposition animations. These
                    // peers follow only its drawings, including their pressed size and on/off fades.
                    var visual = new DesignSurface();
                    CopySlot(original,visual);
                    Bind(visual,FrameworkElement.WidthProperty,original,"Width");
                    Bind(visual,FrameworkElement.HeightProperty,original,"Height");
                    visual.RenderTransformOrigin = original.RenderTransformOrigin;
                    if (original.RenderTransform is CompositeTransform source)
                    {
                        var transform = new CompositeTransform();
                        BindingOperations.SetBinding(transform,CompositeTransform.ScaleXProperty,new Binding { Source=source,Path=new PropertyPath("ScaleX") });
                        BindingOperations.SetBinding(transform,CompositeTransform.ScaleYProperty,new Binding { Source=source,Path=new PropertyPath("ScaleY") });
                        visual.RenderTransform=transform;
                    }
                    HideDrawing(original);
                    original.SizeChanged += NativePartSizeChanged;
                    host.Children.Insert(host.Children.IndexOf(original)+1,visual);
                    _switchParts.Add((original,visual,original.Name is "SwitchKnobBounds" or "SwitchKnobOn",original.Name is "SwitchKnobOff" or "SwitchKnobOn"));
                }
                _switchCommonStates = parts.SelectMany(e=>VisualStateManager.GetVisualStateGroups(e)).FirstOrDefault(g=>g.Name=="CommonStates");
                if (_switchCommonStates is not null) _switchCommonStates.CurrentStateChanged += SwitchStateChanged;
            }
            var pressed = _switchCommonStates?.CurrentState?.Name == "Pressed";
            foreach (var (original,visual,on,knob) in _switchParts)
            {
                var color = !control.IsEnabled ? p.Muted : on ? p.OnAccent : p.Foreground;
                var face = knob ? color : !control.IsEnabled ? p.Card : on ? p.Accent : _hover ? p.Selection : p.Card;
                var border = !control.IsEnabled ? p.Muted : on ? p.Accent : _hover ? p.Accent : p.Border;
                var palette = p with { Foreground=knob ? color : border, Border=knob ? color : border };
                visual.Draw(d,palette,original.ActualWidth,original.ActualHeight,HighContrast,pressed,false,false,face,
                    d.Decoration is DesignDecoration.Rounded or DesignDecoration.Original ? (knob ? 7 : 10) : d.CheckRadius);
                if (knob && d.Decoration==DesignDecoration.Terminal)
                {
                    visual.Children.Clear();
                    visual.Children.Add(new TextBlock { Text=on ? "1" : "0",FontFamily=new FontFamily(d.FontFamily),FontSize=12,
                        Foreground=DesignSurface.Brush(color),Width=original.ActualWidth,Height=original.ActualHeight,TextAlignment=TextAlignment.Center });
                }
            }
        }

        private void NativePartSizeChanged(object sender, SizeChangedEventArgs args) => QueuePaint();
        private void SwitchStateChanged(object sender, VisualStateChangedEventArgs args) => Refresh();

        private void ApplyMenuCheck(Control control, DesignDefinition d)
        {
            if (_menuCheckVisual is null)
            {
                var original = Descendants(control).OfType<FrameworkElement>().FirstOrDefault(e=>e.Name=="CheckGlyph");
                if (original is null || VisualTreeHelper.GetParent(original) is not Grid host) return;
                _menuCheckOriginal=original; _menuCheckTag=original.Tag;
                original.Tag=DesignRuntime.IconScopeMarker;
                HideDrawing(original);
                _menuCheckVisual=new Grid { Width=12,Height=12,IsHitTestVisible=false,Tag=DesignRuntime.IconScopeMarker };
                CopySlot(original,_menuCheckVisual);
                host.Children.Insert(host.Children.IndexOf(original)+1,_menuCheckVisual);
            }
            _menuCheckVisual.Children.Clear();
            if (control is RadioMenuFlyoutItem)
            {
                if (d.Decoration==DesignDecoration.Terminal)
                {
                    var text=new TextBlock { Text="*",FontFamily=new FontFamily(d.FontFamily),FontSize=12,TextAlignment=TextAlignment.Center };
                    Bind(text,TextBlock.ForegroundProperty,_menuCheckOriginal!,"Foreground");
                    _menuCheckVisual.Children.Add(text);
                }
                else
                {
                    var dot=new Border { Width=7,Height=7,HorizontalAlignment=HorizontalAlignment.Center,VerticalAlignment=VerticalAlignment.Center,
                        CornerRadius=new CornerRadius(d.Decoration is DesignDecoration.Pixel or DesignDecoration.Frame or DesignDecoration.Editorial ? 0 : d.Decoration==DesignDecoration.Ink ? 2 : 4) };
                    Bind(dot,Border.BackgroundProperty,_menuCheckOriginal!,"Foreground");
                    _menuCheckVisual.Children.Add(dot);
                }
            }
            else
            {
                var mark=DesignIcons.Create("Check",d,12);
                Bind(mark,IconElement.ForegroundProperty,_menuCheckOriginal!,"Foreground");
                _menuCheckVisual.Children.Add(mark);
            }
        }

        private void ApplyProgress(ProgressBar control, DesignDefinition d, DesignPalette p)
        {
            // Keep the real indicators: their width, opacity, translation and error/paused color
            // animations belong to ProgressBar. A three-pixel indicator uses a small matching cap.
            control.Foreground=DesignSurface.Brush(control.IsEnabled ? p.Accent : p.Muted);
            control.Background=DesignSurface.Brush(p.Border);
            control.MinHeight=Math.Max(8,control.MinHeight);
            var radius=HighContrast ? 0 : d.Decoration is DesignDecoration.Rounded or DesignDecoration.Precision or DesignDecoration.Solid or DesignDecoration.Original ? 2 : 0;
            control.CornerRadius=new CornerRadius(radius);
            var track=Descendants(control).OfType<Rectangle>().FirstOrDefault(e=>e.Name=="ProgressBarTrack");
            if (track is null || VisualTreeHelper.GetParent(track) is not Grid host) return;
            track.RadiusX=track.RadiusY=radius;
            foreach (var indicator in host.Children.OfType<Rectangle>().Where(e=>e.Name.Contains("ProgressBarIndicator",StringComparison.Ordinal)))
            {
                indicator.Height=3;
                indicator.VerticalAlignment=VerticalAlignment.Center;
            }
            if (_progressTrack is null)
            {
                _progressTrack=new DesignSurface();
                // Canvas has no desired size; this ornament cannot enlarge an auto-sized row.
                host.Children.Insert(0,_progressTrack);
                if (!_sizeHook) { Element.SizeChanged+=SizeChanged; _sizeHook=true; }
            }
            // Leave space above/below the 3px moving fill for folded/stepped/ink edges. This keeps
            // those contours separate from the indicator and inside the native clipping bounds.
            _progressTrack.Draw(d,p with { Foreground=p.Border },host.ActualWidth,host.ActualHeight,HighContrast,panel:true);
        }

        private void ReleaseNativeParts()
        {
            foreach(var (original,visual,textToken,sizeToken) in _treeGlyphs)
            {
                original.UnregisterPropertyChangedCallback(TextBlock.TextProperty,textToken);
                original.UnregisterPropertyChangedCallback(TextBlock.FontSizeProperty,sizeToken);
                RemoveDecoration(visual);
            }
            _treeGlyphs.Clear();
            if (_treePresenter is not null && _treePresenter.ReadLocalValue(ContentPresenter.VerticalContentAlignmentProperty) is VerticalAlignment.Center)
                _treePresenter.ClearValue(ContentPresenter.VerticalContentAlignmentProperty);
            _treePresenter=null;
            if (_switchCommonStates is not null) _switchCommonStates.CurrentStateChanged-=SwitchStateChanged;
            _switchCommonStates=null;
            foreach (var (original,visual,_,_) in _switchParts) { original.SizeChanged-=NativePartSizeChanged; RemoveDecoration(visual); }
            _switchParts.Clear();
            RemoveDecoration(_menuCheckVisual); _menuCheckVisual=null;
            if (_menuCheckOriginal is not null) _menuCheckOriginal.Tag=_menuCheckTag;
            _menuCheckOriginal=null; _menuCheckTag=null;
            RemoveDecoration(_progressTrack); _progressTrack=null;
            foreach(var (host,brush) in _hitSurfaces)
                if (ReferenceEquals(host.Background,brush)) host.ClearValue(Panel.BackgroundProperty);
            _hitSurfaces.Clear();
        }

        private void ApplyScrollBar(ScrollBar control,DesignDefinition d,DesignPalette p)
        {
            // WinUI's native ScrollBarCornerRadius is 3, independent of the button radius.
            control.CornerRadius=new CornerRadius(d.Decoration == DesignDecoration.Original ? 3 :
                d.Decoration is DesignDecoration.Rounded or DesignDecoration.Precision ? 4 : 0);
            foreach(var track in Descendants(control).OfType<Rectangle>().Where(e=>e.Name is "HorizontalTrackRect" or "VerticalTrackRect").ToArray())
            {
                if (VisualTreeHelper.GetParent(track) is not Grid host) continue;
                if (!_chromeParts.TryGetValue(track,out var overlay))
                {
                    HideDrawing(track); overlay=new DesignOverlay(track,host); _chromeParts.Add(track,overlay);
                }
                overlay.Draw((surface,w,h)=>surface.Draw(d,p,w,h,HighContrast,fill:p.Card,
                    radius:d.Decoration == DesignDecoration.Original ? 3 : null));
            }
            foreach(var thumb in Descendants(control).OfType<Thumb>().ToArray())
            {
                // The native rectangle is also the thumb's sole hit surface. Keep it intact;
                // the narrow moving fill uses matching caps inside the shaped track.
                thumb.CornerRadius=control.CornerRadius;
            }
            // The two-pixel panning hint remains a simple line and retains all native fading.
            foreach(var hint in Descendants(control).OfType<Border>().Where(e=>e.Name is "HorizontalPanningThumb" or "VerticalPanningThumb"))
                hint.CornerRadius=control.CornerRadius;
        }

        private void ApplyCheck(ToggleButton control,DesignDefinition d,DesignPalette p,bool radio)
        {
            control.Foreground = DesignSurface.Brush(control.IsEnabled ? p.Foreground : p.Muted);
            control.CornerRadius = new CornerRadius(d.CheckRadius);
            var partName = radio ? "OuterEllipse" : "NormalRectangle";
            var outline = Descendants(control).OfType<FrameworkElement>().FirstOrDefault(e => e.Name == partName);
            if (outline is null || VisualTreeHelper.GetParent(outline) is not Grid host) return;
            EnsureHitSurface(host);
            if (_checkVisual is null || !ReferenceEquals(host,_checkHost))
            {
                _checkHost = host;
                foreach(var child in host.Children.OfType<FrameworkElement>())
                    if (child.Name is "NormalRectangle" or "CheckGlyph" or "OuterEllipse" or "CheckOuterEllipse" or "PressedCheckGlyph")
                        HideDrawing(child);
                _checkVisual = new Grid { Width = 20, Height = 20,
                    HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
                    IsHitTestVisible = false, Tag = DesignRuntime.IconScopeMarker };
                host.Children.Add(_checkVisual);
            }
            _checkVisual.Children.Clear();
            var selected = control.IsChecked != false;
            var specimenCheck = !radio && d.Decoration != DesignDecoration.Paper && !HighContrast;
            var line = !control.IsEnabled ? p.Muted : _hover || specimenCheck && selected ? p.Accent : p.Border;
            var fill = selected ? p.Accent : _hover ? p.Selection : p.Card;
            if (d.Decoration == DesignDecoration.Original && !HighContrast && !selected)
            {
                line = _hover ? p.Foreground : p.Muted;
                fill = _hover ? p.Selection : p.Background;
            }
            var ink = !control.IsEnabled ? p.Muted : selected ? p.OnAccent : p.Foreground;
            if (!control.IsEnabled) { fill = p.Card; p = p with { Foreground=p.Muted, Border=p.Muted, Accent=p.Muted }; }
            if (d.Decoration == DesignDecoration.Terminal)
            {
                _checkVisual.Children.Add(new TextBlock
                {
                    Text = radio ? (selected ? "(x)" : "( )") : control.IsChecked is true ? "[x]" : control.IsChecked is null ? "[-]" : "[ ]",
                    FontFamily = new FontFamily(d.FontFamily), FontSize = 11,
                    Foreground = DesignSurface.Brush(control.IsEnabled ? p.Foreground : p.Muted),
                    VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Center,
                });
                return;
            }
            if (radio) _checkVisual.Children.Add(new Border
            {
                Margin = new Thickness(1), Background = DesignSurface.Fill(d,p,fill,control.IsPressed,false,HighContrast),
                BorderBrush = DesignSurface.Brush(line), BorderThickness = new Thickness(d.BorderWidth),
                CornerRadius = radio && d.Decoration is not (DesignDecoration.Pixel or DesignDecoration.Frame or DesignDecoration.Terminal)
                    ? new CornerRadius(10) : new CornerRadius(d.CheckRadius),
            });
            if (!radio || d.Decoration is DesignDecoration.Pixel or DesignDecoration.Frame)
            {
                var surface = new DesignSurface();
                surface.Draw(d,p with { Border=line },20,20,HighContrast,control.IsPressed,false,
                    selected && !specimenCheck,fill,d.CheckRadius);
                _checkVisual.Children.Add(surface);
            }
            if (selected)
            {
                if (radio)
                    _checkVisual.Children.Add(new Border { Width = 8, Height = 8,
                        HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
                        Background = DesignSurface.Brush(ink), CornerRadius = new CornerRadius(d.Decoration is DesignDecoration.Pixel or DesignDecoration.Frame ? 0 : 4) });
                else
                {
                    var icon = DesignIcons.Create(control.IsChecked is null ? "Mixed" : "Check",d,14);
                    icon.Foreground = DesignSurface.Brush(ink); _checkVisual.Children.Add(icon);
                }
            }
        }

        private void ApplyArrow(Control control,DesignDefinition d)
        {
            if (_arrowVisual is null)
            {
                var original = Descendants(control).OfType<AnimatedIcon>().FirstOrDefault(e => e.Name == "DropDownGlyph");
                if (original is null || VisualTreeHelper.GetParent(original) is not Grid host) return;
                HideDrawing(original);
                _arrowOriginal = original; _arrowTag = original.Tag;
                original.Tag = DesignRuntime.IconScopeMarker;
                _arrowVisual = DesignIcons.Create("Down",d,12);
                CopySlot(original,_arrowVisual);
                Bind(_arrowVisual,IconElement.ForegroundProperty,original,"Foreground");
                host.Children.Add(_arrowVisual);
            }
            DesignIcons.Apply(_arrowVisual,"Down",d,12);
        }

        private void ApplySlider(Slider slider,DesignDefinition d,DesignPalette p)
        {
            slider.Foreground = DesignSurface.Brush(slider.IsEnabled ? p.Accent : p.Muted);
            slider.Background = DesignSurface.Brush(p.Border);
            // Native slider tokens: track 2, outer thumb 10, inner ellipse 12x12.
            // Only the drawing changes; dimensions, hit targets and native animations remain owned by the template.
            slider.CornerRadius = new CornerRadius(d.Decoration == DesignDecoration.Original ? 2 :
                d.Decoration is DesignDecoration.Rounded or DesignDecoration.Precision ? 4 : 0);
            foreach(var thumb in Descendants(slider).OfType<Thumb>().ToArray())
            {
                var inner = Descendants(thumb).OfType<Ellipse>().FirstOrDefault(e => e.Name == "SliderInnerThumb");
                if (inner is null) continue;
                if (VisualTreeHelper.GetParent(inner) is Border outer)
                    outer.CornerRadius = d.Decoration == DesignDecoration.Original ? new CornerRadius(10) : Radius(d,false);
                if (VisualTreeHelper.GetParent(thumb) is not Grid host) continue;
                var existing = _thumbVisuals.FirstOrDefault(v => ReferenceEquals(v.Tag,inner));
                if (existing is null)
                {
                    // Keep the native thumb and its animation target. The peer follows its fill,
                    // opacity and transform, so dragging, capture, focus and disabled states remain native.
                    HideDrawing(inner);
                    existing = new Border { Width = inner.Width, Height = inner.Height, IsHitTestVisible = false, Tag = inner,
                        HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
                        RenderTransformOrigin = new Point(.5,.5) };
                    Grid.SetRow(existing,Grid.GetRow(thumb)); Grid.SetColumn(existing,Grid.GetColumn(thumb));
                    Grid.SetRowSpan(existing,Grid.GetRowSpan(thumb)); Grid.SetColumnSpan(existing,Grid.GetColumnSpan(thumb));
                    Bind(existing,Border.BackgroundProperty,inner,"Fill");
                    Bind(existing,UIElement.OpacityProperty,inner,"Opacity");
                    if (inner.RenderTransform is CompositeTransform sourceTransform)
                    {
                        var transform = new CompositeTransform();
                        BindingOperations.SetBinding(transform,CompositeTransform.ScaleXProperty,new Binding { Source = sourceTransform, Path = new PropertyPath("ScaleX") });
                        BindingOperations.SetBinding(transform,CompositeTransform.ScaleYProperty,new Binding { Source = sourceTransform, Path = new PropertyPath("ScaleY") });
                        existing.RenderTransform = transform;
                    }
                    Bind(existing,UIElement.VisibilityProperty,thumb,"Visibility");
                    DesignScope.ProtectContent(existing);
                    host.Children.Add(existing); _thumbVisuals.Add(existing);
                }
                existing.CornerRadius = new CornerRadius(d.Decoration == DesignDecoration.Original ? 6 :
                    d.Decoration is DesignDecoration.Rounded or DesignDecoration.Precision or DesignDecoration.Solid ? 7 :
                    d.Decoration == DesignDecoration.Ink ? 3 : 0);
                existing.BorderBrush = DesignSurface.Brush(p.Foreground);
                existing.BorderThickness = new Thickness(d.Decoration is DesignDecoration.Frame or DesignDecoration.Mechanical ? 1 : 0);
            }
        }

        private static void Bind(FrameworkElement target,DependencyProperty property,object source,string path)
            => target.SetBinding(property,new Binding { Source = source, Path = new PropertyPath(path), Mode = BindingMode.OneWay });
        private void HideDrawing(UIElement element)
        {
            TraceLifecycle("HideDrawing before",part:element);
            if (!_hiddenParts.ContainsKey(element)) _hiddenParts[element] = element.Clip;
            element.Clip = new RectangleGeometry { Rect = new Rect(0,0,0,0) };
            TraceLifecycle("HideDrawing after",part:element);
        }
    }
}
