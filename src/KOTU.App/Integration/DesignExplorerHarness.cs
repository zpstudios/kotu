#if KOTU_DESIGN_TEST_HARNESS
using System.Diagnostics;
using System.Text.Json;
using KOTU.App.Appearance;
using KOTU.App.Controls;
using KOTU.Core.Appearance;
using KOTU.Core.Routing;
using KOTU.Core.Settings;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace KOTU.App.Integration;

// A separate diagnostic branch using the actual compiled explorer controls and templates.
// It supplies one owned build-folder entry; it never navigates or reads user file contents.
internal sealed class DesignExplorerHarness(string output,DesignExplorerHarness.ListObservationMode listObservationMode=DesignExplorerHarness.ListObservationMode.LateOnly) : App
{
    internal enum ListObservationMode { LateOnly,EarlyLookupOnly,EarlyLookupEvents,EarlyLookupDetails,LegacyDetailed }
    private ListObservationMode ObservationMode=>listObservationMode;
    private const int ExpectedCases=4;
    private static DesignExplorerHarness? s_active;
    private readonly List<object> _timeline=[],_snapshots=[],_results=[];
    private readonly List<string> _failures=[];
    private readonly Dictionary<FrameworkElement,string> _observed=[];
    private readonly Dictionary<ListViewItem,ListViewBase> _listContainers=[];
    private readonly Dictionary<FrameworkElement,HashSet<string>> _listElements=[];
    private readonly List<(FrameworkElement Element,RoutedEventHandler Loaded,RoutedEventHandler Unloaded)> _listHooks=[];
    // The three early modes retain the same minimal lookup references. These are not
    // assertion labels; retired containers remain evidence and never become successes.
    private readonly List<(ListViewBase List,ListViewItem Container,FrameworkElement? Root,Grid? Content,FontIcon[] Icons)> _earlyLookups=[];
    private readonly Dictionary<FrameworkElement,int> _earlyElements=[];
    private readonly List<(int CaseId,int ElementId,bool Loaded,long Timestamp)> _earlyEventMarks=[];
    private readonly List<object> _earlyLookupSummaries=[];
    private readonly Dictionary<string,string> _launchSourceHashes=JsonSerializer.Deserialize<Dictionary<string,string>>(
        Environment.GetEnvironmentVariable("KOTU_DESIGN_EXPLORER_SOURCE_HASHES") ?? "{}") ?? [];
    private int _nextEarlyElementId;
    private bool _capturingList;
    private Window? _sentinel,_window;
    private Grid? _root;
    private ExplorerPathBar? _pathBar;
    private ThumbnailExplorer? _explorer;
    private int _caseId,_sequence;
    private string _phase="startup";
    private object[] _ambient=[];
    private readonly List<(string Stage,object[] Snapshot)> _ownershipStages=[];
    private LiveDesign.LoadedOwnershipAudit? _ownershipAudit;

    protected override async void OnLaunched(LaunchActivatedEventArgs args)
    {
        try
        {
            AppDesignService.Initialize(new DesignPreferences(new MemorySettingsService()));
            _ambient=LiveDesign.CaptureLoadedSnapshot();
            s_active=this;
            LiveDesign.DiagnosticTreeObserver=detail=>Record("design tree",detail);
            _sentinel=new Window { Title="KOTU explorer design diagnostic — cleanup",Content=new Border() };
            _sentinel.AppWindow.Resize(new Windows.Graphics.SizeInt32(200,100));
            _sentinel.Activate();
            await Task.Delay(100);
            foreach(var theme in new[] { ElementTheme.Light,ElementTheme.Dark })
                foreach(var late in new[] { false,true }) await RunCase(theme,late);
        }
        catch(Exception error) { _failures.Add("Explorer diagnostic exception: "+error); }
        finally
        {
            try
            {
                _window?.Close();_window=null;
                await Task.Delay(100);
                _phase="final ownership";
                _ownershipAudit=LiveDesign.AuditLoadedOwnership(_ambient,(FrameworkElement)_sentinel!.Content,_ownershipStages);
                Check(_ownershipAudit.ClosedFixtureStates.Length==0 && _ownershipAudit.UnexpectedStates.Length==0,
                    "No closed-fixture or unexpected loaded state remains");
                Save(true);
                Environment.ExitCode=Passed ? 0 : 1;
            }
            catch(Exception error) { _failures.Add("Explorer finalization exception: "+error); Environment.ExitCode=1; Save(false); }
            finally
            {
                DetachListObservations();
                LiveDesign.DiagnosticTreeObserver=null;s_active=null;
                _sentinel?.Close();_sentinel=null;
                Exit();
            }
        }
    }

