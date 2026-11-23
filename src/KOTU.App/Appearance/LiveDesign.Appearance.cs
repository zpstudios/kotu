using KOTU.Core.Appearance;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;

namespace KOTU.App.Appearance;

public sealed partial class LiveDesign
{
    private sealed partial class ElementState
    {
        private DesignSurface? _surface;
        private DesignOverlay? _surfaceOverlay;
        private bool _sizeHook;
        private string? _collapsedGlyph, _expandedGlyph;
        private bool InToolbar => HasAncestor(e => e.Name is "BottomBar" or "TransportBar" or "StatusBar" || DesignScope.GetRole(e) == DesignElementRole.Toolbar);
        private SelectorItem? OwnerSelector
        {
            get
            {
                for(var parent=VisualTreeHelper.GetParent(Element);parent is not null;parent=VisualTreeHelper.GetParent(parent))
                    if (parent is SelectorItem selector) return selector;
                return null;
            }
        }
        private bool HasAncestor(Func<FrameworkElement, bool> test)
        {
            DependencyObject? node = Element;
            for (var depth=0;node is not null && depth<160;depth++,node=VisualTreeHelper.GetParent(node))
                if(node is FrameworkElement element && test(element)) return true;
            return false;
        }

        private void Apply(DesignDefinition d, DesignPalette p)
        {
            var role = DesignScope.GetRole(Element);
            if (Element is FontIcon icon)
            {
                var drawing = _glyph!.Render(d);
                if (drawing.Known)
                {
                    icon.FontFamily = new FontFamily(DesignIconCatalog.FontFamily(d));
                    icon.Glyph = drawing.Glyph;
                    var size = DesignScope.IconSize(_fontSize,d);
                    icon.FontSize = size * drawing.Scale;
                    icon.IsTextScaleFactorEnabled = false;
                }
                else { icon.FontFamily = _originalFont!; icon.FontSize = _fontSize; }
                return;
            }
            if (Element is TextBlock text)
            {
                // FontIcon owns its native text's glyph, font, size and state colors.
                // Terminal ASCII must not acquire a local body font that masks the next icon font.
                if (HasAncestor(element => element is FontIcon)) return;
                if (_glyph is not null)
                {
                    var drawing=_glyph.Render(d);
                    if (drawing.Known)
                    {
                        text.FontFamily=new FontFamily(DesignIconCatalog.FontFamily(d));
                        text.Text=drawing.Glyph; text.FontSize=DesignScope.IconSize(_fontSize,d)*drawing.Scale;
                        text.IsTextScaleFactorEnabled=false;
                        return;
                    }
                }
                if (text.Name is "CollapsedGlyph" or "ExpandedGlyph")
                {
                    text.FontFamily = new FontFamily(DesignIconCatalog.FontFamily(d));
                    return; // Owner glyph properties keep the template bindings and visibility intact.
                }
                if (text.Text.Any(c => c is >= '\uE000' and <= '\uF8FF') ||
                    (_originalFont?.Source.Contains("Symbol",StringComparison.OrdinalIgnoreCase) ?? false)) return;
                // Native presenters inherit their owner's scaled typography. Do not scale it twice.
                if (!_nativeText)
                {
                    var weight=AuthoredWeight;
                    var size=AuthoredSize;
                    var heading = role == DesignElementRole.Heading || size >= 18 || weight >= 600;
                    var filename = role == DesignElementRole.FileName || DesignScope.IsFileName(text.Name);
                    var family = filename && d.Decoration == DesignDecoration.Editorial
                        ? "Georgia, ms-appx:///Assets/Fonts/NanumMyeongjo-Regular.ttf#NanumMyeongjo, Segoe UI"
                        : heading ? d.HeadingFontFamily : d.FontFamily;
                    text.FontFamily = new FontFamily(family);
                    if (!_inheritsFontSize) text.FontSize = DesignScope.FontSize(size,d);
                    if (!_inheritsFontWeight)
                        text.FontWeight = new Windows.UI.Text.FontWeight { Weight = DesignScope.FontWeight(weight,d) };
                }
                // Preserve semantic colors and state-dependent text inherited from native controls.
                var cardText = HasAncestor(e => DesignScope.GetRole(e) == DesignElementRole.Card);
                if (cardText || !HasAncestor(e => e is ButtonBase or MenuFlyoutItemBase or SelectorItem or TextBox or ComboBox or ToggleSwitch))
                {
                    if (role==DesignElementRole.NeutralText || text.ReadLocalValue(TextBlock.ForegroundProperty) == DependencyProperty.UnsetValue || _neutralText)
                    {
                        _neutralText = true;
                        var owner=OwnerSelector;
                        text.Foreground = DesignSurface.Brush(owner is { IsEnabled:false } ? p.Muted :
                            HighContrast && owner is { IsSelected:true } ? p.OnAccent :
                            InToolbar && d.Decoration == DesignDecoration.Solid && !HighContrast ? p.Background : p.Foreground);
                    }
                }
                return;
            }
            if (Element is Control control)
            {
                if (control is RepeatButton && HasAncestor(e=>e is ScrollBar)) return;
                if (UsesControlTypography(control))
                {
                    var weight=AuthoredWeight;
                    var size=AuthoredSize;
                    control.FontFamily = new FontFamily(size >= 18 || weight >= 600 ? d.HeadingFontFamily : d.FontFamily);
                    if (!_inheritsFontSize) control.FontSize = DesignScope.FontSize(size,d);
                    if (!_inheritsFontWeight)
                        control.FontWeight = new Windows.UI.Text.FontWeight { Weight = DesignScope.FontWeight(weight,d) };
                }
                if (control is TreeViewItem tree)
                {
                    _collapsedGlyph ??= tree.CollapsedGlyph;
                    _expandedGlyph ??= tree.ExpandedGlyph;
                    tree.CollapsedGlyph = d.Decoration == DesignDecoration.Terminal ? ">" : _collapsedGlyph;
                    tree.ExpandedGlyph = d.Decoration == DesignDecoration.Terminal ? "v" : _expandedGlyph;
                    foreach(var part in Descendants(tree).OfType<TextBlock>().Where(t => t.Name is "CollapsedGlyph" or "ExpandedGlyph"))
                        part.FontFamily = new FontFamily(DesignIconCatalog.FontFamily(d));
                }
                if (control is CheckBox check) { ApplyCheck(check,d,p,false); return; }
                if (control is RadioButton radio) { ApplyCheck(radio,d,p,true); return; }
                if (control is Slider slider) { ApplySlider(slider,d,p); return; }
                if (control is ScrollBar scrollBar) { ApplyScrollBar(scrollBar,d,p); return; }
                if (control is ToggleSwitch toggleSwitch) { ApplySwitch(toggleSwitch,d,p); return; }
                if (control is ProgressBar progress) { ApplyProgress(progress,d,p); return; }
                if (control is ProgressRing ring) { ring.Foreground=DesignSurface.Brush(ring.IsEnabled ? p.Accent : p.Muted); return; }
                if (control is ToggleMenuFlyoutItem or RadioMenuFlyoutItem) ApplyMenuCheck(control,d);
                if (control is ComboBox or TextBox or PasswordBox) { ApplyInput(control,d,p); return; }
                if (control is ContentDialog dialog) { ApplyDialog(dialog,d,p); return; }
                if (control is ButtonBase or DropDownButton or ComboBox or TextBox or PasswordBox)
                {
                    ApplyControl(control,d,p);
                    if (control is DropDownButton or ComboBox) ApplyArrow(control,d);
                }
                else if (control is SelectorItem selector)
                    ApplySelector(selector,d,p);
                else if (control is MenuFlyoutPresenter or FlyoutPresenter or ToolTip)
                {
                    control.Background = DesignSurface.Fill(d,p,p.Card,false,true,HighContrast);
                    control.Foreground = DesignSurface.Brush(p.Foreground);
                    control.BorderBrush = DesignSurface.Brush(p.Border);
                    control.CornerRadius = Radius(d,true);
                    EnsureSurface();
                    DrawSurface(d,p,true);
                    if (_surface is not null) { control.Background=DesignSurface.Brush(0); control.BorderBrush=DesignSurface.Brush(0); }
                }
                else if (control is MenuFlyoutItemBase)
                    ApplyMenu(control,d,p);
            }
            if (Element is Grid grid)
            {
                if (grid.Name is "RootLayout" or "ExplorerHost") grid.Background = DesignSurface.Brush(p.Background);
                if (grid.Name == "BottomBar" || role == DesignElementRole.Toolbar)
                {
                    grid.Background = DesignSurface.Brush(d.Decoration == DesignDecoration.Solid && !HighContrast ? p.Foreground : p.Panel);
                    EnsureSurface(); DrawSurface(d,p,true);
                    if (_surface is not null) grid.Background = DesignSurface.Brush(0);
                }
                if (role == DesignElementRole.Card)
                {
                    grid.Background = DesignSurface.Fill(d,p,p.Card,false,false,HighContrast);
                    grid.CornerRadius = Radius(d,false);
                    EnsureSurface(); DrawSurface(d,p,false);
                    if (_surface is not null) grid.Background = DesignSurface.Brush(0);
                }
            }
            if (Element is Border border)
            {
                var panel = role == DesignElementRole.Panel || border.Name is "PanelBackdrop" or "S4CenterBackdrop" or "PopupBorder";
                var selection = role == DesignElementRole.Selection || border.Name == "TileSelectionOutline";
                if (panel || role == DesignElementRole.Card)
                {
                    border.Background = DesignSurface.Fill(d,p,panel ? p.Panel : p.Card,false,panel,HighContrast);
                    border.CornerRadius = Radius(d,panel);
                    EnsureSurface(); DrawSurface(d,p,panel);
                    if (_surface is not null) border.Background = DesignSurface.Brush(0);
                    if (_surface is not null) border.BorderBrush = DesignSurface.Brush(0);
                }
                else if (border.Name is "PanelBorder" or "OverlayBorder" or "PathBarHost")
                {
                    border.CornerRadius = Radius(d,true);
                    border.BorderBrush = DesignSurface.Brush(p.Border);
                    EnsureSurface(); DrawSurface(d,p,true);
                    if (_surface is not null) border.BorderBrush=DesignSurface.Brush(0);
                }
                else if (selection)
                {
                    border.BorderBrush = DesignSurface.Brush(p.Accent);
                    border.CornerRadius = Radius(d,false);
                    EnsureSurface(); DrawSurface(d,p,false,true);
                    if (_surface is not null) border.BorderBrush=DesignSurface.Brush(0);
                }
                else if (border.Name.Length == 0 && VisualTreeHelper.GetParent(border) is Grid { Name: "BottomBar" })
                    border.Background = DesignSurface.Brush(0); // The toolbar's profile surface supplies its face.
            }
        }
        private bool _neutralText;

