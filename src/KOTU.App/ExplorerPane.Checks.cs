using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

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

    internal Action? CreateUncheckAllCommand(string? folder) =>
        _session.CreateUncheckAllCommand(folder);

    private MenuFlyoutItem MakeUncheckAllMenuItem(ListViewBase owner)
    {
        var command = CreateUncheckAllCommand(_displayFolder);
        var sequence = _loadSeq;
        var item = new MenuFlyoutItem
        {
            Text = "Uncheck all",
            Icon = new FontIcon { Glyph = "\uE739" },
            IsEnabled = command is not null,
        };
        item.Click += (_, _) =>
        {
            if (IsLoaded && !_presentationUnloaded && !_presentationDisposed &&
                IsVisibleCheckMenuOwner(owner) && sequence == _loadSeq)
                command?.Invoke();
        };
        return item;
    }

    private static bool IsVisibleCheckMenuOwner(ListViewBase owner)
    {
        for (DependencyObject? node = owner; node is not null; node = VisualTreeHelper.GetParent(node))
            if (node is UIElement element && element.Visibility != Visibility.Visible) return false;
        return owner.IsLoaded;
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
