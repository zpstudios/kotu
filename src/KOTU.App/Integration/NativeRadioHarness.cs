#if KOTU_DESIGN_TEST_HARNESS
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Automation.Provider;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Markup;
using Microsoft.UI.Xaml.Media;

namespace KOTU.App.Integration;

// This conditional entry branch uses only the native metadata provider and controls.
// It neither constructs the product Application nor loads its XAML or design services.
internal sealed class NativeRadioHarness : Application, IXamlMetadataProvider
{
    private const int RepetitionsPerTheme=4;
    private const int ExpectedCases=RepetitionsPerTheme*2;
    private readonly string _output;
    private readonly IXamlMetadataProvider _metadata=new Microsoft.UI.Xaml.XamlTypeInfo.XamlControlsXamlMetaDataProvider();
    private readonly List<CaseResult> _results=[];
    private readonly List<string> _failures=[],_configurationFailures=[];
    private readonly List<object> _resourceAudits=[],_presenterObservations=[],_radioObservations=[],_timeline=[];
    private XamlControlsResources? _controls;
    private Window? _sentinel;
    private Fixture? _fixture;
    private object? _runtime;
    private sealed record CaseResult(string Theme,int Iteration,int FixtureId,bool Completed,bool Passed,int FailureCount,int ConfigurationFailureCount);

    internal NativeRadioHarness(string output)
    {
        _output=output;
        UnhandledException+=OnUnhandledException;
    }
    public IXamlType GetXamlType(Type type)=>_metadata.GetXamlType(type);
    public IXamlType GetXamlType(string fullName)=>_metadata.GetXamlType(fullName);
    public XmlnsDefinition[] GetXmlnsDefinitions()=>_metadata.GetXmlnsDefinitions();

    protected override async void OnLaunched(LaunchActivatedEventArgs args)
    {
        try
        {
            Resources=new ResourceDictionary();
            _controls=new XamlControlsResources();
            Resources.MergedDictionaries.Add(_controls);
            // Keep an owned blank window alive until final JSON and exit status are saved.
            _sentinel=new Window { Title="KOTU native radio comparison — cleanup",Content=new Border() };
            _sentinel.AppWindow.Resize(new Windows.Graphics.SizeInt32(200,100));
            _sentinel.Activate();
            await Settle();
            _runtime=CaptureRuntime();
            AuditResources("initial");
            SaveResult(completed:false);
            foreach(var theme in new[] { ElementTheme.Light,ElementTheme.Dark })
                for(var iteration=1;iteration<=RepetitionsPerTheme;iteration++)
                {
                    await RunCase(theme,iteration);
                    AuditResources($"after {theme} iteration {iteration}");
                    SaveResult(completed:false);
                }
        }
        catch(Exception error) { _failures.Add("Native-only run exception: "+error); }
        finally
        {
            try
            {
                _fixture?.Close();_fixture=null;
                await Settle();
                AuditResources("final");
                SaveResult(completed:true);
                Environment.ExitCode=AllPassed ? 0 : 1;
            }
            catch(Exception error)
            {
                _failures.Add("Native-only finalization exception: "+error);
                Environment.ExitCode=1;
                SaveResult(completed:false);
            }
            finally
            {
                UnhandledException-=OnUnhandledException;
                _sentinel?.Close();_sentinel=null;
                Exit();
            }
        }
    }