        private void ApplyControl(Control control, DesignDefinition d, DesignPalette p)
        {
            var pressed = control is ButtonBase { IsPressed: true };
            var selected = control is ToggleButton { IsChecked: true };
            var inverse = InToolbar && d.Decoration == DesignDecoration.Solid && !HighContrast;
            var fill = selected ? p.Accent : _hover || pressed ? p.Selection : inverse ? p.Foreground : p.Card;
            var foreground = selected || HighContrast && (_hover || pressed) ? p.OnAccent : inverse ? p.Background : p.Foreground;
            if (!control.IsEnabled) { foreground = p.Muted; fill = p.Card; }
            control.Foreground = DesignSurface.Brush(foreground);
            control.Background = DesignSurface.Fill(d,p,fill,pressed,false,HighContrast);
            if (DesignScope.GetRole(control) == DesignElementRole.PreviewChoice)
            {
                EnsureSurface();
                // Its template border is the picker's pending-choice marker, independent of the applied design.
                _surfaceOverlay?.Draw((surface,w,h)=>surface.Draw(d,p with { Border=0, Foreground=0 },w,h,HighContrast,fill:fill));
                return;
            }
            control.BorderBrush = DesignSurface.Brush(HighContrast ? p.Foreground : d.Decoration is DesignDecoration.Frame or DesignDecoration.Ink or DesignDecoration.Pixel ? p.Foreground : p.Border);
            control.CornerRadius = Radius(d,false);
            // The ornament owns stroke weight. Keep the original content inset so an 18px
            // icon's native line box still fits in the fixed 32px button (including Fit halves).
            EnsureSurface(); DrawSurface(d,p,false,selected);
            if (_surfaceBacking) control.BorderBrush = DesignSurface.Brush(0);
        }

