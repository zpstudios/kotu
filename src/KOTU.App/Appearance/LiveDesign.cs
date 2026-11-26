using System.Runtime.CompilerServices;
using KOTU.Core.Appearance;
using Microsoft.UI.System;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Media;
using Windows.UI.ViewManagement;

namespace KOTU.App.Appearance;

/// <summary>
/// Registers realized visuals, never data items. A design change edits appearance properties
/// on those same objects; it does not re-template controls, replace content, or reload files.
/// </summary>
public sealed partial class LiveDesign : DependencyObject
{
    public static readonly DependencyProperty ManagedProperty = DependencyProperty.RegisterAttached(
        "Managed", typeof(bool), typeof(LiveDesign), new PropertyMetadata(false, OnManagedChanged));
    public static bool GetManaged(DependencyObject target) => (bool)target.GetValue(ManagedProperty);
    public static void SetManaged(DependencyObject target, bool value) => target.SetValue(ManagedProperty, value);
    // Bind changing semantic emphasis here, rather than to the displayed weight. A 400->600
    // source change must remain observable even when the active design already displays 600.
    public static readonly DependencyProperty AuthoredFontWeightProperty = DependencyProperty.RegisterAttached(
        "AuthoredFontWeight",typeof(Windows.UI.Text.FontWeight),typeof(LiveDesign),
        new PropertyMetadata(new Windows.UI.Text.FontWeight { Weight=400 },OnAuthoredFontWeightChanged));
    public static Windows.UI.Text.FontWeight GetAuthoredFontWeight(DependencyObject target) => (Windows.UI.Text.FontWeight)target.GetValue(AuthoredFontWeightProperty);
    public static void SetAuthoredFontWeight(DependencyObject target,Windows.UI.Text.FontWeight value) => target.SetValue(AuthoredFontWeightProperty,value);
    private static void OnAuthoredFontWeightChanged(DependencyObject sender,DependencyPropertyChangedEventArgs args)
    {
        if (!DesignRuntime.MultiSkinEnabled)
        {
            // The open-file emphasis is authored data, independent of the dormant skin engine.
            if (sender is TextBlock text) text.FontWeight = (Windows.UI.Text.FontWeight)args.NewValue;
            else if (sender is Control control) control.FontWeight = (Windows.UI.Text.FontWeight)args.NewValue;
            return;
        }
        if (sender is not FrameworkElement element) return;
        Track(element);
        if (States.TryGetValue(element,out var state)) state.RememberFontWeight(((Windows.UI.Text.FontWeight)args.NewValue).Weight);
    }
    private static readonly ConditionalWeakTable<FrameworkElement, ElementState> States = new();
    private static readonly ConditionalWeakTable<Window, WindowScope> WindowScopes = new();
    private static readonly HashSet<ElementState> LoadedStates = [];
    private static readonly Dictionary<nint, WindowScope> ActiveWindows = [];
    private static readonly AccessibilitySettings Accessibility = new();
    private static readonly UISettings UiSettings = new();
    private static bool s_initialized;
    private static Microsoft.UI.Dispatching.DispatcherQueue? s_dispatcher;

    internal static void Initialize()
    {
        if (!DesignRuntime.MultiSkinEnabled) return;
        if (s_initialized) return;
        s_initialized = true;
        s_dispatcher = Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread();
        DesignRuntime.Changed += RefreshAll;
        DesignScope.ManagementRequested += OnManagementRequested;
    }

    private static void OnManagementRequested(object target)
    {
        if (!DesignRuntime.MultiSkinEnabled) return;
        if (target is not FrameworkElement element || s_dispatcher is not { } dispatcher) return;
        if (dispatcher.HasThreadAccess) Track(element);
        else dispatcher.TryEnqueue(() => Track(element));
    }

    private static void OnManagedChanged(DependencyObject sender, DependencyPropertyChangedEventArgs args)
    {
        if (!DesignRuntime.MultiSkinEnabled) return;
        if (sender is FrameworkElement element && args.NewValue is true) Track(element);
    }

    private static void Track(FrameworkElement element)
    {
        if (!DesignRuntime.MultiSkinEnabled) return;
        var state = States.GetValue(element, static target => new ElementState(target));
        if (element.IsLoaded) { state.TraceLifecycle("Load requested","Track"); state.Load(); state.Refresh(); }
    }

    internal static void AttachTree(DependencyObject root)
    {
        if (!DesignRuntime.MultiSkinEnabled) return;
#if KOTU_DESIGN_TEST_HARNESS
        using var diagnosticWork=MeasureDiagnosticWork(DiagnosticWorkKind.AttachTree,root);
        DiagnosticTreeObserver?.Invoke(new { Action="AttachTree enter",Root=DiagnosticElement(root) });
#endif
        // Only a newly loaded root or realized container is walked, never every layout frame.
        var pending = new Stack<DependencyObject>();
        pending.Push(root);
        while (pending.TryPop(out var node))
        {
            if (node is FrameworkElement element)
            {
                if (IsBoundary(element)) continue;
                Track(element);
            }
            for (var i = VisualTreeHelper.GetChildrenCount(node) - 1; i >= 0; i--)
                pending.Push(VisualTreeHelper.GetChild(node, i));
        }
#if KOTU_DESIGN_TEST_HARNESS
        DiagnosticTreeObserver?.Invoke(new { Action="AttachTree exit",Root=DiagnosticElement(root) });
#endif
    }

    private static bool IsBoundary(FrameworkElement element) =>
        ReferenceEquals(element.Tag, DesignRuntime.PreviewScopeMarker) ||
        ReferenceEquals(element.Tag, DesignRuntime.IconScopeMarker) ||
        DesignScope.GetRole(element) == DesignElementRole.Content ||
        DesignScope.IsContentBoundary(element.Name, element.GetType().Name);

    private static bool IsExcluded(FrameworkElement element)
    {
        DependencyObject? node = element;
        for (var depth = 0; node is not null && depth < 160; depth++, node = VisualTreeHelper.GetParent(node))
            if (node is FrameworkElement candidate && IsBoundary(candidate)) return true;
        return false;
    }

    private static void RefreshAll()
    {
        if (!DesignRuntime.MultiSkinEnabled) return;
        if (s_dispatcher is { HasThreadAccess: false }) { s_dispatcher.TryEnqueue(RefreshAll); return; }
        foreach (var state in LoadedStates.ToArray()) state.Refresh();
    }

    internal static void AttachWindow(Window window, FrameworkElement root)
    {
        if (!DesignRuntime.MultiSkinEnabled) return;
        _ = WindowScopes.GetValue(window, target => new WindowScope(target, root));
    }

    // Resource and XamlRoot projections can be recreated by WinRT. Compare their native
    // identity, keeping the registered root alive for exactly its window's active lifetime.
    private static nint NativeIdentity(object? value) => value is WinRT.IWinRTObject native ? native.NativeObject.ThisPtr : 0;
    private static XamlRoot? ActiveRoot(XamlRoot? root)
    {
        var identity=NativeIdentity(root);
        return identity!=0 && ActiveWindows.TryGetValue(identity, out var scope) ? scope._xamlRoot : null;
    }

