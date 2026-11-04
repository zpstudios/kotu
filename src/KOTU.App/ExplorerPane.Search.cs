using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace KOTU.App;

public sealed partial class ExplorerPane
{
    internal bool OwnsFileListFocus(DependencyObject focused) =>
        Within(focused, ListPane) || Within(focused, IconGrid);

    private static bool Within(DependencyObject? node, DependencyObject root)
    {
        while (node is not null)
        {
            if (ReferenceEquals(node, root)) return true;
            node = Microsoft.UI.Xaml.Media.VisualTreeHelper.GetParent(node);
        }
        return false;
    }

    private void RestoreNameSelection(string path) => ListPane.SelectedItem = FindVmByPath(path);

    internal bool SelectNameMatch(string path)
    {
        if (FindVmByPath(path) is not { } vm) return false;
        ListPane.SelectedItems.Clear();
        ListPane.SelectedItem = vm;
        var container = RealizeListContainer(vm);
        if (container is not null) container.Focus(FocusState.Keyboard);
        else ListPane.Focus(FocusState.Keyboard);
        return true;
    }

    internal void FocusNameSelection()
    {
        if (SelectedEntry is { } entry) SelectNameMatch(entry.Path);
        else ListPane.Focus(FocusState.Keyboard);
    }
}