        private static CornerRadius Radius(DesignDefinition d,bool panel) => d.Decoration == DesignDecoration.Ink
            ? new CornerRadius(7,3,9,2) : new CornerRadius(panel ? d.PanelRadius : d.ControlRadius);

        private void EnsureSurface()
        {
            if (!_sizeHook) { Element.SizeChanged += SizeChanged; _sizeHook = true; }
            if (_surface is not null) return;
            Grid? host = Element as Grid;
            if (host is null && Element is Border { Child: Grid child }) host = child;
            if (host is null && Element is Border { Name:"PopupBorder",Child:ScrollViewer scroll })
                host=Descendants(scroll).OfType<Grid>().FirstOrDefault();
            if (host is null && Element is Control control)
                host = Descendants(control).OfType<Grid>().FirstOrDefault(g => g.Name is "KotuControlRoot" or "KotuPopupRoot" or "KotuSelectorRoot");
            if (host is not null)
            {
                var source=Element is Border { Child:Grid } ? Element : host;
                _surfaceOverlay=new DesignOverlay(source,host);
                _surfaceBacking = Element is Control or Grid || Element is Border && (DesignScope.GetRole(Element) is DesignElementRole.Card or DesignElementRole.Panel || Element.Name=="PopupBorder");
            }
            else if (VisualTreeHelper.GetParent(Element) is Grid parent)
            {
                _surfaceOverlay=new DesignOverlay(Element,parent,Element.Name=="TileSelectionOutline" || DesignScope.GetRole(Element)==DesignElementRole.Selection);
                _surfaceBacking = Element.Name is "PanelBackdrop" or "S4CenterBackdrop" || DesignScope.GetRole(Element) == DesignElementRole.Panel;
            }
            _surface=_surfaceOverlay?.Surface;
        }
        private bool _surfaceBacking;
        private void DrawSurface(DesignDefinition d,DesignPalette p,bool panel,bool selected=false)
        {
            if (_surface is null) return;
            var fill = panel ? p.Panel : selected ? p.Accent : _hover || Element is ButtonBase { IsPressed:true } ? p.Selection : p.Card;
            if (DesignScope.GetRole(Element)==DesignElementRole.Card && OwnerSelector is { IsSelected:true }) { fill=p.Selection; selected=true; }
            if (InToolbar && d.Decoration == DesignDecoration.Solid && !HighContrast && !selected) fill = p.Foreground;
            if (Element is Control { IsEnabled: false }) { fill = p.Card; p = p with { Foreground=p.Muted, Border=p.Muted, Accent=p.Muted }; }
            _surfaceOverlay?.Draw((surface,w,h)=>surface.Draw(d,p,w,h,HighContrast,Element is ButtonBase { IsPressed:true },panel,selected,_surfaceBacking ? fill : null));
        }
        private bool _paintQueued;
        private void SizeChanged(object sender,SizeChangedEventArgs e) => QueuePaint();
        private void QueuePaint()
        {
            if (_paintQueued || !_loaded) return;
            _paintQueued=true;
            Element.DispatcherQueue.TryEnqueue(()=>{ _paintQueued=false; if (_loaded) Refresh(); });
        }