    private sealed class WindowScope
    {
        private readonly Window _window;
        private readonly FrameworkElement _root;
        private ThemeSettings? _theme;
        private bool _closed;
        internal XamlRoot? _xamlRoot;
        internal WindowScope(Window window, FrameworkElement root)
        {
            _window = window; _root = root;
            root.Loaded += OnLoaded;
            window.Closed += OnClosed;
            if (root.IsLoaded) LoadRoot();
        }
        private void OnLoaded(object sender, RoutedEventArgs args) => LoadRoot();
        private void LoadRoot()
        {
            if (_closed || _root.XamlRoot is not { } root) return;
            var identity=NativeIdentity(root);
            if (identity==0) return;
            if (NativeIdentity(_xamlRoot) != identity) UnregisterRoot();
            _xamlRoot = root;
            ActiveWindows[identity] = this;
#if KOTU_DESIGN_TEST_HARNESS
            RecordDiagnosticWindow(_window,_root);
#endif
            if (_theme is null)
            {
                _theme = ThemeSettings.CreateForWindowId(_window.AppWindow.Id);
                _theme.Changed += OnThemeChanged;
            }
            AttachTree(_root);
        }
        private void OnThemeChanged(ThemeSettings sender, object args) => _root.DispatcherQueue.TryEnqueue(() =>
        {
            if (_closed) return;
            AppDesignService.RefreshSystemColors();
            foreach (var state in LoadedStates.ToArray())
                if (NativeIdentity(state.OwnerRoot) == NativeIdentity(_xamlRoot)) state.Refresh();
        });
        private void OnClosed(object sender, WindowEventArgs args)
        {
            _closed = true;
            if (_theme is not null) _theme.Changed -= OnThemeChanged;
            _theme = null;
            _root.Loaded -= OnLoaded;
            _window.Closed -= OnClosed;
            WindowScopes.Remove(_window);
            UnregisterRoot();
        }
        private void UnregisterRoot()
        {
            var identity = NativeIdentity(_xamlRoot);
            if (identity == 0) return;
            if (ActiveWindows.TryGetValue(identity, out var scope) && ReferenceEquals(scope, this))
                ActiveWindows.Remove(identity);
            foreach (var state in LoadedStates.ToArray())
                if (NativeIdentity(state.OwnerRoot) == identity)
                {
                    state.TraceLifecycle("Unload requested",_closed ? "window Closed" : "root replaced");
                    state.Unload();
                }
            _xamlRoot = null;
        }
    }

    internal static DesignPalette Palette(FrameworkElement element)
    {
        if (!Accessibility.HighContrast) return AppDesignService.Palette(DesignRuntime.Current, element.ActualTheme == ElementTheme.Dark);
        uint C(UIElementType type) { var c = UiSettings.UIElementColor(type); return (uint)(c.A << 24 | c.R << 16 | c.G << 8 | c.B); }
        return new(C(UIElementType.Window), C(UIElementType.Window), C(UIElementType.ButtonFace), C(UIElementType.WindowText),
            C(UIElementType.GrayText), C(UIElementType.WindowText), C(UIElementType.Highlight), C(UIElementType.Highlight), C(UIElementType.HighlightText));
    }

