#if KOTU_DESIGN_TEST_HARNESS
using System.Text.Json;
using KOTU.App.Appearance;
using KOTU.Core.Appearance;
using KOTU.Core.Integration;
using KOTU.Core.Settings;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Markup;
using Microsoft.UI.Xaml.Data;

namespace KOTU.App.Integration;

// Explicit compile-only entry point. Run only after permission to open native review windows.
// It loads the real App.xaml resources, uses memory settings, and never calls App.OnLaunched,
// Program.Main, shell registration, updates, file routing, native capture or user content.
internal static class DesignHarness
{
    [STAThread]
    private static int Main(string[] args)
    {
        var nativeOnly=args.Length==2 && args[0]=="--native-only";
        var explorerOnly=(args.Length==2 || args.Length==4) && args[0]=="--explorer-only";
        var overlayExplorerOnly=args.Length==2 && args[0]=="--overlay-explorer-only";
        var typographyRecycling=args.Length==2 && args[0]=="--typography-recycling";
        var recordSources=args.Length==2 && args[0]=="--record-sources";
        var listObservationMode=DesignExplorerHarness.ListObservationMode.LateOnly;
        if (explorerOnly && args.Length==4 && (args[2]!="--list-observation" ||
            !Enum.GetNames<DesignExplorerHarness.ListObservationMode>().Contains(args[3],StringComparer.Ordinal) ||
            !Enum.TryParse(args[3],out listObservationMode))) return 2;
        if (!DistributionPolicy.IsStandalone || (!nativeOnly && !explorerOnly && !overlayExplorerOnly && !typographyRecycling && !recordSources &&
            (args.Length!=1 || args[0] is "--native-only" or "--explorer-only" or "--overlay-explorer-only" or "--typography-recycling" or "--record-sources"))) return 2;
        var output = Path.GetFullPath(args[nativeOnly || explorerOnly || overlayExplorerOnly || typographyRecycling || recordSources ? 1 : 0]);
        try
        {
            WinRT.ComWrappersSupport.InitializeComWrappers();
            Application.Start(callback =>
            {
                SynchronizationContext.SetSynchronizationContext(
                    new DispatcherQueueSynchronizationContext(DispatcherQueue.GetForCurrentThread()));
                if (nativeOnly) _ = new NativeRadioHarness(output);
                else if (explorerOnly) _ = new DesignExplorerHarness(output,listObservationMode);
                else if (overlayExplorerOnly) _ = new DesignOverlayExplorerHarness(output);
                else if (typographyRecycling) _ = new DesignTypographyRecyclingHarness(output);
                else if (recordSources) _ = new DesignRecordSourceHarness(output);
                else _ = new HarnessApp(output);
            });
        }
        catch (Exception error)
        {
            if (nativeOnly) NativeRadioHarness.WriteStartupFailure(output,error);
            else File.WriteAllText(output,JsonSerializer.Serialize(new { Passed=false, Error=error.ToString() }));
            return 1;
        }
        return Environment.ExitCode;
    }

