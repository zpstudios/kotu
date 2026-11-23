#if KOTU_DESIGN_TEST_HARNESS
using System.Collections;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Reflection;
using System.Text.Json;
using KOTU.App.Appearance;
using KOTU.Core.Appearance;
using KOTU.Core.Settings;
using KOTU.Module.Record;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace KOTU.App.Integration;

// Actual source-grid factory and catalog updates, without constructing RecordView or
// running discovery/capture. Child identity/state is read only after the 100ms settle.
internal sealed class DesignRecordSourceHarness(string output) : App
{
    private const int ExpectedCases=6,ExpectedSnapshotsPerCase=4,ExpectedIconsPerSnapshot=2;
    private const int ExpectedTextsPerSnapshot=2;
    private static readonly Assembly RecordAssembly=typeof(RecordView).Assembly;
    private static readonly Type CatalogType=RecordAssembly.GetType("KOTU.Module.Record.RecordingSourceCatalog",throwOnError:true)!;
    private static readonly MethodInfo CreateGrid=typeof(RecordView).GetMethod("CreateSourceGrid",BindingFlags.Static|BindingFlags.NonPublic)
        ?? throw new InvalidOperationException("Actual Record source-grid factory missing");
    private static readonly MethodInfo ApplyCatalog=CatalogType.GetMethod("Apply",BindingFlags.Static|BindingFlags.NonPublic)
        ?? throw new InvalidOperationException("Actual Record catalog Apply missing");
    private readonly List<object> _results=[],_snapshots=[],_timeline=[];
    private readonly List<string> _failures=[];
    private readonly List<(string Stage,object[] Snapshot)> _ownershipStages=[];
    private readonly Dictionary<string,string> _launchSourceHashes=JsonSerializer.Deserialize<Dictionary<string,string>>(
        Environment.GetEnvironmentVariable("KOTU_DESIGN_RECORD_SOURCE_HASHES") ?? "{}") ?? [];
    private Window? _sentinel,_window;
    private Grid? _root;
    private SourceFixture? _fixture;
    private object[] _ambient=[];
    private LiveDesign.LoadedOwnershipAudit? _ownershipAudit;
    private int _caseId,_sequence,_iconObservationCount;
    private int _textObservationCount;
    private string _phase="startup";

    protected override async void OnLaunched(LaunchActivatedEventArgs args)
    {
        try
        {
            AppDesignService.Initialize(new DesignPreferences(new MemorySettingsService()));
            _ambient=LiveDesign.CaptureLoadedSnapshot();
            _sentinel=new Window { Title="KOTU Record source diagnostic — cleanup",Content=new Border() };
            _sentinel.AppWindow.Resize(new Windows.Graphics.SizeInt32(200,100));
            _sentinel.Activate();
            await Task.Delay(100);
            foreach(var theme in new[] { ElementTheme.Light,ElementTheme.Dark })
                foreach(var category in new[] { "Window","Camera","SystemAudio" }) await RunCase(theme,category);
        }
        catch(Exception error) { _failures.Add("Record source diagnostic exception: "+error); }
        finally
        {
            try
            {
                _window?.Close();_window=null;
                await Task.Delay(100);
                _phase="final ownership";
                if (_sentinel is not null)
                    _ownershipAudit=LiveDesign.AuditLoadedOwnership(_ambient,(FrameworkElement)_sentinel.Content,_ownershipStages);
                Check(_ownershipAudit is { ClosedFixtureStates.Length:0,UnexpectedStates.Length:0 } &&
                    _ownershipAudit.AmbientRemaining==_ownershipAudit.InitialAmbient &&
                    _ownershipAudit.Remaining==_ownershipAudit.AmbientRemaining+_ownershipAudit.LiveSentinelRemaining,
                    "Only initial ambient or cleanup-sentinel loaded states remain");
                Save(true);
                Environment.ExitCode=Passed ? 0 : 1;
            }
            catch(Exception error) { _failures.Add("Record source finalization exception: "+error);Environment.ExitCode=1;Save(false); }
            finally { _sentinel?.Close();_sentinel=null;Exit(); }
        }
    }

