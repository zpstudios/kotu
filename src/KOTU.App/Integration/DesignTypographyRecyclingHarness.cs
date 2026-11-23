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
using Windows.Foundation;

namespace KOTU.App.Integration;

// Separate late-only experiment. No child Loaded/Unloaded observers, Track calls,
// ApplyTemplate, forced GC or modified product wait/assertion paths are introduced.
internal sealed class DesignTypographyRecyclingHarness(string output) : App
{
    private const int ExpectedCases=2,ExpectedSnapshotsPerCase=8,EntryCount=160;
    private static readonly string[] TextNames=["TileCaption","RowSizeText","RowTypeText","RowCreatedText","RowModifiedText","RowSummaryText"];
    private static readonly string[] RequiredTextNames=TextNames.Where(name=>name!="RowSummaryText").ToArray();
    private readonly List<object> _results=[],_snapshots=[],_timeline=[];
    private readonly List<string> _failures=[];
    private readonly List<(string Stage,object[] Snapshot)> _ownershipStages=[];
    private readonly Dictionary<string,string> _launchSourceHashes=JsonSerializer.Deserialize<Dictionary<string,string>>(
        Environment.GetEnvironmentVariable("KOTU_DESIGN_TYPOGRAPHY_SOURCE_HASHES") ?? "{}") ?? [];
    private Window? _sentinel,_window;
    private Grid? _root;
    private ThumbnailExplorer? _explorer;
    private ListView? _list;
    private ScrollViewer? _viewport;
    private ExplorerEntryVm[] _entries=[];
    private object[] _ambient=[];
    private LiveDesign.LoadedOwnershipAudit? _ownershipAudit;
    private int _caseId,_sequence;
    private string _phase="startup";

    protected override async void OnLaunched(LaunchActivatedEventArgs args)
    {
        try
        {
            AppDesignService.Initialize(new DesignPreferences(new MemorySettingsService()));
            _ambient=LiveDesign.CaptureLoadedSnapshot();
            _sentinel=new Window { Title="KOTU typography recycling diagnostic — cleanup",Content=new Border() };
            _sentinel.AppWindow.Resize(new Windows.Graphics.SizeInt32(200,100));
            _sentinel.Activate();
            await Task.Delay(100);
            foreach(var theme in new[] { ElementTheme.Light,ElementTheme.Dark }) await RunCase(theme);
        }
        catch(Exception error) { _failures.Add("Typography recycling exception: "+error); }
        finally
        {
            try
            {
                _window?.Close();_window=null;
                await Task.Delay(100);
                _phase="final ownership";
                if (_sentinel is not null)
                    _ownershipAudit=LiveDesign.AuditLoadedOwnership(_ambient,(FrameworkElement)_sentinel.Content,_ownershipStages);
                Check(_ownershipAudit is { ClosedFixtureStates.Length:0,UnexpectedStates.Length:0 },"No closed-fixture or unexpected loaded state remains");
                Save(true);
                Environment.ExitCode=Passed ? 0 : 1;
            }
            catch(Exception error) { _failures.Add("Typography recycling finalization exception: "+error);Environment.ExitCode=1;Save(false); }
            finally { _sentinel?.Close();_sentinel=null;Exit(); }
        }
    }