    internal static bool HighContrast => Accessibility.HighContrast;
#if KOTU_DESIGN_LIVE_PROBE
    internal static object CapturePassiveDesignState(FrameworkElement element)
    {
        // Read existing fields only; do not register, resolve typography owners or realize templates.
        var hasState = States.TryGetValue(element, out var state);
        var root = element.XamlRoot;
        return new { NativeIdentity = $"0x{NativeIdentity(element):X}", element.IsLoaded,
            CurrentRootNative = $"0x{NativeIdentity(root):X}", CurrentRootRegistered = ActiveRoot(root) is not null,
            HasState = hasState, State = state?.PassiveDesignState() };
    }
#endif
#if KOTU_DESIGN_TEST_HARNESS
    // Weak, opt-in diagnostics retain no element through the trace output. Keep the sink
    // through Window.Closed so cleanup after the fixture's own Closed handler is observed.
    private static readonly ConditionalWeakTable<FrameworkElement,Action<object>> LifecycleObservers=new();
    internal static void ObserveLifecycle(FrameworkElement element,Action<object> record)=>LifecycleObservers.Add(element,record);
    internal static void StopObservingLifecycle(FrameworkElement element)=>LifecycleObservers.Remove(element);
    internal static Action<object>? DiagnosticTreeObserver { get; set; }
    internal static object DiagnosticConnection(FrameworkElement element)
    {
        // Exact asserted object only: no parent/child, style, typography or exclusion walk.
        var hasState=States.TryGetValue(element,out var state);
        var root=element.XamlRoot;
        return new { Type=element.GetType().Name,element.Name,NativeIdentity=$"0x{NativeIdentity(element):X}",element.IsLoaded,
            CurrentRootNative=$"0x{NativeIdentity(root):X}",CurrentRootRegistered=ActiveRoot(root) is not null,
            HasState=hasState,State=state?.DiagnosticConnectionState() };
    }
    internal static object DiagnosticTypographyBaseline(FrameworkElement element)
    {
        // Late-only recycling observations: read captured fields without Track, styles,
        // owner resolution, template realization or Loaded/Unloaded subscriptions.
        var hasState=States.TryGetValue(element,out var state);
        return new { Connection=DiagnosticConnection(element),Captured=hasState ? state!.DiagnosticTypographyBaseline() : null };
    }
    internal static object DiagnosticElement(DependencyObject value)
    {
        static string Identity(object? target)=>$"0x{NativeIdentity(target):X}";
        if (value is not FrameworkElement element) return new { Type=value.GetType().Name,NativeIdentity=Identity(value) };
        var hasState=States.TryGetValue(element,out var state);
        var currentRoot=element.XamlRoot;
        return new { Type=element.GetType().Name,element.Name,NativeIdentity=Identity(element),element.IsLoaded,
            element.ActualWidth,element.ActualHeight,Managed=GetManaged(element),IsExcluded=IsExcluded(element),
            CurrentRootNative=Identity(currentRoot),CurrentRootRegistered=ActiveRoot(currentRoot) is not null,
            HasState=hasState,State=state?.DiagnosticState(),Appearance=DiagnosticAppearance(element) };
    }
    private static object DiagnosticAppearance(FrameworkElement element)
    {
        static object Local(DependencyObject target,DependencyProperty property)
        {
            var value=target.ReadLocalValue(property);
            return new { IsUnset=value==DependencyProperty.UnsetValue,Type=value?.GetType().Name,
                Value=value==DependencyProperty.UnsetValue ? null : value is FontFamily family ? family.Source :
                    value is Windows.UI.Text.FontWeight weight ? weight.Weight.ToString() : value?.ToString() };
        }
        var implicitStyle=Application.Current.Resources.TryGetValue(element.GetType(),out var resource) ? resource as Style : null;
        return new { Glyph=(element as FontIcon)?.Glyph,Text=(element as TextBlock)?.Text,
            GlyphCodeUnits=(element as FontIcon)?.Glyph.Select(value=>$"U+{(int)value:X4}").ToArray(),
            TextCodeUnits=(element as TextBlock)?.Text.Select(value=>$"U+{(int)value:X4}").ToArray(),
            FontFamily=element is FontIcon icon ? icon.FontFamily.Source : element is TextBlock text ? text.FontFamily.Source : (element as Control)?.FontFamily.Source,
            FontSize=element is FontIcon sizedIcon ? (double?)sizedIcon.FontSize : element is TextBlock sizedText ? sizedText.FontSize : (element as Control)?.FontSize,
            FontWeight=element is TextBlock weightedText ? (ushort?)weightedText.FontWeight.Weight : (element as Control)?.FontWeight.Weight,
            LocalStyle=Local(element,FrameworkElement.StyleProperty),
            StyleNative=$"0x{NativeIdentity(element.Style):X}",ImplicitAppStyleNative=$"0x{NativeIdentity(implicitStyle):X}",
            LocalManaged=Local(element,ManagedProperty),
            LocalFont=element is FontIcon ? Local(element,FontIcon.FontFamilyProperty) : element is TextBlock ? Local(element,TextBlock.FontFamilyProperty) :
                element is Control ? Local(element,Control.FontFamilyProperty) : null,
            LocalFontWeight=element is TextBlock ? Local(element,TextBlock.FontWeightProperty) : element is Control ? Local(element,Control.FontWeightProperty) : null,
            LocalFontSize=element is FontIcon ? Local(element,FontIcon.FontSizeProperty) : element is TextBlock ? Local(element,TextBlock.FontSizeProperty) :
                element is Control ? Local(element,Control.FontSizeProperty) : null,
            TypographyOwner=ElementState.DiagnosticTypographyOwner(element) };
    }
    private sealed record DiagnosticOwner(string Label,FrameworkElement FixtureRoot,UIElement? WindowContent,UIElement? RootContent,nint RootIdentity);
    private static readonly List<DiagnosticOwner> DiagnosticOwners=[];
    internal static void RecordDiagnosticWindow(Window window,FrameworkElement root)
    {
        if (root.XamlRoot is not { } xamlRoot || DiagnosticOwners.Any(owner=>ReferenceEquals(owner.FixtureRoot,root))) return;
        DiagnosticOwners.Add(new DiagnosticOwner(window.Title,root,window.Content,xamlRoot.Content,NativeIdentity(xamlRoot)));
    }
    internal static string[] DiagnosticOwnerDescriptions=>DiagnosticOwners.Select(owner=>
        $"{owner.Label} rootNative=0x{owner.RootIdentity:X} contentNative=0x{NativeIdentity(owner.RootContent):X} windowContentNative=0x{NativeIdentity(owner.WindowContent):X} contentIsFixture={ReferenceEquals(owner.RootContent,owner.FixtureRoot)} contentIsWindow={ReferenceEquals(owner.RootContent,owner.WindowContent)}").ToArray();
    internal static int LoadedElementCount => LoadedStates.Count;
    private static string DescribeLoadedState(ElementState state)
    {
        var ownerIdentity=NativeIdentity(state.OwnerRoot);
        var content=state.OwnerRoot?.Content;
        var matched=string.Join(",",DiagnosticOwners.Where(owner=>owner.RootIdentity==ownerIdentity).Select(owner=>owner.Label));
        var contentMatches=string.Join(",",DiagnosticOwners.Where(owner=>NativeIdentity(owner.WindowContent)==NativeIdentity(content)).Select(owner=>owner.Label));
        return $"{state.Element.GetType().Name}:{state.Element.Name} loaded={state.Element.IsLoaded} ownerRoot={state.OwnerRoot is not null} ownerNative=0x{ownerIdentity:X} elementRootNative=0x{NativeIdentity(state.Element.XamlRoot):X} content={content?.GetType().Name}:{(content as FrameworkElement)?.Name} contentNative=0x{NativeIdentity(content):X} owners=[{matched}] windowContents=[{contentMatches}] visualParent={VisualTreeHelper.GetParent(state.Element)?.GetType().Name ?? "null"}";
    }
    internal static string[] LoadedElementDescriptions=>LoadedStates.Select(DescribeLoadedState).ToArray();
    private sealed record LoadedSnapshotEntry(ElementState State,nint RootIdentity,string Description);
    internal static object[] CaptureLoadedSnapshot()=>LoadedStates.Select(state=>(object)new LoadedSnapshotEntry(state,NativeIdentity(state.OwnerRoot),DescribeLoadedState(state))).ToArray();
    internal static string[] DescribeLoadedSnapshot(object[] snapshot)=>snapshot.OfType<LoadedSnapshotEntry>().Select(entry=>entry.Description).ToArray();
    internal static string[] SummarizeLoadedSnapshot(object[] snapshot)=>snapshot.OfType<LoadedSnapshotEntry>().GroupBy(entry=>entry.RootIdentity)
        .Select(group=>$"rootNative=0x{group.Key:X} count={group.Count()} examples={string.Join(",",group.Take(4).Select(entry=>entry.State.Element.GetType().Name+":"+entry.State.Element.Name))}").ToArray();
    internal sealed record LoadedOwnershipAudit(int Remaining,int InitialAmbient,int AmbientRemaining,int LiveSentinelRemaining,
        string[] ClosedFixtureStates,string[] UnexpectedStates,string[] UnexpectedRootTrees);
    internal static LoadedOwnershipAudit AuditLoadedOwnership(object[] initial,FrameworkElement sentinel,IReadOnlyList<(string Stage,object[] Snapshot)> stages)
    {
        var ambient=initial.OfType<LoadedSnapshotEntry>().ToDictionary(entry=>entry.State,entry=>entry.RootIdentity);
        var sentinelIdentity=NativeIdentity(sentinel.XamlRoot);
        var closedRoots=DiagnosticOwners.Where(owner=>!ReferenceEquals(owner.FixtureRoot,sentinel)).Select(owner=>owner.RootIdentity).ToHashSet();
        var closed=new List<string>();var unexpected=new List<ElementState>();var ambientRemaining=0;var sentinelRemaining=0;
        foreach(var state in LoadedStates)
        {
            var owner=NativeIdentity(state.OwnerRoot);
            if (owner!=0 && owner==sentinelIdentity) sentinelRemaining++;
            else if (owner!=0 && closedRoots.Contains(owner)) closed.Add(DescribeLoadedState(state));
            else if (ambient.TryGetValue(state,out var initialOwner) && initialOwner==owner) ambientRemaining++;
            else unexpected.Add(state);
        }
        string DescribeUnexpected(ElementState state)
        {
            var first=stages.FirstOrDefault(stage=>stage.Snapshot.OfType<LoadedSnapshotEntry>().Any(entry=>ReferenceEquals(entry.State,state))).Stage ?? "final cleanup";
            return "firstSeen="+first+" "+DescribeLoadedState(state);
        }
        return new(LoadedStates.Count,ambient.Count,ambientRemaining,sentinelRemaining,closed.ToArray(),unexpected.Select(DescribeUnexpected).ToArray(),
            unexpected.GroupBy(state=>NativeIdentity(state.OwnerRoot)).Select(group=>DescribeDiagnosticRoot(group.First().OwnerRoot)).ToArray());
    }
    private static string DescribeDiagnosticRoot(XamlRoot? root)
    {
        if (root?.Content is not { } content) return $"rootNative=0x{NativeIdentity(root):X} content=null";
        var lines=new List<string>();var pending=new Stack<(DependencyObject Element,int Depth)>();pending.Push((content,0));
        while(pending.TryPop(out var entry) && lines.Count<20)
        {
            lines.Add(entry.Depth+":"+entry.Element.GetType().Name+":"+(entry.Element as FrameworkElement)?.Name);
            if (entry.Depth>=4) continue;
            for(var index=VisualTreeHelper.GetChildrenCount(entry.Element)-1;index>=0;index--) pending.Push((VisualTreeHelper.GetChild(entry.Element,index),entry.Depth+1));
        }
        return $"rootNative=0x{NativeIdentity(root):X} tree="+string.Join(";",lines);
    }
#endif