        private void ReleaseParts()
        {
            TraceLifecycle("ReleaseParts enter");
            ReleaseNativeParts();
            ReleaseChromeParts();
            if (_sizeHook) Element.SizeChanged -= SizeChanged;
            _sizeHook = false;
            _surfaceOverlay?.Dispose(); _surfaceOverlay=null; _surface=null; _surfaceBacking=false;
            RemoveDecoration(_checkVisual); _checkVisual = null; _checkHost = null;
            RemoveDecoration(_arrowVisual); _arrowVisual = null;
            if (_arrowOriginal is not null) _arrowOriginal.Tag = _arrowTag;
            _arrowOriginal = null; _arrowTag = null;
            foreach(var part in _thumbVisuals) RemoveDecoration(part);
            _thumbVisuals.Clear();
            foreach(var (part,clip) in _hiddenParts)
            {
                TraceLifecycle("ReleaseParts restore Clip before",part:part);
                part.Clip = clip;
                TraceLifecycle("ReleaseParts restore Clip after",part:part);
            }
            _hiddenParts.Clear();
            TraceLifecycle("ReleaseParts exit");
        }
        private static void RemoveDecoration(FrameworkElement? element)
        {
            if (element is not null && VisualTreeHelper.GetParent(element) is Panel panel && panel.Children.Contains(element)) panel.Children.Remove(element);
        }

        private static void CopySlot(FrameworkElement original,FrameworkElement decoration)
        {
            decoration.Margin = original.Margin;
            decoration.HorizontalAlignment = original.HorizontalAlignment;
            decoration.VerticalAlignment = original.VerticalAlignment;
            Grid.SetRow(decoration,Grid.GetRow(original)); Grid.SetColumn(decoration,Grid.GetColumn(original));
            Grid.SetRowSpan(decoration,Grid.GetRowSpan(original)); Grid.SetColumnSpan(decoration,Grid.GetColumnSpan(original));
            Bind(decoration,UIElement.VisibilityProperty,original,"Visibility");
            Bind(decoration,UIElement.OpacityProperty,original,"Opacity");
            Bind(decoration,FrameworkElement.MarginProperty,original,"Margin");
            Bind(decoration,FrameworkElement.HorizontalAlignmentProperty,original,"HorizontalAlignment");
            Bind(decoration,FrameworkElement.VerticalAlignmentProperty,original,"VerticalAlignment");
        }
        private static IEnumerable<DependencyObject> Descendants(DependencyObject root)
        {
            var pending = new Stack<DependencyObject>();
            for(var i=VisualTreeHelper.GetChildrenCount(root)-1;i>=0;i--) pending.Push(VisualTreeHelper.GetChild(root,i));
            while(pending.TryPop(out var node))
            {
                if (node is FrameworkElement element && IsBoundary(element)) continue;
                yield return node;
                for(var i=VisualTreeHelper.GetChildrenCount(node)-1;i>=0;i--) pending.Push(VisualTreeHelper.GetChild(node,i));
            }
        }
    }
}
