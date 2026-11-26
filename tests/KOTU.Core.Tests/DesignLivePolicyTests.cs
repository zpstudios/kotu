using System.Text.RegularExpressions;
using System.Xml.Linq;
using KOTU.Core.Appearance;
using Xunit;

namespace KOTU.Core.Tests;

public sealed class DesignLivePolicyTests
{
    [Fact]
    public void OriginalProfilesKeepAuthoredSizesEmphasisAndIconSlots()
    {
        foreach (var thin in new[] { false, true })
        {
            var design = DesignCatalog.Resolve("original", thin);
            foreach (var size in new[] { 10d, 12, 14, 18, 32, 38 })
            {
                Assert.Equal(size, DesignScope.FontSize(size, design));
                Assert.Equal(size, DesignScope.IconSize(size, design));
            }
            Assert.Equal(400, DesignScope.FontWeight(400, design));
            Assert.Equal(600, DesignScope.FontWeight(600, design));
            Assert.Equal(4, design.ControlRadius);
            Assert.Equal(0, design.PanelRadius);
            Assert.Equal(1, design.BorderWidth);
        }
    }

    [Theory]
    [InlineData(0x00ffffffu, 0xff202020u, 0xff202020u)]
    [InlineData(0xb3ffffffu, 0xfff3f3f3u, 0xfffbfbfbu)]
    [InlineData(0x09000000u, 0xfff3f3f3u, 0xffeaeaeau)]
    [InlineData(0x0fffffffu, 0xff202020u, 0xff2d2d2du)]
    [InlineData(0xff123456u, 0xfff3f3f3u, 0xff123456u)]
    public void NativeTransparentColorsBecomeOpaqueOnTheirOriginalSurface(uint token, uint background, uint expected)
        => Assert.Equal(expected, DesignPalette.Composite(token, background));

    [Fact]
    public void RoleMetadataSurvivesWhileExplicitManagementIsInactive()
    {
        var element=new object();
        var calls=0;
        void Observe(object requested) => calls++;
        DesignScope.ManagementRequested+=Observe;
        try
        {
            DesignScope.SetRole(element,DesignElementRole.Card);
            DesignScope.SetOriginalFontSize(element,11);
            Assert.False(DesignRuntime.MultiSkinEnabled);
            Assert.Equal(DesignElementRole.Card,DesignScope.GetRole(element));
            Assert.Equal(11,DesignScope.OriginalFontSize(element,-1));
            Assert.Equal(0,calls);
            DesignScope.RequestManagement(element);
            Assert.Equal(0,calls);
            Assert.Equal(DesignElementRole.Card,DesignScope.GetRole(element));
            Assert.Equal(11,DesignScope.OriginalFontSize(element,-1));
        }
        finally { DesignScope.ManagementRequested-=Observe; }
        DesignScope.RequestManagement(element);
        Assert.Equal(0,calls);
    }

    [Fact]
    public void CanonicalGlyphSurvivesTerminalApplicationAndExternalRecycling()
    {
        var state = new DesignGlyphState(DesignIconCatalog.GlyphFor("Folder").ToString());
        foreach(var design in DesignCatalog.All.Concat(DesignCatalog.All.Reverse()))
        {
            var rendered = state.Render(design);
            state.Observe(rendered.Glyph); // A property observer also sees our own visual update.
            Assert.Equal(DesignIconCatalog.GlyphFor("Folder").ToString(),state.Canonical);
        }
        state.Render(DesignCatalog.Resolve("terminal"));
        state.Observe(DesignIconCatalog.GlyphFor("Document").ToString());
        Assert.Equal("[T]",state.Render(DesignCatalog.Resolve("terminal")).Glyph);
        Assert.Equal(DesignIconCatalog.GlyphFor("Document").ToString(),state.Render(DesignCatalog.Default).Glyph);
        state.Observe("unknown symbol");
        Assert.False(state.Render(DesignCatalog.Resolve("terminal")).Known);
        Assert.Equal("unknown symbol",state.Render(DesignCatalog.Default).Glyph);
    }

    [Fact]
    public void PixelDrawingsUseIntegerCellsWithoutChangingTheRequestedLayoutSlot()
    {
        var pixel = DesignCatalog.Resolve("pixel");
        Assert.Equal(16,DesignScope.IconSize(18,pixel));
        Assert.Equal(32,DesignScope.IconSize(28,pixel));
        Assert.Equal(32,DesignScope.IconSize(30,pixel));
        Assert.Equal(32,DesignScope.IconSize(38,pixel));
        Assert.Equal(12,DesignScope.IconSize(12,pixel));
        foreach(var design in DesignCatalog.All.Where(d=>d.Id!="pixel")) Assert.Equal(30,DesignScope.IconSize(30,design));
    }