    private sealed partial class ElementState
    {
        internal FrameworkElement Element { get; }
        internal XamlRoot? OwnerRoot { get; private set; }
        private bool _loaded, _applying, _queued, _hover;
        private bool _initialized;
        private double _fontSize;
        private ushort _fontWeight;
        private DesignGlyphState? _glyph;
        private bool _nativeText;
        private bool _originalControlLettering;
        private bool _inheritsFontWeight, _inheritsFontSize;
        private FontFamily? _originalFont;
        private readonly List<(DependencyProperty Property, long Token)> _callbacks = [];
        private readonly HashSet<FlyoutBase> _flyouts = [];
        private ItemCollection? _menuPresenterItems;
        private bool _menuTreeQueued;
        private int _menuTreeGeneration;
        private int _loadGeneration;
        private int _popupGeneration;
        private RealizationPass? _templatePass, _popupPass;

        internal ElementState(FrameworkElement element)
        {
            Element = element;
            element.Loaded += (_, _) => { TraceLifecycle("Load requested","Loaded event"); Load(); };
            element.Unloaded += OnElementUnloaded;
        }

#if KOTU_DESIGN_LIVE_PROBE
        internal object PassiveDesignState() => new { StateLoaded = _loaded, Initialized = _initialized,
            InLoadedStates = LoadedStates.Contains(this), LoadGeneration = _loadGeneration,
            OwnerRootNative = $"0x{NativeIdentity(OwnerRoot):X}", OwnerRootRegistered = ActiveRoot(OwnerRoot) is not null,
            OriginalFontSize = _fontSize, OriginalFontWeight = _fontWeight,
            InheritsFontSize = _inheritsFontSize, InheritsFontWeight = _inheritsFontWeight,
            NativeText = _nativeText, OriginalFont = _originalFont?.Source,
            GlyphCanonical = _glyph?.Canonical, GlyphDisplayed = _glyph?.Displayed };
#endif
#if KOTU_DESIGN_TEST_HARNESS
        internal object DiagnosticConnectionState()=>new { StateLoaded=_loaded,Initialized=_initialized,
            InLoadedStates=LoadedStates.Contains(this),LoadGeneration=_loadGeneration,
            OwnerRootNative=$"0x{NativeIdentity(OwnerRoot):X}",OwnerRootRegistered=ActiveRoot(OwnerRoot) is not null };
        internal object DiagnosticTypographyBaseline()=>new { OriginalFontSize=_fontSize,OriginalFontWeight=_fontWeight,
            InheritsFontSize=_inheritsFontSize,InheritsFontWeight=_inheritsFontWeight,NativeText=_nativeText,
            OriginalFont=_originalFont?.Source,GlyphCanonical=_glyph?.Canonical,GlyphDisplayed=_glyph?.Displayed };
        internal object DiagnosticState()=>new { StateLoaded=_loaded,Initialized=_initialized,InLoadedStates=LoadedStates.Contains(this),LoadGeneration=_loadGeneration,
            OwnerRootNative=$"0x{NativeIdentity(OwnerRoot):X}",OwnerRootRegistered=ActiveRoot(OwnerRoot) is not null,
            OriginalFont=_originalFont?.Source,OriginalFontSize=_fontSize,GlyphCanonical=_glyph?.Canonical,GlyphDisplayed=_glyph?.Displayed,
            OriginalFontWeight=_fontWeight,InheritsFontWeight=_inheritsFontWeight,InheritsFontSize=_inheritsFontSize,
            ResolvedAuthoredWeight=AuthoredWeight,ResolvedAuthoredSize=AuthoredSize,
            NativeText=_nativeText,NativeTemplateLabel=Element is TextBlock text && IsNativeTemplateLabel(text) };
        internal static object? DiagnosticTypographyOwner(FrameworkElement element)
        {
            if (element is not Control && element is not TextBlock) return null;
            var owner=FindTypographyOwner(element);
            if (owner is null) return null;
            var hasState=States.TryGetValue(owner,out var state);
            var localWeight=owner.ReadLocalValue(Control.FontWeightProperty);
            var localSize=owner.ReadLocalValue(Control.FontSizeProperty);
            return new { Type=owner.GetType().Name,owner.Name,NativeIdentity=$"0x{NativeIdentity(owner):X}",owner.IsLoaded,
                EffectiveWeight=owner.FontWeight.Weight,EffectiveSize=owner.FontSize,
                LocalWeightIsUnset=localWeight==DependencyProperty.UnsetValue,
                LocalWeight=localWeight is Windows.UI.Text.FontWeight weight ? (ushort?)weight.Weight : null,
                LocalSizeIsUnset=localSize==DependencyProperty.UnsetValue,LocalSize=localSize is double size ? (double?)size : null,
                HasState=hasState,StateLoaded=state?._loaded,Initialized=state?._initialized,
                OriginalWeight=state?._fontWeight,OriginalSize=state?._fontSize,
                InheritsWeight=state?._inheritsFontWeight,InheritsSize=state?._inheritsFontSize,
                ResolvedOriginalWeight=OriginalOwnerWeight(owner),ResolvedOriginalSize=OriginalOwnerSize(owner) };
        }
#endif