    private bool ConfigurationPassed=>_configurationFailures.Count==0 && _resourceAudits.Count==ExpectedCases+2 &&
        _presenterObservations.Count==ExpectedCases*8 && _radioObservations.Count==ExpectedCases*5;
    private bool AllPassed=>ConfigurationPassed && _failures.Count==0 && _results.Count==ExpectedCases && _results.All(result=>result.Completed && result.Passed);
    private static Task Settle()=>Task.Delay(100);
    private void Check(bool condition,string message) { if (!condition) _failures.Add(message); }
    private void Configure(bool condition,string message) { if (!condition) _configurationFailures.Add(message); }
    private void OnUnhandledException(object sender,Microsoft.UI.Xaml.UnhandledExceptionEventArgs args)
    {
        _failures.Add("Native-only unhandled exception: "+args.Exception);
        Environment.ExitCode=1;
        SaveResult(completed:false);
    }
    internal static void WriteStartupFailure(string output,Exception error)=>WriteAtomic(output,new {
        Mode="NativeOnly",Completed=false,Passed=false,ConfigurationPassed=false,ExpectedCaseCount=ExpectedCases,CompletedCaseCount=0,Error=error.ToString() });
    private void SaveResult(bool completed)=>WriteAtomic(_output,new {
        Mode="NativeOnly",Completed=completed,Passed=completed && AllPassed,ConfigurationPassed=completed && ConfigurationPassed,
        ExpectedCaseCount=ExpectedCases,CompletedCaseCount=_results.Count(result=>result.Completed),AttemptedCaseCount=_results.Count,
        RepetitionsPerTheme,ExpectedPresenterObservations=ExpectedCases*8,ExpectedRadioObservations=ExpectedCases*5,
        Scope="Separate Application with only standard XamlControlsResources. No design is applied. These results never replace or override the separate product and shared-resource comparison results.",
        Runtime=_runtime,Results=_results,Failures=_failures,ConfigurationFailures=_configurationFailures,
        ResourceAudits=_resourceAudits,PresenterObservations=_presenterObservations,RadioObservations=_radioObservations,MenuTimeline=_timeline });
    private static void WriteAtomic(string output,object value)
    {
        var pending=output+".writing";
        File.WriteAllText(pending,JsonSerializer.Serialize(value,new JsonSerializerOptions { WriteIndented=true }));
        File.Move(pending,output,true);
    }
    private object CaptureRuntime()
    {
        using var process=Process.GetCurrentProcess();
        var native=process.Modules.Cast<ProcessModule>().Where(module=>string.Equals(module.ModuleName,"Microsoft.UI.Xaml.dll",StringComparison.OrdinalIgnoreCase))
            .Select(module=>FileSnapshot(module.FileName)).ToArray();
        Configure(native.Length==1,"Exactly one loaded Microsoft.UI.Xaml.dll must identify the native runtime.");
        return new { ProcessId=process.Id,NativeWinUI=native,ManagedWinUI=FileSnapshot(typeof(Application).Assembly.Location),
            ExecutingAssembly=FileSnapshot(typeof(NativeRadioHarness).Assembly.Location),MetadataProvider=Snapshot(_metadata) };
    }
    private static object FileSnapshot(string path)
    {
        using var stream=File.OpenRead(path);
        var version=FileVersionInfo.GetVersionInfo(path);
        return new { Path=path,version.FileVersion,version.ProductVersion,SHA256=Convert.ToHexString(SHA256.HashData(stream)) };
    }
    private void AuditResources(string stage)
    {
        var current=Current;
        var ancestry=new List<string>();
        for(Type? type=current.GetType();type is not null;type=type.BaseType) ancestry.Add(type.FullName ?? type.Name);
        var isProductApplication=ancestry.Contains("KOTU.App.App",StringComparer.Ordinal);
        var keys=Resources.Keys.Select(key=>key?.ToString()).ToArray();
        var merged=Resources.MergedDictionaries.ToArray();
        var forbiddenKeys=new[] { "KotuMenuFlyoutPresenterTemplate","KotuControlFontFamily","KotuUiFontSize","KotuHeadingFontFamily" };
        var presentKeys=forbiddenKeys.Where(Resources.ContainsKey).ToArray();
        var valid=current.GetType()==typeof(NativeRadioHarness) && !isProductApplication && SameNative(current,this) &&
            keys.Length==0 && Resources.ThemeDictionaries.Count==0 && merged.Length==1 &&
            merged[0].GetType()==typeof(XamlControlsResources) && SameNative(merged[0],_controls) && presentKeys.Length==0 &&
            _metadata.GetType()==typeof(Microsoft.UI.Xaml.XamlTypeInfo.XamlControlsXamlMetaDataProvider);
        _resourceAudits.Add(new { Stage=stage,Application=Snapshot(current),Ancestry=ancestry,IsProductApplication=isProductApplication,
            Resources=Snapshot(Resources),LocalKeys=keys,LocalKeyCount=keys.Length,ThemeDictionaryCount=Resources.ThemeDictionaries.Count,
            MergedCount=merged.Length,Merged=merged.Select(Snapshot).ToArray(),ExpectedControls=Snapshot(_controls),
            MetadataProvider=Snapshot(_metadata),ForbiddenKeysPresent=presentKeys,Passed=valid });
        Configure(valid,"Native-only Application/resources/provider configuration: "+stage);
    }

