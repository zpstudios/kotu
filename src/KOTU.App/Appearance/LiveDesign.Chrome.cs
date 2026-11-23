using KOTU.Core.Appearance;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Media;

namespace KOTU.App.Appearance;

public sealed partial class LiveDesign
{
    private sealed partial class ElementState
    {
        private readonly Dictionary<FrameworkElement,DesignOverlay> _chromeParts=[];
        private readonly List<VisualStateGroup> _nativeGroups=[];
        private bool _nativeGroupsFound;
        private Grid? _selectorHost;
        private readonly Dictionary<FrameworkElement,long> _selectorOpacityNodes=[];
        private bool NativeHot=>_hover || _nativeGroups.Any(g=>g.CurrentState?.Name.Contains("PointerOver",StringComparison.Ordinal)==true || g.CurrentState?.Name=="SubMenuOpened");
        private bool NativePressed=>_nativeGroups.Any(g=>g.CurrentState?.Name.Contains("Pressed",StringComparison.Ordinal)==true);

        private void EnsureNativeGroups(Control control)
        {
            if (_nativeGroupsFound) return;
            foreach(var element in Descendants(control).OfType<FrameworkElement>())
            {
                var groups=VisualStateManager.GetVisualStateGroups(element);
                if (groups.Count==0) continue;
                foreach(var group in groups) { _nativeGroups.Add(group); group.CurrentStateChanged+=NativeStateChanged; }
                _nativeGroupsFound=true;
                break;
            }
        }
        private void NativeStateChanged(object sender,VisualStateChangedEventArgs args)=>RefreshSelection();

        private void ApplyInput(Control control,DesignDefinition d,DesignPalette p)
        {
            EnsureNativeGroups(control);
            var focused=control.FocusState!=FocusState.Unfocused;
            control.Foreground=DesignSurface.Brush(control.IsEnabled ? p.Foreground : p.Muted);
            control.Background=DesignSurface.Brush(p.Card);
            control.BorderBrush=DesignSurface.Brush(p.Border);
            control.CornerRadius=Radius(d,false);
            foreach(var part in Descendants(control).OfType<Border>().Where(e=>e.Child is null &&
                (e.Name is "BorderElement" or "Background" or "HighlightBackground" or "DropDownOverlay")).ToArray())
            {
                if (VisualTreeHelper.GetParent(part) is not Grid host) continue;
                EnsureHitSurface(host);
                if (!_chromeParts.TryGetValue(part,out var overlay))
                {
                    // These four named parts are drawing-only peers of input/content, never owners.
                    HideDrawing(part); overlay=new DesignOverlay(part,host); _chromeParts.Add(part,overlay);
                }
                var highlight=part.Name=="HighlightBackground";
                var edge=!control.IsEnabled ? p.Muted : focused || NativeHot ? p.Accent : p.Border;
                var palette=p with { Border=edge,Foreground=edge };
                overlay.Draw((surface,w,h)=>surface.Draw(d,palette,w,h,HighContrast,NativePressed,selected:focused || highlight,
                    fill:highlight ? null : HighContrast ? p.Background : NativeHot && !focused ? p.Selection : p.Card));
            }
            if (control is ComboBox) ApplyArrow(control,d);
        }

        private void ApplyMenu(Control control,DesignDefinition d,DesignPalette p)
        {
            EnsureNativeGroups(control);
            var active=NativeHot || NativePressed || control.FocusState!=FocusState.Unfocused;
            control.Foreground=DesignSurface.Brush(!control.IsEnabled ? p.Muted : HighContrast && active ? p.OnAccent : p.Foreground);
            control.Background=DesignSurface.Brush(0);
            control.BorderBrush=DesignSurface.Brush(0);
            control.CornerRadius=Radius(d,false);
            var root=Descendants(control).OfType<Grid>().FirstOrDefault(g=>g.Name=="LayoutRoot");
            if (root is null) return;
            if (!_chromeParts.TryGetValue(root,out var overlay)) { overlay=new DesignOverlay(root,root); _chromeParts.Add(root,overlay); }
            overlay.Draw((surface,w,h)=>surface.Draw(d,p,w,h,HighContrast,NativePressed,selected:active,
                fill:active ? p.Selection : null));
        }

