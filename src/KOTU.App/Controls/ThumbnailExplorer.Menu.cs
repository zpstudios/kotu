using KOTU.Core.Routing;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Windows.ApplicationModel.DataTransfer;

namespace KOTU.App.Controls;

public sealed partial class ThumbnailExplorer
{
    internal event Action? ShowDetailsRequested;
    private long _menuGeneration;
    private bool _menuCommandPending;
    private const string StaleMenuNotice = "The folder view or selection changed. Open the menu again";
    private enum MenuCommand { Open, OpenNew, Shell, CopyPath, Cut, Copy, Paste, Rename, Delete, Details }
    private sealed record MenuIntent(ExplorerMenuTarget Target, SelectorItem Row, ExplorerEntryVm Vm,
        ListViewBase Surface, int ShowSequence);

    private void BuildTileContextMenu(MenuFlyout flyout, SelectorItem item, ExplorerEntryVm vm)
    {
        flyout.Items.Clear();
        var target = ExplorerMenuTarget.Capture(CurrentFolder, _menuGeneration, vm.Path,
            _vms.Select(v => v.Entry), CheckedPaths(), SelectedPaths());
        if (target is null || !ReferenceEquals(vm.Entry, target.Clicked)) return;
        var intent = new MenuIntent(target, item, vm, ActiveItems, _showSeq);
        void Add(string label, string glyph, MenuCommand command, bool enabled = true)
        {
            var menuItem = new MenuFlyoutItem { Text = label, Icon = new FontIcon { Glyph = glyph },
                IsEnabled = enabled && !_menuCommandPending };
            menuItem.Click += async (_, _) => await ExecuteMenuAsync(intent, command);
            flyout.Items.Add(menuItem);
        }
        void Separator() => flyout.Items.Add(new MenuFlyoutSeparator());
        if (target.Clicked.IsFolder)
        {
            Add("Open", "\uE8E5", MenuCommand.Open);
            Add("Open in Windows Explorer", "\uE8A7", MenuCommand.Shell);
            Add("Copy path", "\uE8C8", MenuCommand.CopyPath);
        }
        else Add("Open in new instance", "\uE8A7", MenuCommand.OpenNew);
        Separator();
        var scope = target.Clicked.IsFolder ? $" ({target.ScopeLabel})" : "";
        Add("Cut" + scope, "\uE8C6", MenuCommand.Cut);
        Add("Copy" + scope, "\uE8C8", MenuCommand.Copy);
        if (target.Clicked.IsFolder)
            Add("Paste into this folder", "\uE77F", MenuCommand.Paste, ExplorerFileOps.CanPasteFromClipboard());
        Separator();
        Add("Rename", "\uE8AC", MenuCommand.Rename);
        Add("Delete" + scope, "\uE74D", MenuCommand.Delete);
        if (target.Clicked.IsFolder)
        {
            Separator();
            Add("Show details", "\uE946", MenuCommand.Details);
        }
    }

    private static bool UsesWorkingSet(MenuCommand command) => command is MenuCommand.Cut or MenuCommand.Copy or MenuCommand.Delete;

    private bool MenuStillCurrent(MenuIntent intent, MenuCommand command) =>
        IsLoaded && IsVisibleSurface() && !_parentNavigationPending &&
        ReferenceEquals(ActiveItems, intent.Surface) && _showSeq == intent.ShowSequence &&
        ReferenceEquals(VmOf(intent.Row), intent.Vm) && _vms.Contains(intent.Vm) &&
        intent.Target.IsCurrent(CurrentFolder, _menuGeneration, _vms.Select(v => v.Entry),
            CheckedPaths(), SelectedPaths(), UsesWorkingSet(command));

