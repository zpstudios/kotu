using Microsoft.UI.Xaml;

namespace KOTU.App;

public sealed partial class ExplorerPane
{
    // A416: the sidebar remains the sole owner of the checked workset, including when hidden.
    internal event Action? ChecksChanged;

    internal IReadOnlyList<string> CheckedPathsForFolder(string? folder) =>
        string.Equals(folder, _displayFolder, StringComparison.OrdinalIgnoreCase) &&
        string.Equals(folder, _folder, StringComparison.OrdinalIgnoreCase)
            ? CheckedPathsInView() : [];

    internal void SetItemChecked(string? folder, string path, bool value)
    {
        if (!string.Equals(folder, _folder, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(folder, _displayFolder, StringComparison.OrdinalIgnoreCase) ||
            FindVmByPath(path) is not { } vm) return;
        var changed = value ? _checkedPaths.Add(vm.Path) : _checkedPaths.Remove(vm.Path);
        vm.IsChecked = value;
        if (changed) ChecksChanged?.Invoke();
    }

    internal bool ToggleFocusedCheck(DependencyObject focused, bool repeat)
    {
        if (!OwnsFileListFocus(focused) || VmOf(ItemFromSource(focused)) is not { } vm) return false;
        _lastClick = null;
        _lastPress = null;
        if (!repeat) SetItemChecked(_displayFolder, vm.Path, !_checkedPaths.Contains(vm.Path));
        return true;
    }
}