    private async Task RunCase(ElementTheme theme,bool late)
    {
        DetachListObservations();
        _caseId++;_observed.Clear();
        var initialFailures=_failures.Count;
        _phase="create root";
        AppDesignService.Apply("precision");
        _root=new Grid { RequestedTheme=theme };
        _root.RowDefinitions.Add(new RowDefinition { Height=GridLength.Auto });
        _root.RowDefinitions.Add(new RowDefinition());
        _window=new Window { Title=$"KOTU explorer design diagnostic — {theme} {(late ? "late" : "initial")}",Content=_root };
        _window.AppWindow.Resize(new Windows.Graphics.SizeInt32(900,650));
        _root.Loaded+=(_,_)=>Record("root Loaded",LiveDesign.DiagnosticElement(_root));
        LiveDesign.AttachWindow(_window,_root);
        if (!late) Populate();
        _phase="activate root";
        _window.Activate();
        await WaitReady(()=>_root.IsLoaded,"root loaded");
        if (late) Populate();
        await WaitReady(()=>_pathBar!.IsLoaded && _explorer!.IsLoaded &&
            _observed.Any(pair=>pair.Value=="parent preview caption" && pair.Key.IsLoaded),"actual preview controls loaded");
        await Task.Delay(100);
        ObserveGeneralFolder();
        Capture("precision preview",DesignCatalog.Resolve("precision"),"preview");
        _phase="apply terminal to existing preview";
        Record("apply enter",new { Design="terminal" });
        AppDesignService.Apply("terminal");
        Record("apply exit",new { Design="terminal" });
        await Task.Delay(100);
        Capture("terminal preview",DesignCatalog.Resolve("terminal"),"preview");
        _phase="apply mechanical after terminal";
        AppDesignService.Apply("mechanical");
        await Task.Delay(100);
        Capture("mechanical preview after terminal",DesignCatalog.Resolve("mechanical"),"preview");
        AppDesignService.Apply("terminal");
        await Task.Delay(100);
        _phase="realize actual parent list";
        _explorer!.Presentation.Mode=ExplorerPresentationState.ViewMode.List;
        ShowEntries();
        await WaitReady(()=>_observed.Any(pair=>pair.Value=="parent list caption" && pair.Key.IsLoaded),"actual list controls loaded");
        await Task.Delay(100);
        ObserveGeneralListFolder();
        Capture("terminal list new",DesignCatalog.Resolve("terminal"),"list");
        _phase="apply precision to existing list";
        AppDesignService.Apply("precision");
        await Task.Delay(100);
        Capture("precision list",DesignCatalog.Resolve("precision"),"list");
        _phase="apply terminal to existing list";
        AppDesignService.Apply("terminal");
        await Task.Delay(100);
        Capture("terminal list",DesignCatalog.Resolve("terminal"),"list");
        _ownershipStages.Add(($"case {_caseId} before close",LiveDesign.CaptureLoadedSnapshot()));
        _phase="close fixture";
        _window.Close();_window=null;
        await Task.Delay(100);
        foreach(var pair in _observed) Record("after close",new { Label=pair.Value,Element=LiveDesign.DiagnosticElement(pair.Key) });
        DetachListObservations();
        _results.Add(new { CaseId=_caseId,Theme=theme.ToString(),Construction=late ? "late" : "initial",Completed=true,
            Passed=_failures.Count==initialFailures,FailureCount=_failures.Count-initialFailures });
        Save(false);
    }