    private async Task RunCase(ElementTheme theme,int iteration)
    {
        var before=_failures.Count;var configurationBefore=_configurationFailures.Count;var completed=false;
        var fixtureId=0;
        try
        {
            _fixture=new Fixture(theme,iteration,_timeline.Add);
            var fixture=_fixture;fixtureId=fixture.Id;
            fixture.Window.Activate();await Settle();
            await OpenMenu(fixture,"first opening");await Settle();
            CheckMenuConfiguration(fixture,"first top-level opening",includeNested:false);
            var lateTop=new MenuFlyoutItem { Text="Late menu item" };
            fixture.Menu.Items.Add(lateTop);await Settle();
            fixture.Menu.Items.Remove(lateTop);
            await CloseMenu(fixture,"first close");await Settle();
            await OpenMenu(fixture,"second opening");await Settle();
            CheckMenuConfiguration(fixture,"second top-level opening",includeNested:false);
            var peer=FrameworkElementAutomationPeer.CreatePeerForElement(fixture.Submenu);
            fixture.Pattern=peer?.GetPattern(PatternInterface.ExpandCollapse) as IExpandCollapseProvider;
            Check(fixture.Pattern is not null,fixture.Label+": native submenu expand/collapse provider");
            if (fixture.Pattern is null) return;
            var pattern=fixture.Pattern;
            var late=new RadioMenuFlyoutItem { Text="Late nested device",GroupName=fixture.Group };
            using var observation=fixture.ObserveLate(late);
            await AwaitState(fixture,"expand initial submenu",()=>fixture.Menu.IsOpen && pattern.ExpandCollapseState==ExpandCollapseState.Expanded && fixture.Initial.IsLoaded,
                pattern.Expand,fixture.Initial);
            await Settle();fixture.Trace("before initial nested checks");
            CheckMenuConfiguration(fixture,"initial nested submenu",includeNested:true);
            var initialTemplate=fixture.Initial.Template;
            await AwaitState(fixture,"add late nested radio",()=>fixture.Menu.IsOpen && pattern.ExpandCollapseState==ExpandCollapseState.Expanded && fixture.Initial.IsLoaded && late.IsLoaded,
                ()=>fixture.Submenu.Items.Add(late),fixture.Initial,late);
            await Settle();fixture.Trace("after awaited late add");
            CheckMenuConfiguration(fixture,"after late nested add",includeNested:true,includeLate:true);
            fixture.Trace("before late IsChecked setter");late.IsChecked=true;fixture.Trace("after synchronous late IsChecked setter");
            await Settle();fixture.Trace("after awaited late IsChecked setter");
            Check(late.IsChecked && !fixture.Initial.IsChecked,fixture.Label+$": native nested radio group state group={fixture.Group} lateChecked={late.IsChecked} initialChecked={fixture.Initial.IsChecked}");
            Check(SameNative(initialTemplate,fixture.Initial.Template),fixture.Label+": nested native template identity");
            await AwaitState(fixture,"collapse before reopen",()=>pattern.ExpandCollapseState==ExpandCollapseState.Collapsed,pattern.Collapse,fixture.Initial,late);
            await Settle();
            await AwaitState(fixture,"reopen late submenu",()=>fixture.Menu.IsOpen && pattern.ExpandCollapseState==ExpandCollapseState.Expanded && fixture.Initial.IsLoaded && late.IsLoaded,
                pattern.Expand,fixture.Initial,late);
            await Settle();
            CheckMenuConfiguration(fixture,"reopened nested submenu",includeNested:true,includeLate:true);
            await AwaitState(fixture,"final submenu collapse",()=>pattern.ExpandCollapseState==ExpandCollapseState.Collapsed,pattern.Collapse,fixture.Initial,late);
            await Settle();fixture.Trace("before removing late radio");fixture.Submenu.Items.Remove(late);fixture.Trace("after removing late radio");
            fixture.Pattern=null;
            await CloseMenu(fixture,"after nested checks");
            completed=true;
        }
        catch(Exception error) { _failures.Add($"{theme} iteration {iteration}: native-only case exception: {error}"); }
        finally
        {
            try { _fixture?.Close(); }
            catch(Exception error) { completed=false;_failures.Add($"{theme} iteration {iteration}: close exception: {error}"); }
            _fixture=null;await Settle();
            _results.Add(new CaseResult(theme.ToString(),iteration,fixtureId,completed,
                completed && _failures.Count==before && _configurationFailures.Count==configurationBefore,
                _failures.Count-before,_configurationFailures.Count-configurationBefore));
        }
    }
    private Task OpenMenu(Fixture fixture,string stage)
    {
        fixture.Stage=stage;var opened=fixture.Opened;
        var items=fixture.Menu.Items.OfType<FrameworkElement>().ToArray();
        return AwaitState(fixture,"open top-level menu",()=>fixture.Opened>opened && fixture.Phase=="Opened" && fixture.Menu.IsOpen && items.All(item=>item.IsLoaded),
            ()=>fixture.Menu.ShowAt(fixture.Anchor),items);
    }
    private Task CloseMenu(Fixture fixture,string stage)
    {
        fixture.Stage=stage;var closed=fixture.Closed;
        var alreadyClosed=fixture.Phase=="Closed" && !fixture.Menu.IsOpen;
        return AwaitState(fixture,"close top-level menu",()=> (alreadyClosed || fixture.Closed>closed) && fixture.Phase=="Closed" && !fixture.Menu.IsOpen,
            fixture.Menu.Hide,fixture.Menu.Items.OfType<FrameworkElement>().ToArray());
    }
    private async Task AwaitState(Fixture fixture,string action,Func<bool> ready,Action transition,params FrameworkElement[] elements)
    {
        var completion=new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var active=true;var queued=false;long? token=null;
        void Probe()
        {
            queued=false;if (!active || completion.Task.IsCompleted) return;
            try { if (fixture.IsClosed) completion.TrySetResult(false);else if (ready()) completion.TrySetResult(true); }
            catch(Exception error) { completion.TrySetException(error); }
        }
        void QueueProbe()
        {
            if (!active || queued) return;queued=true;
            if (!fixture.Root.DispatcherQueue.TryEnqueue(Probe)) { queued=false;completion.TrySetResult(false); }
        }
        void OnElementState(object sender,RoutedEventArgs args)=>QueueProbe();
        using var deadline=new CancellationTokenSource();
        try
        {
            fixture.Changed+=QueueProbe;
            foreach(var element in elements) { element.Loaded+=OnElementState;element.Unloaded+=OnElementState; }
            token=fixture.Menu.RegisterPropertyChangedCallback(FlyoutBase.IsOpenProperty,(_,_)=>QueueProbe());
            fixture.Trace("before "+action);transition();fixture.Trace("after synchronous "+action);QueueProbe();
            var timeout=Task.Delay(TimeSpan.FromSeconds(2),deadline.Token);
            var finished=await Task.WhenAny(completion.Task,timeout);
            var passed=finished==completion.Task && await completion.Task;
            fixture.Trace((passed ? "ready " : "timeout or closed ")+action);
            Check(passed,fixture.Label+": "+fixture.Stage+" "+action+" reached event-backed state within 2s");
        }
        finally
        {
            active=false;deadline.Cancel();fixture.Changed-=QueueProbe;
            foreach(var element in elements) { element.Loaded-=OnElementState;element.Unloaded-=OnElementState; }
            if (token is { } registered) fixture.Menu.UnregisterPropertyChangedCallback(FlyoutBase.IsOpenProperty,registered);
        }
    }

