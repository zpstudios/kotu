using KOTU.Core.Appearance;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using Windows.UI;

namespace KOTU.App.Appearance;

/// <summary>
/// Publishes UI-only resources before Core broadcasts the change. Existing controls subscribe
/// to DesignRuntime.Changed and refresh in place; this service never replaces a window or view.
/// </summary>
internal static class AppDesignService
{
    private static DesignPreferences? s_preferences;
    private static DispatcherQueue? s_dispatcher;
    private static ResourceDictionary? s_resources;
    private static readonly Dictionary<string,PaletteResources> OwnedPalettes=new(StringComparer.Ordinal);
    private sealed class PaletteResources(ResourceDictionary resources)
    {
        internal ResourceDictionary Resources { get; }=resources;
        internal Dictionary<string,SolidColorBrush> Brushes { get; }=new(StringComparer.Ordinal);
    }
    internal static DesignDefinition Current => DesignRuntime.Current;

    internal static void Initialize(DesignPreferences preferences)
    {
        if (s_preferences is not null) s_preferences.Changed -= RefreshResources;
        s_preferences = preferences;
        s_dispatcher = DispatcherQueue.GetForCurrentThread();
        s_resources ??= Application.Current.Resources;
        preferences.Changed += RefreshResources;
        RefreshResources();
        LiveDesign.Initialize();
        DesignRuntime.Initialize(preferences);
    }

    internal static bool Apply(string id) => (s_preferences
        ?? throw new InvalidOperationException("The design service is not initialized.")).Apply(id);

    private static void RefreshResources()
    {
        if (s_dispatcher is { HasThreadAccess: false })
        {
            s_dispatcher.TryEnqueue(RefreshResources);
            return;
        }
        var design = s_preferences!.Current;
        var resources = s_resources!;
        resources["KotuControlFontFamily"] = new FontFamily(design.FontFamily);
        resources["KotuHeadingFontFamily"] = new FontFamily(design.HeadingFontFamily);
        resources["KotuUiFontSize"] = design.FontSize;
        resources["KotuUiFontWeight"] = new Windows.UI.Text.FontWeight { Weight = design.FontWeight };
        resources["KotuIconStrokeWidth"] = design.IconStrokeWidth;
        resources["KotuControlCornerRadius"] = new CornerRadius(design.ControlRadius);
        resources["KotuPanelCornerRadius"] = new CornerRadius(design.PanelRadius);
        resources["KotuCheckCornerRadius"] = new CornerRadius(design.CheckRadius);
        resources["KotuControlBorderThickness"] = new Thickness(design.BorderWidth);
        // The complete profile is also available to linked icon factories via DesignRuntime.
        // Do not infer a design from a font name or a thin/thick threshold.
        UpdatePalette(resources, "Light", design.Light);
        UpdatePalette(resources, "Default", design.Dark);
        // HighContrast remains the live system-bound dictionary in App.xaml.
    }

