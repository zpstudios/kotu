using KOTU.Core.Routing;
using KOTU.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace KOTU.App.Controls;

public sealed partial class ThumbnailExplorer
{
    internal Func<(ExplorerListing.SortKey Key, bool Descending, bool Available)>? SortStateProvider { get; set; }
    internal Action<ExplorerListing.SortKey>? SortRequested { get; set; }
    private readonly Dictionary<ExplorerListing.SortKey, ToggleMenuFlyoutItem> _sortOptions = new();

    private void InitializeExplorerTools()
    {
        ExplorerTools.Tag = HotkeySupport.PassThroughTag;
        var menu = new MenuFlyout();
        foreach (var key in new[] { ExplorerListing.SortKey.Name, ExplorerListing.SortKey.Type,
            ExplorerListing.SortKey.Size, ExplorerListing.SortKey.Created, ExplorerListing.SortKey.Modified })
        {
            var item = new ToggleMenuFlyoutItem { Text = key.ToString() };
            item.Click += (_, _) => RequestSort(key);
            _sortOptions.Add(key, item);
            menu.Items.Add(item);
        }
        menu.Opening += (_, _) => SyncSortTools();
        SortKeyButton.Flyout = menu;
        Loaded += (_, _) => SyncSortTools();
        SyncSortTools();
    }

    internal void SyncSortTools()
    {
        var state = SortStateProvider?.Invoke() ?? (Key: ExplorerListing.SortKey.Name, Descending: false, Available: false);
        SortKeyText.Text = state.Key.ToString();
        SortKeyButton.IsEnabled = SortDirectionButton.IsEnabled = state.Available;
        SortDirectionIcon.Glyph = state.Descending ? "\uE70D" : "\uE70E";
        AutomationProperties.SetName(SortDirectionButton, state.Descending ? "Sort descending" : "Sort ascending");
        ToolTipService.SetToolTip(SortDirectionButton, state.Descending
            ? "Descending. Click for ascending" : "Ascending. Click for descending");
        foreach (var (key, item) in _sortOptions) item.IsChecked = key == state.Key;
        LayoutToolGroups(ExplorerTools.ActualWidth);
    }

    private void RequestSort(ExplorerListing.SortKey key)
    {
        // The session alone toggles the current key or chooses a new key's default direction.
        SortRequested?.Invoke(key);
        SyncSortTools();
    }

    private void OnSortDirectionClick(object sender, RoutedEventArgs e)
    {
        if (SortStateProvider is { } current) RequestSort(current().Key);
    }

    internal bool OwnsToolFocus(DependencyObject focused) => IsToolSource(focused);

    private bool IsToolSource(object source)
    {
        var node = source as DependencyObject;
        for (var depth = 0; node is not null && depth < 64; depth++, node = VisualTreeHelper.GetParent(node))
            if (ReferenceEquals(node, ExplorerTools)) return true;
        return false;
    }
}
