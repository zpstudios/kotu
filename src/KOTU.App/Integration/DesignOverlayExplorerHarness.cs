#if KOTU_DESIGN_TEST_HARNESS
using System.Diagnostics;
using System.Text.Json;
using KOTU.App.Appearance;
using KOTU.App.Overlays;
using KOTU.Core.Appearance;
using KOTU.Core.Settings;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace KOTU.App.Integration;

// Visibility-only isolation using the actual compiled FileListOverlay and its path bar.
// No Show(), session start, navigation, drive enumeration, ApplyTemplate or UpdateLayout.
// Reading logical Button.Content must not connect or realize the nodes under observation.
internal sealed class DesignOverlayExplorerHarness(string output) : App
{
    private const int ExpectedCases=4;
    private readonly List<object> _timeline=[],_snapshots=[],_results=[];
    private readonly List<string> _failures=[];
    private readonly Dictionary<FrameworkElement,string> _observed=[];
    private readonly Dictionary<string,FontIcon> _icons=[];
    private readonly List<(string Stage,object[] Snapshot)> _ownershipStages=[];
    private object[] _ambient=[];
    private Window? _sentinel,_window;
    private Grid? _root,_center;
    private FileListOverlay? _overlay;
    private ExplorerPathBar? _pathBar;
    private LiveDesign.LoadedOwnershipAudit? _ownershipAudit;
    private int _caseId,_sequence;
    private string _phase="startup";

    protected override async void OnLaunched(LaunchActivatedEventArgs args)
    {
        try
        {
            AppDesignService.Initialize(new DesignPreferences(new MemorySettingsService()));
            _ambient=LiveDesign.CaptureLoadedSnapshot();
            LiveDesign.DiagnosticTreeObserver=detail=>Record("design tree",detail);
            _sentinel=new Window { Title="KOTU overlay explorer diagnostic — cleanup",Content=new Border() };
            _sentinel.AppWindow.Resize(new Windows.Graphics.SizeInt32(200,100));
            _sentinel.Activate();
            await Task.Delay(100);
            foreach(var theme in new[] { ElementTheme.Light,ElementTheme.Dark })
                foreach(var late in new[] { false,true }) await RunCase(theme,late);
        }
        catch(Exception error) { _failures.Add("Overlay explorer diagnostic exception: "+error); }
        finally
        {
            try
            {
                CloseFixture();
                await Task.Delay(100);
                _phase="final ownership";
                _ownershipAudit=LiveDesign.AuditLoadedOwnership(_ambient,(FrameworkElement)_sentinel!.Content,_ownershipStages);
                Check(_ownershipAudit.ClosedFixtureStates.Length==0 && _ownershipAudit.UnexpectedStates.Length==0,
                    "No closed-fixture or unexpected loaded state remains");
                Save(true);
                Environment.ExitCode=Passed ? 0 : 1;
            }
            catch(Exception error) { _failures.Add("Overlay explorer finalization exception: "+error);Environment.ExitCode=1;Save(false); }
            finally
            {
                LiveDesign.DiagnosticTreeObserver=null;
                _sentinel?.Close();_sentinel=null;
                Exit();
            }
        }
    }

