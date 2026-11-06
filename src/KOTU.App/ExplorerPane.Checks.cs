using Microsoft.UI.Xaml;

namespace KOTU.App;

public sealed partial class ExplorerPane
{
    // A417: the legacy sidebar presents the session's workset, including when hidden.
    internal event Action? ChecksChanged;

    internal IReadOnlyList<string> CheckedPathsForFolder(string? folder) =>
        _session.CheckedPathsForFolder(folder);

    internal void SetItemChecked(string? folder, string path, bool value)
    {
        _session.SetItemChecked(folder, path, value);
    }

    private void OnSessionChecksChanged()
    {
        foreach (var vm in _displayVms) vm.IsChecked = _session.IsChecked(vm.Path);
        ChecksChanged?.Invoke();
    }

    internal bool ToggleFocusedCheck(DependencyObject focused, bool repeat)
    {
        if (!OwnsFileListFocus(focused) || VmOf(ItemFromSource(focused)) is not { } vm) return false;
        _lastClick = null;
        _lastPress = null;
        if (!repeat) SetItemChecked(_displayFolder, vm.Path, !_session.IsChecked(vm.Path));
        return true;
    }
}