    private async Task RunCase(ElementTheme theme)
    {
        _caseId++;
        var initialFailures=_failures.Count;
        var initialSnapshots=_snapshots.Count;
        _phase="construct cached text preview entries";
        AppDesignService.Apply("pixel");
        _root=new Grid { RequestedTheme=theme };
        _explorer=new ThumbnailExplorer();
        var folder=Path.Combine(AppContext.BaseDirectory,"A427TypographyFixture");
        var stamp=new DateTime(2026,11,23,0,0,0,DateTimeKind.Utc);
        var listing=Enumerable.Range(1,EntryCount).Select(index=>new ExplorerListing.Entry(
            Path.Combine(folder,$"검수 파일 {index:000} - Sample.txt"),$"검수 파일 {index:000} - Sample.txt",false,
            35,stamp,stamp,Attributes:FileAttributes.Normal)).ToArray();
        _explorer.ShowEntries(folder,listing);
        var tiles=(GridView)_explorer.FindName("TileGrid");
        _list=(ListView)_explorer.FindName("DetailList");
        _entries=tiles.Items.OfType<ExplorerEntryVm>().ToArray();
        Check(_entries.Length==EntryCount,"Exactly 160 owned synthetic entries before realization");
        // Populate the normal preview cache before the root is attached. The real preview
        // template can realize without opening any file or invoking a shell provider.
        for(var index=0;index<_entries.Length;index++) _entries[index].PreviewText="A427 virtualized explorer fixture "+(index+1);
        _root.Children.Add(_explorer);
        _window=new Window { Title=$"KOTU typography recycling diagnostic — {theme}",Content=_root };
        _window.AppWindow.Resize(new Windows.Graphics.SizeInt32(2000,1300));
        LiveDesign.AttachWindow(_window,_root);
        _window.Activate();
        await WaitReady(()=>_root.IsLoaded && tiles.IsLoaded && tiles.ActualWidth>0,"actual Preview loaded and arranged");
        await Task.Delay(100);
        Record("preview realized",new { tiles.IsLoaded,tiles.ActualWidth,tiles.ActualHeight,Items=tiles.Items.Count,
            TextPreviewRequests=_explorer.PreviewRequestCounts.Text,ShellPreviewRequests=_explorer.PreviewRequestCounts.Shell,
            Observation="No preview child lookup or subscription" });
        Check(_explorer.PreviewRequestCounts is { Text:0,Shell:0 },"Cached preview realization made no file or shell preview request");

        _phase="Preview to actual List";
        _explorer.Presentation.Mode=ExplorerPresentationState.ViewMode.List;
        _explorer.ShowEntries(folder,listing);
        await WaitReady(()=>_list.IsLoaded && _list.ActualWidth>0,"actual List loaded and arranged");
        await Task.Delay(100);
        _viewport=VisualDescendants(_list).OfType<ScrollViewer>().FirstOrDefault();
        if (_viewport is null) throw new InvalidOperationException("Actual List ScrollViewer missing");
        Check(_viewport.ViewportWidth>=700,"Wide list columns are visible in this diagnostic viewport");
        _entries[0].IsChecked=true;
        _list.SelectedItem=_entries[0];
        await Task.Delay(100);
        Capture("pixel initial list",1,1);

        await ScrollTo(100);
        Capture("pixel middle",100,1);
        var previousOffset=_viewport.VerticalOffset;
        _phase="apply Ink while list is scrolled";
        AppDesignService.Apply("ink");
        await Task.Delay(100);
        Check(Math.Abs(_viewport.VerticalOffset-previousOffset)<0.01,"Design application preserves the exact scroll offset");
        Capture("ink same middle",100,1);

        await ScrollTo(160);
        _entries[159].IsChecked=true;
        await Task.Delay(100);
        Capture("ink bottom",160,2);
        await ScrollTo(1);
        Capture("ink returned top",1,2);

        _phase="collapse and reopen same actual explorer";
        _explorer.Visibility=Visibility.Collapsed;
        await Task.Delay(100);
        _explorer.Visibility=Visibility.Visible;
        await WaitReady(()=>_list.IsLoaded && _list.ActualWidth>0,"reopened actual List loaded and arranged");
        await Task.Delay(100);
        Capture("ink reopened top",1,2);

        _phase="return to Precision baseline";
        AppDesignService.Apply("precision");
        await Task.Delay(100);
        Capture("precision after recycling",1,2);
        _phase="apply Ink again after recycling";
        AppDesignService.Apply("ink");
        await Task.Delay(100);
        Capture("ink reapplied after recycling",1,2);

        _ownershipStages.Add(($"case {_caseId} before close",LiveDesign.CaptureLoadedSnapshot()));
        _window.Close();_window=null;
        await Task.Delay(100);
        Check(_snapshots.Count-initialSnapshots==ExpectedSnapshotsPerCase,"Exactly eight list snapshots completed");
        _results.Add(new { CaseId=_caseId,Theme=theme.ToString(),Completed=true,Passed=_failures.Count==initialFailures,
            FailureCount=_failures.Count-initialFailures,SnapshotCount=_snapshots.Count-initialSnapshots });
        _entries=[];_list=null;_viewport=null;_explorer=null;_root=null;
        Save(false);
    }

    private async Task ScrollTo(int number)
    {
        _phase="scroll to synthetic entry "+number;
        var list=_list ?? throw new InvalidOperationException("List missing during scroll");
        var viewport=_viewport ?? throw new InvalidOperationException("Viewport missing during scroll");
        // Native programmatic scrolling only; this is not a physical-wheel acceptance test.
        list.ScrollIntoView(_entries[number-1],ScrollIntoViewAlignment.Leading);
        await Task.Delay(100);
        Record("scroll settled",new { Number=number,viewport.VerticalOffset,viewport.ViewportHeight });
    }