        private void ApplySelector(SelectorItem control,DesignDefinition d,DesignPalette p)
        {
            EnsureNativeGroups(control);
            control.CornerRadius=Radius(d,false);
            control.Background=DesignSurface.Brush(0); control.BorderBrush=DesignSurface.Brush(0);
            var selected=control.IsSelected;
            var active=selected || NativeHot || NativePressed;
            var clear=DesignSurface.Brush(0);
            var nativePresenter=control is ListViewItem or GridViewItem;
            var root=nativePresenter ? SelectorContentHost(control) :
                Descendants(control).OfType<Grid>().FirstOrDefault(g=>g.Name=="ContentPresenterGrid");
            if (!ReferenceEquals(_selectorHost,root))
            {
                if (_selectorHost is { } old && _chromeParts.Remove(old,out var previous)) previous.Dispose();
                _selectorHost=root;
            }
            // A native presenter cannot be wrapped. Decorate only an exact content host, never
            // an arbitrary nested grid (for example a thumbnail). Unsupported content retains
            // native state brushes, including when a recycled container changes content kind.
            var stateFill=root is null ? DesignSurface.Brush(p.Selection) : clear;
            var stateBorder=root is null ? DesignSurface.Brush(p.Accent) : clear;
            var disabledFill=root is null ? DesignSurface.Brush(p.Background) : clear;
            // Keep the native presenter and its selection/check/drag/focus machinery. Only its
            // rectangular wash is replaced; its public drawing brushes are not VSM targets.
            foreach(var presenter in Descendants(control).OfType<ListViewItemPresenter>())
            {
                presenter.PointerOverBackground=stateFill; presenter.PressedBackground=stateFill;
                presenter.SelectedBackground=stateFill; presenter.SelectedPointerOverBackground=stateFill;
                presenter.SelectedPressedBackground=stateFill; presenter.SelectedDisabledBackground=disabledFill;
                presenter.PointerOverBorderBrush=stateBorder; presenter.SelectedBorderBrush=stateBorder;
                presenter.SelectedPointerOverBorderBrush=stateBorder; presenter.SelectedPressedBorderBrush=stateBorder;
                presenter.SelectedDisabledBorderBrush=root is null ? DesignSurface.Brush(p.Muted) : clear;
                presenter.SelectedInnerBorderBrush=stateBorder;
                presenter.CornerRadius=Radius(d,false);
            }
            foreach(var presenter in Descendants(control).OfType<GridViewItemPresenter>())
            {
                presenter.PointerOverBackground=stateFill; presenter.SelectedBackground=stateFill;
                presenter.SelectedPointerOverBackground=stateFill;
                presenter.SelectedPointerOverBorderBrush=stateBorder;
            }
            if (root is null) return;
            if (!_chromeParts.TryGetValue(root,out var overlay))
            {
                overlay=new DesignOverlay(nativePresenter ? control : root,root,descendantHost:nativePresenter);
                _chromeParts.Add(root,overlay);
            }
            overlay.Draw((surface,w,h)=>surface.Draw(d,p,w,h,HighContrast,NativePressed,selected:selected,
                fill:active ? p.Selection : null));
            if (control.ContentTemplateRoot is Border { Child:Grid } card)
            {
                DesignScope.SetRole(card,DesignElementRole.Card);
                Track(card);
                if (States.TryGetValue(card,out var state)) state.Refresh();
            }
        }

        private Grid? SelectorContentHost(SelectorItem control)
        {
            var root=(control.ContentTemplateRoot ?? control.Content as UIElement) as FrameworkElement;
            var host=root is null || IsBoundary(root) ? null : root switch
            {
                Grid grid=>grid,
                Border { Child:Grid grid }=>grid,
                _=>null,
            };
            var observed=host is null ? Array.Empty<FrameworkElement>() : new FrameworkElement[] { root!,host }.Distinct().ToArray();
            foreach(var previous in _selectorOpacityNodes.Keys.Except(observed).ToArray())
            {
                previous.UnregisterPropertyChangedCallback(UIElement.OpacityProperty,_selectorOpacityNodes[previous]);
                _selectorOpacityNodes.Remove(previous);
            }
            foreach(var node in observed)
                if (!_selectorOpacityNodes.ContainsKey(node))
                    _selectorOpacityNodes.Add(node,node.RegisterPropertyChangedCallback(UIElement.OpacityProperty,(_,_)=>RefreshSelection()));
            // Known cut-file templates place their opacity on an inner content-only grid.
            // An arbitrary dimmed root is not a safe selection host: retain native selection.
            return host is not null && !IsBoundary(host) && observed.All(node=>node.Opacity==1) ? host : null;
        }

        private void ApplyDialog(ContentDialog control,DesignDefinition d,DesignPalette p)
        {
            control.Foreground=DesignSurface.Brush(p.Foreground);
            control.Background=DesignSurface.Brush(0); control.BorderBrush=DesignSurface.Brush(0);
            control.CornerRadius=Radius(d,true);
            var parts=Descendants(control).OfType<FrameworkElement>().ToArray();
            var root=parts.OfType<Grid>().FirstOrDefault(e=>e.Name=="DialogSpace");
            if (root is null) return;
            if (!_chromeParts.TryGetValue(root,out var overlay)) { overlay=new DesignOverlay(root,root); _chromeParts.Add(root,overlay); }
            // Native dialog animation remains on BackgroundElement, above this entire subtree.
            foreach(var grid in parts.OfType<Grid>().Where(e=>e.Name=="CommandSpace" ||
                e.Name.Length==0 && e.Children.OfType<ContentPresenter>().Any(c=>c.Name=="Content")))
                grid.Background=DesignSurface.Brush(0);
            foreach(var presenter in parts.OfType<ContentPresenter>().Where(e=>e.Name=="Content"))
            {
                presenter.FontFamily=new FontFamily(d.FontFamily);
                presenter.FontSize=DesignScope.FontSize(14,d);
            }
            overlay.Draw((surface,w,h)=>surface.Draw(d,p,w,h,HighContrast,panel:true,fill:p.Card));
        }

        private void ReleaseChromeParts()
        {
            foreach(var overlay in _chromeParts.Values) overlay.Dispose();
            _chromeParts.Clear();
            _selectorHost=null;
            foreach(var (node,token) in _selectorOpacityNodes) node.UnregisterPropertyChangedCallback(UIElement.OpacityProperty,token);
            _selectorOpacityNodes.Clear();
            foreach(var group in _nativeGroups) group.CurrentStateChanged-=NativeStateChanged;
            _nativeGroups.Clear(); _nativeGroupsFound=false;
        }
    }
}