    private async Task RunCase(ElementTheme theme,bool late)
    {
        _caseId++;_observed.Clear();_icons.Clear();
        var initialFailures=_failures.Count;
        _phase="construct shell-shaped root";
        AppDesignService.Apply("precision");
        var scaleHost=new Grid { Name="ScaleHost",RequestedTheme=theme };
        _root=new Grid { Name="RootLayout" };
        _center=new Grid { Name="CenterArea" };
        _center.ColumnDefinitions.Add(new ColumnDefinition { Width=new GridLength(0) });
        _center.ColumnDefinitions.Add(new ColumnDefinition { Width=new GridLength(10,GridUnitType.Star) });
        _center.ColumnDefinitions.Add(new ColumnDefinition { Width=new GridLength(0) });
        _root.Children.Add(_center);scaleHost.Children.Add(_root);
        _window=new Window { Title=$"KOTU overlay explorer diagnostic — {theme} {(late ? "late" : "initial")}",Content=scaleHost };
        _window.AppWindow.Resize(new Windows.Graphics.SizeInt32(1000,650));
        Observe(_root,"registered root");Observe(_center,"center area");
        // MainWindow constructs its XAML children before registering RootLayout.
        if (!late) Populate();
        LiveDesign.AttachWindow(_window,_root);
        _phase="activate root with collapsed overlay";
        _window.Activate();
        await WaitReady(()=>_root.IsLoaded,"root loaded");
        if (late) Populate();
        await Task.Delay(100);
        Capture("precision initially collapsed",false);

        _phase="make overlay visible";
        Record("visibility before",View(_overlay!));
        _overlay!.Visibility=Visibility.Visible;
        Record("visibility after",View(_overlay));
        await WaitReady(()=>_pathBar!.IsLoaded && _icons.Count==3 && _icons.Values.All(icon=>icon.IsLoaded && icon.ActualWidth>0),
            "actual overlay path icons loaded and arranged");
        await Task.Delay(100);
        Capture("precision first visible",true);
        foreach(var id in new[] { "terminal","mechanical","terminal" })
        {
            _phase="apply "+id+" while visible";
            Record("apply enter",new { Design=id });
            AppDesignService.Apply(id);
            Record("apply exit",new { Design=id });
            await Task.Delay(100);
            Capture(id+" visible",true);
        }
        _phase="collapse existing overlay";
        _overlay.Visibility=Visibility.Collapsed;
        await Task.Delay(100);
        Capture("terminal collapsed after first visibility",false);
        _phase="apply precision while collapsed";
        AppDesignService.Apply("precision");
        await Task.Delay(100);
        Capture("precision applied while collapsed",false);
        _phase="reveal existing overlay";
        _overlay.Visibility=Visibility.Visible;
        await WaitReady(()=>_icons.Count==3 && _icons.Values.All(icon=>icon.IsLoaded && icon.ActualWidth>0),"reopened path icons loaded and arranged");
        await Task.Delay(100);
        Capture("precision reopened",true);
        _phase="apply terminal after reopen";
        AppDesignService.Apply("terminal");
        await Task.Delay(100);
        Capture("terminal reopened",true);

        _ownershipStages.Add(($"case {_caseId} before close",LiveDesign.CaptureLoadedSnapshot()));
        _phase="close fixture";
        CloseFixture();
        await Task.Delay(100);
        foreach(var pair in _observed) Record("after close",new { Label=pair.Value,Element=View(pair.Key) });
        _results.Add(new { CaseId=_caseId,Theme=theme.ToString(),Construction=late ? "late" : "initial",Completed=true,
            Passed=_failures.Count==initialFailures,FailureCount=_failures.Count-initialFailures });
        Save(false);
    }

    private void Populate()
    {
        _phase="construct actual collapsed FileListOverlay";
        _overlay=new FileListOverlay(false) { Name="ListOverlay",Visibility=Visibility.Collapsed };
        _pathBar=(ExplorerPathBar)_overlay.FindName("ExplorerPath");
        Observe(_overlay,"file list overlay");
        Observe((FrameworkElement)_overlay.FindName("PathBarHost"),"path bar host");
        Observe(_pathBar,"actual overlay path bar");
        Check(!_overlay.IsLegacyListEnabled,"actual overlay uses default non-legacy path bar");
        foreach(var (name,label) in new[] { ("UpButton","Parent"),("HomeButton","Home"),("FilterButton","Filter") })
        {
            var button=(ContentControl)_pathBar.FindName(name);
            Observe(button,label+" button");
            if (button.Content is FontIcon icon) { _icons.Add(label,icon);Observe(icon,label+" icon"); }
            else Check(false,label+": actual logical FontIcon content exists");
        }
        Record("constructed before attachment",View(_overlay));
        Grid.SetColumnSpan(_overlay,3);
        _center!.Children.Add(_overlay);
        Record("attached while collapsed",View(_overlay));
    }

    private void Observe(FrameworkElement element,string label)
    {
        if (!_observed.TryAdd(element,label)) return;
        Record("observe first",new { Label=label,Element=View(element) });
        LiveDesign.ObserveLifecycle(element,detail=>Record("design lifecycle",new { Label=label,Detail=detail }));
        element.Loaded+=(_,_)=>
        {
            Record("element Loaded",new { Label=label,Element=View(element) });
            if (element is FontIcon icon) ObserveNativeText(icon,label);
        };
        element.Unloaded+=(_,_)=>Record("element Unloaded",new { Label=label,Element=View(element) });
    }

    private void ObserveNativeText(FontIcon icon,string label)
    {
        var index=0;
        foreach(var text in VisualDescendants(icon).OfType<TextBlock>()) Observe(text,label+" native text "+index++);
    }