    private void Capture(string stage,int requiredVisibleEntry,int expectedChecks)
    {
        _phase=stage;
        var design=DesignRuntime.Current;
        var list=_list ?? throw new InvalidOperationException("List missing during capture");
        var viewport=_viewport ?? throw new InvalidOperationException("Viewport missing during capture");
        var rows=new List<object>();
        var visibleNumbers=new List<int>();
        var snapshotFontCheckCount=0;
        var panel=list.ItemsPanelRoot as Panel;
        Check(panel is not null,stage+": actual items panel exists");
        foreach(var container in panel?.Children.OfType<ListViewItem>() ?? [])
        {
            if (container.Content is not ExplorerEntryVm vm) continue;
            var number=Array.IndexOf(_entries,vm)+1;
            if (number==0) continue;
            var owned=SameNative(list.ContainerFromItem(vm),container);
            var bounds=container.TransformToVisual(viewport).TransformBounds(new Rect(0,0,container.ActualWidth,container.ActualHeight));
            var intersects=owned && container.Visibility==Visibility.Visible && bounds.Bottom>0 && bounds.Top<viewport.ViewportHeight &&
                bounds.Right>0 && bounds.Left<viewport.ViewportWidth;
            if (!intersects) continue; // Retired/offscreen cache rows never determine pass/fail.
            visibleNumbers.Add(number);
            Check(container.IsLoaded,stage+$": visible entry {number:000} container loaded");
            var root=container.ContentTemplateRoot as Grid;
            var content=root?.FindName("ExplorerTileContent") as Grid;
            Check(content is not null,stage+$": visible entry {number:000} actual template content exists");
            var texts=content?.Children.OfType<TextBlock>().Where(text=>TextNames.Contains(text.Name)).ToArray() ?? [];
            Check(texts.Length==TextNames.Length,stage+$": visible entry {number:000} retains six authored labels");
            Check(RequiredTextNames.All(name=>texts.Count(text=>text.Name==name)==1),
                stage+$": visible entry {number:000} has each of the five required wide-list labels exactly once");
            var textSamples=new List<object>();
            var rowFontCheckCount=0;
            foreach(var text in texts)
            {
                var authored=text.Name=="RowSummaryText" ? 11d : 12d;
                var expected=DesignScope.FontSize(authored,design);
                var displayed=text.FontSize;
                var baseline=DesignScope.OriginalFontSize(text,-1);
                var fontChecked=RequiredTextNames.Contains(text.Name);
                // Every required wide-list label must be realized. Only the hidden summary
                // stays evidence-only; a zero-size required label must never produce a pass.
                if (fontChecked)
                {
                    rowFontCheckCount++;
                    Check(text.Visibility==Visibility.Visible,stage+$": entry {number:000} {text.Name} visible");
                    Check(text.IsLoaded,stage+$": entry {number:000} {text.Name} loaded");
                    Check(text.ActualWidth>0 && text.ActualHeight>0,stage+$": entry {number:000} {text.Name} has positive arranged size");
                    Check(Math.Abs(displayed-expected)<0.000001,stage+$": entry {number:000} {text.Name} FontSize {displayed:R}, expected {expected:R}");
                    Check(Math.Abs(baseline-authored)<0.000001,stage+$": entry {number:000} {text.Name} original size {baseline:R}, expected {authored:R}");
                }
                textSamples.Add(new { text.Name,text.Text,text.IsLoaded,Visibility=text.Visibility.ToString(),text.ActualWidth,text.ActualHeight,
                    FontChecked=fontChecked,FontSize=displayed,ExpectedFontSize=expected,OriginalSizeMetadata=baseline,FontWeight=text.FontWeight.Weight,
                    FontFamily=text.FontFamily.Source,LocalFontSize=LocalNumber(text,TextBlock.FontSizeProperty),
                    Baseline=LiveDesign.DiagnosticTypographyBaseline(text) });
            }
            Check(rowFontCheckCount==RequiredTextNames.Length,stage+$": entry {number:000} has exactly five font-size verdicts");
            snapshotFontCheckCount+=rowFontCheckCount;
            var icons=content?.Children.OfType<FontIcon>().Where(icon=>Grid.GetColumn(icon)==1).Select(icon=>new {
                icon.Glyph,icon.FontSize,FontFamily=icon.FontFamily.Source,Baseline=LiveDesign.DiagnosticTypographyBaseline(icon) }).ToArray();
            rows.Add(new { Number=number,vm.Name,vm.IsChecked,vm.IsCurrent,Selected=container.IsSelected,Owned=owned,
                FontCheckCount=rowFontCheckCount,ExpectedFontCheckCount=RequiredTextNames.Length,
                Bounds=new { bounds.X,bounds.Y,bounds.Width,bounds.Height },Container=LiveDesign.DiagnosticConnection(container),Texts=textSamples,Icons=icons });
        }
        Check(snapshotFontCheckCount==visibleNumbers.Count*RequiredTextNames.Length,stage+": five font-size verdicts per visible row");
        Check(visibleNumbers.Contains(requiredVisibleEntry),stage+$": target entry {requiredVisibleEntry:000} intersects viewport");
        Check(_entries.Count(entry=>entry.IsChecked)==expectedChecks && _entries[0].IsChecked &&
            (expectedChecks==1 || _entries[159].IsChecked),stage+": check set preserved without leakage");
        Check(ReferenceEquals(list.SelectedItem,_entries[0]),stage+": selected entry 001 preserved");
        var targets=new[] { 1,8,100,160 }.Select(number=>new { Number=number,Name=_entries[number-1].Name,
            Container=list.ContainerFromItem(_entries[number-1]) is FrameworkElement target ? LiveDesign.DiagnosticConnection(target) : null }).ToArray();
        _snapshots.Add(new { CaseId=_caseId,Stage=stage,Design=design.Id,VisibleNumbers=visibleNumbers,Rows=rows,Targets=targets,
            FontCheckCount=snapshotFontCheckCount,ExpectedFontCheckCount=visibleNumbers.Count*RequiredTextNames.Length,
            Viewport=new { viewport.VerticalOffset,viewport.ViewportWidth,viewport.ViewportHeight,viewport.ExtentHeight },
            CheckedNumbers=_entries.Select((entry,index)=>(entry,index)).Where(pair=>pair.entry.IsChecked).Select(pair=>pair.index+1).ToArray() });
    }