    private void CheckMenuConfiguration(Fixture fixture,string stage,bool includeNested,bool includeLate=false)
    {
        ObservePresenter("top",fixture.Submenu);
        if (!includeNested) return;
        ObservePresenter("nested",fixture.Initial);
        ObserveRadio("initial",fixture.Initial);
        if (includeLate) ObserveRadio("late",fixture.Late!);
        void ObservePresenter(string location,FrameworkElement item)
        {
            MenuFlyoutPresenter? presenter=null;
            for(var parent=VisualTreeHelper.GetParent(item);parent is not null;parent=VisualTreeHelper.GetParent(parent))
                if (parent is MenuFlyoutPresenter found) { presenter=found;break; }
            var childCount=presenter is null ? 0 : VisualTreeHelper.GetChildrenCount(presenter);
            var root=childCount>0 ? VisualTreeHelper.GetChild(presenter!,0) : null;
            var border=root as Border;var scroll=border?.Child as ScrollViewer;var items=scroll?.Content as ItemsPresenter;
            var valid=presenter is { IsLoaded:true,Template:not null } && NativeIdentity(presenter.Template)!=0 &&
                childCount==1 && border is not null && scroll is { Name:"MenuFlyoutPresenterScrollViewer" } && items is not null &&
                fixture.Menu.MenuFlyoutPresenterStyle is null && fixture.Root.Resources.Count==0 && fixture.Root.Resources.MergedDictionaries.Count==0;
            _presenterObservations.Add(new { Fixture=fixture.Id,Theme=fixture.Theme.ToString(),fixture.Iteration,Stage=stage,Location=location,
                Presenter=Snapshot(presenter),Style=Snapshot(presenter?.Style),Template=Snapshot(presenter?.Template),ItemsPanelRoot=Snapshot(presenter?.ItemsPanelRoot),
                MenuPresenterStyle=Snapshot(fixture.Menu.MenuFlyoutPresenterStyle),VisualChildCount=childCount,FirstVisualRoot=Snapshot(root),
                BorderChild=Snapshot(border?.Child),ScrollContent=Snapshot(scroll?.Content),Passed=valid });
            Configure(valid,fixture.Label+": native presenter configuration "+stage+" "+location);
        }
        void ObserveRadio(string location,RadioMenuFlyoutItem item)
        {
            var children=Descendants(item).OfType<FrameworkElement>().ToArray();
            var check=children.FirstOrDefault(child=>child.Name=="CheckGlyph");
            var text=children.OfType<TextBlock>().FirstOrDefault(child=>child.Name=="TextBlock");
            var localStyle=item.ReadLocalValue(FrameworkElement.StyleProperty);
            var localTemplate=item.ReadLocalValue(Control.TemplateProperty);
            var valid=item.IsLoaded && item.Template is not null && NativeIdentity(item.Template)!=0 &&
                localStyle==DependencyProperty.UnsetValue && localTemplate==DependencyProperty.UnsetValue &&
                check is not null && check.Clip is null && text is not null;
            _radioObservations.Add(new { Fixture=fixture.Id,Theme=fixture.Theme.ToString(),fixture.Iteration,Stage=stage,Location=location,
                Radio=RadioSnapshot(item),StyleLocallyUnset=localStyle==DependencyProperty.UnsetValue,TemplateLocallyUnset=localTemplate==DependencyProperty.UnsetValue,
                CheckGlyph=Snapshot(check),CheckClip=Snapshot(check?.Clip),TextBlock=Snapshot(text),Passed=valid });
            Configure(valid,fixture.Label+": native Radio configuration "+stage+" "+location);
        }
    }