        private void OnElementUnloaded(object sender,RoutedEventArgs args)
        {
            TraceLifecycle("Unload requested","Unloaded event");
            var wasLoaded=_loaded;
            var stillLoaded=Element.IsLoaded;
            var rootIdentity=wasLoaded && stillLoaded ? NativeIdentity(ActiveRoot(Element.XamlRoot)) : 0;
            var skipReason=!wasLoaded ? "state already unloaded at event" : !stillLoaded ? "element not loaded at event" :
                rootIdentity==0 ? "root not registered at event" : NativeIdentity(OwnerRoot)!=rootIdentity ? "owner root changed at event" : null;
            var generation=unchecked(_loadGeneration+1);
            // WinUI can deliver an old Unloaded after the same element is already loaded.
            // Always release everything first; one deferred check may restore that live owner.
            Unload();
            if (skipReason is not null) { TraceLifecycle("Reconnect skipped",skipReason); return; }
            if (!Element.DispatcherQueue.TryEnqueue(() =>
            {
                if (_loaded) { TraceLifecycle("Reconnect skipped","already loaded"); return; }
                if (_loadGeneration!=generation) { TraceLifecycle("Reconnect skipped","generation changed"); return; }
                if (!Element.IsLoaded) { TraceLifecycle("Reconnect skipped","element no longer loaded"); return; }
                var currentRoot=Element.XamlRoot;
                if (NativeIdentity(currentRoot)!=rootIdentity) { TraceLifecycle("Reconnect skipped","current root changed"); return; }
                if (NativeIdentity(ActiveRoot(currentRoot))!=rootIdentity) { TraceLifecycle("Reconnect skipped","root no longer registered"); return; }
                if (IsExcluded(Element)) { TraceLifecycle("Reconnect skipped","content scope excluded"); return; }
                TraceLifecycle("Reconnect executing",$"generation={generation} root=0x{rootIdentity:X}");
                Load();
            })) TraceLifecycle("Reconnect skipped","dispatcher rejected");
            else TraceLifecycle("Reconnect scheduled",$"generation={generation} root=0x{rootIdentity:X}");
        }

        [System.Diagnostics.Conditional("KOTU_DESIGN_TEST_HARNESS")]
        internal void TraceLifecycle(string action,string reason="",UIElement? part=null)
        {
#if KOTU_DESIGN_TEST_HARNESS
            if (!LifecycleObservers.TryGetValue(Element,out var record)) return;
            var currentRoot=Element.XamlRoot;
            var observedPart=part ?? _menuCheckOriginal;
            RectangleGeometry? savedClip=null;
            var hasSavedClip=observedPart is not null && _hiddenParts.TryGetValue(observedPart,out savedClip);
            static string Identity(object? value)=>$"0x{NativeIdentity(value):X}";
            static object ClipSnapshot(RectangleGeometry? clip)=>new { Present=clip is not null,NativeIdentity=Identity(clip),
                X=clip?.Rect.X,Y=clip?.Rect.Y,Width=clip?.Rect.Width,Height=clip?.Rect.Height };
            record(new { Action=action,Reason=reason,Utc=DateTime.UtcNow,Timestamp=System.Diagnostics.Stopwatch.GetTimestamp(),
                ElementNative=Identity(Element),ElementType=Element.GetType().Name,ElementName=Element.Name,
                Appearance=DiagnosticTreeObserver is not null ? DiagnosticAppearance(Element) : null,
                StateLoaded=_loaded,ElementIsLoaded=Element.IsLoaded,InLoadedStates=LoadedStates.Contains(this),LoadGeneration=_loadGeneration,
                OwnerRootNative=Identity(OwnerRoot),CurrentRootNative=Identity(currentRoot),
                OwnerRootRegistered=ActiveRoot(OwnerRoot) is not null,CurrentRootRegistered=ActiveRoot(currentRoot) is not null,
                RegisteredRootNative=Identity(ActiveRoot(currentRoot)),
                MenuCheckNative=Identity(_menuCheckOriginal),MenuOverlayNative=Identity(_menuCheckVisual),HiddenPartCount=_hiddenParts.Count,
                PartNative=Identity(observedPart),PartName=(observedPart as FrameworkElement)?.Name,
                PartIsLoaded=(observedPart as FrameworkElement)?.IsLoaded,Clip=ClipSnapshot(observedPart?.Clip),
                HasSavedClip=hasSavedClip,SavedClip=ClipSnapshot(savedClip) });
#endif
        }

        internal void Load()
        {
#if KOTU_DESIGN_TEST_HARNESS
            using var diagnosticWork=MeasureDiagnosticWork(DiagnosticWorkKind.Load,Element);
#endif
            TraceLifecycle("Load enter");
            if (_loaded || !Element.IsLoaded || IsExcluded(Element) || ActiveRoot(Element.XamlRoot) is not { } owner)
            { TraceLifecycle("Load skipped"); return; }
            _loaded = true;
            _loadGeneration++;
            OwnerRoot = owner;
            LoadedStates.Add(this);
            Element.ActualThemeChanged += ThemeChanged;
            if (!_initialized)
            {
                _initialized = true;
                switch (Element)
                {
                    case FontIcon icon:
                        _fontSize = icon.FontSize; _originalFont = icon.FontFamily;
                        _glyph = new(CanonicalNativeGlyph(icon.Glyph)); break;
                    case TextBlock text:
                        _nativeText = IsNativeTemplateLabel(text) || text.ReadLocalValue(TextBlock.FontSizeProperty) == DependencyProperty.UnsetValue &&
                            text.ReadLocalValue(TextBlock.FontFamilyProperty) == DependencyProperty.UnsetValue &&
                            VisualTreeHelper.GetParent(text) is ContentPresenter;
                        _fontSize = text.FontSize; _fontWeight = text.FontWeight.Weight; _originalFont = text.FontFamily;
                        _originalControlLettering = text.ReadLocalValue(TextBlock.FontFamilyProperty) is FontFamily authoredFamily &&
                            (authoredFamily.Source.Contains("KotuFit.ttf", StringComparison.OrdinalIgnoreCase) ||
                            Application.Current.Resources.TryGetValue("KotuControlFontFamily", out var lettering) &&
                            lettering is FontFamily controlFamily && authoredFamily.Source == controlFamily.Source);
                        // Font size/family may be authored while weight still inherits. Never turn
                        // a managed parent's already displayed weight into this label's baseline.
                        _inheritsFontWeight=!_nativeText && InheritsManagedProperty(text,TextBlock.FontWeightProperty);
                        _inheritsFontSize=!_nativeText && InheritsManagedProperty(text,TextBlock.FontSizeProperty);
                        break;
                    case Control control:
                        _fontSize = control.FontSize; _fontWeight = control.FontWeight.Weight; _originalFont = control.FontFamily;
                        _inheritsFontWeight=UsesControlTypography(control) && InheritsManagedProperty(control,Control.FontWeightProperty);
                        _inheritsFontSize=UsesControlTypography(control) && InheritsManagedProperty(control,Control.FontSizeProperty);
                        break;
                }
                _fontSize=DesignScope.OriginalFontSize(Element,_fontSize);
                DesignScope.SetOriginalFontSize(Element,AuthoredSize);
                if (!_nativeText && Element.ReadLocalValue(AuthoredFontWeightProperty)!=DependencyProperty.UnsetValue)
                {
                    _inheritsFontWeight=false;
                    _fontWeight=GetAuthoredFontWeight(Element).Weight;
                }
            }
            if (Element is FontIcon fontIcon)
            {
                _glyph!.Observe(CanonicalNativeGlyph(fontIcon.Glyph));
                Watch(FontIcon.GlyphProperty, () => { _glyph.Observe(CanonicalNativeGlyph(fontIcon.Glyph)); Refresh(); });
            }
            if (Element is TextBlock { Name:"GlyphElement" } nativeGlyph && HasAncestor(e=>e is TextBox or PasswordBox))
                _glyph ??= new DesignGlyphState(CanonicalNativeGlyph(nativeGlyph.Text));
            if (Element is TextBlock) Watch(TextBlock.TextProperty, RefreshTextGlyph);
            if (Element is TextBlock weightedText && !_nativeText)
                Watch(TextBlock.FontWeightProperty,()=>ObserveWeight(TextBlock.FontWeightProperty,weightedText.FontWeight.Weight));
            if (Element is Control weightedControl)
                Watch(Control.FontWeightProperty,()=>ObserveWeight(Control.FontWeightProperty,weightedControl.FontWeight.Weight));
            if (Element is TextBlock)
                Watch(TextBlock.ForegroundProperty,()=>_neutralText=false);
            if (Element is TextBlock sizedText && !_nativeText)
                Watch(TextBlock.FontSizeProperty,()=>ObserveSize(TextBlock.FontSizeProperty,sizedText.FontSize));
            if (Element is FontIcon sizedIcon)
                Watch(FontIcon.FontSizeProperty,()=>RememberFontSize(sizedIcon.FontSize));
            if (Element is Control sizedControl)
                Watch(Control.FontSizeProperty,()=>ObserveSize(Control.FontSizeProperty,sizedControl.FontSize));
            if (Element is Control)
            {
                Watch(Control.IsEnabledProperty, () =>
                {
                    if (Element is SelectorItem && FileTileOwner is not null) RefreshSelection();
                    else Refresh();
                });
                Watch(Control.FocusStateProperty, Refresh);
                Watch(Control.TemplateProperty, () => { TraceLifecycle("ReleaseParts requested","Template changed"); ReleaseParts(); QueueTree(); QueueTemplateReady(); });
                Element.PointerEntered += Entered;
                Element.PointerExited += Exited;
            }
            if (Element is ButtonBase) Watch(ButtonBase.IsPressedProperty, Refresh);
            if (Element is ToggleButton) Watch(ToggleButton.IsCheckedProperty, RefreshCheckState);
            if (Element is ToggleSwitch) Watch(ToggleSwitch.IsOnProperty, Refresh);
            if (Element is ToggleMenuFlyoutItem) Watch(ToggleMenuFlyoutItem.IsCheckedProperty, Refresh);
            if (Element is RadioMenuFlyoutItem) Watch(RadioMenuFlyoutItem.IsCheckedProperty, Refresh);
            if (Element is SelectorItem) Watch(SelectorItem.IsSelectedProperty, RefreshSelection);
            if (Element is ContentControl) Watch(ContentControl.ContentProperty, QueueTree);
            if (Element is ContentPresenter) Watch(ContentPresenter.ContentProperty, QueueTree);
            if (Element is ListViewBase list) list.ContainerContentChanging += ContainerChanged;
            if (Element is ComboBox combo)
            {
                combo.DropDownOpened += DropDownOpened;
                combo.DropDownClosed += DropDownClosed;
            }
            if (Element is MenuFlyoutPresenter presenter)
            {
                _menuPresenterItems=presenter.Items;
                _menuPresenterItems.VectorChanged+=MenuItemsChanged;
                TrackMenuItems();
                QueueMenuTree();
            }
            Watch(UIElement.ContextFlyoutProperty, RegisterFlyouts);
            if (Element is Button) Watch(Button.FlyoutProperty, RegisterFlyouts);
            if (Element is DropDownButton) Watch(DropDownButton.FlyoutProperty, RegisterFlyouts);
            RegisterFlyouts();
            Refresh();
            QueueTemplateReady();
            TraceLifecycle("Load exit");
        }