    [Fact]
    public void TinyPixelCornersDoNotCrossTheOppositeEdge()
    {
        foreach(var (width,height) in new[] { (7d,7d),(159d,7d),(5d,199d),(19d,19d),(799d,43d) })
        {
            var step=DesignScope.PixelCornerStep(width,height);
            Assert.InRange(step,1,4);
            Assert.True(step<width/2 && step<height/2);
            Assert.Equal(Math.Floor(step),step);
        }
    }

    [Fact]
    public void DynamicTextEditorsInheritTheOriginalSizeWithoutScalingTwice()
    {
        var label=new object();
        DesignScope.SetOriginalFontSize(label,12);
        DesignScope.SetRole(label,DesignElementRole.FileName);
        foreach(var design in DesignCatalog.All)
        {
            var shown=DesignScope.FontSize(12,design);
            var editorBaseline=DesignScope.OriginalFontSize(label,shown);
            Assert.Equal(shown,DesignScope.FontSize(editorBaseline,design));
            Assert.Equal(12,editorBaseline);
            Assert.Equal(DesignElementRole.FileName,DesignScope.GetRole(label));
        }
    }

    [Fact]
    public void AuthoredWeightPreservesCurrentItemEmphasisAboveEveryDesignBaseline()
    {
        foreach(var design in DesignCatalog.All.Concat(DesignCatalog.All.Reverse()))
        {
            var normal=DesignScope.FontWeight(400,design);
            var current=DesignScope.FontWeight(600,design);
            Assert.Equal(design.FontWeight,normal);
            Assert.Equal(200,current-normal);
            Assert.InRange(DesignScope.FontWeight(100,design),100,900);
            Assert.InRange(DesignScope.FontWeight(900,design),100,900);
            Assert.Equal(normal,DesignScope.FontWeight(400,design));
        }
    }

    [Fact]
    public void ContentBoundariesProtectTextWithoutExcludingTheViewToolbar()
    {
        foreach(var name in new[] { "EditorBox","DecorLayer","RenderPane","RenderStack","TextList","PageHost","DocumentTextPreview","Vlc" })
            Assert.True(DesignScope.IsContentBoundary(name,"Grid"));
        foreach(var name in new[] { "StatusBar","BottomBar","FileNameText","FindBox","ZoomText","FitControls" })
            Assert.False(DesignScope.IsContentBoundary(name,"Grid"));
        Assert.True(DesignScope.IsContentBoundary("","VirtualEditorPane"));
        var first = new object(); var second = new object();
        DesignScope.ProtectContent(first);
        Assert.Equal(DesignElementRole.Content,DesignScope.GetRole(first));
        Assert.Equal(DesignElementRole.Automatic,DesignScope.GetRole(second));
    }

    [Fact]
    public void NativeListPresenterIsTheDirectTemplateRoot()
    {
        var xaml=XDocument.Load(Path.Combine(FindRepository(),"src/KOTU.App/Appearance/NativeDesignTemplates.xaml"));
        foreach(var target in new[] { "ListViewItem","GridViewItem" })
        {
            var template=xaml.Root!.Elements().Single(e=>e.Name.LocalName=="ControlTemplate" && (string?)e.Attribute("TargetType")==target);
            Assert.Equal("ListViewItemPresenter",Assert.Single(template.Elements()).Name.LocalName);
        }
    }