    private static object LocalNumber(DependencyObject element,DependencyProperty property)
    {
        var value=element.ReadLocalValue(property);
        return new { Unset=value==DependencyProperty.UnsetValue,Number=value is double number ? (double?)number : null,Type=value?.GetType().Name };
    }
    private async Task WaitReady(Func<bool> ready,string label)
    {
        var watch=Stopwatch.StartNew();
        var value=ready();
        while(!value && watch.ElapsedMilliseconds<2000) { await Task.Delay(25);value=ready(); }
        Record("readiness",new { Label=label,Ready=value,ElapsedMilliseconds=watch.Elapsed.TotalMilliseconds,DeadlineMilliseconds=2000 });
        Check(value && watch.ElapsedMilliseconds<=2000,label+" within 2s");
    }
    private void Check(bool condition,string message) { if (!condition) _failures.Add($"case {_caseId}: "+message); }
    private void Record(string action,object detail)=>_timeline.Add(new { Sequence=++_sequence,CaseId=_caseId,Phase=_phase,Action=action,
        Utc=DateTime.UtcNow,Timestamp=Stopwatch.GetTimestamp(),Detail=detail });
    private bool Passed=>_results.Count==ExpectedCases && _snapshots.Count==ExpectedCases*ExpectedSnapshotsPerCase && _failures.Count==0;
    private void Save(bool completed)
    {
        var pending=output+".writing";
        File.WriteAllText(pending,JsonSerializer.Serialize(new { Mode="TypographyRecycling",Completed=completed,Passed=completed && Passed,
            ExpectedCaseCount=ExpectedCases,CompletedCaseCount=_results.Count,ExpectedSnapshotCount=ExpectedCases*ExpectedSnapshotsPerCase,
            CompletedSnapshotCount=_snapshots.Count,Results=_results,Failures=_failures,Snapshots=_snapshots,Timeline=_timeline,
            FinalOwnershipAudit=_ownershipAudit,LaunchSourceHashes=_launchSourceHashes,
            Scope="Actual ThumbnailExplorer Preview then List templates with 160 synthetic entries and prepopulated text-preview caches; Pixel to Ink, native programmatic scroll, collapse/reopen and Precision/Ink reapply. Light/Dark. No user file or shell preview IO.",
            Limits="Not the full FileListOverlay/Document close routing or physical wheel input. No child lifecycle subscription, early typography lookup, Track, ApplyTemplate or forced GC. Snapshots read only after the original 100ms settle; only current viewport-intersecting rows determine typography verdicts. Other harness verdicts remain separate." },new JsonSerializerOptions { WriteIndented=true }));
        File.Move(pending,output,true);
    }
    private static nint NativeIdentity(object? value)=>value is WinRT.IWinRTObject native ? native.NativeObject.ThisPtr : 0;
    private static bool SameNative(object? first,object? second)=>NativeIdentity(first)!=0 && NativeIdentity(first)==NativeIdentity(second);
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