    private sealed class HarnessApp(string output) : App
    {
        private readonly List<object> _results = [];
        private readonly List<string> _failures = [];
        private readonly List<object> _nativeBaselineResults = [];
        private readonly List<string> _nativeBaselineFailures = [];
        private readonly List<object> _defaultPresenterResults = [];
        private readonly List<string> _defaultPresenterFailures = [];
        private readonly List<object> _presenterObservations = [];
        private readonly List<object> _menuTimeline = [];
        private readonly List<DesignIconProbe.CaseResult> _iconProbeResults = [];
        private DesignIconProbe? _iconProbe;
        private IReadOnlyList<DesignDefinition> _designs=DesignCatalog.All;
        private Fixture? _first, _second, _nativeBaseline;
        private Window? _cleanupWindow;
        private object[] _ambientBaseline=[];
        private readonly List<(string Stage,object[] Snapshot)> _loadedStages=[];
        private LiveDesign.LoadedOwnershipAudit? _ownershipAudit;
        protected override async void OnLaunched(LaunchActivatedEventArgs args)
        {
            try
            {
                var filter=Environment.GetEnvironmentVariable("KOTU_DESIGN_HARNESS_IDS");
                if (!string.IsNullOrWhiteSpace(filter))
                {
                    var ids=filter.Split(',',StringSplitOptions.TrimEntries|StringSplitOptions.RemoveEmptyEntries).Distinct().ToArray();
                    if (ids.Length==0 || ids.Any(id=>DesignCatalog.Find(id) is null)) throw new InvalidOperationException("Unknown focused harness design ID.");
                    _designs=ids.Select(DesignCatalog.Resolve).ToArray();
                }
                AppDesignService.Initialize(new DesignPreferences(new MemorySettingsService()));
                _ambientBaseline=LiveDesign.CaptureLoadedSnapshot();
                _loadedStages.Add(("after initialization before fixtures",_ambientBaseline));
                var paletteBrushes=CapturePaletteBrushes();
                _first = new Fixture("Existing window",_menuTimeline.Add);
                CaptureLoadedStage("after first fixture construction");
                _first.Open();
                await Settle();
                CaptureLoadedStage("after first fixture opened");
                CheckSelectorContracts(_first,DesignRuntime.Current,"initial");
                await CheckPopupTypography(_first,DesignRuntime.Current,"initial");
                CaptureLoadedStage("after first popups");
                var contentIdentity = _first.Button.Content;
                var templateIdentity = _first.Button.Template;
                var originalInset=_first.Button.BorderThickness;
                _first.List.ScrollIntoView(_first.List.Items[4]); await Settle();
                _first.Input.Select(2,3);
                var protectedFont = _first.ContentText.FontFamily.Source;
                var protectedSize = _first.ContentText.FontSize;
                var previewFont = _first.PreviewText.FontFamily.Source;
                Check(_first.WeightLabel.FontWeight.Weight==DesignScope.FontWeight(600,DesignRuntime.Current),"initial pre-load authored weight");
                foreach(var theme in new[] { ElementTheme.Light, ElementTheme.Dark })
                {
                    _first.Root.RequestedTheme = theme;
                    foreach(var design in _designs)
                    {
                        var listRootIdentity=(_first.List.ContainerFromIndex(4) as ListViewItem)?.ContentTemplateRoot;
                        await ForceCollection();
                        AppDesignService.Apply(design.Id);
                        await Settle();
                        CheckPaletteBrushes(paletteBrushes,design);
                        CheckSelectorContracts(_first,design,"existing");
                        await CheckSelectorOpacity(_first,design);
                        await CheckPopupTypography(_first,design,"existing and reopened");
                        Check(ReferenceEquals(contentIdentity,_first.Button.Content),design.Id+": button content identity");
                        Check(ReferenceEquals(templateIdentity,_first.Button.Template),design.Id+": template identity");
                        Check(_first.Button.BorderThickness==originalInset,design.Id+": original button content inset");
                        Check(ReferenceEquals(listRootIdentity,(_first.List.ContainerFromIndex(4) as ListViewItem)?.ContentTemplateRoot),design.Id+": native data-template root identity");
                        Check(listRootIdentity is Grid { Name:"HarnessRow" },design.Id+": native data-template root contract");
                        Check(_first.Input.Text=="Editable sample 가나다" && _first.Input.SelectionStart==2 && _first.Input.SelectionLength==3,design.Id+": text input/selection preserved");
                        foreach(var width in new[] { 420d,180d,420d })
                        {
                            _first.ResizeHost.Width=width; await Settle();
                            Check(Math.Abs(_first.Backdrop.ActualWidth-(width-6))<1,design.Id+": source shrinks and expands");
                            var layers=_first.ResizeHost.Children.OfType<DesignOverlay>().ToArray();
                            Check(layers.Length==2,design.Id+": no duplicate decorations");
                            Check(layers.All(layer=>layer.DesiredSize.Width==0 && layer.DesiredSize.Height==0),design.Id+": decorations have zero desired size");
                            Check(layers.Any(layer=>Math.Abs(layer.Surface.ActualWidth-_first.Backdrop.ActualWidth)<1 && Math.Abs(layer.Opacity-.37)<.001),design.Id+": backdrop geometry and single opacity");
                        }
                        var paddedLayers=((Grid)_first.PaddedCard.Child).Children.OfType<DesignOverlay>().ToArray();
                        Check(paddedLayers.Length==1 && ReferenceEquals(paddedLayers[0].Source,_first.PaddedCard),design.Id+": padded border owns its paint bounds");
                        if (paddedLayers.Length==1)
                        {
                            var layer=paddedLayers[0];
                            var origin=layer.Surface.TransformToVisual(_first.PaddedCard).TransformPoint(new Windows.Foundation.Point(0,0));
                            Check(Math.Abs(origin.X)<.5 && Math.Abs(origin.Y)<.5,design.Id+": padded border outer origin");
                            Check(Math.Abs(layer.Surface.ActualWidth-_first.PaddedCard.ActualWidth)<.5 && Math.Abs(layer.Surface.ActualHeight-_first.PaddedCard.ActualHeight)<.5,design.Id+": padded border outer size");
                            Check(layer.DesiredSize.Width==0 && layer.DesiredSize.Height==0 && layer.Opacity==1 && Math.Abs(_first.PaddedCard.Opacity-.6)<.000001,
                                FormattableString.Invariant($"{design.Id}: padded decoration cannot inflate or multiply opacity layer={layer.Opacity:G17} card={_first.PaddedCard.Opacity:G17} desired=({layer.DesiredSize.Width:G17},{layer.DesiredSize.Height:G17})"));
                        }
                        Check(ReferenceEquals(_first.SemanticLabel.Foreground,_first.SemanticBrush),design.Id+": local artwork contrast preserved");
                        Check(_first.NeutralLabel.Foreground is SolidColorBrush neutral && neutral.Color==DesignSurface.Brush(LiveDesign.Palette(_first.NeutralLabel).Foreground).Color,design.Id+": explicit neutral card label contrast");
                        foreach(var weight in new ushort[] { 400,600,400,600 })
                        {
                            _first.WeightSource.FontWeight=new Windows.UI.Text.FontWeight { Weight=weight }; await Settle();
                            Check(_first.WeightLabel.FontWeight.Weight==DesignScope.FontWeight(weight,design) && _first.WeightButton.FontWeight.Weight==DesignScope.FontWeight(weight,design),design.Id+": bound current-item emphasis "+weight);
                        }
                        Check(_first.WeightLabel.GetBindingExpression(LiveDesign.AuthoredFontWeightProperty) is not null && _first.WeightButton.GetBindingExpression(LiveDesign.AuthoredFontWeightProperty) is not null,design.Id+": authored bindings retained");
                        _first.WeightHost.Children.Remove(_first.WeightLabel); await Settle();
                        _first.WeightSource.FontWeight=new Windows.UI.Text.FontWeight { Weight=400 };
                        _first.WeightHost.Children.Add(_first.WeightLabel); await Settle();
                        Check(_first.WeightLabel.FontWeight.Weight==DesignScope.FontWeight(400,design),design.Id+": recycled label uses updated authored weight");
                        _first.DirectWeightLabel.FontWeight=new Windows.UI.Text.FontWeight { Weight=700 };
                        _first.DirectWeightButton.FontWeight=new Windows.UI.Text.FontWeight { Weight=700 }; await Settle();
                        Check(_first.DirectWeightLabel.FontWeight.Weight==DesignScope.FontWeight(700,design) && _first.DirectWeightButton.FontWeight.Weight==DesignScope.FontWeight(700,design),design.Id+": external text/control weight baseline");
                        _first.DirectWeightLabel.FontWeight=new Windows.UI.Text.FontWeight { Weight=400 };
                        _first.DirectWeightButton.FontWeight=new Windows.UI.Text.FontWeight { Weight=400 };
                        CheckGalleryWeights(_first,design,"existing");
                        Check(_first.NativeNormalButton.FontWeight.Weight==design.FontWeight,design.Id+": native Button Normal overrides inherited emphasis");
                        Check(Math.Abs(_first.NativeNormalButton.FontSize-DesignScope.FontSize(14,design))<.01,design.Id+": native Button size overrides inherited size");
                        var lateCaption=new TextBlock { Text="Late gallery caption",FontSize=12 };
                        _first.GalleryBody.Children.Add(lateCaption); await Settle();
                        Check(lateCaption.FontWeight.Weight==design.FontWeight && lateCaption.ReadLocalValue(TextBlock.FontWeightProperty)==DependencyProperty.UnsetValue,design.Id+": late caption inherits display once");
                        Check(lateCaption.FontFamily.Source==design.FontFamily,design.Id+": late caption keeps body family");
                        _first.GalleryBody.Children.Remove(lateCaption);
                        _first.GalleryBody.Children.Remove(_first.GalleryCaption); await Settle();
                        _first.GalleryBody.Children.Add(_first.GalleryCaption); await Settle();
                        CheckGalleryWeights(_first,design,"reloaded");
                        Check(_first.ContentText.FontFamily.Source==protectedFont && _first.ContentText.FontSize==protectedSize,design.Id+": protected content");
                        Check(_first.PreviewText.FontFamily.Source==previewFont,design.Id+": explicit preview isolation");
                        Check(_first.Choice.BorderThickness.Left==3 && ReferenceEquals(_first.Choice.BorderBrush,_first.ChoiceMarker),design.Id+": pending choice marker");
                        Check(_first.List.SelectedIndex==4,design.Id+": list selection");
                        Check(Math.Abs(_first.Slider.Value-37)<.001,design.Id+": slider value");
                        Check(_first.Button.Width==32 && _first.Button.Height==32 && _first.Bar.Height==44,design.Id+": fixed toolbar metrics");
                        Check(_first.Checks[0].IsChecked==false && _first.Checks[1].IsChecked==true && _first.Checks[2].IsChecked is null,design.Id+": checkbox state");
                        Check(!_first.Switches[0].IsOn && _first.Switches[1].IsOn && _first.Switches[2].IsOn && !_first.Switches[2].IsEnabled,design.Id+": switch state");
                        Check(_first.Progress.Value==37 && _first.PausedProgress.ShowPaused && _first.ErrorProgress.ShowError && _first.BusyProgress.IsIndeterminate,design.Id+": progress states");
                        _first.ScaleLabel.FontSize=11; await Settle();
                        Check(Math.Abs(_first.ScaleLabel.FontSize-DesignScope.FontSize(11,design))<.01,design.Id+": external UI scale remains a baseline");
                        await CheckHit(_first,_first.Checks[^1],new Windows.Foundation.Point(10,_first.Checks[^1].ActualHeight/2),design.Id+": empty checkbox hit surface");
                        await CheckHit(_first,_first.BlankInput,new Windows.Foundation.Point(12,_first.BlankInput.ActualHeight/2),design.Id+": empty input hit surface");
                        await CheckHit(_first,_first.Combo,new Windows.Foundation.Point(55,_first.Combo.ActualHeight/2),design.Id+": combo empty-area hit surface");
                        await CheckHit(_first,_first.Switches[0],new Windows.Foundation.Point(20,_first.Switches[0].ActualHeight/2),design.Id+": switch hit surface");
                        _first.Icon18.Glyph = DesignIconCatalog.GlyphFor("Document").ToString();
                        await Settle();
                        Check(_first.Icon18.Glyph==(design.Decoration==DesignDecoration.Terminal ? "[T]" : DesignIconCatalog.GlyphFor("Document").ToString()),design.Id+": external glyph update");
                        _first.Icon18.Glyph = DesignIconCatalog.GlyphFor("Folder").ToString();
                        if (design.Decoration==DesignDecoration.Pixel)
                            Check(_first.Icon18.FontSize==16 && _first.Icon28.FontSize==32 && _first.Icon30.FontSize==32,"pixel: 18/28/30 glyph sizes");

                        var scroll = Descendants(_first.List).OfType<ScrollViewer>().FirstOrDefault();
                        if (scroll is not null)
                        {
                            scroll.ChangeView(null,180,null,true); await Settle();
                            var offset = scroll.VerticalOffset;
                            var alternate = design.Id=="ink" ? "precision" : "ink";
                            AppDesignService.Apply(alternate); AppDesignService.Apply(design.Id); await Settle();
                            CheckPaletteBrushes(paletteBrushes,design);
                            Check(Math.Abs(scroll.VerticalOffset-offset)<1,design.Id+": scroll preserved");
                            CheckGalleryWeights(_first,design,"round trip");
                        }

                        var before = LiveDesign.LoadedElementCount;
                        var newWindowBeforeConstruction=LiveDesign.CaptureDiagnosticGcSnapshot();
                        _second = new Fixture("New window — "+design.Id,_menuTimeline.Add);
                        _second.Root.RequestedTheme = theme;
                        _second.Open();
                        var newWindowAfterOpen=LiveDesign.CaptureDiagnosticGcSnapshot();
                        await Settle();
                        var newWindowAfterSettle=LiveDesign.CaptureDiagnosticGcSnapshot();
                        CheckSelectorContracts(_second,design,"new window");
                        CheckGalleryWeights(_second,design,"new window");
                        Check(_second.Button.FontFamily.Source==design.FontFamily,design.Id+": new window font");
                        Check(_second.Button.Focus(FocusState.Keyboard),design.Id+": keyboard focus");
                        var nativeCheck = Descendants(_second.Checks[1]).OfType<FrameworkElement>().FirstOrDefault(e=>e.Name=="NormalRectangle");
                        var nativeCheckPassed=nativeCheck is not null && nativeCheck.Clip is not null;
                        Check(nativeCheckPassed,design.Id+": native checkbox replacement");
                        var nativeCheckTimestamp=System.Diagnostics.Stopwatch.GetTimestamp();
                        _second.TraceAssertion("new window checkbox",new { Passed=nativeCheckPassed,PartFound=nativeCheck is not null,
                            ClipPresent=nativeCheckPassed,Owner=LiveDesign.DiagnosticConnection(_second.Checks[1]),
                            Part=nativeCheck is null ? null : LiveDesign.DiagnosticConnection(nativeCheck) },nativeCheckTimestamp);
                        foreach(var toggle in _second.Switches)
                        {
                            var drawings=Descendants(toggle).OfType<FrameworkElement>().Where(e=>e.Name is "OuterBorder" or "SwitchKnobBounds" or "SwitchKnobOff" or "SwitchKnobOn").ToArray();
                            var drawingsPassed=drawings.Length==4 && drawings.All(e=>e.Clip is not null);
                            Check(drawingsPassed,design.Id+": switch drawing replacement");
                            var drawingsTimestamp=System.Diagnostics.Stopwatch.GetTimestamp();
                            Check(Descendants(toggle).OfType<Thumb>().Any(e=>e.Name=="SwitchThumb"),design.Id+": native switch input target");
                            _second.TraceAssertion("new window switch",new { Passed=drawingsPassed,DrawingCount=drawings.Length,
                                Owner=LiveDesign.DiagnosticConnection(toggle),
                                Drawings=drawings.Select(part=>new { ClipPresent=part.Clip is not null,Element=LiveDesign.DiagnosticConnection(part) }).ToArray() },drawingsTimestamp);
                        }
                        _second.TraceAssertion("new window initial timing",new { BeforeConstruction=newWindowBeforeConstruction,
                            AfterOpen=newWindowAfterOpen,AfterOriginal100msSettle=newWindowAfterSettle,
                            ConstructionAndOpen=LiveDesign.DiagnosticGcDifference(newWindowBeforeConstruction,newWindowAfterOpen),
                            OriginalSettle=LiveDesign.DiagnosticGcDifference(newWindowAfterOpen,newWindowAfterSettle) });
                        _second.Switches[0].IsOn=true; await Settle();
                        Check(_second.Switches[0].IsOn,design.Id+": switch state transition");
                        var indicator=Descendants(_second.Progress).OfType<FrameworkElement>().FirstOrDefault(e=>e.Name=="DeterminateProgressBarIndicator");
                        Check(indicator is not null && indicator.Clip is null && indicator.ActualHeight==3,design.Id+": native progress animation target");
                        await CheckPopupTypography(_second,design,"new window first popup");
                        await OpenMenu(_second,"new window second opening"); await Settle();
                        _second.TraceMenu("before top-level mark checks");
                        CheckMenuTypography(_second,design,"second opening");
                        foreach(var menuCheck in _second.Menu.Items.Where(item=>item is ToggleMenuFlyoutItem or RadioMenuFlyoutItem))
                        {
                            var original=Descendants(menuCheck).OfType<FrameworkElement>().FirstOrDefault(e=>e.Name=="CheckGlyph");
                            var parts=string.Join(";",Descendants(menuCheck).OfType<FrameworkElement>().Where(e=>e.Name.Contains("Check",StringComparison.Ordinal))
                                .Select(e=>$"{e.GetType().Name}:{e.Name} clip={e.Clip?.GetType().Name ?? "null"} visibility={e.Visibility}"));
                            Check(original is not null && original.Clip is not null,design.Id+": "+theme+" native menu mark replacement "+menuCheck.GetType().Name+" text="+(menuCheck as MenuFlyoutItem)?.Text+" loaded="+menuCheck.IsLoaded+" "+parts);
                        }
                        foreach(var menuItem in _second.Menu.Items.OfType<MenuFlyoutItem>())
                        {
                            var label=Descendants(menuItem).OfType<TextBlock>().FirstOrDefault(e=>e.Name=="TextBlock");
                            var textParts=string.Join(";",Descendants(menuItem).OfType<TextBlock>().Select(text=>$"{text.Name}={text.FontSize:G17}"));
                            Check(label is not null && Math.Abs(label.FontSize-menuItem.FontSize)<.01,
                                FormattableString.Invariant($"{design.Id}: menu typography is scaled once item={menuItem.Text} itemSize={menuItem.FontSize:G17} label={label?.FontSize:G17} parts={textParts}"));
                        }
                        foreach(var subMenu in _second.Menu.Items.OfType<MenuFlyoutSubItem>())
                        {
                            var chevron=Descendants(subMenu).OfType<FontIcon>().FirstOrDefault(e=>e.Name=="SubItemChevron");
                            var expected=design.Decoration==DesignDecoration.Terminal ? DesignIconCatalog.Create("Next",design).Text : DesignIconCatalog.GlyphFor("Next").ToString();
                            Check(chevron is not null && chevron.Glyph==expected && chevron.FontFamily.Source==DesignIconCatalog.FontFamily(design),design.Id+": submenu chevron design family and terminal alias");
                        }
                        await CheckNestedMenuRealization(_second,design);
                        await CloseMenu(_second,"after nested checks");
                        var afterOpen = LiveDesign.LoadedElementCount;
                        _second.Window.Close(); _second=null; await Settle();
                        var afterClose = LiveDesign.LoadedElementCount;
                        Check(afterClose<=before,design.Id+": window subscriptions released");
                        _results.Add(new { Design=design.Id, Theme=theme.ToString(), HighContrast=LiveDesign.HighContrast,
                            LoadedBefore=before, LoadedOpen=afterOpen, LoadedAfterClose=afterClose,
                            Font=_first.Button.FontFamily.Source, ToolbarGlyphSize=_first.Icon18.FontSize,
                            Tile28Size=_first.Icon28.FontSize, Tile30Size=_first.Icon30.FontSize });
                        await CheckNativeRadioBaseline(design,theme);
                        await CheckNativeRadioBaseline(design,theme,useDefaultPresenter:true);
                        // Each explicit factory case owns a fresh host; the existing
                        // product fixture keeps the message loop alive between probes.
                        _iconProbe?.Window.Close();
                        _iconProbe=new DesignIconProbe();
                        _iconProbeResults.Add(await _iconProbe.Observe(design,theme));
                        LiveDesign.RecordDiagnosticWindow(_iconProbe.Window,_iconProbe.Root);
                        CaptureLoadedStage("after "+theme+" "+design.Id);
                        SaveResult(completed:false);
                    }
                }
            }
            catch(Exception error) { _failures.Add(error.ToString()); }
            finally
            {
                // Closing the last WinUI window ends the message loop before an awaited
                // continuation can run. This excluded blank window keeps cleanup observable.
                _cleanupWindow=new Window { Title="KOTU A427 validation — cleanup",
                    Content=new Border { Tag=DesignRuntime.PreviewScopeMarker } };
                _cleanupWindow.AppWindow.Resize(new Windows.Graphics.SizeInt32(200,100));
                _cleanupWindow.Activate();
                _nativeBaseline?.Window.Close();_second?.Window.Close(); _first?.Window.Close();
                _iconProbe?.Window.Close();_iconProbe=null;
                _nativeBaseline=null;_second=null;_first=null;
                await Settle();
                LiveDesign.RecordDiagnosticWindow(_cleanupWindow,(FrameworkElement)_cleanupWindow.Content);
                _ownershipAudit=LiveDesign.AuditLoadedOwnership(_ambientBaseline,(FrameworkElement)_cleanupWindow.Content,_loadedStages);
                Check(_ownershipAudit.ClosedFixtureStates.Length==0,"closed fixture subscriptions released "+string.Join(";",_ownershipAudit.ClosedFixtureStates));
                Check(_ownershipAudit.UnexpectedStates.Length==0,"no new unowned subscriptions "+string.Join(";",_ownershipAudit.UnexpectedStates));
                SaveResult(completed:true);
                Environment.ExitCode=_failures.Count==0 && _results.Count==_designs.Count*2 &&
                    _nativeBaselineFailures.Count==0 && _nativeBaselineResults.Count==_designs.Count*2 &&
                    _defaultPresenterFailures.Count==0 && _defaultPresenterResults.Count==_designs.Count*2 && IconProbesPassed ? 0 : 1;
                _cleanupWindow.Close();_cleanupWindow=null;
                Exit();
            }
        }
        private void CaptureLoadedStage(string stage)=>_loadedStages.Add((stage,LiveDesign.CaptureLoadedSnapshot()));
        private bool IconProbesPassed=>_iconProbeResults.Count==_designs.Count*2 &&
            _iconProbeResults.All(result=>result.Passed && result.Observations.Count==DesignIconProbe.ObservationsPerCase);
        private void SaveResult(bool completed)
        {
            var json=JsonSerializer.Serialize(new { Completed=completed,
                Passed=completed && _failures.Count==0 && _results.Count==_designs.Count*2 &&
                    _nativeBaselineFailures.Count==0 && _nativeBaselineResults.Count==_designs.Count*2 &&
                    _defaultPresenterFailures.Count==0 && _defaultPresenterResults.Count==_designs.Count*2 && IconProbesPassed,
                ProductChecksPassed=completed && _failures.Count==0 && _results.Count==_designs.Count*2,
                NativeBaselinePassed=completed && _nativeBaselineFailures.Count==0 && _nativeBaselineResults.Count==_designs.Count*2,
                DefaultPresenterPassed=completed && _defaultPresenterFailures.Count==0 && _defaultPresenterResults.Count==_designs.Count*2,
                UsesRealAppResources=true, UsesMemorySettings=true, RemainingLoaded=LiveDesign.LoadedElementCount,
                RemainingLoadedDetails=LiveDesign.LoadedElementDescriptions,
                KnownWindowOwners=LiveDesign.DiagnosticOwnerDescriptions,
                InitialAmbientStates=LiveDesign.DescribeLoadedSnapshot(_ambientBaseline),FinalOwnershipAudit=_ownershipAudit,
                LoadedStages=_loadedStages.Select(stage=>new { stage.Stage,Roots=LiveDesign.SummarizeLoadedSnapshot(stage.Snapshot) }).ToArray(),
                SelectedDesignIds=_designs.Select(design=>design.Id).ToArray(), ExpectedCaseCount=_designs.Count*2,
                CompletedCaseCount=_results.Count,IsFocusedRun=_designs.Count!=DesignCatalog.All.Count,
                Results=_results, Failures=_failures, MenuTimeline=_menuTimeline,
                NativeBaselineResults=_nativeBaselineResults,NativeBaselineFailures=_nativeBaselineFailures,
                NativeBaselineExpectedCaseCount=_designs.Count*2,NativeBaselineCompletedCaseCount=_nativeBaselineResults.Count,
                NativeBaselineScope="Separate unregistered Window with the same App resources/templates. Isolates LiveDesign window tracking, not stock WinUI or all A427 templates. Baseline success never suppresses product failures.",
                DefaultPresenterResults=_defaultPresenterResults,DefaultPresenterFailures=_defaultPresenterFailures,
                DefaultPresenterExpectedCaseCount=_designs.Count*2,DefaultPresenterCompletedCaseCount=_defaultPresenterResults.Count,
                DefaultPresenterScope="Separate unregistered Window with an explicit empty presenter Style before first ShowAt. Native template selection is verified at runtime; App palette and other styles are still shared. Not stock WinUI. Success never suppresses either existing group.",
                PresenterObservations=_presenterObservations,
                IconProbePassed=completed && IconProbesPassed,IconProbeResults=_iconProbeResults,
                IconProbeExpectedCaseCount=_designs.Count*2,IconProbeCompletedCaseCount=_iconProbeResults.Count,
                IconProbeExpectedObservationCount=_designs.Count*2*DesignIconProbe.ObservationsPerCase,
                IconProbeCompletedObservationCount=_iconProbeResults.Sum(result=>result.Observations.Count),
                IconProbeScope="A separate unregistered preview-isolated Window per design/theme case, using the common icon factory. Geometry bounds exclude unfilled viewport figures. No forced child sizing, geometry replacement, or template changes. Native layout clips are not inferred from Clip=null. These checks do not replace screenshots or any existing group.",
                ManualChecksStillRequired=new[] { "Screenshots and visual clipping", "Pointer hover/pressed and keyboard activation", "OS high contrast colors", "Real module content/playback and reparented bottom bars", "A112 Back/Esc/F11/F12" } },new JsonSerializerOptions { WriteIndented=true });
            var pending=output+".writing";
            File.WriteAllText(pending,json);
            File.Move(pending,output,true);
        }
        private void Check(bool value,string description) { if (!value) _failures.Add(description); }
        private void CheckFixture(Fixture fixture,bool value,string description)
        {
            if (fixture.RegisteredWithLiveDesign) Check(value,description);
            else if (fixture.UsesDefaultPresenter) { if (!value) _defaultPresenterFailures.Add(description); }
            else if (!value) _nativeBaselineFailures.Add(description);
        }
        private sealed record PaletteProbe(SolidColorBrush Owned,SolidColorBrush Captured);
        private static Dictionary<(string Theme,string Key),PaletteProbe> CapturePaletteBrushes()
        {
            var captured=new Dictionary<(string,string),PaletteProbe>();
            foreach(var theme in new[] { "Light","Default" })
            {
                var dictionary=(ResourceDictionary)Application.Current.Resources.ThemeDictionaries[theme];
                foreach(var key in new[] { "TextFillColorPrimaryBrush","ButtonForeground","MenuFlyoutItemForeground","ListViewItemForeground","ButtonBackground" })
                    captured.Add((theme,key),new PaletteProbe(AppDesignService.OwnedPaletteBrush(theme,key),(SolidColorBrush)dictionary[key]));
            }
            return captured;
        }
        private void CheckPaletteBrushes(Dictionary<(string Theme,string Key),PaletteProbe> captured,DesignDefinition design)
        {
            foreach(var ((theme,key),probe) in captured)
            {
                var dictionary=(ResourceDictionary)Application.Current.Resources.ThemeDictionaries[theme];
                var palette=theme=="Light" ? design.Light : design.Dark;
                var expected=DesignSurface.Color(key=="ButtonBackground" ? palette.Card : palette.Foreground);
                Check(ReferenceEquals(probe.Owned,AppDesignService.OwnedPaletteBrush(theme,key)),design.Id+": "+theme+" owned palette identity "+key);
                var resolved=(SolidColorBrush)dictionary[key];
                Check(probe.Captured.Color==expected && probe.Owned.Color==expected && resolved.Color==expected,
                    $"{design.Id}: {theme} captured native palette refresh {key} captured={probe.Captured.Color} owned={probe.Owned.Color} resolved={resolved.Color} expected={expected}");
            }
        }
        private async Task CheckSelectorOpacity(Fixture fixture,DesignDefinition design)
        {
            if (fixture.List.ContainerFromIndex(4) is not ListViewItem { ContentTemplateRoot:Grid root } item ||
                root.FindName("HarnessCutContent") is not Grid content)
            {
                Check(false,design.Id+": content-opacity fixture realized");
                return;
            }
            content.Opacity=.4; await Settle();
            var overlay=root.Children.OfType<DesignOverlay>().SingleOrDefault(layer=>ReferenceEquals(layer.Source,item));
            var alpha=1d;
            if (overlay is not null)
                for(DependencyObject? node=overlay;node is UIElement element && !ReferenceEquals(node,item);node=VisualTreeHelper.GetParent(node)) alpha*=element.Opacity;
            Check(Math.Abs(content.Opacity-.4)<.000001 && root.Opacity==1 && item.Opacity==1 && overlay is not null && Math.Abs(alpha-1)<.001,
                FormattableString.Invariant($"{design.Id}: content-only cut opacity preserves full selection opacity content={content.Opacity:G17} root={root.Opacity:G17} item={item.Opacity:G17} alpha={alpha:G17} overlay={overlay is not null}"));
            content.Opacity=1;
            // Unknown templates may put opacity directly on their root: use native state fill
            // until that root is safe again, without moving/recreating the original content.
            root.Opacity=.4; await Settle();
            var presenter=Descendants(item).OfType<ListViewItemPresenter>().FirstOrDefault();
            Check(!root.Children.OfType<DesignOverlay>().Any(layer=>ReferenceEquals(layer.Source,item)) && item.Opacity==1 &&
                presenter is { Opacity:1,SelectedBackground:SolidColorBrush { Color.A:255 } },design.Id+": arbitrary dimmed root retains full native selection");
            root.Opacity=1; await Settle();
            Check(ReferenceEquals(root,item.ContentTemplateRoot) && root.Children.OfType<DesignOverlay>().Count(layer=>ReferenceEquals(layer.Source,item))==1,
                design.Id+": opacity restoration preserves root and restores one decoration");
        }
        private void CheckSelectorContracts(Fixture fixture,DesignDefinition design,string stage)
        {
            foreach(var (list,index,decorated,name) in new (ListViewBase,int,bool,string)[]
                { (fixture.List,4,true,"List"),(fixture.Tiles,1,true,"Tiles"),(fixture.PlainList,1,false,"PlainList") })
            {
                var label=design.Id+": "+stage+" "+name;
                var item=list.ContainerFromIndex(index) as SelectorItem;
                Check(item is not null,label+" realized selector");
                if (item is null) continue;
                Check(VisualTreeHelper.GetChildrenCount(item)==1 && VisualTreeHelper.GetChild(item,0) is ListViewItemPresenter,
                    label+" direct native presenter root");
                var presenter=Descendants(item).OfType<ListViewItemPresenter>().FirstOrDefault();
                Check(presenter is not null,label+" native selection/check/focus presenter retained");
                var layers=Descendants(item).OfType<DesignOverlay>().Where(layer=>ReferenceEquals(layer.Source,item)).ToArray();
                Check(layers.Length==(decorated ? 1 : 0),label+" exact selector content host");
                var layersTimestamp=stage=="new window" ? System.Diagnostics.Stopwatch.GetTimestamp() : 0;
                if (layers.Length==1)
                {
                    var layer=layers[0];
                    var origin=layer.Surface.TransformToVisual(item).TransformPoint(new Windows.Foundation.Point(0,0));
                    var layerOrigin=layer.TransformToVisual(item).TransformPoint(new Windows.Foundation.Point(0,0));
                    var slot=LayoutInformation.GetLayoutSlot(layer);
                    Check(Math.Abs(origin.X)<.5 && Math.Abs(origin.Y)<.5,
                        FormattableString.Invariant($"{label} selector padding/check origin actual=({origin.X:F3},{origin.Y:F3}) layer=({layerOrigin.X:F3},{layerOrigin.Y:F3}) slot=({slot.X:F3},{slot.Y:F3},{slot.Width:F3},{slot.Height:F3})"));
                    Check(Math.Abs(layer.Surface.ActualWidth-item.ActualWidth)<.5 && Math.Abs(layer.Surface.ActualHeight-item.ActualHeight)<.5,
                        FormattableString.Invariant($"{label} selector outer size surface=({layer.Surface.ActualWidth:F3},{layer.Surface.ActualHeight:F3}) item=({item.ActualWidth:F3},{item.ActualHeight:F3})"));
                    Check(layer.DesiredSize.Width==0 && layer.DesiredSize.Height==0,label+" selector decoration adds no desired size");
                }
                Check(item.IsSelected,label+" selector state retained");
                if (!decorated && presenter is not null)
                    Check(presenter.SelectedBackground is SolidColorBrush { Color.A:>0 },label+" unsupported host keeps visible native selection");
                if (stage=="new window")
                {
                    // Reuse the original assertion's filtered array; inspect only direct host relations afterward.
                    var contentRoot=item.ContentTemplateRoot;
                    fixture.TraceAssertion(stage+" selector "+name,new { Index=index,ExpectedLayers=decorated ? 1 : 0,
                        LayerCount=layers.Length,Passed=layers.Length==(decorated ? 1 : 0),
                        Owner=LiveDesign.DiagnosticConnection(item),ContentTemplateRoot=ObjectSnapshot(contentRoot),
                        Layers=layers.Select(layer=>
                        {
                            var host=VisualTreeHelper.GetParent(layer);
                            return new { SourceIsItem=ReferenceEquals(layer.Source,item),Source=ObjectSnapshot(layer.Source),
                                Element=LiveDesign.DiagnosticConnection(layer),Host=ObjectSnapshot(host),
                                HostIsContentTemplateRoot=SameNative(host,contentRoot) };
                        }).ToArray() },layersTimestamp);
                }
            }
        }
        private void CheckGalleryWeights(Fixture fixture,DesignDefinition design,string stage)
        {
            var captionWeight=fixture.GalleryCaption.FontWeight.Weight;
            Check(captionWeight==design.FontWeight,design.Id+": "+stage+" caption inherits weight once");
            var captionLocalWeight=fixture.GalleryCaption.ReadLocalValue(TextBlock.FontWeightProperty);
            Check(captionLocalWeight==DependencyProperty.UnsetValue,design.Id+": "+stage+" caption keeps weight inheritance");
            var captionFamily=fixture.GalleryCaption.FontFamily.Source;
            Check(captionFamily==design.FontFamily,design.Id+": "+stage+" caption uses body baseline for family");
            var captionTimestamp=stage=="new window" ? System.Diagnostics.Stopwatch.GetTimestamp() : 0;
            foreach(var heading in new[] { fixture.GalleryTitle,fixture.StyledGalleryTitle,fixture.ImplicitGalleryTitle })
            {
                var headingWeight=heading.FontWeight.Weight;
                Check(headingWeight==DesignScope.FontWeight(600,design),design.Id+": "+stage+" explicit/style emphasis retained");
                var headingTimestamp=stage=="new window" ? System.Diagnostics.Stopwatch.GetTimestamp() : 0;
                if (stage=="new window") fixture.TraceAssertion(stage+" gallery heading",new { Weight=headingWeight,
                    ExpectedWeight=DesignScope.FontWeight(600,design),Element=LiveDesign.DiagnosticConnection(heading) },headingTimestamp);
            }
            if (stage=="new window") fixture.TraceAssertion(stage+" gallery caption",new { Weight=captionWeight,
                ExpectedWeight=design.FontWeight,LocalWeightIsUnset=captionLocalWeight==DependencyProperty.UnsetValue,
                Family=captionFamily,ExpectedFamily=design.FontFamily,Element=LiveDesign.DiagnosticConnection(fixture.GalleryCaption) },captionTimestamp);
        }
        private async Task CheckPopupTypography(Fixture fixture,DesignDefinition design,string stage)
        {
            fixture.MenuStage=stage;
            await BringFixtureTargetIntoView(fixture,fixture.Combo);
            await OpenCombo(fixture); await Settle();
            fixture.TraceMenu("before combo typography checks");
            for(var index=0;index<fixture.Combo.Items.Count;index++)
            {
                var item=fixture.Combo.ContainerFromIndex(index) as ComboBoxItem;
                var styled=ReferenceEquals(item,fixture.StyledComboItem);
                var explicitItem=ReferenceEquals(item,fixture.EmphasizedComboItem);
                var expectedSize=styled ? DesignScope.FontSize(16,design) : explicitItem ? DesignScope.FontSize(20,design) : fixture.Combo.FontSize;
                var expectedWeight=DesignScope.FontWeight(styled || explicitItem ? (ushort)600 : (ushort)400,design);
                var details=FormattableString.Invariant($"index={index} text={item?.Content} type={item?.GetType().Name} loaded={item?.IsLoaded} managed={(item is not null && LiveDesign.GetManaged(item))} weight={item?.FontWeight.Weight}/{expectedWeight} size={item?.FontSize:G17}/{expectedSize:G17} ownerSize={fixture.Combo.FontSize:G17}");
                Check(item is not null && item.FontWeight.Weight==expectedWeight,design.Id+": "+stage+" combo item weight "+details);
                Check(item is not null && Math.Abs(item.FontSize-expectedSize)<.01,design.Id+": "+stage+" combo item size "+details);
                if (item is not null && !styled && !explicitItem)
                {
                    Check(item.ReadLocalValue(Control.FontWeightProperty)==DependencyProperty.UnsetValue && item.ReadLocalValue(Control.FontSizeProperty)==DependencyProperty.UnsetValue,design.Id+": "+stage+" combo item inherits typography");
                    Check(item.FontFamily.Source==design.FontFamily,design.Id+": "+stage+" combo item keeps body family");
                }
            }
            var lateItem=new ComboBoxItem { Content="Late item" };
            fixture.Combo.Items.Add(lateItem); await Settle();
            Check(lateItem.FontWeight.Weight==fixture.Combo.FontWeight.Weight && Math.Abs(lateItem.FontSize-fixture.Combo.FontSize)<.01,design.Id+": late combo item inherits parent once");
            fixture.Combo.Items.Remove(lateItem);
            await CloseCombo(fixture); await Settle();
            await BringFixtureTargetIntoView(fixture,fixture.Dropdown);
            await OpenMenu(fixture,stage); await Settle();
            CheckMenuTypography(fixture,design,stage);
            var lateMenuItem=new MenuFlyoutItem { Text="Late menu item" };
            using(var observation=fixture.ObserveLateMenuTypography(lateMenuItem))
            {
                try
                {
                    observation.Capture("before Add");
                    var lateBeforeAdd=LiveDesign.CaptureDiagnosticGcSnapshot();
                    fixture.Menu.Items.Add(lateMenuItem);
                    var lateAfterAdd=LiveDesign.CaptureDiagnosticGcSnapshot();
                    observation.Capture("after Add");
                    await Settle();
                    var lateAfterSettle=LiveDesign.CaptureDiagnosticGcSnapshot();
                    observation.Capture("after original 100ms settle, before assertions",
                        runtime:new Fixture.MenuTypographyRuntimeSamples(lateBeforeAdd,lateAfterAdd,lateAfterSettle));
                    Check(lateMenuItem.FontWeight.Weight==design.FontWeight,design.Id+": late menu item inherits parent once");
                    Check(Math.Abs(lateMenuItem.FontSize-DesignScope.FontSize(14,design))<.01,design.Id+": late menu item uses native size once");
                }
                finally
                {
                    observation.Capture("before Remove");
                    fixture.Menu.Items.Remove(lateMenuItem);
                    observation.Capture("after Remove");
                }
            }
            await CloseMenu(fixture,stage);await Settle();
        }