    private void Capture(string stage,bool assertAppearance)
    {
        _phase=stage;
        foreach(var pair in _icons) ObserveNativeText(pair.Value,pair.Key+" icon");
        var design=DesignRuntime.Current;
        _snapshots.Add(new { CaseId=_caseId,Stage=stage,Design=design.Id,AssertAppearance=assertAppearance,
            Elements=_observed.Select(pair=>new { Label=pair.Value,Element=View(pair.Key) }).ToArray() });
        if (!assertAppearance) return;
        Check(_overlay?.Visibility==Visibility.Visible,"overlay visible during "+stage);
        Check(_icons.Count==3,"three logical path icons during "+stage);
        foreach(var (symbol,icon) in _icons)
        {
            var expected=DesignIconCatalog.Create(symbol,design).Text ?? DesignIconCatalog.GlyphFor(symbol).ToString();
            Check(icon.IsLoaded && icon.ActualWidth>0,stage+": loaded and arranged "+symbol);
            Check(icon.Glyph==expected && icon.FontFamily.Source==DesignIconCatalog.FontFamily(design),stage+": design glyph/font "+symbol);
            var nativeTexts=VisualDescendants(icon).OfType<TextBlock>().ToArray();
            Check(nativeTexts.Length==1,stage+": exactly one native icon TextBlock "+symbol);
            foreach(var text in nativeTexts)
                Check(text.Text==icon.Glyph && text.FontFamily.Source==icon.FontFamily.Source,stage+": native icon text/font "+symbol);
        }
    }

    private static object View(FrameworkElement element)
    {
        var ancestors=new List<object>();
        for(var parent=VisualTreeHelper.GetParent(element);parent is not null && ancestors.Count<20;parent=VisualTreeHelper.GetParent(parent))
            if (parent is FrameworkElement ancestor) ancestors.Add(new { Type=ancestor.GetType().Name,ancestor.Name,ancestor.IsLoaded,
                Visibility=ancestor.Visibility.ToString(),ancestor.ActualWidth,ancestor.ActualHeight });
        return new { Live=LiveDesign.DiagnosticElement(element),Visibility=element.Visibility.ToString(),
            VisualChildCount=VisualTreeHelper.GetChildrenCount(element),Ancestors=ancestors.ToArray() };
    }

    private void CloseFixture()
    {
        _overlay?.DisposeExplorerSession();_overlay=null;
        _window?.Close();_window=null;
    }
    private async Task WaitReady(Func<bool> ready,string label)
    {
        var watch=Stopwatch.StartNew();
        var value=ready();
        while(!value && watch.ElapsedMilliseconds<2000) { await Task.Delay(25);value=ready(); }
        Record("readiness",new { Label=label,Ready=value,ElapsedMilliseconds=watch.Elapsed.TotalMilliseconds,DeadlineMilliseconds=2000 });
        Check(value && watch.ElapsedMilliseconds<=2000,label+" within 2s");
    }
    private void Record(string action,object detail)=>_timeline.Add(new { Sequence=++_sequence,CaseId=_caseId,Phase=_phase,Action=action,
        Utc=DateTime.UtcNow,Timestamp=Stopwatch.GetTimestamp(),Design=DesignRuntime.Current.Id,Detail=detail });
    private void Check(bool condition,string message) { if (!condition) _failures.Add($"case {_caseId}: "+message); }
    private bool Passed=>_results.Count==ExpectedCases && _failures.Count==0;
    private void Save(bool completed)
    {
        var pending=output+".writing";
        File.WriteAllText(pending,JsonSerializer.Serialize(new { Mode="OverlayExplorerOnly",Completed=completed,Passed=completed && Passed,
            ExpectedCaseCount=ExpectedCases,CompletedCaseCount=_results.Count,Results=_results,Failures=_failures,
            Scope="Visibility-only isolation: actual FileListOverlay(false) in ScaleHost/RootLayout/CenterArea; initial/late collapsed construction, reveal, design transitions, collapse/reveal, Light/Dark. No FileListOverlay.Show, explorer session start, navigation, drive enumeration, template realization calls or manual design attachment to observed controls. This does not establish full shell/session equivalence or override other harness verdicts.",
            Snapshots=_snapshots,Timeline=_timeline,FinalOwnershipAudit=_ownershipAudit },new JsonSerializerOptions { WriteIndented=true }));
        File.Move(pending,output,true);
    }
    private static IEnumerable<DependencyObject> VisualDescendants(DependencyObject root)
    {
        for(var index=0;index<VisualTreeHelper.GetChildrenCount(root);index++)
        {
            var child=VisualTreeHelper.GetChild(root,index);yield return child;
            foreach(var node in VisualDescendants(child)) yield return node;
        }
    }
}
#endif