    private async Task RunCase(ElementTheme theme,string category)
    {
        _caseId++;
        var initialFailures=_failures.Count;
        var initialSnapshots=_snapshots.Count;
        var initialIcons=_iconObservationCount;
        var initialTexts=_textObservationCount;
        _phase="construct actual source grid";
        AppDesignService.Apply("ink");
        _fixture=new SourceFixture(category);
        _fixture.Refresh("A427 original source 가나다");
        _root=new Grid { RequestedTheme=theme,Padding=new Thickness(16) };
        _root.Children.Add(_fixture.Grid);
        _window=new Window { Title=$"KOTU Record source diagnostic — {theme} {category}",Content=_root };
        _window.AppWindow.Resize(new Windows.Graphics.SizeInt32(1200,800));
        LiveDesign.AttachWindow(_window,_root);
        _window.Activate();
        await WaitReady(()=>_root.IsLoaded && _fixture.Grid.IsLoaded && _fixture.Grid.ActualWidth>0,"actual source grid loaded and arranged");
        await Task.Delay(100);
        Capture("ink initial","ink");

        _phase="same-key catalog replacement";
        var oldItem=_fixture.Rows[0];
        _fixture.Refresh("A427 refreshed source 가나다");
        Check(!ReferenceEquals(oldItem,_fixture.Rows[0]),"Actual catalog replaced changed record while retaining its key");
        await Task.Delay(100);
        Capture("ink after source refresh","ink");

        _phase="Precision application";
        AppDesignService.Apply("precision");
        await Task.Delay(100);
        Capture("precision after refresh","precision");
        _phase="Ink reapplication";
        AppDesignService.Apply("ink");
        await Task.Delay(100);
        Capture("ink reapplied after refresh","ink");

        _ownershipStages.Add(($"case {_caseId} before close",LiveDesign.CaptureLoadedSnapshot()));
        _window.Close();_window=null;
        await Task.Delay(100);
        Check(_snapshots.Count-initialSnapshots==ExpectedSnapshotsPerCase,"Exactly four source snapshots completed");
        Check(_iconObservationCount-initialIcons==ExpectedSnapshotsPerCase*ExpectedIconsPerSnapshot,"Exactly eight required icon observations completed");
        Check(_textObservationCount-initialTexts==ExpectedSnapshotsPerCase*ExpectedTextsPerSnapshot,"Exactly eight required text observations completed");
        _results.Add(new { CaseId=_caseId,Theme=theme.ToString(),Category=category,Completed=true,Passed=_failures.Count==initialFailures,
            FailureCount=_failures.Count-initialFailures,SnapshotCount=_snapshots.Count-initialSnapshots,IconObservationCount=_iconObservationCount-initialIcons,
            TextObservationCount=_textObservationCount-initialTexts });
        _fixture=null;_root=null;
        Save(false);
    }