    private void Populate()
    {
        _phase="construct actual controls";
        Record("construct enter",new { Design=DesignRuntime.Current.Id });
        _pathBar=new ExplorerPathBar();
        foreach(var (name,label) in new[] { ("UpButton","path up"),("HomeButton","path home"),("FilterButton","path filter") })
        {
            var button=(ContentControl)_pathBar.FindName(name);
            Observe(button,label+" button");
            if (button.Content is FontIcon icon) Observe(icon,label+" icon");
            else Check(false,label+": actual FontIcon content exists");
        }
        _explorer=new ThumbnailExplorer();
        Observe((FontIcon)_explorer.FindName("SortDirectionIcon"),"sort icon");
        Record("construct exit",new { PathBar=LiveDesign.DiagnosticElement(_pathBar),Explorer=LiveDesign.DiagnosticElement(_explorer) });
        Grid.SetRow(_explorer,1);
        _phase="attach actual controls";
        _root!.Children.Add(_pathBar);_root.Children.Add(_explorer);
        ShowEntries();
    }

    private void ShowEntries()
    {
        var folder=AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar);
        var ownedFolder=Path.Combine(folder,"Assets");
        var stamp=new DateTime(2026,11,22,0,0,0,DateTimeKind.Utc);
        // The actual IsFolder branch paints its canonical icon without shell preview work.
        _explorer!.ShowEntries(folder,[new ExplorerListing.Entry(ownedFolder,"Owned build assets",true,0,stamp,stamp,Attributes:FileAttributes.Directory)]);
    }

    internal static void ObserveParent(Button button)
    {
        if (s_active is not { } owner) return;
        var content=button.Content as Grid;
        var kind=content?.Name=="ParentListRow" ? "list" : "preview";
        owner.Observe(button,$"parent {kind} button");
        if (content is null) { owner.Check(false,$"parent {kind}: actual Grid content exists"); return; }
        var descendants=LogicalDescendants(content).ToArray();
        var icons=descendants.OfType<FontIcon>().ToArray();
        if (icons.Length!=2) owner.Check(false,$"parent {kind}: exactly two authored FontIcons");
        else { owner.Observe(icons[0],$"parent {kind} folder");owner.Observe(icons[1],$"parent {kind} up"); }
        var captions=descendants.OfType<TextBlock>().Where(text=>text.Text=="Up one folder").ToArray();
        if (captions.Length!=1) owner.Check(false,$"parent {kind}: one authored caption");
        else owner.Observe(captions[0],$"parent {kind} caption");
        owner.Record("parent button Loaded handler",new { Kind=kind,Button=LiveDesign.DiagnosticElement(button) });
    }

    private void ObserveGeneralFolder()
    {
        var grid=(GridView)_explorer!.FindName("TileGrid");
        var item=grid.ContainerFromIndex(1) as GridViewItem;
        var icons=item is null ? [] : VisualDescendants(item).OfType<FontIcon>().Where(icon=>icon.Glyph=="\uE8B7").ToArray();
        Check(icons.Length==1,"one actual general-folder FontIcon before design switch");
        if (icons.Length==1) Observe(icons[0],"general folder icon");
    }

    private void ObserveGeneralListFolder()
    {
        _phase="observe actual general-folder list icon";
        var list=(ListView)_explorer!.FindName("DetailList");
        var item=list.ContainerFromIndex(1) as ListViewItem;
        var ownedFolder=Path.Combine(AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar),"Assets");
        Check(item?.Content is ExplorerEntryVm { IsFolder:true } vm && vm.Path==ownedFolder,
            "actual list container belongs to the owned folder entry");
        if (item is not null) Observe(item,"general list folder container");
        var root=item?.ContentTemplateRoot as Grid;
        var content=root?.FindName("ExplorerTileContent") as Grid;
        // Identify the authored direct child by its template column, not its current
        // glyph: a correctly connected Terminal icon has already changed that glyph.
        var icons=content is null ? [] : content.Children.OfType<FontIcon>().Where(icon=>Grid.GetColumn(icon)==1).ToArray();
        Check(icons.Length==1,"one actual general-folder list FontIcon in the authored icon column");
        if (icons.Length==1) Observe(icons[0],"general list folder icon");
        if (listObservationMode==ListObservationMode.LegacyDetailed && item is not null)
            CaptureListStructure(list,item,"original 100ms first list observation");
    }

    internal static void ObserveListContainer(ListViewBase list,ContainerContentChangingEventArgs args)
    {
        // Return before even reading the CCC arguments in the run28-equivalent mode.
        if (s_active is not { } owner || owner.ObservationMode==ListObservationMode.LateOnly) return;
        if (args.ItemContainer is not ListViewItem item) return;
        var ownedFolder=Path.Combine(AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar),"Assets");
        var owned=args.Item is ExplorerEntryVm { IsFolder:true } vm && vm.Path==ownedFolder;
        if (!owned && !owner._listContainers.ContainsKey(item)) return;
        owner._listContainers[item]=list;
        if (owner.ObservationMode!=ListObservationMode.LegacyDetailed)
        {
            owner.LookupEarlyListElements(list,item);
            if (owner.ObservationMode==ListObservationMode.EarlyLookupDetails)
                owner.CaptureListStructure(list,item,"ContainerContentChanging phase "+args.Phase,subscribe:false);
            return;
        }
        owner.Record("list ContainerContentChanging entry",new { args.Phase,args.InRecycleQueue,OwnedArgument=owned,
            Argument=ListVmView(args.Item),ContainerContent=ListVmView(item.Content),Container=ListElementView(item) });
        owner.CaptureListStructure(list,item,"ContainerContentChanging phase "+args.Phase);
    }

    private void LookupEarlyListElements(ListViewBase list,ListViewItem container)
    {
        var root=container.ContentTemplateRoot as FrameworkElement;
        var content=(root as Grid)?.FindName("ExplorerTileContent") as Grid;
        var icons=content is null ? [] : content.Children.OfType<FontIcon>().Where(icon=>Grid.GetColumn(icon)==1).ToArray();
        _earlyLookups.Add((list,container,root,content,icons));
        RetainEarlyListElement(container);
        if (root is not null) RetainEarlyListElement(root);
        if (content is not null) RetainEarlyListElement(content);
        foreach(var icon in icons) RetainEarlyListElement(icon);
    }

    private void RetainEarlyListElement(FrameworkElement element)
    {
        if (_earlyElements.ContainsKey(element)) return;
        var id=++_nextEarlyElementId;
        _earlyElements.Add(element,id);
        if (listObservationMode!=ListObservationMode.EarlyLookupEvents) return;
        var caseId=_caseId;
        // No UI property, identity, payload or tree read is allowed in these callbacks.
        RoutedEventHandler loaded=(_,_)=>_earlyEventMarks.Add((caseId,id,true,Stopwatch.GetTimestamp()));
        RoutedEventHandler unloaded=(_,_)=>_earlyEventMarks.Add((caseId,id,false,Stopwatch.GetTimestamp()));
        element.Loaded+=loaded;element.Unloaded+=unloaded;
        _listHooks.Add((element,loaded,unloaded));
    }

    private void ObserveListElement(FrameworkElement element,string role,ListViewBase list,ListViewItem container)
    {
        if (_listElements.TryGetValue(element,out var existing)) { existing.Add(role);return; }
        var roles=new HashSet<string> { role };
        _listElements.Add(element,roles);
        // This registry is separate from the original assertion labels and never Track()s.
        RoutedEventHandler loaded=(_,_)=>
        {
            Record("list element Loaded",new { Roles=roles.ToArray(),Element=ListElementView(element) });
            CaptureListStructure(list,container,"Loaded "+role);
        };
        RoutedEventHandler unloaded=(_,_)=>
        {
            Record("list element Unloaded",new { Roles=roles.ToArray(),Element=ListElementView(element) });
            CaptureListStructure(list,container,"Unloaded "+role);
        };
        element.Loaded+=loaded;element.Unloaded+=unloaded;
        _listHooks.Add((element,loaded,unloaded));
        Record("list observe first",new { Roles=roles.ToArray(),Element=ListElementView(element) });
    }

    private void CaptureListStructure(ListViewBase list,ListViewItem container,string trigger,bool subscribe=true)
    {
        if (_capturingList) return;
        _capturingList=true;
        try
        {
            var root=container.ContentTemplateRoot as FrameworkElement;
            var namedContent=(root as Grid)?.FindName("ExplorerTileContent") as Grid;
            var logicalIcons=namedContent is null ? [] : namedContent.Children.OfType<FontIcon>().Where(icon=>Grid.GetColumn(icon)==1).ToArray();
            var visual=VisualDescendants(container).OfType<FrameworkElement>().ToArray();
            var visualContents=visual.OfType<Grid>().Where(grid=>grid.Name=="ExplorerTileContent").ToArray();
            var visualIcons=visual.OfType<FontIcon>().Where(icon=>Grid.GetColumn(icon)==1 &&
                VisualTreeHelper.GetParent(icon) is Grid { Name:"ExplorerTileContent" }).ToArray();
            if (subscribe)
            {
                ObserveListElement(container,"container",list,container);
                if (root is not null) ObserveListElement(root,"ContentTemplateRoot",list,container);
                if (namedContent is not null) ObserveListElement(namedContent,"FindName content",list,container);
                foreach(var content in visualContents) ObserveListElement(content,"visual content",list,container);
                foreach(var icon in logicalIcons) ObserveListElement(icon,"logical column icon",list,container);
                foreach(var icon in visualIcons) ObserveListElement(icon,"visual column icon",list,container);
                foreach(var icon in logicalIcons.Concat(visualIcons).Distinct())
                    foreach(var child in VisualDescendants(icon).OfType<FrameworkElement>())
                        ObserveListElement(child,"native icon child",list,container);
            }
            var nativeOwner=ItemsControl.ItemsControlFromItemContainer(container);
            var viewport=VisualDescendants(list).OfType<ScrollViewer>().FirstOrDefault();
            var current=list.ContainerFromIndex(1);
            var ownedFolder=Path.Combine(AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar),"Assets");
            Record("list structure",new { Trigger=trigger,
                Container=ListElementView(container),ContainerContent=ListVmView(container.Content),ContainerDataContext=ListVmView(container.DataContext),
                ContentIsOwnedFolder=container.Content is ExplorerEntryVm { IsFolder:true } vm && vm.Path==ownedFolder,
                CurrentIndexOne=ListObjectView(current),ContainerIsCurrentIndexOne=SameNative(container,current),
                ItemsOwner=ListObjectView(nativeOwner),OwnerIsObservedList=SameNative(nativeOwner,list),List=ListElementView(list),
                ContentTemplateRoot=root is null ? null : ListElementView(root),RootIsVisualDescendant=visual.Any(element=>SameNative(element,root)),
                FindNameContent=namedContent is null ? null : ListElementView(namedContent),FindNameIsVisualDescendant=visual.Any(element=>SameNative(element,namedContent)),
                LogicalIcons=logicalIcons.Select(icon=>new { Element=ListElementView(icon),
                    IsVisualDescendant=visual.Any(element=>SameNative(element,icon)),MatchesVisualColumnIcons=visualIcons.Select(other=>SameNative(icon,other)).ToArray() }).ToArray(),
                VisualContents=visualContents.Select(ListElementView).ToArray(),VisualColumnIcons=visualIcons.Select(ListElementView).ToArray(),
                VisualTree=visual.Select(element=>new { Element=ListObjectView(element),Parent=ListObjectView(VisualTreeHelper.GetParent(element)),
                    element.IsLoaded,Visibility=element.Visibility.ToString(),element.ActualWidth,element.ActualHeight }).ToArray(),
                Viewport=viewport is null ? null : new { Element=ListElementView(viewport),viewport.ViewportWidth,viewport.ViewportHeight,
                    viewport.ExtentWidth,viewport.ExtentHeight,viewport.HorizontalOffset,viewport.VerticalOffset } });
        }
        finally { _capturingList=false; }
    }

    private void DetachListObservations()
    {
        // Normal case cleanup follows all three original assertion stages and closure.
        // This also handles abort cleanup without reading any element's UI state.
        if (_earlyLookups.Count!=0)
            _earlyLookupSummaries.Add(new { CaseId=_caseId,LookupCount=_earlyLookups.Count,
                RetainedElements=_earlyElements.Count,RetainedIcons=_earlyLookups.SelectMany(lookup=>lookup.Icons).Distinct().Count(),
                Lookups=_earlyLookups.Select(lookup=>new { ContainerId=_earlyElements[lookup.Container],
                    RootId=lookup.Root is null ? 0 : _earlyElements[lookup.Root],ContentId=lookup.Content is null ? 0 : _earlyElements[lookup.Content],
                    IconIds=lookup.Icons.Select(icon=>_earlyElements[icon]).ToArray() }).ToArray(),
                AssertionLabels=_observed.Select(pair=>new { Label=pair.Value,ElementId=_earlyElements.GetValueOrDefault(pair.Key) }).ToArray() });
        foreach(var (element,loaded,unloaded) in _listHooks) { element.Loaded-=loaded;element.Unloaded-=unloaded; }
        _listHooks.Clear();_listElements.Clear();_listContainers.Clear();
        _earlyLookups.Clear();_earlyElements.Clear();
    }
    private static object ListVmView(object? value)=>value is ExplorerEntryVm vm
        ? new { Type=value.GetType().Name,vm.Path,vm.Name,vm.IsFolder }
        : new { Type=value?.GetType().Name };
    private static nint NativeIdentity(object? value)=>value is WinRT.IWinRTObject native ? native.NativeObject.ThisPtr : 0;
    private static bool SameNative(object? first,object? second)=>NativeIdentity(first)!=0 && NativeIdentity(first)==NativeIdentity(second);
    private static object ListObjectView(object? value)=>new { Type=value?.GetType().Name,Name=(value as FrameworkElement)?.Name,
        NativeIdentity=$"0x{NativeIdentity(value):X}" };
    private static object ListElementView(FrameworkElement element)
    {
        var ancestors=new List<object>();
        for(var parent=VisualTreeHelper.GetParent(element);parent is not null && ancestors.Count<24;parent=VisualTreeHelper.GetParent(parent))
            if (parent is FrameworkElement ancestor) ancestors.Add(new { Element=ListObjectView(ancestor),ancestor.IsLoaded,
                Visibility=ancestor.Visibility.ToString(),ancestor.ActualWidth,ancestor.ActualHeight });
        return new { Live=LiveDesign.DiagnosticElement(element),Visibility=element.Visibility.ToString(),element.IsHitTestVisible,
            VisualParent=ListObjectView(VisualTreeHelper.GetParent(element)),VisualChildCount=VisualTreeHelper.GetChildrenCount(element),
            Ancestors=ancestors.ToArray() };
    }

    private void Observe(FrameworkElement element,string label)
    {
        if (!_observed.TryAdd(element,label)) return;
        Record("observe first",new { Label=label,Element=LiveDesign.DiagnosticElement(element) });
        LiveDesign.ObserveLifecycle(element,detail=>Record("design lifecycle",new { Label=label,Detail=detail }));
        element.Loaded+=(_,_)=>Record("element Loaded",new { Label=label,Element=LiveDesign.DiagnosticElement(element) });
        element.Unloaded+=(_,_)=>Record("element Unloaded",new { Label=label,Element=LiveDesign.DiagnosticElement(element) });
    }

    private void Capture(string stage,DesignDefinition design,string mode)
    {
        _phase=stage;
        if (mode=="list" && listObservationMode==ListObservationMode.LegacyDetailed)
            foreach(var (container,list) in _listContainers.ToArray()) CaptureListStructure(list,container,"existing assertion stage "+stage);
        foreach(var pair in _observed.Where(pair=>pair.Key is FontIcon).ToArray())
            foreach(var child in VisualDescendants(pair.Key).OfType<TextBlock>()) Observe(child,pair.Value+" native text");
        var samples=_observed.Select(pair=>new { Label=pair.Value,Element=LiveDesign.DiagnosticElement(pair.Key),
            NativeChildren=pair.Key is FontIcon ? VisualDescendants(pair.Key).OfType<FrameworkElement>().Select(LiveDesign.DiagnosticElement).ToArray() : [] }).ToArray();
        _snapshots.Add(new { CaseId=_caseId,Stage=stage,Design=design.Id,Mode=mode,Elements=samples });
        var labels=new List<string> { "path up icon","path home icon","path filter icon",$"parent {mode} folder",$"parent {mode} up",$"parent {mode} caption" };
        if (mode=="preview") labels.Add("general folder icon");
        if (mode=="list") labels.Add("general list folder icon");
        foreach(var label in labels)
        {
            var elements=_observed.Where(pair=>pair.Value==label && pair.Key.IsLoaded).Select(pair=>pair.Key).ToArray();
            Check(elements.Length==1,stage+": one loaded "+label);
            if (elements.Length!=1) continue;
            var element=elements[0];
            if (element is FontIcon icon)
            {
                var symbol=label.Contains("folder",StringComparison.Ordinal) ? "Folder" : label.Contains("home",StringComparison.Ordinal) ? "Home" :
                    label.Contains("filter",StringComparison.Ordinal) ? "Filter" : "Parent";
                var expected=DesignIconCatalog.Create(symbol,design).Text ?? DesignIconCatalog.GlyphFor(symbol).ToString();
                Check(icon.Glyph==expected && icon.FontFamily.Source==DesignIconCatalog.FontFamily(design),stage+": design glyph/font "+label);
                var nativeTexts=VisualDescendants(icon).OfType<TextBlock>().ToArray();
                Check(nativeTexts.Length==1,stage+": exactly one native icon TextBlock "+label);
                foreach(var text in nativeTexts)
                    Check(text.Text==icon.Glyph && text.FontFamily.Source==icon.FontFamily.Source,stage+": native icon text/font "+label);
            }
            else if (element is TextBlock text) Check(text.FontFamily.Source==design.FontFamily,stage+": design font "+label);
        }
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
        File.WriteAllText(pending,JsonSerializer.Serialize(new { Mode="ExplorerOnly",ListObservationMode=listObservationMode.ToString(),
            Completed=completed,Passed=completed && Passed,
            LaunchSourceHashes=_launchSourceHashes,SourceHashProvenance="Launcher-reported canonical/staged source hashes; the launch receipt separately records the executed DLL hash.",
            ExpectedCaseCount=ExpectedCases,CompletedCaseCount=_results.Count,Results=_results,Failures=_failures,
            Scope="Actual ExplorerPathBar and ThumbnailExplorer, initial/late construction, preview/list, Light/Dark. This does not override the separate menu harness verdicts.",
            ListObservationScope="Only the original current index-one owned-folder assertion labels affect pass/fail. Detailed retired-container observations are evidence only. The four controlled modes differ only at CCC entry; LegacyDetailed additionally retains run29 callbacks and detailed assertion-stage reads.",
            EarlyLookupSummaries=_earlyLookupSummaries,
            EarlyEventMarks=_earlyEventMarks.Select(mark=>new { mark.CaseId,mark.ElementId,mark.Loaded,mark.Timestamp }).ToArray(),
            Snapshots=_snapshots,Timeline=_timeline,FinalOwnershipAudit=_ownershipAudit },new JsonSerializerOptions { WriteIndented=true }));
        File.Move(pending,output,true);
    }
    private static IEnumerable<DependencyObject> LogicalDescendants(DependencyObject root)
    {
        IEnumerable<DependencyObject> children=root switch
        {
            Panel panel=>panel.Children,
            Viewbox { Child:{ } child }=>[child],
            ContentControl { Content:DependencyObject child }=>[child],
            _=>[],
        };
        foreach(var child in children) { yield return child;foreach(var node in LogicalDescendants(child)) yield return node; }
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