    [Fact]
    public void PreservedSkinPaletteOverridesRetainSystemHighContrastBindings()
    {
        var root = FindRepository();
        var source = File.ReadAllText(Path.Combine(root,"src/KOTU.App/Appearance/AppDesignService.cs"));
        var keys = Regex.Matches(source,"\"([A-Za-z]+(?:Brush|Background|Foreground|Pressed|Over|Checked|Selected|Disabled))\"")
            .Select(m => m.Groups[1].Value).Where(k => !k.StartsWith("Kotu",StringComparison.Ordinal)).Distinct().ToArray();
        var xaml = XDocument.Load(Path.Combine(root,"src/KOTU.App/Appearance/MultiDesignResources.xaml"));
        XNamespace x = "http://schemas.microsoft.com/winfx/2006/xaml";
        var dictionary = xaml.Descendants().Single(e => (string?)e.Attribute(x+"Key") == "HighContrast");
        var brushes = dictionary.Elements().ToDictionary(e => (string)e.Attribute(x+"Key")!,e => (string?)e.Attribute("Color"));
        Assert.True(keys.Length > 40);
        foreach(var key in keys)
        {
            Assert.True(brushes.ContainsKey(key),$"Missing high contrast alias: {key}");
            Assert.StartsWith("{ThemeResource SystemColor",brushes[key]);
        }
        Assert.Equal("{ThemeResource SystemColorHighlightTextColor}",brushes["ListViewItemForegroundSelected"]);
        Assert.Equal("{ThemeResource SystemColorHighlightColor}",brushes["ListViewItemBackgroundSelected"]);
        foreach(var suffix in new[] { "Disabled","SelectedDisabled" })
        {
            Assert.Equal("{ThemeResource SystemColorWindowColor}",brushes["TreeViewItemBackground"+suffix]);
            Assert.Equal("{ThemeResource SystemColorGrayTextColor}",brushes["TreeViewItemForeground"+suffix]);
        }
    }

    [Fact]
    public void ActiveControlsUseNativeStatesAndRetainNormalizedCommandMetrics()
    {
        var root = FindRepository();
        var app = XDocument.Load(Path.Combine(root,"src/KOTU.App/App.xaml"));
        XNamespace x = "http://schemas.microsoft.com/winfx/2006/xaml";
        var styles = app.Descendants().Where(e => e.Name.LocalName == "Style").ToArray();
        foreach (var target in new[] { "Button", "ToggleButton", "DropDownButton", "RepeatButton", "CheckBox" })
        {
            var style = styles.Single(e => (string?)e.Attribute("TargetType") == target && e.Attribute(x + "Key") is null);
            Assert.DoesNotContain(style.Elements(), e => (string?)e.Attribute("Property") is "Template" or "Background" or "BorderBrush");
        }
        Assert.DoesNotContain(app.Descendants(), e => e.Name.LocalName == "Setter" && (string?)e.Attribute("Property") == "appearance:LiveDesign.Managed");
        Assert.DoesNotContain(app.Descendants().Attributes("Source"), a => a.Value.Contains("Design", StringComparison.Ordinal));
        Assert.DoesNotContain(app.Descendants(), e => e.Name.LocalName == "SolidColorBrush");

        foreach (var key in new[] { "BottomBarButtonStyle", "BottomBarToggleButtonStyle", "BottomBarWideDropDownButtonStyle", "BottomBarWideComboBoxStyle" })
        {
            var style = styles.Single(e => (string?)e.Attribute(x + "Key") == key);
            Assert.Equal("32", (string?)style.Elements().Single(e => (string?)e.Attribute("Property") == "Height").Attribute("Value"));
        }
        var fit = styles.Single(e => (string?)e.Attribute(x + "Key") == "BottomBarFitOptionsStyle");
        Assert.Equal("20", (string?)fit.Elements().Single(e => (string?)e.Attribute("Property") == "Width").Attribute("Value"));
        foreach (var state in new[] { "PointerOver", "Pressed", "Disabled" })
        {
            var visualState = fit.Descendants().Single(e => e.Name.LocalName == "VisualState" && (string?)e.Attribute(x + "Name") == state);
            Assert.Contains(visualState.Descendants(), e => (string?)e.Attribute("Target") == "RootGrid.Background");
        }
        Assert.Contains(styles, e => (string?)e.Attribute(x + "Key") == "KotuManagedFontIconStyle");
        Assert.Contains(styles, e => (string?)e.Attribute(x + "Key") == "KotuManagedTextBlockStyle");
    }

    [Fact]
    public void DimensionalStateMarksHaveNoSecondContainerAroundTheirMeaning()
    {
        var dimensional = DesignCatalog.Resolve("isometric");
        var flat = dimensional with { Decoration = DesignDecoration.Precision };
        foreach(var name in new[] { "Check","Mixed","Down","Up","Next","Previous" })
        {
            var actual = DesignIconCatalog.Create(name,dimensional);
            var expected = DesignIconCatalog.Create(name,flat);
            Assert.Equal(expected.Contours.Length,actual.Contours.Length);
            for(var i=0;i<actual.Contours.Length;i++) Assert.Equal(expected.Contours[i],actual.Contours[i]);
        }
    }

    private static string FindRepository()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while(directory is not null && !File.Exists(Path.Combine(directory.FullName,"Directory.Build.props"))) directory=directory.Parent;
        return directory?.FullName ?? throw new InvalidOperationException("Repository not found");
    }
}