    private static void UpdatePalette(ResourceDictionary root, string theme, DesignPalette palette)
    {
        if (!OwnedPalettes.TryGetValue(theme,out var dictionary))
        {
            if (!root.ThemeDictionaries.TryGetValue(theme,out var value) || value is not ResourceDictionary resources)
            {
                resources=new ResourceDictionary();
                root.ThemeDictionaries[theme]=resources;
            }
            // Native dictionary projections are not stable weak-table keys. Keep the actual
            // dictionary and every brush strongly owned by this application, indexed by theme.
            dictionary=new PaletteResources(resources);
            OwnedPalettes.Add(theme,dictionary);
        }
        SetBrush(dictionary, "KotuBackgroundBrush", palette.Background);
        SetBrush(dictionary, "KotuPanelBrush", palette.Panel);
        SetBrush(dictionary, "KotuCardBrush", palette.Card);
        SetBrush(dictionary, "KotuForegroundBrush", palette.Foreground);
        SetBrush(dictionary, "KotuMutedBrush", palette.Muted);
        SetBrush(dictionary, "KotuBorderBrush", palette.Border);
        SetBrush(dictionary, "KotuAccentBrush", palette.Accent);
        SetBrush(dictionary, "KotuSelectionBrush", palette.Selection);
        SetBrush(dictionary, "KotuOnAccentBrush", palette.OnAccent);
        // Native visual states keep their own transitions and consume these shared brushes.
        // Mutating our own brush also updates code-created controls that captured it.
        foreach (var key in new[] { "SolidBackgroundFillColorBaseBrush" }) SetBrush(dictionary,key,palette.Background);
        foreach (var key in new[] { "LayerFillColorDefaultBrush", "SystemControlBackgroundChromeMediumLowBrush" }) SetBrush(dictionary,key,palette.Panel);
        foreach (var key in new[] { "CardBackgroundFillColorDefaultBrush", "ButtonBackground", "ToggleButtonBackground", "ComboBoxBackground", "MenuFlyoutPresenterBackground", "FlyoutPresenterBackground" }) SetBrush(dictionary,key,palette.Card);
        foreach (var key in new[] { "ButtonBorderBrush", "ToggleButtonBorderBrush", "ComboBoxBorderBrush", "CardStrokeColorDefaultBrush", "ControlStrokeColorDefaultBrush", "DividerStrokeColorDefaultBrush" }) SetBrush(dictionary,key,palette.Border);
        foreach (var key in new[] { "TextFillColorPrimaryBrush", "ButtonForeground", "ButtonForegroundPointerOver", "ButtonForegroundPressed", "ToggleButtonForeground", "ComboBoxForeground", "MenuFlyoutItemForeground", "MenuFlyoutItemForegroundPointerOver", "MenuFlyoutItemForegroundPressed", "ListViewItemForeground" }) SetBrush(dictionary,key,palette.Foreground);
        foreach (var key in new[] { "TextFillColorSecondaryBrush", "ButtonForegroundDisabled", "ToggleButtonForegroundDisabled", "ComboBoxForegroundDisabled", "MenuFlyoutItemForegroundDisabled" }) SetBrush(dictionary,key,palette.Muted);
        foreach (var key in new[] { "AccentFillColorDefaultBrush", "AccentTextFillColorPrimaryBrush", "SystemControlHighlightAccentBrush", "ToggleButtonBackgroundChecked", "ToggleButtonBackgroundCheckedPointerOver", "ToggleButtonBackgroundCheckedPressed", "SliderThumbBackground" }) SetBrush(dictionary,key,palette.Accent);
        foreach (var key in new[] { "ToggleButtonForegroundChecked", "ToggleButtonForegroundCheckedPointerOver", "ToggleButtonForegroundCheckedPressed" }) SetBrush(dictionary,key,palette.OnAccent);
        foreach (var key in new[] { "ButtonBackgroundPointerOver", "ButtonBackgroundPressed", "ToggleButtonBackgroundPointerOver", "ToggleButtonBackgroundPressed", "ComboBoxBackgroundPointerOver", "ComboBoxBackgroundPressed", "MenuFlyoutItemBackgroundPointerOver", "MenuFlyoutItemBackgroundPressed", "ListViewItemBackgroundPointerOver", "ListViewItemBackgroundPressed", "ListViewItemBackgroundSelected", "ListViewItemBackgroundSelectedPointerOver", "ListViewItemBackgroundSelectedPressed" }) SetBrush(dictionary,key,palette.Selection);
        foreach (var key in new[] { "ListViewItemForegroundSelected", "ListViewItemForegroundSelectedPointerOver", "ListViewItemForegroundSelectedPressed" }) SetBrush(dictionary,key,palette.Foreground);
        foreach (var key in new[] { "ToggleSwitchContentForeground", "ToggleSwitchHeaderForeground", "MenuFlyoutSubItemChevron", "MenuFlyoutSubItemForegroundPressed" }) SetBrush(dictionary,key,palette.Foreground);
        foreach (var key in new[] { "ToggleSwitchContentForegroundDisabled", "ToggleSwitchHeaderForegroundDisabled", "MenuFlyoutSubItemForegroundDisabled" }) SetBrush(dictionary,key,palette.Muted);
        // Native menu/tree state targets remain present. Their ordinary rectangular washes are
        // transparent because LiveDesign paints the corresponding shaped state underneath.
        foreach (var key in new[] { "MenuFlyoutItemBackgroundPointerOver", "MenuFlyoutItemBackgroundPressed", "MenuFlyoutSubItemBackgroundPointerOver", "MenuFlyoutSubItemBackgroundPressed", "MenuFlyoutSubItemBackgroundSubMenuOpened",
            "TreeViewItemBackground", "TreeViewItemBackgroundPointerOver", "TreeViewItemBackgroundPressed", "TreeViewItemBackgroundDisabled", "TreeViewItemBackgroundSelected", "TreeViewItemBackgroundSelectedPointerOver", "TreeViewItemBackgroundSelectedPressed", "TreeViewItemBackgroundSelectedDisabled",
            "TreeViewItemBorderBrush", "TreeViewItemBorderBrushPointerOver", "TreeViewItemBorderBrushPressed", "TreeViewItemBorderBrushDisabled", "TreeViewItemBorderBrushSelected", "TreeViewItemBorderBrushSelectedPointerOver", "TreeViewItemBorderBrushSelectedPressed", "TreeViewItemBorderBrushSelectedDisabled", "TreeViewItemMultiSelectBorderBrushSelected" }) SetBrush(dictionary,key,0);
        foreach (var key in new[] { "TreeViewItemForeground", "TreeViewItemForegroundPointerOver", "TreeViewItemForegroundPressed", "TreeViewItemForegroundSelected", "TreeViewItemForegroundSelectedPointerOver", "TreeViewItemForegroundSelectedPressed", "ComboBoxDropDownGlyphForeground", "ComboBoxDropDownGlyphForegroundFocused", "ComboBoxDropDownGlyphForegroundFocusedPressed", "MenuFlyoutSubItemForeground", "MenuFlyoutSubItemForegroundPointerOver", "MenuFlyoutSubItemForegroundSubMenuOpened" }) SetBrush(dictionary,key,palette.Foreground);
        foreach (var key in new[] { "TreeViewItemForegroundDisabled", "TreeViewItemForegroundSelectedDisabled", "ComboBoxDropDownGlyphForegroundDisabled", "ToggleMenuFlyoutItemKeyboardAcceleratorTextForeground", "ToggleMenuFlyoutItemKeyboardAcceleratorTextForegroundDisabled", "ToggleMenuFlyoutItemKeyboardAcceleratorTextForegroundPointerOver", "ToggleMenuFlyoutItemKeyboardAcceleratorTextForegroundPressed" }) SetBrush(dictionary,key,palette.Muted);
        foreach (var key in new[] { "ScrollBarPanningThumbBackground", "ScrollBarThumbBackground", "ScrollBarThumbFill", "ScrollBarThumbFillPointerOver", "ScrollBarButtonArrowForeground", "ScrollBarButtonArrowForegroundPointerOver" }) SetBrush(dictionary,key,palette.Muted);
        foreach (var key in new[] { "ScrollBarThumbFillPressed", "ScrollBarButtonArrowForegroundPressed" }) SetBrush(dictionary,key,palette.Accent);
        foreach (var key in new[] { "ScrollBarThumbFillDisabled", "ScrollBarButtonArrowForegroundDisabled", "ScrollBarPanningThumbBackgroundDisabled" }) SetBrush(dictionary,key,palette.Muted);
    }

    private static void SetBrush(PaletteResources palette, string key, uint argb)
    {
        var color = Color.FromArgb((byte)(argb >> 24), (byte)(argb >> 16), (byte)(argb >> 8), (byte)argb);
        // Resource lookup can resolve a sealed XAML/system brush after native templates load.
        // Own each replacement from initialization onward; never adopt a looked-up instance.
        var owned=palette.Brushes;
        if (owned.TryGetValue(key,out var brush)) brush.Color=color;
        else
        {
            brush=new SolidColorBrush(color);
            owned.Add(key,brush);
            palette.Resources[key]=brush;
        }
    }
#if KOTU_DESIGN_TEST_HARNESS
    internal static SolidColorBrush OwnedPaletteBrush(string theme,string key)=>OwnedPalettes[theme].Brushes[key];
#endif
}