        private Task OpenMenu(Fixture fixture,string stage)
        {
            fixture.MenuStage=stage;
            var previousOpened=fixture.MenuOpenedCount;
            var items=fixture.Menu.Items.OfType<FrameworkElement>().ToArray();
            return AwaitMenuState(fixture,"open top-level menu",()=>
                fixture.MenuOpenedCount>previousOpened && fixture.MenuPhase=="Opened" && fixture.Menu.IsOpen && items.All(item=>item.IsLoaded),
                ()=>fixture.Menu.ShowAt(fixture.Dropdown),items);
        }
        private Task CloseMenu(Fixture fixture,string stage)
        {
            fixture.MenuStage=stage;
            var previousClosed=fixture.MenuClosedCount;
            var alreadyClosed=fixture.MenuPhase=="Closed" && !fixture.Menu.IsOpen;
            var items=fixture.Menu.Items.OfType<FrameworkElement>().ToArray();
            return AwaitMenuState(fixture,"close top-level menu",()=>
                (alreadyClosed || fixture.MenuClosedCount>previousClosed) && fixture.MenuPhase=="Closed" && !fixture.Menu.IsOpen,
                fixture.Menu.Hide,items);
        }
        private Task OpenCombo(Fixture fixture)
        {
            var previousOpened=fixture.ComboOpenedCount;
            return AwaitFixtureState(fixture,"open combo",()=>fixture.ComboOpenedCount>previousOpened && fixture.ComboPhase=="Opened" && fixture.Combo.IsDropDownOpen &&
                Enumerable.Range(0,fixture.Combo.Items.Count).All(index=>fixture.Combo.ContainerFromIndex(index) is ComboBoxItem { IsLoaded:true }),
                ()=>fixture.Combo.IsDropDownOpen=true,true,fixture.Combo.Items.OfType<FrameworkElement>().ToArray());
        }
        private Task CloseCombo(Fixture fixture)
        {
            var previousClosed=fixture.ComboClosedCount;
            var alreadyClosed=fixture.ComboPhase=="Closed" && !fixture.Combo.IsDropDownOpen;
            return AwaitFixtureState(fixture,"close combo",()=>
                (alreadyClosed || fixture.ComboClosedCount>previousClosed) && fixture.ComboPhase=="Closed" && !fixture.Combo.IsDropDownOpen,
                ()=>fixture.Combo.IsDropDownOpen=false,true,fixture.Combo.Items.OfType<FrameworkElement>().ToArray());
        }
        private Task AwaitMenuState(Fixture fixture,string stage,Func<bool> ready,Action transition,params FrameworkElement[] elements)=>
            AwaitFixtureState(fixture,stage,ready,transition,false,elements);
        private async Task AwaitFixtureState(Fixture fixture,string stage,Func<bool> ready,Action transition,bool observeCombo,params FrameworkElement[] elements)
        {
            var diagnosticGcBefore=LiveDesign.CaptureDiagnosticGcSnapshot();
            var completed=new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var active=true;var queued=false;
            var started=System.Diagnostics.Stopwatch.GetTimestamp();
            // Capture the UI dispatcher here. The worker sees neither fixture nor ready.
            var diagnosticDispatcher=fixture.Root.DispatcherQueue;
            using var diagnosticWork=LiveDesign.BeginDiagnosticWorkObservation();
            var diagnosticBefore=diagnosticWork.Snapshot();
            DiagnosticDispatcherPings? diagnosticPings=null;
            var diagnosticRecorded=false;
            void RecordWork(string outcome)
            {
                // End the runtime sample before allocating/serializing observation payloads.
                var gcAfter=LiveDesign.CaptureDiagnosticGcSnapshot();
                var after=diagnosticWork.Snapshot();
                diagnosticWork.Dispose();
                var pings=diagnosticPings?.StopAndSnapshot();
                fixture.TraceReadinessWork(stage,new { Outcome=outcome,StartedTimestamp=started,ObserveCombo=observeCombo,
                    Before=diagnosticBefore,After=after,Delta=LiveDesign.DiagnosticWorkObservation.Difference(diagnosticBefore,after),
                    Gc=new { Before=diagnosticGcBefore,After=gcAfter,Delta=LiveDesign.DiagnosticGcDifference(diagnosticGcBefore,gcAfter) },Pings=pings });
                diagnosticRecorded=true;
            }
            var probeTimeline=new List<object>();
            void TraceProbe(string action,bool? isReady=null)
            {
                if (observeCombo) return;
                probeTimeline.Add(new { Action=action,ElapsedMs=System.Diagnostics.Stopwatch.GetElapsedTime(started).TotalMilliseconds,
                    Active=active,Queued=queued,Completed=completed.Task.IsCompleted,Ready=isReady });
            }
            var observed=new HashSet<FrameworkElement>();
            void Observe(FrameworkElement element)
            {
                if (observed.Add(element)) { element.Loaded+=OnElementState;element.Unloaded+=OnElementState; }
            }
            void Probe()
            {
                TraceProbe("probe invoked");
                queued=false;
                if (!active || completed.Task.IsCompleted) { TraceProbe("probe skipped"); return; }
                try
                {
                    // These are the fixture's four/five known inputs. Observe containers as
                    // native layout creates them; no application data source is enumerated.
                    if (observeCombo)
                        for(var index=0;index<fixture.Combo.Items.Count;index++)
                            if (fixture.Combo.ContainerFromIndex(index) is FrameworkElement container) Observe(container);
                    if (fixture.IsClosed) { TraceProbe("fixture closed"); completed.TrySetResult(false); }
                    else
                    {
                        var isReady=ready();
                        TraceProbe("ready evaluated",isReady);
                        if (isReady) completed.TrySetResult(true);
                    }
                }
                catch(Exception error) { TraceProbe("probe exception: "+error.GetType().Name); completed.TrySetException(error); }
            }
            void QueueProbe()
            {
                TraceProbe("queue requested");
                if (!active || queued) return;
                queued=true;
                if (!fixture.Root.DispatcherQueue.TryEnqueue(Probe)) { queued=false;TraceProbe("enqueue rejected");completed.TrySetResult(false); }
                else TraceProbe("probe enqueued");
            }
            void OnElementState(object sender,RoutedEventArgs args)=>QueueProbe();
            void OnComboLayout(object? sender,object args)=>QueueProbe();
            using var deadline=new CancellationTokenSource();
            long? openToken=null;
            long? comboToken=null;
            try
            {
                fixture.MenuStateChanged+=QueueProbe;
                foreach(var element in elements) Observe(element);
                openToken=fixture.Menu.RegisterPropertyChangedCallback(FlyoutBase.IsOpenProperty,(_,_)=>QueueProbe());
                if (observeCombo)
                {
                    fixture.Combo.LayoutUpdated+=OnComboLayout;
                    comboToken=fixture.Combo.RegisterPropertyChangedCallback(ComboBox.IsDropDownOpenProperty,(_,_)=>QueueProbe());
                }
                fixture.TraceMenu("before "+stage);
                transition();
                fixture.TraceMenu("after synchronous "+stage);
                QueueProbe();
                var timeout=Task.Delay(TimeSpan.FromSeconds(2),deadline.Token);
                diagnosticPings=new DiagnosticDispatcherPings(diagnosticDispatcher);
                var finished=await Task.WhenAny(completed.Task,timeout);
                var passed=finished==completed.Task && await completed.Task;
                RecordWork(finished==completed.Task ? "condition" : "timeout");
                if (!observeCombo) fixture.TraceMenu("readiness diagnostic "+stage,readiness:new {
                    ElapsedMs=System.Diagnostics.Stopwatch.GetElapsedTime(started).TotalMilliseconds,
                    Winner=finished==completed.Task ? "condition" : "timeout",ConditionStatus=completed.Task.Status.ToString(),
                    TimeoutStatus=timeout.Status.ToString(),Passed=passed,Probes=probeTimeline.ToArray() });
                fixture.TraceMenu((passed ? "ready " : "timeout or closed ")+stage);
                CheckFixture(fixture,passed,$"{DesignRuntime.Current.Id}: {fixture.Root.RequestedTheme} {fixture.MenuStage} {stage} reached event-backed state within 2s");
            }
            finally
            {
                active=false;deadline.Cancel();
                if (!diagnosticRecorded) RecordWork("exception or interrupted");
                diagnosticPings?.Dispose();
                fixture.MenuStateChanged-=QueueProbe;
                foreach(var element in observed) { element.Loaded-=OnElementState;element.Unloaded-=OnElementState; }
                fixture.Combo.LayoutUpdated-=OnComboLayout;
                try
                {
                    if (openToken is { } token) fixture.Menu.UnregisterPropertyChangedCallback(FlyoutBase.IsOpenProperty,token);
                }
                finally
                {
                    if (comboToken is { } token) fixture.Combo.UnregisterPropertyChangedCallback(ComboBox.IsDropDownOpenProperty,token);
                }
            }
        }
        private void CheckMenuTypography(Fixture fixture,DesignDefinition design,string stage)
        {
            foreach(var item in fixture.Menu.Items.OfType<Control>())
            {
                var emphasized=ReferenceEquals(item,fixture.EmphasizedMenuItem);
                var expectedWeight=DesignScope.FontWeight(emphasized ? (ushort)600 : (ushort)400,design);
                var expectedSize=DesignScope.FontSize(emphasized ? 20 : 14,design);
                var text=item is MenuFlyoutItem menuItem ? menuItem.Text : item is MenuFlyoutSubItem submenu ? submenu.Text : "";
                var details=FormattableString.Invariant($"type={item.GetType().Name} text={text} loaded={item.IsLoaded} managed={LiveDesign.GetManaged(item)} weight={item.FontWeight.Weight}/{expectedWeight} size={item.FontSize:G17}/{expectedSize:G17}");
                Check(item.FontWeight.Weight==expectedWeight,design.Id+": "+stage+" menu item weight "+details);
                Check(Math.Abs(item.FontSize-expectedSize)<.01,design.Id+": "+stage+" menu native/local size scaled once "+details);
                if (!emphasized) Check(item.ReadLocalValue(Control.FontWeightProperty)==DependencyProperty.UnsetValue,design.Id+": "+stage+" menu inherits weight");
            }
        }
        private async Task CheckNestedMenuRealization(Fixture fixture,DesignDefinition design)
        {
            var peer=Microsoft.UI.Xaml.Automation.Peers.FrameworkElementAutomationPeer.CreatePeerForElement(fixture.DelayedSubmenu);
            var pattern=peer?.GetPattern(Microsoft.UI.Xaml.Automation.Peers.PatternInterface.ExpandCollapse)
                as Microsoft.UI.Xaml.Automation.Provider.IExpandCollapseProvider;
            CheckFixture(fixture,pattern is not null,design.Id+": native submenu expand/collapse provider");
            if (pattern is null) return;
            // The outer flyout is already open. Realize this submenu separately, then emulate
            // the asynchronous arrival of an audio device while the submenu remains open.
            var late=new RadioMenuFlyoutItem { Text="Late nested device",GroupName=fixture.NestedRadioGroup };
            fixture.SubmenuPattern=pattern;
            using var observation=fixture.ObserveLateRadio(late);
            try
            {
                await AwaitMenuState(fixture,"expand initial submenu",()=>fixture.Menu.IsOpen &&
                    pattern.ExpandCollapseState==Microsoft.UI.Xaml.Automation.ExpandCollapseState.Expanded && fixture.NestedRadio.IsLoaded,
                    pattern.Expand,fixture.NestedRadio);
                await Settle();
                fixture.TraceMenu("before initial nested checks");
                CheckPresenterConfiguration(fixture,"initial nested submenu",includeNested:true);
                CheckRadioFixture(fixture,fixture.NestedRadio,design,"initial nested device");
                var template=fixture.NestedRadio.Template;
                await AwaitMenuState(fixture,"add late nested radio",()=>fixture.Menu.IsOpen &&
                    pattern.ExpandCollapseState==Microsoft.UI.Xaml.Automation.ExpandCollapseState.Expanded && fixture.NestedRadio.IsLoaded && late.IsLoaded,
                    ()=>fixture.DelayedSubmenu.Items.Add(late),fixture.NestedRadio,late);
                await Settle();
                fixture.TraceMenu("after awaited late add");
                CheckPresenterConfiguration(fixture,"after late nested add",includeNested:true);
                CheckRadioFixture(fixture,fixture.NestedRadio,design,"initial nested device after late add");
                CheckRadioFixture(fixture,late,design,"late nested device");
                fixture.TraceMenu("before late IsChecked setter");
                late.IsChecked=true;
                fixture.TraceMenu("after synchronous late IsChecked setter");
                await Settle();
                fixture.TraceMenu("after awaited late IsChecked setter");
                CheckFixture(fixture,late.IsChecked && !fixture.NestedRadio.IsChecked,
                    $"{design.Id}: {fixture.Root.RequestedTheme} native nested radio group state group={fixture.NestedRadioGroup} lateChecked={late.IsChecked} initialChecked={fixture.NestedRadio.IsChecked}");
                CheckFixture(fixture,ReferenceEquals(template,fixture.NestedRadio.Template),design.Id+": nested native template identity");
                await AwaitMenuState(fixture,"collapse before reopen",()=>
                    pattern.ExpandCollapseState==Microsoft.UI.Xaml.Automation.ExpandCollapseState.Collapsed,pattern.Collapse,fixture.NestedRadio,late);
                await Settle();
                await AwaitMenuState(fixture,"reopen late submenu",()=>fixture.Menu.IsOpen &&
                    pattern.ExpandCollapseState==Microsoft.UI.Xaml.Automation.ExpandCollapseState.Expanded && fixture.NestedRadio.IsLoaded && late.IsLoaded,
                    pattern.Expand,fixture.NestedRadio,late);
                await Settle();
                CheckPresenterConfiguration(fixture,"reopened nested submenu",includeNested:true);
                CheckRadioFixture(fixture,late,design,"reopened late nested device");
                await AwaitMenuState(fixture,"final submenu collapse",()=>
                    pattern.ExpandCollapseState==Microsoft.UI.Xaml.Automation.ExpandCollapseState.Collapsed,pattern.Collapse,fixture.NestedRadio,late);
                await Settle();
            }
            finally
            {
                fixture.TraceMenu("before removing late radio");
                fixture.DelayedSubmenu.Items.Remove(late);
                fixture.TraceMenu("after removing late radio");
                fixture.SubmenuPattern=null;
            }
        }
        private async Task CheckNativeRadioBaseline(DesignDefinition design,ElementTheme theme,bool useDefaultPresenter=false)
        {
            var failures=useDefaultPresenter ? _defaultPresenterFailures : _nativeBaselineFailures;
            var results=useDefaultPresenter ? _defaultPresenterResults : _nativeBaselineResults;
            var label=useDefaultPresenter ? "default presenter comparison" : "native baseline";
            var failuresBefore=failures.Count;
            try
            {
                _nativeBaseline=new Fixture("Unregistered "+label+" — "+design.Id,_menuTimeline.Add,registerWithLiveDesign:false,useDefaultPresenter:useDefaultPresenter);
                var fixture=_nativeBaseline;
                fixture.Root.RequestedTheme=theme;
                fixture.Open();await Settle();
                LiveDesign.RecordDiagnosticWindow(fixture.Window,fixture.Root);
                await BringFixtureTargetIntoView(fixture,fixture.Dropdown);
                await OpenMenu(fixture,label+" first opening");await Settle();
                CheckPresenterConfiguration(fixture,"first top-level opening",includeNested:false);
                var lateTopItem=new MenuFlyoutItem { Text="Late menu item" };
                using(var observation=fixture.ObserveLateMenuTypography(lateTopItem))
                {
                    try
                    {
                        observation.Capture("before Add");
                        var lateBeforeAdd=LiveDesign.CaptureDiagnosticGcSnapshot();
                        fixture.Menu.Items.Add(lateTopItem);
                        var lateAfterAdd=LiveDesign.CaptureDiagnosticGcSnapshot();
                        observation.Capture("after Add");
                        await Settle();
                        var lateAfterSettle=LiveDesign.CaptureDiagnosticGcSnapshot();
                        observation.Capture("after original 100ms settle, comparison has no typography assertion",
                            runtime:new Fixture.MenuTypographyRuntimeSamples(lateBeforeAdd,lateAfterAdd,lateAfterSettle));
                    }
                    finally
                    {
                        observation.Capture("before Remove");
                        fixture.Menu.Items.Remove(lateTopItem);
                        observation.Capture("after Remove");
                    }
                }
                await CloseMenu(fixture,label+" first close");await Settle();
                await OpenMenu(fixture,label+" second opening");await Settle();
                CheckPresenterConfiguration(fixture,"second top-level opening",includeNested:false);
                await CheckNestedMenuRealization(fixture,design);
                await CloseMenu(fixture,label+" after nested checks");
            }
            catch(Exception error) { failures.Add($"{design.Id}: {theme} {label} exception: {error}"); }
            finally
            {
                _nativeBaseline?.Window.Close();_nativeBaseline=null;
                await Settle();
                results.Add(new { Design=design.Id,Theme=theme.ToString(),RegisteredWithLiveDesign=false,
                    UsesDefaultPresenter=useDefaultPresenter,SharedAppResourcesAndTemplates=true,
                    Passed=failures.Count==failuresBefore,FailureCount=failures.Count-failuresBefore });
            }
        }
        private void CheckPresenterConfiguration(Fixture fixture,string stage,bool includeNested)
        {
            var appTemplate=Application.Current.Resources["KotuMenuFlyoutPresenterTemplate"] as ControlTemplate;
            Observe("top",fixture.DelayedSubmenu);
            if (includeNested) Observe("nested",fixture.NestedRadio);
            void Observe(string location,FrameworkElement item)
            {
                MenuFlyoutPresenter? presenter=null;
                for(var parent=VisualTreeHelper.GetParent(item);parent is not null;parent=VisualTreeHelper.GetParent(parent))
                    if (parent is MenuFlyoutPresenter found) { presenter=found;break; }
                var visualChildCount=presenter is null ? 0 : VisualTreeHelper.GetChildrenCount(presenter);
                var root=visualChildCount>0 ? VisualTreeHelper.GetChild(presenter!,0) : null;
                var border=root as Border;
                var scroll=border?.Child as ScrollViewer;
                var items=scroll?.Content as ItemsPresenter;
                var explicitStyle=fixture.DefaultPresenterStyle;
                var styleMatches=SameNative(presenter?.Style,explicitStyle);
                var templateDiffers=presenter?.Template is not null && NativeIdentity(presenter.Template)!=0 &&
                    appTemplate is not null && NativeIdentity(appTemplate)!=0 && !SameNative(presenter.Template,appTemplate);
                var nativeStructure=visualChildCount==1 && border is not null &&
                    scroll is { Name:"MenuFlyoutPresenterScrollViewer" } && items is not null;
                var configurationPassed=!fixture.RegisteredWithLiveDesign && explicitStyle is not null &&
                    explicitStyle.BasedOn is null && explicitStyle.Setters.Count==0 &&
                    SameNative(fixture.Menu.MenuFlyoutPresenterStyle,explicitStyle) && styleMatches && templateDiffers && nativeStructure;
                _presenterObservations.Add(new { Fixture=fixture.Id,Design=DesignRuntime.Current.Id,Theme=fixture.Root.RequestedTheme.ToString(),
                    fixture.RegisteredWithLiveDesign,fixture.UsesDefaultPresenter,Stage=stage,Location=location,
                    Presenter=ObjectSnapshot(presenter),Style=ObjectSnapshot(presenter?.Style),Template=ObjectSnapshot(presenter?.Template),
                    ItemsPanelRoot=ObjectSnapshot(presenter?.ItemsPanelRoot),ExplicitStyle=ObjectSnapshot(explicitStyle),
                    MenuPresenterStyle=ObjectSnapshot(fixture.Menu.MenuFlyoutPresenterStyle),AppTemplate=ObjectSnapshot(appTemplate),
                    ExplicitStyleBasedOn=ObjectSnapshot(explicitStyle?.BasedOn),ExplicitStyleSetterCount=explicitStyle?.Setters.Count,
                    StyleMatchesExplicit=styleMatches,TemplateDiffersFromApp=templateDiffers,VisualChildCount=visualChildCount,
                    FirstVisualRoot=ObjectSnapshot(root),BorderChild=ObjectSnapshot(border?.Child),ScrollContent=ObjectSnapshot(scroll?.Content),
                    NativeThreeNodeStructure=nativeStructure,ConfigurationPassed=fixture.UsesDefaultPresenter ? configurationPassed : (bool?)null,
                    InitialRadio=Fixture.RadioSnapshot(fixture.NestedRadio) });
                if (fixture.UsesDefaultPresenter)
                    CheckFixture(fixture,configurationPassed,$"{DesignRuntime.Current.Id}: {fixture.Root.RequestedTheme} default presenter configuration {stage} {location} styleMatches={styleMatches} templateDiffers={templateDiffers} nativeStructure={nativeStructure} root={root?.GetType().Name}:{(root as FrameworkElement)?.Name}");
            }
        }
        private void CheckRadioFixture(Fixture fixture,RadioMenuFlyoutItem item,DesignDefinition design,string stage)
        {
            if (fixture.RegisteredWithLiveDesign) { CheckNestedRadio(fixture,item,design,stage);return; }
            var original=Descendants(item).OfType<FrameworkElement>().FirstOrDefault(element=>element.Name=="CheckGlyph");
            var label=Descendants(item).OfType<TextBlock>().FirstOrDefault(element=>element.Name=="TextBlock");
            fixture.TraceRadioVisual(stage,item,original);
            CheckFixture(fixture,item.IsLoaded && item.Template is not null && original is not null && label is not null,
                $"{design.Id}: {fixture.Root.RequestedTheme} native baseline {stage} loaded={item.IsLoaded} checked={item.IsChecked} template={item.Template is not null} check={original is not null} clip={original?.Clip is not null} label={label is not null}");
        }
        private void CheckNestedRadio(Fixture fixture,RadioMenuFlyoutItem item,DesignDefinition design,string stage)
        {
            var original=Descendants(item).OfType<FrameworkElement>().FirstOrDefault(element=>element.Name=="CheckGlyph");
            var label=Descendants(item).OfType<TextBlock>().FirstOrDefault(element=>element.Name=="TextBlock");
            fixture.TraceRadioVisual(stage,item,original);
            var detail=FormattableString.Invariant($"{design.Id}: {stage} group={item.GroupName} checked={item.IsChecked} loaded={item.IsLoaded} template={item.Template is not null} check={original is not null} clip={original?.Clip is not null} font={item.FontFamily.Source} size={item.FontSize:G17} weight={item.FontWeight.Weight}");
            Check(item.IsLoaded && item.Template is not null && original?.Clip is not null && label is not null,detail+" native template and design check");
            Check(item.FontFamily.Source==design.FontFamily && item.FontWeight.Weight==design.FontWeight && Math.Abs(item.FontSize-DesignScope.FontSize(14,design))<.01,
                detail+" typography");
            Check(label is not null && Math.Abs(label.FontSize-item.FontSize)<.01,detail+" label size once");
        }
        private static Task Settle() => Task.Delay(100);
        private static Task ForceCollection()=>Task.Run(()=>
        {
            GC.Collect();GC.WaitForPendingFinalizers();GC.Collect();
        });
        private static async Task BringFixtureTargetIntoView(Fixture fixture,FrameworkElement target)
        {
            fixture.Window.Activate();
            var origin=target.TransformToVisual(fixture.Root).TransformPoint(new Windows.Foundation.Point(0,0));
            fixture.Viewport.ChangeView(Math.Max(0,origin.X-12),Math.Max(0,origin.Y-12),null,true);
            await Settle();
        }
        private async Task CheckHit(Fixture fixture,FrameworkElement target,Windows.Foundation.Point local,string description)
        {
            await BringFixtureTargetIntoView(fixture,target);
            var root=fixture.Root;
            var point=target.TransformToVisual(root).TransformPoint(local);
            var hostPoint=target.TransformToVisual(null).TransformPoint(local);
            var bounds=target.TransformToVisual(null).TransformBounds(new Windows.Foundation.Rect(0,0,target.ActualWidth,target.ActualHeight));
            var hits=VisualTreeHelper.FindElementsInHostCoordinates(hostPoint,root).ToArray();
            var found=hits.Any(hit=>
            {
                for(DependencyObject? node=hit;node is not null;node=VisualTreeHelper.GetParent(node))
                    if (ReferenceEquals(node,target)) return true;
                return false;
            });
            var hitNames=string.Join(";",hits.Take(8).Select(hit=>hit is FrameworkElement element ? element.GetType().Name+":"+element.Name : hit.GetType().Name));
            var inClient=hostPoint.X>=0 && hostPoint.Y>=0 && hostPoint.X<root.XamlRoot.Size.Width && hostPoint.Y<root.XamlRoot.Size.Height;
            Check(found && inClient,FormattableString.Invariant($"{description} target={target.GetType().Name}:{target.Name} loaded={target.IsLoaded} hitVisible={target.IsHitTestVisible} visibility={target.Visibility} rootPoint=({point.X:F3},{point.Y:F3}) hostPoint=({hostPoint.X:F3},{hostPoint.Y:F3}) bounds=({bounds.X:F3},{bounds.Y:F3},{bounds.Width:F3},{bounds.Height:F3}) client=({root.XamlRoot.Size.Width:F3},{root.XamlRoot.Size.Height:F3}) scale={root.XamlRoot.RasterizationScale:F3} windowVisible={fixture.Window.AppWindow.IsVisible} inClient={inClient} hits={hitNames}"));
        }
    }