    private async Task<bool> ValidateMenuAsync(MenuIntent intent, MenuCommand command)
    {
        if (!MenuStillCurrent(intent, command)) { ShowNotice(StaleMenuNotice); return false; }
        var entries = UsesWorkingSet(command) ? intent.Target.Working : [intent.Target.Clicked];
        var error = await Task.Run(() => ExplorerMenuValidation.Validate(entries, ExplorerMenuValidation.ReadIdentity));
        if (!MenuStillCurrent(intent, command)) { ShowNotice(StaleMenuNotice); return false; }
        if (error is null) return true;
        ShowNotice(error);
        return false;
    }

    private async Task ExecuteMenuAsync(MenuIntent intent, MenuCommand command)
    {
        if (_menuCommandPending) return;
        _menuCommandPending = true;
        try
        {
            if (!await ValidateMenuAsync(intent, command)) return;
            var entry = intent.Target.Clicked;
            var paths = intent.Target.Working.Select(e => e.Path).ToArray();
            Func<Task<bool>> guard = () => ValidateMenuAsync(intent, command);
            switch (command)
            {
                case MenuCommand.Open:
                    FolderActivated?.Invoke(entry.Path);
                    break;
                case MenuCommand.OpenNew:
                    FileActivatedNewWindow?.Invoke(entry.Path);
                    break;
                case MenuCommand.Shell:
                    var folder = await ExplorerFolderShell.ResolveAsync(entry);
                    if (!await guard()) return;
                    if (await ExplorerFolderShell.OpenAsync(folder, entry, guard) is { } shellError) ShowNotice(shellError);
                    break;
                case MenuCommand.CopyPath:
                    var data = new DataPackage { RequestedOperation = DataPackageOperation.Copy };
                    data.SetText(entry.Path); // text only, deliberately no StorageItems
                    Clipboard.SetContent(data);
                    ExplorerFileOps.ClearCutMarks();
                    break;
                case MenuCommand.Cut:
                case MenuCommand.Copy:
                    if (await ExplorerFileOps.CopyToClipboardAsync(paths, command == MenuCommand.Cut, guard) is { } copyError)
                        ShowNotice(copyError);
                    break;
                case MenuCommand.Paste:
                    var ui = MakeOpUi();
                    var (didWork, pasted, pasteError) = await ExplorerFileOps.PasteFromClipboardAsync(entry.Path, ui, guard);
                    if (didWork && MenuStillCurrent(intent, command)) RefreshViaShell();
                    await ExplorerFileOps.ReportAsync(pasteError, pasted.Denied, ui);
                    break;
                case MenuCommand.Delete:
                    var deleted = await ExplorerFileOps.DeleteToRecycleAsync(paths, guard);
                    if (MenuStillCurrent(intent, command)) RefreshViaShell();
                    await ExplorerFileOps.ReportAsync(deleted.Notice("deleted"), deleted.Denied, MakeOpUi());
                    break;
                case MenuCommand.Rename:
                    // Flyout focus restoration finishes before the inline editor takes focus.
                    DispatcherQueue.TryEnqueue(async () =>
                    {
                        if (await ValidateMenuAsync(intent, MenuCommand.Rename))
                            BeginRenameOf(intent.Row, async name =>
                            {
                                if (!await ValidateMenuAsync(intent, MenuCommand.Rename))
                                    return (StaleMenuNotice, false);
                                return await Task.Run(() =>
                                {
                                    var error = ExplorerMenuValidation.Validate([entry], ExplorerMenuValidation.ReadIdentity);
                                    return error is null ? ExplorerFileOps.Rename(entry.Path, name) : (error, false);
                                });
                            });
                    });
                    break;
                case MenuCommand.Details:
                    ActiveItems.SelectedItems.Clear();
                    ActiveItems.SelectedItem = intent.Vm;
                    ShowDetailsRequested?.Invoke(); // selection event plus the same S1/S4 info-panel route
                    break;
            }
        }
        catch (Exception ex) { ShowNotice("Could not complete the folder action - " + ex.Message); }
        finally { _menuCommandPending = false; }
    }
}