        internal void Unload()
        {
            TraceLifecycle("Unload enter");
            if (!_loaded) { TraceLifecycle("Unload skipped"); return; }
            _loaded = false; _hover = false;
            _loadGeneration++;
            _queued=false;
            _templatePass?.Cancel();_templatePass=null;
            CancelPopupPass();
            LoadedStates.Remove(this);
            Element.ActualThemeChanged -= ThemeChanged;
            Element.PointerEntered -= Entered;
            Element.PointerExited -= Exited;
            foreach (var (property, token) in _callbacks) Element.UnregisterPropertyChangedCallback(property, token);
            _callbacks.Clear();
            if (Element is ListViewBase list) list.ContainerContentChanging -= ContainerChanged;
            if (Element is ComboBox combo)
            {
                combo.DropDownOpened -= DropDownOpened;
                combo.DropDownClosed -= DropDownClosed;
            }
            if (_menuPresenterItems is { } menuItems) menuItems.VectorChanged-=MenuItemsChanged;
            _menuPresenterItems=null;
            _menuTreeGeneration++;_menuTreeQueued=false;
            foreach (var flyout in _flyouts) { flyout.Opened -= FlyoutOpened;flyout.Closed -= FlyoutClosed; }
            _flyouts.Clear();
            TraceLifecycle("ReleaseParts requested","Unload");
            ReleaseParts();
            OwnerRoot = null;
            TraceLifecycle("Unload exit");
        }

        private void Watch(DependencyProperty property, Action action) => _callbacks.Add((property,
            Element.RegisterPropertyChangedCallback(property, (_, _) => { if (!_applying && _loaded) action(); })));
        private void ThemeChanged(FrameworkElement sender, object args) => Refresh();
        private void Entered(object sender, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e) { _hover = true; RefreshSelection(); }
        private void Exited(object sender, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e) { _hover = false; RefreshSelection(); }
        private void RefreshSelection()
        {
            Refresh();
            if (Element is not SelectorItem) return;
            foreach(var child in Descendants(Element).OfType<FrameworkElement>())
                if ((child is TextBlock || DesignScope.GetRole(child)==DesignElementRole.Card) && States.TryGetValue(child,out var state)) state.Refresh();
        }
        private void RefreshTextGlyph()
        {
            if (_glyph is not null && Element is TextBlock text) _glyph.Observe(CanonicalNativeGlyph(text.Text));
            if (_glyph is not null || Element.Name is "CollapsedGlyph" or "ExpandedGlyph") Refresh();
        }
        private void RememberFontSize(double value)
        {
            // User scale controls (notably sensor graph scale) can update authored sizes after
            // loading. Our own writes are guarded, so these changes become the next baseline.
            _inheritsFontSize=false;
            _fontSize=value; DesignScope.SetOriginalFontSize(Element,value); Refresh();
        }
        internal void RememberFontWeight(ushort value)
        {
            if (_nativeText) return;
            _inheritsFontWeight=false;
            _fontWeight=value; Refresh();
        }
        private void ObserveWeight(DependencyProperty property,ushort value)
        {
            if (_inheritsFontWeight && Element.ReadLocalValue(property)==DependencyProperty.UnsetValue)
            {
                Refresh(); // Inherited display changed; its owner still owns the authored baseline.
                return;
            }
            RememberFontWeight(value);
        }
        private void ObserveSize(DependencyProperty property,double value)
        {
            if (_inheritsFontSize && Element.ReadLocalValue(property)==DependencyProperty.UnsetValue)
            {
                DesignScope.SetOriginalFontSize(Element,AuthoredSize); Refresh(); return;
            }
            RememberFontSize(value);
        }
        private static bool InheritsManagedProperty(FrameworkElement element,DependencyProperty property) =>
            (property!=Control.FontWeightProperty && property!=TextBlock.FontWeightProperty || element.ReadLocalValue(AuthoredFontWeightProperty)==DependencyProperty.UnsetValue) &&
            element.ReadLocalValue(property)==DependencyProperty.UnsetValue && !HasTypographySetter(element,property) && FindTypographyOwner(element) is not null;