    private sealed class Fixture
    {
        private static int s_nextId;
        private readonly Action<object> _trace;
        private readonly HashSet<RadioObserver> _observers=[];
        private int _sequence;
        internal int Id { get; }=Interlocked.Increment(ref s_nextId);
        internal ElementTheme Theme { get; }
        internal int Iteration { get; }
        internal string Label=>$"{Theme} iteration {Iteration} fixture {Id}";
        internal string Group=>"NativeOnlyNested_"+Id;
        internal Window Window { get; }
        internal Grid Root { get; }=new() { Padding=new Thickness(12) };
        internal Button Anchor { get; }=new() { Content="Menu",HorizontalAlignment=HorizontalAlignment.Left,VerticalAlignment=VerticalAlignment.Top };
        internal MenuFlyout Menu { get; }=new();
        internal MenuFlyoutSubItem Submenu { get; }=new() { Text="Delayed devices" };
        internal RadioMenuFlyoutItem Initial { get; }=new() { Text="Initial nested device" };
        internal RadioMenuFlyoutItem? Late { get; private set; }
        internal IExpandCollapseProvider? Pattern { get; set; }
        internal string Stage { get; set; }="construction";
        internal string Phase { get; private set; }="Closed";
        internal int Opened { get; private set; }
        internal int Closed { get; private set; }
        internal bool IsClosed { get; private set; }
        internal event Action? Changed;
        internal Fixture(ElementTheme theme,int iteration,Action<object> trace)
        {
            Theme=theme;Iteration=iteration;_trace=trace;Root.RequestedTheme=theme;
            Initial.GroupName=Group;Initial.IsChecked=true;
            Root.Children.Add(Anchor);Submenu.Items.Add(Initial);
            Menu.Items.Add(new MenuFlyoutItem { Text="Action" });Menu.Items.Add(Submenu);
            Window=new Window { Title="KOTU native radio comparison — "+Label,Content=Root };
            try
            {
                Window.Closed+=OnWindowClosed;
                Window.AppWindow.Resize(new Windows.Graphics.SizeInt32(850,760));
                Menu.Opening+=OnOpening;Menu.Opened+=OnOpened;Menu.Closing+=OnClosing;Menu.Closed+=OnClosed;
                _observers.Add(new RadioObserver(this,Initial,"initial"));
            }
            catch { Window.Close();throw; }
        }
        internal IDisposable ObserveLate(RadioMenuFlyoutItem item)
        {
            Late=item;var observer=new RadioObserver(this,item,"late");_observers.Add(observer);return observer;
        }
        private void OnOpening(object? sender,object args) { Phase="Opening";Signal("menu Opening"); }
        private void OnOpened(object? sender,object args) { Phase="Opened";Opened++;Signal("menu Opened"); }
        private void OnClosing(object? sender,FlyoutBaseClosingEventArgs args) { Phase="Closing";Signal("menu Closing cancel="+args.Cancel); }
        private void OnClosed(object? sender,object args) { Phase="Closed";Closed++;Signal("menu Closed"); }
        private void Signal(string action,object? routedEvent=null) { Trace(action,routedEvent);Changed?.Invoke(); }
        internal void Trace(string action,object? routedEvent=null)
        {
            string expanded;
            try { expanded=Pattern?.ExpandCollapseState.ToString() ?? "unobserved"; }
            catch(Exception error) { expanded="unavailable: "+error.GetType().Name; }
            _trace(new { Fixture=Id,Theme=Theme.ToString(),Iteration,Sequence=++_sequence,Utc=DateTime.UtcNow,Stage,Action=action,
                MenuOpen=Menu.IsOpen,Phase,Opened,Closed,WindowClosed=IsClosed,Expanded=expanded,RoutedEvent=routedEvent,
                TopItems=Menu.Items.Select(item=>new { Type=item.GetType().Name,Loaded=item.IsLoaded }).ToArray(),
                Initial=RadioSnapshot(Initial),Late=Late is { } late ? RadioSnapshot(late) : null });
        }
        internal void Close() { if (!IsClosed) Window.Close(); }
        private void OnWindowClosed(object sender,WindowEventArgs args)
        {
            IsClosed=true;
            try { Signal("window Closed"); }
            finally
            {
                Menu.Opening-=OnOpening;Menu.Opened-=OnOpened;Menu.Closing-=OnClosing;Menu.Closed-=OnClosed;
                Window.Closed-=OnWindowClosed;
                foreach(var observer in _observers.ToArray()) observer.Dispose();
            }
        }
        private sealed class RadioObserver : IDisposable
        {
            private readonly Fixture _fixture;
            private readonly RadioMenuFlyoutItem _item;
            private readonly string _label;
            private long? _publicToken,_internalToken;
            private bool _disposed;
            internal RadioObserver(Fixture fixture,RadioMenuFlyoutItem item,string label)
            {
                _fixture=fixture;_item=item;_label=label;
                try
                {
                    item.Loaded+=OnLoaded;item.Unloaded+=OnUnloaded;
                    _publicToken=item.RegisterPropertyChangedCallback(RadioMenuFlyoutItem.IsCheckedProperty,(_,_)=>Change("public checked changed"));
                    try
                    {
                        _=item.GetValue(ToggleMenuFlyoutItem.IsCheckedProperty);
                        _internalToken=item.RegisterPropertyChangedCallback(ToggleMenuFlyoutItem.IsCheckedProperty,(_,_)=>Change("internal checked changed"));
                    }
                    catch(Exception error) { Change("internal checked observation unavailable: "+error.GetType().Name); }
                }
                catch { Dispose();throw; }
            }
            private void Change(string action)=>_fixture.Signal(_label+" radio "+action);
            private void OnLoaded(object sender,RoutedEventArgs args)=>Routed("Loaded",sender,args);
            private void OnUnloaded(object sender,RoutedEventArgs args)=>Routed("Unloaded",sender,args);
            private void Routed(string action,object sender,RoutedEventArgs args)=>_fixture.Signal(_label+" radio "+action,
                new { Observed=Snapshot(_item),Sender=Snapshot(sender),OriginalSource=Snapshot(args.OriginalSource),
                    SenderIsObserved=SameNative(sender,_item),OriginalSourceIsObserved=SameNative(args.OriginalSource,_item) });
            public void Dispose()
            {
                if (_disposed) return;_disposed=true;
                _item.Loaded-=OnLoaded;_item.Unloaded-=OnUnloaded;_fixture._observers.Remove(this);
                if (ReferenceEquals(_fixture.Late,_item)) _fixture.Late=null;
                try { if (_publicToken is { } token) _item.UnregisterPropertyChangedCallback(RadioMenuFlyoutItem.IsCheckedProperty,token); }
                finally { if (_internalToken is { } token) _item.UnregisterPropertyChangedCallback(ToggleMenuFlyoutItem.IsCheckedProperty,token); }
            }
        }
    }
    private static object RadioSnapshot(RadioMenuFlyoutItem item)
    {
        string internalChecked;
        try { internalChecked=item.GetValue(ToggleMenuFlyoutItem.IsCheckedProperty)?.ToString() ?? "null"; }
        catch(Exception error) { internalChecked="unavailable: "+error.GetType().Name; }
        return new { item.Text,item.GroupName,Loaded=item.IsLoaded,Checked=item.IsChecked,InternalChecked=internalChecked,
            Element=Snapshot(item),Style=Snapshot(item.Style),Template=Snapshot(item.Template) };
    }
    private static nint NativeIdentity(object? value)=>value is WinRT.IWinRTObject native ? native.NativeObject.ThisPtr : 0;
    private static bool SameNative(object? left,object? right)=>NativeIdentity(left)!=0 && NativeIdentity(left)==NativeIdentity(right);
    private static object Snapshot(object? value)=>new { Type=value?.GetType().FullName,Name=(value as FrameworkElement)?.Name,NativeIdentity=$"0x{NativeIdentity(value):X}" };
    private static IEnumerable<DependencyObject> Descendants(DependencyObject root)
    {
        for(var index=0;index<VisualTreeHelper.GetChildrenCount(root);index++)
        {
            var child=VisualTreeHelper.GetChild(root,index);yield return child;
            foreach(var descendant in Descendants(child)) yield return descendant;
        }
    }
}
#endif