    private void Capture(string stage,string expectedDesignId)
    {
        _phase=stage;
        var fixture=_fixture ?? throw new InvalidOperationException("Source fixture missing");
        var grid=fixture.Grid;
        var actualDesignId=DesignRuntime.Current.Id;
        Check(actualDesignId==expectedDesignId,stage+": expected design is actually applied");
        var design=DesignCatalog.Resolve(expectedDesignId);
        Check(grid.Items.Count==1 && fixture.Rows.Count==1,stage+": exactly one required selected source");
        Check(ReferenceEquals(grid.SelectedItem,fixture.Rows[0]) && fixture.Key(grid.SelectedItem!)==fixture.SelectedKey,
            stage+": source key and actual selected object preserved");
        var container=grid.ContainerFromItem(fixture.Rows[0]) as GridViewItem;
        Check(container is { IsLoaded:true,IsSelected:true,ActualWidth:>0,ActualHeight:>0 },stage+": selected source container loaded and arranged");
        var tile=container?.ContentTemplateRoot as Border;
        var content=tile?.Child as Grid;
        Check(content is not null,stage+": actual source template Border/Grid exists");
        var mainIcons=content?.Children.OfType<FontIcon>().ToArray() ?? [];
        var badges=content?.Children.OfType<Border>().Where(border=>border.Name=="SelectionBadge").ToArray() ?? [];
        Check(mainIcons.Length==1,stage+": exactly one required main FontIcon");
        Check(badges.Length==1 && badges[0].Child is FontIcon,stage+": exactly one required badge FontIcon");
        Check(badges.Length==1 && badges[0].Visibility==Visibility.Visible,stage+": selected source badge visible");
        var observations=new List<object>();
        foreach(var icon in mainIcons) observations.Add(CaptureIcon(icon,"Source",fixture.Glyph,30,stage,design));
        foreach(var badge in badges)
            if (badge.Child is FontIcon icon) observations.Add(CaptureIcon(icon,"SelectionBadge","\uE73E",12,stage,design));
        Check(observations.Count==ExpectedIconsPerSnapshot,stage+": exactly two required icon verdicts");
        var texts=content?.Children.OfType<TextBlock>().ToArray() ?? [];
        Check(texts.Length==ExpectedTextsPerSnapshot && texts.Count(text=>Grid.GetRow(text)==1)==1 && texts.Count(text=>Grid.GetRow(text)==2)==1,
            stage+": exactly one required category and source-label TextBlock");
        var labels=texts.Select(text=>CaptureText(text,stage,design)).ToArray();
        Check(texts.Any(label=>label.Text==fixture.CurrentLabel),stage+": actual Label binding shows current source record");
        _snapshots.Add(new { CaseId=_caseId,Stage=stage,Category=fixture.Category,Design=actualDesignId,ExpectedDesign=expectedDesignId,SelectedKey=fixture.SelectedKey,
            CurrentLabel=fixture.CurrentLabel,ItemType=fixture.ItemType.FullName,ItemCount=grid.Items.Count,
            Container=container is null ? null : LiveDesign.DiagnosticConnection(container),
            TileNative=Identity(tile),Labels=labels,Icons=observations,IconObservationCount=observations.Count,ExpectedIconObservationCount=ExpectedIconsPerSnapshot,
            TextObservationCount=labels.Length,ExpectedTextObservationCount=ExpectedTextsPerSnapshot });
    }

    private object CaptureText(TextBlock text,string stage,DesignDefinition design)
    {
        _textObservationCount++;
        var row=Grid.GetRow(text);
        var role=row==1 ? "Category" : "SourceLabel";
        var authoredSize=row==1 ? 10d : 12d;
        var expectedSize=DesignScope.FontSize(authoredSize,design);
        var diagnostic=JsonSerializer.SerializeToElement(LiveDesign.DiagnosticTypographyBaseline(text));
        var captured=diagnostic.GetProperty("Captured");
        var label=stage+": "+role;
        Check(text.Visibility==Visibility.Visible && text.IsLoaded,label+" visible and loaded");
        Check(text.ActualWidth>0 && text.ActualHeight>0,label+" has positive arranged size");
        Check(Math.Abs(text.FontSize-expectedSize)<0.000001,label+" exact expected size");
        Check(captured.ValueKind==JsonValueKind.Object && Math.Abs(captured.GetProperty("OriginalFontSize").GetDouble()-authoredSize)<0.000001,
            label+" authored size baseline preserved");
        return new { Role=role,Row=row,NativeIdentity=Identity(text),text.Text,text.FontSize,FontFamily=text.FontFamily.Source,
            text.IsLoaded,text.ActualWidth,text.ActualHeight,Visibility=text.Visibility.ToString(),ExpectedSize=expectedSize,
            ExpectedBaseline=authoredSize,Diagnostic=diagnostic };
    }

