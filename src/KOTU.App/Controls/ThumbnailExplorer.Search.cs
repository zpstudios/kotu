using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace KOTU.App.Controls;

public sealed partial class ThumbnailExplorer
{
    internal bool SelectNameMatch(string path)
    {
        var vm = _vms.FirstOrDefault(item => string.Equals(item.Path, path, StringComparison.OrdinalIgnoreCase));
        if (vm is null) return false;
        BeginUserFocusNavigation();
        ActiveItems.SelectedItems.Clear();
        ActiveItems.SelectedItem = vm;
        ActiveItems.ScrollIntoView(vm);
        ActiveItems.UpdateLayout();
        if (ActiveItems.ContainerFromItem(vm) is Control container) container.Focus(FocusState.Keyboard);
        else ActiveItems.Focus(FocusState.Keyboard);
        return true;
    }

    internal void FocusNameSelection()
    {
        if (SelectedEntry is { } entry) SelectNameMatch(entry.Path);
        else FocusGrid();
    }
}