        private static object? ScopedResource(FrameworkElement source,object key)
        {
            for(DependencyObject? node=source;node is not null;node=VisualTreeHelper.GetParent(node))
                if (node is FrameworkElement element && element.Resources.TryGetValue(key,out var resource)) return resource;
            return Application.Current.Resources.TryGetValue(key,out var appResource) ? appResource : null;
        }
        private static bool StyleSetsProperty(Style? style,DependencyProperty property)
        {
            for(var current=style;current is not null;current=current.BasedOn)
                if (current.Setters.OfType<Setter>().Any(setter=>setter.Property==property)) return true;
            return false;
        }
        private static bool HasTypographySetter(FrameworkElement element,DependencyProperty property)
        {
            // An implicit/explicit Style overlays the native default style. Both can own a
            // property: native defaults still win over inheritance without a local setter.
            var style=element.Style ?? ScopedResource(element,element.GetType()) as Style;
            if (StyleSetsProperty(style,property)) return true;
            if (element is not Control control) return false;
            var nativeType=control.GetValue(Control.DefaultStyleKeyProperty) as Type ?? control.GetType();
            var nativeKey=nativeType==typeof(TreeViewItem) ? "MUX_TreeViewItemStyle" : "Default"+nativeType.Name+"Style";
            if (StyleSetsProperty(ScopedResource(element,nativeKey) as Style,property)) return true;
            // WinUI 1.8 generic.xaml: these default styles explicitly set FontWeight=Normal.
            // The other managed native controls (including menus/ComboBoxItem) have no setter.
            // Keep this fallback for native theme resources not exposed through app resources.
            if (property==Control.FontWeightProperty)
                return nativeType==typeof(Button) || nativeType==typeof(ToggleButton) || nativeType==typeof(RepeatButton) ||
                    nativeType==typeof(DropDownButton) || nativeType==typeof(AppBarButton) || nativeType==typeof(AppBarToggleButton);
            // FontSize has a separate default-style contract. In particular ComboBoxItem and
            // menu/flyout presenters inherit it, whereas menu items and ordinary buttons set it.
            return property==Control.FontSizeProperty && nativeType.Name is
                "Button" or "ToggleButton" or "RepeatButton" or "DropDownButton" or "TextBox" or "PasswordBox" or
                "MenuFlyoutItem" or "ToggleMenuFlyoutItem" or "RadioMenuFlyoutItem" or "MenuFlyoutSubItem" or
                "HyperlinkButton" or "RadioButton" or "CheckBox" or "ToggleSwitch" or "GridViewItem" or "ListViewItem" or "TreeViewItem" or "ToolTip" or "ComboBox";
        }
        private static Control? FindTypographyOwner(FrameworkElement element)
        {
            for(var parent=VisualTreeHelper.GetParent(element);parent is not null;parent=VisualTreeHelper.GetParent(parent))
            {
                if (parent is FrameworkElement boundary && IsBoundary(boundary)) return null;
                if (parent is Control control && UsesControlTypography(control)) return control;
            }
            // ComboBox item containers live under a Popup rather than below the ComboBox in
            // the visual tree. Use its native item owner, without changing popup/content parents.
            return ItemsControl.ItemsControlFromItemContainer(element) is { } owner && !ReferenceEquals(owner,element) && UsesControlTypography(owner) ? owner : null;
        }
        private ushort AuthoredWeight => _inheritsFontWeight && FindTypographyOwner(Element) is { } owner ? OriginalOwnerWeight(owner) : _fontWeight;
        private double AuthoredSize => _inheritsFontSize && FindTypographyOwner(Element) is { } owner ? OriginalOwnerSize(owner) : _fontSize;
        private static ushort OriginalOwnerWeight(Control owner,int depth=0)
        {
            if (owner.ReadLocalValue(AuthoredFontWeightProperty)!=DependencyProperty.UnsetValue) return GetAuthoredFontWeight(owner).Weight;
            var initialized=States.TryGetValue(owner,out var state) && state._initialized;
            var inherits=initialized ? state!._inheritsFontWeight : InheritsManagedProperty(owner,Control.FontWeightProperty);
            if (depth<160 && inherits && FindTypographyOwner(owner) is { } ancestor) return OriginalOwnerWeight(ancestor,depth+1);
            // Before the owner's first Load it has not written a design weight yet. After
            // Load use its captured baseline, independently of parent/child Loaded order.
            return initialized ? state!._fontWeight : owner.FontWeight.Weight;
        }
        private static double OriginalOwnerSize(Control owner,int depth=0)
        {
            var initialized=States.TryGetValue(owner,out var state) && state._initialized;
            var inherits=initialized ? state!._inheritsFontSize : InheritsManagedProperty(owner,Control.FontSizeProperty);
            if (depth<160 && inherits && FindTypographyOwner(owner) is { } ancestor) return OriginalOwnerSize(ancestor,depth+1);
            return initialized ? state!._fontSize : DesignScope.OriginalFontSize(owner,owner.FontSize);
        }
        private static bool UsesControlTypography(Control control) => control is
            ButtonBase or MenuFlyoutItemBase or ComboBox or SelectorItem or TextBox or PasswordBox or ToggleSwitch or ToolTip or FlyoutPresenter or MenuFlyoutPresenter or ContentDialog;
        private static string CanonicalNativeGlyph(string glyph)=>glyph switch
        {
            "\uEDDA" or "\uE974"=>DesignIconCatalog.GlyphFor("Next").ToString(),
            "\uEDDB"=>DesignIconCatalog.GlyphFor("Previous").ToString(),
            "\uEDDC"=>DesignIconCatalog.GlyphFor("Up").ToString(),
            "\uEDDD"=>DesignIconCatalog.GlyphFor("Down").ToString(),
            "\uE894"=>DesignIconCatalog.GlyphFor("Close").ToString(),
            "\uF78D"=>DesignIconCatalog.GlyphFor("View").ToString(),
            _=>glyph,
        };
        private bool IsNativeTemplateLabel(TextBlock text) =>
            (text.Name is "TextBlock" or "KeyboardAcceleratorTextBlock") && HasAncestor(e=>e is MenuFlyoutItemBase) ||
            (text.Name is "PlaceholderTextBlock" or "PlaceholderTextContentPresenter") && HasAncestor(e=>e is TextBox or PasswordBox or ComboBox);
        private void QueueTree()
        {
            if (_queued || !_loaded) return;
            _queued = true;
            var generation=_loadGeneration;
            if (!Element.DispatcherQueue.TryEnqueue(() =>
            {
                if (generation!=_loadGeneration) return;
                _queued=false;
                if (_loaded) { AttachTree(Element); Refresh(); }
            })) _queued=false;
        }