    private sealed class Fixture
    {
        private static int s_nextFixtureId;
        private readonly int _fixtureId=Interlocked.Increment(ref s_nextFixtureId);
        private readonly Action<object> _recordMenu;
        private readonly string _title;
        private readonly HashSet<RadioObservation> _radioObservations=[];
        private int _menuSequence;
        private RadioMenuFlyoutItem? _lateRadio;
        internal string MenuStage { get; set; }="construction";
        internal string MenuPhase { get; private set; }="Closed";
        internal int MenuOpenedCount { get; private set; }
        internal int MenuClosedCount { get; private set; }
        internal string ComboPhase { get; private set; }="Closed";
        internal int ComboOpenedCount { get; private set; }
        internal int ComboClosedCount { get; private set; }
        internal bool RegisteredWithLiveDesign { get; }
        internal bool UsesDefaultPresenter=>DefaultPresenterStyle is not null;
        internal Style? DefaultPresenterStyle { get; }
        internal int Id=>_fixtureId;
        internal bool IsClosed { get; private set; }
        internal Microsoft.UI.Xaml.Automation.Provider.IExpandCollapseProvider? SubmenuPattern { get; set; }
        internal event Action? MenuStateChanged;
        internal string NestedRadioGroup=>"HarnessNestedDevices_"+_fixtureId;
        internal Window Window { get; }
        internal Grid Root { get; } = new() { Name="RootLayout", MinWidth=828,Padding=new Thickness(12), RowSpacing=8 };
        internal ScrollViewer Viewport { get; }=new() { HorizontalScrollMode=ScrollMode.Enabled,HorizontalScrollBarVisibility=ScrollBarVisibility.Auto,
            VerticalScrollMode=ScrollMode.Enabled,VerticalScrollBarVisibility=ScrollBarVisibility.Auto,ZoomMode=ZoomMode.Disabled };
        internal Grid Bar { get; } = new() { Name="BottomBar", Height=44, ColumnSpacing=6 };
        internal Button Button { get; } = new() { Width=32, Height=32, Padding=new Thickness(2) };
        internal FontIcon Icon18 { get; } = Icon(18);
        internal FontIcon Icon28 { get; } = Icon(28);
        internal FontIcon Icon30 { get; } = Icon(30);
        internal TextBlock ContentText { get; } = new() { Text="Protected content 123 가나다",FontFamily=new FontFamily("Consolas"),FontSize=17 };
        internal TextBlock PreviewText { get; } = new() { Text="Explicit specimen",FontFamily=new FontFamily("Georgia"),FontSize=19 };
        internal ListView List { get; } = new() { Height=160,SelectionMode=ListViewSelectionMode.Single };
        internal GridView Tiles { get; } = new() { Height=160,SelectionMode=ListViewSelectionMode.Single,IsMultiSelectCheckBoxEnabled=true };
        internal ListView PlainList { get; } = new() { Height=160,SelectionMode=ListViewSelectionMode.Single };
        internal Slider Slider { get; } = new() { Minimum=0,Maximum=100,Value=37,Width=280 };
        internal ComboBox Combo { get; } = new() { Width=120 };
        internal ComboBoxItem EmphasizedComboItem { get; } = new() { Content="Explicit item",FontWeight=new Windows.UI.Text.FontWeight { Weight=600 },FontSize=20 };
        internal ComboBoxItem StyledComboItem { get; } = new() { Content="Styled item" };
        internal CheckBox[] Checks { get; } = [new() { Content="Off",IsChecked=false },new() { Content="On",IsChecked=true },new() { Content="Mixed",IsThreeState=true,IsChecked=null },new() { Content="Disabled on",IsChecked=true,IsEnabled=false },new() { Content=null,IsChecked=false }];
        internal ToggleSwitch[] Switches { get; } = [new() { IsOn=false },new() { IsOn=true },new() { IsOn=true,IsEnabled=false }];
        internal ProgressBar Progress { get; } = new() { Width=150,Value=37 };
        internal ProgressBar PausedProgress { get; } = new() { Width=150,Value=65,ShowPaused=true };
        internal ProgressBar ErrorProgress { get; } = new() { Width=150,Value=80,ShowError=true };
        internal ProgressBar BusyProgress { get; } = new() { Width=150,IsIndeterminate=true };
        internal ProgressRing BusyRing { get; } = new() { Width=20,Height=20,IsActive=true };
        internal TextBlock ScaleLabel { get; } = new() { Text="Scale sample",FontSize=12,VerticalAlignment=VerticalAlignment.Center };
        internal TextBox Input { get; } = new() { Text="Editable sample 가나다",Width=200 };
        internal TextBox BlankInput { get; } = new() { Width=160 };
        internal Grid ResizeHost { get; } = new() { Width=420,Height=30,HorizontalAlignment=HorizontalAlignment.Left };
        internal Border Backdrop { get; } = new() { Name="PanelBackdrop",Margin=new Thickness(3),Opacity=.37 };
        internal Border Selection { get; } = new() { Name="TileSelectionOutline",Margin=new Thickness(3),IsHitTestVisible=false };
        internal Border PaddedCard { get; } = new() { Width=210,Height=70,Padding=new Thickness(16),BorderThickness=new Thickness(2,3,4,5),Margin=new Thickness(7),Opacity=.6 };
        internal SolidColorBrush SemanticBrush { get; } = new(Microsoft.UI.Colors.White);
        internal TextBlock SemanticLabel { get; } = new() { Text="Artwork overlay" };
        internal TextBlock NeutralLabel { get; } = new() { Text="EXT",Foreground=new SolidColorBrush(Microsoft.UI.Colors.White) };
        internal TextBlock WeightSource { get; } = new() { FontWeight=new Windows.UI.Text.FontWeight { Weight=600 } };
        internal TextBlock WeightLabel { get; } = new() { Text="Bound current item",FontSize=12 };
        internal Button WeightButton { get; } = new() { Content="Bound weight" };
        internal TextBlock DirectWeightLabel { get; } = new() { Text="Authored label",FontSize=12 };
        internal Button DirectWeightButton { get; } = new() { Content="Authored button" };
        internal StackPanel WeightHost { get; } = new() { Orientation=Orientation.Horizontal,Spacing=8 };
        internal StackPanel GalleryBody { get; } = new() { Orientation=Orientation.Horizontal,Spacing=8 };
        internal TextBlock GalleryCaption { get; } = new() { Text="Gallery caption",FontSize=12 };
        internal TextBlock GalleryTitle { get; } = new() { Text="Explicit title",FontSize=12,FontWeight=new Windows.UI.Text.FontWeight { Weight=600 } };
        internal TextBlock StyledGalleryTitle { get; } = new() { Text="BasedOn title",FontSize=12 };
        internal TextBlock ImplicitGalleryTitle { get; } = new() { Text="Implicit title",FontSize=12 };
        internal Button NativeNormalButton { get; } = new() { Content="Native Normal" };
        internal DropDownButton Dropdown { get; } = new() { Content="Menu" };
        internal MenuFlyout Menu { get; } = new();
        internal MenuFlyoutSubItem DelayedSubmenu { get; }=new() { Text="Delayed devices" };
        internal RadioMenuFlyoutItem NestedRadio { get; }=new() { Text="Initial nested device" };
        internal MenuFlyoutItem EmphasizedMenuItem { get; } = new() { Text="Explicit emphasis",FontWeight=new Windows.UI.Text.FontWeight { Weight=600 },FontSize=20 };
        internal Button Choice { get; } = new() { Content="Pending choice",BorderThickness=new Thickness(3) };
        internal SolidColorBrush ChoiceMarker { get; } = new(Microsoft.UI.Colors.Magenta);
        internal Fixture(string title,Action<object> recordMenu,bool registerWithLiveDesign=true,bool useDefaultPresenter=false)
        {
            _title=title;_recordMenu=recordMenu;
            RegisteredWithLiveDesign=registerWithLiveDesign;
            if (useDefaultPresenter)
            {
                // An explicit empty Style must select the native fallback itself. Do not
                // assign Template/BasedOn or repair a failed configuration while running.
                DefaultPresenterStyle=new Style(typeof(MenuFlyoutPresenter));
                Menu.MenuFlyoutPresenterStyle=DefaultPresenterStyle;
            }
            NestedRadio.GroupName=NestedRadioGroup;
            NestedRadio.IsChecked=true;
            Viewport.Content=Root;
            Window=new Window { Title="KOTU A427 validation — "+title,Content=Viewport };
            Window.AppWindow.Resize(new Windows.Graphics.SizeInt32(850,760));
            for(var i=0;i<12;i++) Root.RowDefinitions.Add(new RowDefinition { Height=GridLength.Auto });
            var checks = new StackPanel { Orientation=Orientation.Horizontal,Spacing=12 };
            foreach(var check in Checks) checks.Children.Add(check);
            Put(checks,0);
            var selectors=new Grid { ColumnSpacing=8 };
            selectors.ColumnDefinitions.Add(new ColumnDefinition());
            selectors.ColumnDefinitions.Add(new ColumnDefinition { Width=new GridLength(160) });
            selectors.ColumnDefinitions.Add(new ColumnDefinition { Width=new GridLength(140) });
            selectors.Children.Add(List);Grid.SetColumn(Tiles,1);selectors.Children.Add(Tiles);Grid.SetColumn(PlainList,2);selectors.Children.Add(PlainList);Put(selectors,1);
            var tools = new StackPanel { Orientation=Orientation.Horizontal,Spacing=12 };
            var itemStyle=new Style(typeof(ComboBoxItem));
            itemStyle.Setters.Add(new Setter(Control.FontWeightProperty,new Windows.UI.Text.FontWeight { Weight=600 }));
            itemStyle.Setters.Add(new Setter(Control.FontSizeProperty,16d));
            StyledComboItem.Style=new Style(typeof(ComboBoxItem)) { BasedOn=itemStyle };
            Combo.Items.Add("1.0×");Combo.Items.Add("1.5×");Combo.Items.Add(EmphasizedComboItem);Combo.Items.Add(StyledComboItem);Combo.SelectedIndex=0;
            tools.Children.Add(Slider);tools.Children.Add(Combo);tools.Children.Add(Dropdown);Put(tools,2);
            DesignScope.ProtectContent(ContentText);Put(ContentText,3);
            var preview = new Grid { Tag=DesignRuntime.PreviewScopeMarker };preview.Children.Add(PreviewText);Put(preview,4);
            Choice.BorderBrush=ChoiceMarker;DesignScope.SetRole(Choice,DesignElementRole.PreviewChoice);Put(Choice,5);
            Button.Content=Icon18;Bar.Children.Add(Button);
            Bar.ColumnDefinitions.Add(new ColumnDefinition { Width=GridLength.Auto });
            Bar.ColumnDefinitions.Add(new ColumnDefinition { Width=GridLength.Auto });
            Bar.ColumnDefinitions.Add(new ColumnDefinition { Width=GridLength.Auto });
            Grid.SetColumn(Icon28,1);Grid.SetColumn(Icon30,2);Bar.Children.Add(Icon28);Bar.Children.Add(Icon30);Put(Bar,7);
            var native=new StackPanel { Spacing=8 };
            var switches=new StackPanel { Orientation=Orientation.Horizontal,Spacing=12 };
            foreach(var toggle in Switches) switches.Children.Add(toggle);
            switches.Children.Add(BusyRing);switches.Children.Add(ScaleLabel);native.Children.Add(switches);
            var progress=new StackPanel { Orientation=Orientation.Horizontal,Spacing=12 };
            foreach(var bar in new[] { Progress,PausedProgress,ErrorProgress,BusyProgress }) progress.Children.Add(bar);
            native.Children.Add(progress);Put(native,6);
            ResizeHost.Children.Add(Backdrop); ResizeHost.Children.Add(Selection);
            var probes=new StackPanel { Orientation=Orientation.Horizontal,Spacing=12 };
            probes.Children.Add(ResizeHost);probes.Children.Add(Input);probes.Children.Add(BlankInput);Put(probes,8);
            var cardContent=new Grid { Margin=new Thickness(3,2,5,4),Padding=new Thickness(2),BorderThickness=new Thickness(1) };
            SemanticLabel.Foreground=SemanticBrush;
            var artworkBand=new Border { Background=new SolidColorBrush(Windows.UI.Color.FromArgb(176,0,0,0)),Child=SemanticLabel };
            cardContent.Children.Add(artworkBand);PaddedCard.Child=cardContent;
            DesignScope.SetRole(PaddedCard,DesignElementRole.Card);
            DesignScope.SetRole(NeutralLabel,DesignElementRole.NeutralText);
            var cards=new StackPanel { Orientation=Orientation.Horizontal };cards.Children.Add(PaddedCard);cards.Children.Add(NeutralLabel);Put(cards,9);
            foreach(var target in new FrameworkElement[] { WeightLabel,WeightButton })
                target.SetBinding(LiveDesign.AuthoredFontWeightProperty,new Binding { Source=WeightSource,Path=new PropertyPath("FontWeight"),Mode=BindingMode.OneWay });
            WeightHost.Children.Add(WeightLabel);WeightHost.Children.Add(WeightButton);WeightHost.Children.Add(DirectWeightLabel);WeightHost.Children.Add(DirectWeightButton);Put(WeightHost,10);
            var weightStyle=new Style(typeof(TextBlock));
            weightStyle.Setters.Add(new Setter(TextBlock.FontWeightProperty,new Windows.UI.Text.FontWeight { Weight=600 }));
            StyledGalleryTitle.Style=new Style(typeof(TextBlock)) { BasedOn=weightStyle };
            var implicitHost=new StackPanel();implicitHost.Resources[typeof(TextBlock)]=weightStyle;implicitHost.Children.Add(ImplicitGalleryTitle);
            GalleryBody.Children.Add(GalleryTitle);GalleryBody.Children.Add(GalleryCaption);GalleryBody.Children.Add(StyledGalleryTitle);GalleryBody.Children.Add(implicitHost);
            GalleryBody.Children.Add(new Button { Content=NativeNormalButton,FontWeight=new Windows.UI.Text.FontWeight { Weight=600 },FontSize=20 });
            Put(new Button { Content=GalleryBody },11);
            List.ItemsSource=Enumerable.Range(0,500).Select(i=>"Virtual row "+i).ToArray();List.SelectedIndex=4;
            List.ItemTemplate=(DataTemplate)XamlReader.Load("""
                <DataTemplate xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation">
                  <Grid Name="HarnessRow"><Grid Name="HarnessCutContent"><TextBlock Text="{Binding}" /></Grid></Grid>
                </DataTemplate>
                """);
            Tiles.ItemsSource=new[] { "Tile zero","Tile one" };Tiles.SelectedIndex=1;
            Tiles.ItemTemplate=(DataTemplate)XamlReader.Load("""
                <DataTemplate xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation">
                  <Grid Name="HarnessTile" Width="110" Height="44" Padding="7,5"><TextBlock Text="{Binding}" /></Grid>
                </DataTemplate>
                """);
            PlainList.ItemsSource=new[] { "Plain zero","Plain one" };PlainList.SelectedIndex=1;
            Menu.Items.Add(new MenuFlyoutItem { Text="Action",Icon=Icon(16) });
            Menu.Items.Add(EmphasizedMenuItem);
            Menu.Items.Add(new ToggleMenuFlyoutItem { Text="Checked",IsChecked=true });
            Menu.Items.Add(new ToggleMenuFlyoutItem { Text="Unchecked",IsChecked=false });
            Menu.Items.Add(new RadioMenuFlyoutItem { Text="Radio checked",GroupName="HarnessTopLevel_"+_fixtureId,IsChecked=true });
            Menu.Items.Add(new MenuFlyoutItem { Text="Disabled",IsEnabled=false });Dropdown.Flyout=Menu;
            foreach(var titleText in new[] { "Audio input","Audio output" })
            {
                var subMenu=new MenuFlyoutSubItem { Text=titleText };
                subMenu.Items.Add(new ToggleMenuFlyoutItem { Text="Default device",IsChecked=true });
                Menu.Items.Add(subMenu);
            }
            DelayedSubmenu.Items.Add(NestedRadio);Menu.Items.Add(DelayedSubmenu);
            Menu.Opening+=OnMenuOpening;
            Menu.Opened+=OnMenuOpened;
            Menu.Closing+=OnMenuClosing;
            Menu.Closed+=OnMenuClosed;
            Combo.DropDownOpened+=OnComboOpened;
            Combo.DropDownClosed+=OnComboClosed;
            Window.Closed+=OnWindowClosed;
            LiveDesign.ObserveLifecycle(NestedRadio,entry=>TraceLifecycle("initial",entry));
            _radioObservations.Add(new RadioObservation(this,NestedRadio,"initial"));
            if (registerWithLiveDesign) LiveDesign.AttachWindow(Window,Root);
        }
        private void OnMenuOpening(object? sender,object args) { MenuPhase="Opening";MenuChanged("menu Opening"); }
        private void OnMenuOpened(object? sender,object args) { MenuPhase="Opened";MenuOpenedCount++;MenuChanged("menu Opened"); }
        private void OnMenuClosing(object? sender,FlyoutBaseClosingEventArgs args) { MenuPhase="Closing";MenuChanged("menu Closing cancel="+args.Cancel); }
        private void OnMenuClosed(object? sender,object args) { MenuPhase="Closed";MenuClosedCount++;MenuChanged("menu Closed"); }
        private void OnComboOpened(object? sender,object args) { ComboPhase="Opened";ComboOpenedCount++;MenuChanged("combo DropDownOpened"); }
        private void OnComboClosed(object? sender,object args) { ComboPhase="Closed";ComboClosedCount++;MenuChanged("combo DropDownClosed"); }
        private void OnWindowClosed(object sender,WindowEventArgs args)
        {
            IsClosed=true;
            try { MenuChanged("window Closed"); }
            finally
            {
                Menu.Opening-=OnMenuOpening;Menu.Opened-=OnMenuOpened;Menu.Closing-=OnMenuClosing;Menu.Closed-=OnMenuClosed;
                Combo.DropDownOpened-=OnComboOpened;Combo.DropDownClosed-=OnComboClosed;
                Window.Closed-=OnWindowClosed;
                foreach(var observation in _radioObservations.ToArray()) observation.Dispose();
            }
        }
        private void MenuChanged(string action,object? routedEvent=null) { TraceMenu(action,routedEvent);MenuStateChanged?.Invoke(); }
        internal void TraceMenu(string action,object? routedEvent=null,object? readiness=null,object? radioVisual=null)
        {
            string expanded;
            try { expanded=SubmenuPattern?.ExpandCollapseState.ToString() ?? "unobserved"; }
            catch(Exception error) { expanded="unavailable: "+error.GetType().Name; }
            _recordMenu(new { Fixture=_fixtureId,Window=_title,Sequence=++_menuSequence,Utc=DateTime.UtcNow,
                RegisteredWithLiveDesign,UsesDefaultPresenter,RoutedEvent=routedEvent,Readiness=readiness,RadioVisual=radioVisual,
                Design=DesignRuntime.Current.Id,Theme=Root.RequestedTheme.ToString(),ActualTheme=Root.ActualTheme.ToString(),Stage=MenuStage,Action=action,
                MenuOpen=Menu.IsOpen,Phase=MenuPhase,Opened=MenuOpenedCount,Closed=MenuClosedCount,WindowClosed=IsClosed,
                ComboOpen=Combo.IsDropDownOpen,ComboPhase,ComboOpened=ComboOpenedCount,ComboClosed=ComboClosedCount,
                ComboItems=Enumerable.Range(0,Combo.Items.Count).Select(index=>
                {
                    var container=Combo.ContainerFromIndex(index) as ComboBoxItem;
                    return new { Index=index,HasContainer=container is not null,Loaded=container?.IsLoaded,FontSize=container?.FontSize,Weight=container?.FontWeight.Weight };
                }).ToArray(),
                Expanded=expanded,TopItems=Menu.Items.Select(item=>new { Type=item.GetType().Name,
                    Text=item is MenuFlyoutItem menuItem ? menuItem.Text : item is MenuFlyoutSubItem subItem ? subItem.Text : "",Loaded=item.IsLoaded }).ToArray(),
                Initial=RadioSnapshot(NestedRadio),Late=_lateRadio is { } late ? RadioSnapshot(late) : null });
        }
        internal static object RadioSnapshot(RadioMenuFlyoutItem item)
        {
            string internalChecked;
            try { internalChecked=item.GetValue(ToggleMenuFlyoutItem.IsCheckedProperty)?.ToString() ?? "null"; }
            catch(Exception error) { internalChecked="unavailable: "+error.GetType().Name; }
            return new { item.Text,item.GroupName,Loaded=item.IsLoaded,Checked=item.IsChecked,InternalChecked=internalChecked,
                Element=ObjectSnapshot(item),Style=ObjectSnapshot(item.Style),Template=ObjectSnapshot(item.Template) };
        }
        internal void TraceRadioVisual(string stage,RadioMenuFlyoutItem item,FrameworkElement? check)=>
            TraceMenu("radio visual check "+stage,radioVisual:new { Element=ObjectSnapshot(item),
                CheckGlyph=ObjectSnapshot(check),CheckGlyphLoaded=check?.IsLoaded,Clip=ObjectSnapshot(check?.Clip),ClipPresent=check?.Clip is not null });
        private void TraceLifecycle(string label,object entry)=>_recordMenu(new {
            Fixture=_fixtureId,Window=_title,Sequence=++_menuSequence,Utc=DateTime.UtcNow,
            RegisteredWithLiveDesign,UsesDefaultPresenter,Design=DesignRuntime.Current.Id,Theme=Root.RequestedTheme.ToString(),
            Stage=MenuStage,Action="live design lifecycle",Radio=label,MenuOpen=Menu.IsOpen,Phase=MenuPhase,
            Opened=MenuOpenedCount,Closed=MenuClosedCount,WindowClosed=IsClosed,Lifecycle=entry });
        internal void TraceReadinessWork(string stage,object work)=>_recordMenu(new {
            Fixture=_fixtureId,Window=_title,Sequence=++_menuSequence,Utc=DateTime.UtcNow,
            RegisteredWithLiveDesign,UsesDefaultPresenter,Design=DesignRuntime.Current.Id,Theme=Root.RequestedTheme.ToString(),
            Stage=MenuStage,Action="readiness timing "+stage,Work=work });
        internal void TraceAssertion(string stage,object detail,long? checkedTimestamp=null)=>_recordMenu(new {
            Fixture=_fixtureId,Window=_title,Sequence=++_menuSequence,Utc=DateTime.UtcNow,
            Timestamp=System.Diagnostics.Stopwatch.GetTimestamp(),RegisteredWithLiveDesign,UsesDefaultPresenter,
            Design=DesignRuntime.Current.Id,Theme=Root.RequestedTheme.ToString(),Stage=stage,
            Action="original assertion observation",CheckedTimestamp=checkedTimestamp,Detail=detail,
            Scope="Original predicate/arrays retained. Object connection and direct host details are read after the original checks without extra descendant walks or event subscriptions." });
        internal readonly record struct MenuTypographyRuntimeSamples(LiveDesign.DiagnosticGcSnapshot BeforeAdd,
            LiveDesign.DiagnosticGcSnapshot AfterAdd,LiveDesign.DiagnosticGcSnapshot AfterOriginal100msSettle);
        internal MenuTypographyObservation ObserveLateMenuTypography(MenuFlyoutItem item)=>new(this,item);
        internal sealed class MenuTypographyObservation : IDisposable
        {
            private readonly Fixture _fixture;
            private readonly MenuFlyoutItem _item;
            private bool _disposed;
            internal MenuTypographyObservation(Fixture fixture,MenuFlyoutItem item)
            {
                _fixture=fixture;_item=item;
                item.Loaded+=OnLoaded;item.Unloaded+=OnUnloaded;
                LiveDesign.ObserveLifecycle(item,entry=>Capture("live design lifecycle",entry));
            }
            private void OnLoaded(object sender,RoutedEventArgs args)=>Capture("Loaded");
            private void OnUnloaded(object sender,RoutedEventArgs args)=>Capture("Unloaded");
            internal void Capture(string stage,object? lifecycle=null,MenuTypographyRuntimeSamples? runtime=null)
            {
                if (_disposed) return;
                // Read only this exact late object and already realized native labels.
                // Do not raise MenuStateChanged or call Track/ApplyTemplate/UpdateLayout.
                _fixture._recordMenu(new {
                    Fixture=_fixture._fixtureId,Window=_fixture._title,Sequence=++_fixture._menuSequence,Utc=DateTime.UtcNow,
                    Timestamp=System.Diagnostics.Stopwatch.GetTimestamp(),_fixture.RegisteredWithLiveDesign,_fixture.UsesDefaultPresenter,
                    Design=DesignRuntime.Current.Id,Theme=_fixture.Root.RequestedTheme.ToString(),Stage=_fixture.MenuStage,
                    Action="late menu typography "+stage,Element=LiveDesign.DiagnosticElement(_item),Lifecycle=lifecycle,Runtime=runtime,
                    NativeLabels=Descendants(_item).OfType<TextBlock>().Where(text=>text.Name is "TextBlock" or "KeyboardAcceleratorTextBlock")
                        .Select(LiveDesign.DiagnosticElement).ToArray() });
            }
            public void Dispose()
            {
                if (_disposed) return;
                _disposed=true;
                _item.Loaded-=OnLoaded;_item.Unloaded-=OnUnloaded;
                LiveDesign.StopObservingLifecycle(_item);
            }
        }
        internal IDisposable ObserveLateRadio(RadioMenuFlyoutItem item)
        {
            _lateRadio=item;
            LiveDesign.ObserveLifecycle(item,entry=>TraceLifecycle("late",entry));
            var observation=new RadioObservation(this,item,"late");
            _radioObservations.Add(observation);
            return observation;
        }
        private sealed class RadioObservation : IDisposable
        {
            private readonly Fixture _fixture;
            private readonly RadioMenuFlyoutItem _item;
            private readonly string _label;
            private long? _checkedToken,_internalToken;
            private bool _disposed;
            internal RadioObservation(Fixture fixture,RadioMenuFlyoutItem item,string label)
            {
                _fixture=fixture;_item=item;_label=label;
                try
                {
                    item.Loaded+=OnLoaded;item.Unloaded+=OnUnloaded;
                    _checkedToken=item.RegisterPropertyChangedCallback(RadioMenuFlyoutItem.IsCheckedProperty,(_,_)=>Changed("public checked changed"));
                    try
                    {
                        // Radio's public API hides its native Toggle base. Read/observe only;
                        // never use the internal property to repair or initialize checked state.
                        _=item.GetValue(ToggleMenuFlyoutItem.IsCheckedProperty);
                        _internalToken=item.RegisterPropertyChangedCallback(ToggleMenuFlyoutItem.IsCheckedProperty,(_,_)=>Changed("internal checked changed"));
                    }
                    catch(Exception error) { Changed("internal checked observation unavailable: "+error.GetType().Name); }
                }
                catch { Dispose();throw; }
            }
            private void OnLoaded(object sender,RoutedEventArgs args)=>RoutedChanged("Loaded",sender,args);
            private void OnUnloaded(object sender,RoutedEventArgs args)=>RoutedChanged("Unloaded",sender,args);
            private void RoutedChanged(string change,object sender,RoutedEventArgs args)=>_fixture.MenuChanged(_label+" radio "+change,
                new { Observed=ObjectSnapshot(_item),Sender=ObjectSnapshot(sender),OriginalSource=ObjectSnapshot(args.OriginalSource),
                    SenderIsObserved=SameNative(sender,_item),OriginalSourceIsObserved=SameNative(args.OriginalSource,_item) });
            private void Changed(string change)=>_fixture.MenuChanged(_label+" radio "+change);
            public void Dispose()
            {
                if (_disposed) return;
                _disposed=true;
                _item.Loaded-=OnLoaded;_item.Unloaded-=OnUnloaded;
                _fixture._radioObservations.Remove(this);
                if (ReferenceEquals(_fixture._lateRadio,_item)) _fixture._lateRadio=null;
                try
                {
                    if (_checkedToken is { } checkedToken) _item.UnregisterPropertyChangedCallback(RadioMenuFlyoutItem.IsCheckedProperty,checkedToken);
                }
                finally
                {
                    if (_internalToken is { } internalToken) _item.UnregisterPropertyChangedCallback(ToggleMenuFlyoutItem.IsCheckedProperty,internalToken);
                }
            }
        }
        internal void Open()=>Window.Activate();
        private void Put(FrameworkElement element,int row) { Grid.SetRow(element,row);Root.Children.Add(element); }
        private static FontIcon Icon(double size)=>new() { Glyph=DesignIconCatalog.GlyphFor("Folder").ToString(),FontSize=size,HorizontalAlignment=HorizontalAlignment.Center,VerticalAlignment=VerticalAlignment.Center };
    }

    private static nint NativeIdentity(object? value)=>value is WinRT.IWinRTObject native ? native.NativeObject.ThisPtr : 0;
    private static bool SameNative(object? left,object? right)=>NativeIdentity(left)!=0 && NativeIdentity(left)==NativeIdentity(right);
    private static object ObjectSnapshot(object? value)=>new { Type=value?.GetType().FullName,Name=(value as FrameworkElement)?.Name,
        NativeIdentity=$"0x{NativeIdentity(value):X}" };

    private static IEnumerable<DependencyObject> Descendants(DependencyObject root)
    {
        for(var i=0;i<VisualTreeHelper.GetChildrenCount(root);i++)
        {
            var child=VisualTreeHelper.GetChild(root,i);yield return child;
            foreach(var descendant in Descendants(child)) yield return descendant;
        }
    }
}
#endif
