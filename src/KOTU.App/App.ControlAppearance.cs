using Microsoft.UI.Xaml.Media;

namespace KOTU.App;

public partial class App
{
    // Called once, before constructing any window. Changing the preference in Settings
    // takes effect on the next launch, across all linked copies of the toolbar factories.
    internal static void SelectControlAppearance(bool thin)
    {
        if (!thin) return;
        var resources = Current.Resources;
        resources["KotuControlFontFamily"] = new FontFamily("Segoe UI Semilight, Segoe UI");
        resources["KotuIconFontFamily"] = new FontFamily("ms-appx:///Assets/KotuIconsThin.ttf#KOTU Icons Thin, Segoe Fluent Icons, Segoe MDL2 Assets");
        resources["KotuIconStrokeWidth"] = 1.0;
    }
}