        private void QueueTemplateReady()
        {
            if (!_loaded || Element is not (MenuFlyoutItemBase or ComboBoxItem)) return;
            _templatePass?.Cancel();
            _templatePass=new RealizationPass(this,()=>VisualTreeHelper.GetChildrenCount(Element)>0,
                ()=>{ AttachTree(Element); Refresh(); });
        }
        private void ContainerChanged(ListViewBase sender, ContainerContentChangingEventArgs args)
        {
            if (args.InRecycleQueue) return;
            var container = args.ItemContainer;
            var generation=_loadGeneration;
            // Never mutate the VM or call shell/WinRT IO during container realization.
            Element.DispatcherQueue.TryEnqueue(() => { if (_loaded && generation==_loadGeneration && container.IsLoaded) AttachTree(container); });
        }
        private void DropDownOpened(object? sender, object args)
        {
            QueuePopupTree(null);
        }
        private void DropDownClosed(object? sender, object args)=>CancelPopupPass();
        private void MenuItemsChanged(Windows.Foundation.Collections.IObservableVector<object> sender,Windows.Foundation.Collections.IVectorChangedEventArgs args)
        {
            TrackMenuItems();
            QueueMenuTree();
        }
        private void TrackMenuItems()
        {
            if (!_loaded || _menuPresenterItems is null) return;
            // Menu presenters contain actual menu elements, never a module's data source.
            // Install Loaded before native realization, including Radio's native-only Style.
            foreach (var item in _menuPresenterItems.OfType<FrameworkElement>()) Track(item);
        }
        private void QueueMenuTree()
        {
            if (!_loaded || _menuTreeQueued) return;
            _menuTreeQueued=true;
            var generation=_menuTreeGeneration;
            // Each native submenu presenter realizes independently. Observe only its own
            // item-list notifications, then visit the realized subtree after layout. Radio
            // items keep their native implicit style, including items added asynchronously.
            if (!Element.DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low,()=>
            {
                if (generation!=_menuTreeGeneration) return;
                _menuTreeQueued=false;
                if (_loaded) AttachTree(Element);
            })) _menuTreeQueued=false;
        }
        private void RegisterFlyouts()
        {
            Register(Element.ContextFlyout);
            if (Element is Button button) Register(button.Flyout);
            if (Element is DropDownButton dropdown) Register(dropdown.Flyout);
            void Register(FlyoutBase? flyout)
            {
                if (flyout is null || !_flyouts.Add(flyout)) return;
                flyout.Opened += FlyoutOpened;
                flyout.Closed += FlyoutClosed;
            }
        }
        private void FlyoutOpened(object? sender, object args)
        {
            QueuePopupTree(sender as FlyoutBase);
        }
        private void FlyoutClosed(object? sender, object args)=>CancelPopupPass();
        private void CancelPopupPass()
        {
            _popupGeneration++;
            _popupPass?.Cancel();_popupPass=null;
        }
        private void QueuePopupTree(FlyoutBase? flyout)
        {
            if (!_loaded || Element.XamlRoot is null) return;
            if (flyout is Flyout { Content: FrameworkElement content }) Track(content);
            if (flyout is MenuFlyout menu) foreach (var item in menu.Items) Track(item);
            var generation=_loadGeneration;
            CancelPopupPass();
            var popupGeneration=_popupGeneration;
            // Explicit item Styles need not contain Managed. The first scan can precede
            // native realization, so also schedule one pass after this popup has laid out.
            // Readiness checks never walk a visual tree or enumerate ComboBox data items.
            _popupPass=new RealizationPass(this,PopupIsReady,()=>AttachPopupTree(flyout));
            Element.DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low,()=>
            {
                if (_loaded && generation==_loadGeneration && popupGeneration==_popupGeneration) AttachPopupTree(flyout);
            });
        }

        private bool PopupIsReady()
        {
            if (Element is ComboBox combo)
                return combo.IsDropDownOpen && combo.ItemsPanelRoot is { IsLoaded:true, ActualWidth:>0 };
            return Element.XamlRoot is { } root && VisualTreeHelper.GetOpenPopupsForXamlRoot(root)
                .Any(popup=>popup.Child is FrameworkElement { IsLoaded:true, ActualWidth:>0 });
        }
        private void AttachPopupTree(FlyoutBase? flyout)
        {
            if (!_loaded || Element.XamlRoot is not { } root) return;
            if (flyout is Flyout { Content: { } content }) AttachTree(content);
            if (flyout is MenuFlyout menu) foreach (var item in menu.Items) AttachTree(item);
            foreach (var popup in VisualTreeHelper.GetOpenPopupsForXamlRoot(root))
                if (popup.Child is { } child) AttachTree(child);
        }

        // A short, bounded readiness hook, not a per-frame tree scan. Detach before
        // queuing the one subtree pass, and cancel on owner Unloaded/window Closed.
        private sealed class RealizationPass
        {
            private readonly ElementState _owner;
            private readonly Func<bool> _ready;
            private readonly Action _apply;
            private readonly int _generation;
            private readonly Microsoft.UI.Dispatching.DispatcherQueueTimer _deadline;
            private int _remaining=8;
            private bool _finished, _cancelled;
            internal RealizationPass(ElementState owner,Func<bool> ready,Action apply)
            {
                _owner=owner;_ready=ready;_apply=apply;_generation=owner._loadGeneration;
                _deadline=owner.Element.DispatcherQueue.CreateTimer();
                _deadline.Interval=TimeSpan.FromMilliseconds(250);
                _deadline.IsRepeating=false;
                _deadline.Tick+=OnDeadline;
                owner.Element.LayoutUpdated+=OnLayout;
                _deadline.Start();
            }
            private void OnLayout(object? sender,object args)=>CompleteIfReady(--_remaining<=0);
            private void OnDeadline(Microsoft.UI.Dispatching.DispatcherQueueTimer sender,object args)=>CompleteIfReady(true);
            private void CompleteIfReady(bool final)
            {
                if (_finished) return;
                if (!_owner._loaded || _generation!=_owner._loadGeneration) { Cancel();return; }
                var ready=_ready();
                if (!ready && !final) return;
                Detach();
                if (ready) _owner.Element.DispatcherQueue.TryEnqueue(()=>
                {
                    if (!_cancelled && _owner._loaded && _generation==_owner._loadGeneration) _apply();
                });
            }
            private void Detach()
            {
                _finished=true;
                _owner.Element.LayoutUpdated-=OnLayout;
                _deadline.Stop();_deadline.Tick-=OnDeadline;
            }
            internal void Cancel() { _cancelled=true;Detach(); }
        }

        internal void Refresh()
        {
#if KOTU_DESIGN_TEST_HARNESS
            using var diagnosticWork=MeasureDiagnosticWork(DiagnosticWorkKind.Refresh,Element);
#endif
            if (!_loaded || _applying) return;
            if (!Element.DispatcherQueue.HasThreadAccess) { Element.DispatcherQueue.TryEnqueue(Refresh); return; }
            if (IsExcluded(Element)) { Unload(); return; }
            _applying = true;
            try { Apply(DesignRuntime.Current, Palette(Element)); }
            finally { _applying = false; }
        }
    }
}