    private object CaptureIcon(FontIcon icon,string role,string canonical,double authoredSize,string stage,DesignDefinition design)
    {
        _iconObservationCount++;
        // This getter only reads the existing connection and captured fields. It does not
        // fetch styles, walk ancestors, register events or initialize a missing state.
        var diagnostic=JsonSerializer.SerializeToElement(LiveDesign.DiagnosticTypographyBaseline(icon));
        var connection=diagnostic.GetProperty("Connection");
        var captured=diagnostic.GetProperty("Captured");
        var state=connection.GetProperty("State");
        var expectedFont=DesignIconCatalog.FontFamily(design);
        var expectedSize=DesignScope.IconSize(authoredSize,design);
        var label=stage+": "+role;
        Check(icon.Visibility==Visibility.Visible && icon.IsLoaded,label+" visible and loaded");
        Check(icon.ActualWidth>0 && icon.ActualHeight>0,label+" has positive arranged size");
        Check(icon.Glyph==canonical,label+" displayed canonical glyph");
        Check(icon.FontFamily.Source==expectedFont,label+" design icon font");
        Check(Math.Abs(icon.FontSize-expectedSize)<0.000001,label+" exact expected size");
        Check(connection.GetProperty("HasState").GetBoolean() && connection.GetProperty("CurrentRootRegistered").GetBoolean() &&
            state.ValueKind==JsonValueKind.Object && state.GetProperty("StateLoaded").GetBoolean() && state.GetProperty("Initialized").GetBoolean() &&
            state.GetProperty("InLoadedStates").GetBoolean() && state.GetProperty("OwnerRootRegistered").GetBoolean(),label+" initialized managed state in an active owner");
        Check(captured.ValueKind==JsonValueKind.Object && captured.GetProperty("GlyphCanonical").GetString()==canonical &&
            captured.GetProperty("GlyphDisplayed").GetString()==canonical,label+" captured canonical/displayed glyph preserved");
        Check(captured.ValueKind==JsonValueKind.Object && Math.Abs(captured.GetProperty("OriginalFontSize").GetDouble()-authoredSize)<0.000001,
            label+" authored size baseline preserved");
        return new { Role=role,NativeIdentity=Identity(icon),icon.Glyph,GlyphCodeUnits=icon.Glyph.Select(value=>$"U+{(int)value:X4}").ToArray(),
            FontFamily=icon.FontFamily.Source,icon.FontSize,icon.IsLoaded,icon.ActualWidth,icon.ActualHeight,Visibility=icon.Visibility.ToString(),
            ExpectedGlyph=canonical,ExpectedFont=expectedFont,ExpectedSize=expectedSize,ExpectedBaseline=authoredSize,Diagnostic=diagnostic };
    }

    private sealed class SourceFixture
    {
        internal string Category { get; }
        internal string Glyph { get; }
        internal GridView Grid { get; }
        internal Type ItemType { get; }
        internal IList Rows { get; }
        internal string SelectedKey { get; }
        internal string CurrentLabel { get; private set; }="";
        private readonly Delegate _key;
        private readonly Delegate? _isDefault;
        private readonly MethodInfo _apply;
        internal SourceFixture(string category)
        {
            Category=category;
            Glyph=category switch { "Window"=>"\uE737","Camera"=>"\uE722",_=>"\uE767" };
            ItemType=RecordAssembly.GetType("KOTU.Module.Record."+(category=="SystemAudio" ? "OutputDevice" : "CaptureSource"),throwOnError:true)!;
            Rows=(IList)Activator.CreateInstance(typeof(ObservableCollection<>).MakeGenericType(ItemType))!;
            var keyMethod=category=="SystemAudio" ? ItemType.GetProperty("Id")!.GetMethod! :
                CatalogType.GetMethod("Key",BindingFlags.Static|BindingFlags.NonPublic)!;
            _key=keyMethod.CreateDelegate(typeof(Func<,>).MakeGenericType(ItemType,typeof(string)));
            if (category=="SystemAudio") _isDefault=ItemType.GetProperty("IsDefault")!.GetMethod!.CreateDelegate(typeof(Func<,>).MakeGenericType(ItemType,typeof(bool)));
            _apply=ApplyCatalog.MakeGenericMethod(ItemType);
            SelectedKey=Key(CreateItem("A427 initial key"));
            Grid=(GridView)CreateGrid.Invoke(null,[Glyph,"A427 "+category,category=="SystemAudio" ? "SYSTEM AUDIO" : category.ToUpperInvariant(),"{Binding Label}"])!;
            Grid.ItemsSource=Rows;
        }
        internal string Key(object item)=>(string)_key.DynamicInvoke(item)!;
        private object CreateItem(string label)
        {
            object?[] arguments=Category=="SystemAudio" ? [label,"A427-fixture-output",true] :
                [label,null,Category=="Window" ? (nint)427 : 0,Category=="Window" ? (int?)427 : null,
                    Category=="Camera" ? "A427-fixture-camera" : null];
            return ItemType.GetConstructors().Single(ctor=>ctor.GetParameters().Length==arguments.Length).Invoke(arguments);
        }
        internal void Refresh(string label)
        {
            var desired=Array.CreateInstance(ItemType,1);
            desired.SetValue(CreateItem(label),0);
            object? selected;
            if (Category=="SystemAudio")
            {
                selected=CatalogType.GetMethod("Select",BindingFlags.Static|BindingFlags.NonPublic)!.MakeGenericMethod(ItemType)
                    .Invoke(null,[desired,SelectedKey,_key,_isDefault,false]);
                _apply.Invoke(null,[Rows,desired,_key]);
                var key=selected is null ? null : Key(selected);
                selected=Rows.Cast<object>().FirstOrDefault(item=>Key(item)==key);
            }
            else
            {
                _apply.Invoke(null,[Rows,desired,_key]);
                selected=CatalogType.GetMethod("SelectVisual",BindingFlags.Static|BindingFlags.NonPublic)!
                    .Invoke(null,[Rows,SelectedKey,Category=="Camera",false]);
            }
            Grid.SelectedItem=selected;
            CurrentLabel=label;
        }
    }

