using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace KOTU.App.Controls;

public sealed partial class ThumbnailExplorer
{
    internal bool SelectNameMatch(string path)
    {
        var vm = _vms.FirstOrDefault(item => string.Equals(item.Path, path, StringComparison.OrdinalIgnoreCase));
        if (vm is null) return false;
        TileGrid.SelectedItems.Clear();
        TileGrid.SelectedItem = vm;
        TileGrid.ScrollIntoView(vm);
        TileGrid.UpdateLayout();
        if (TileGrid.ContainerFromItem(vm) is Control container) container.Focus(FocusState.Keyboard);
        else TileGrid.Focus(FocusState.Keyboard);
        return true;
    }

    internal void FocusNameSelection()
    {
        if (SelectedEntry is { } entry) SelectNameMatch(entry.Path);
        else FocusGrid();
    }
}