    private async Task WaitReady(Func<bool> ready,string label)
    {
        var watch=Stopwatch.StartNew();
        var value=ready();
        while(!value && watch.ElapsedMilliseconds<2000) { await Task.Delay(25);value=ready(); }
        _timeline.Add(new { Sequence=++_sequence,CaseId=_caseId,Phase=_phase,Label=label,Ready=value,
            ElapsedMilliseconds=watch.Elapsed.TotalMilliseconds,DeadlineMilliseconds=2000 });
        Check(value && watch.ElapsedMilliseconds<=2000,label+" within 2s");
    }
    private void Check(bool condition,string message) { if (!condition) _failures.Add($"case {_caseId}: "+message); }
    private bool Passed=>_results.Count==ExpectedCases && _snapshots.Count==ExpectedCases*ExpectedSnapshotsPerCase &&
        _iconObservationCount==ExpectedCases*ExpectedSnapshotsPerCase*ExpectedIconsPerSnapshot &&
        _textObservationCount==ExpectedCases*ExpectedSnapshotsPerCase*ExpectedTextsPerSnapshot && _failures.Count==0;
    private void Save(bool completed)
    {
        var pending=output+".writing";
        File.WriteAllText(pending,JsonSerializer.Serialize(new { Mode="RecordSources",Completed=completed,Passed=completed && Passed,
            ExpectedCaseCount=ExpectedCases,CompletedCaseCount=_results.Count,ExpectedSnapshotCount=ExpectedCases*ExpectedSnapshotsPerCase,
            CompletedSnapshotCount=_snapshots.Count,ExpectedIconObservationCount=ExpectedCases*ExpectedSnapshotsPerCase*ExpectedIconsPerSnapshot,
            CompletedIconObservationCount=_iconObservationCount,ExpectedTextObservationCount=ExpectedCases*ExpectedSnapshotsPerCase*ExpectedTextsPerSnapshot,
            CompletedTextObservationCount=_textObservationCount,Results=_results,Failures=_failures,Snapshots=_snapshots,Timeline=_timeline,
            FinalOwnershipAudit=_ownershipAudit,LaunchSourceHashes=_launchSourceHashes,
            Scope="Actual private RecordView.CreateSourceGrid; actual internal CaptureSource/OutputDevice records with synthetic identities; actual RecordingSourceCatalog.Apply/SelectVisual/Select. One selected source per Window/Camera/SystemAudio case in Light/Dark. Same-key changed-label record replacement, then Precision/Ink reapplication.",
            Limits="No RecordView instance, source discovery, devices, capture, permission prompts or user files. This isolates one selected row's Replace path, not concurrent discovery, multiple-item insert/move/remove or a full recording session. No child lifecycle subscriptions, early child lookup, Track, ApplyTemplate, UpdateLayout or forced GC. Existing 100ms settle and 2s readiness remain; other harness verdicts are separate." },new JsonSerializerOptions { WriteIndented=true }));
        File.Move(pending,output,true);
    }
    private static string Identity(object? value)=>value is WinRT.IWinRTObject native ? $"0x{native.NativeObject.ThisPtr:X}" : "0x0";
}
#endif
