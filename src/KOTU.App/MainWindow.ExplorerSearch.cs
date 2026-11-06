using KOTU.App.Controls;
using KOTU.Core.Routing;
using KOTU.Input;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.System;
using Windows.UI.Core;

namespace KOTU.App;

public sealed partial class MainWindow
{
    private readonly ExplorerTypeAhead _explorerTypeAhead = new();
    private Microsoft.UI.Dispatching.DispatcherQueueTimer? _explorerFilterTimer;
    private bool _explorerFilterComposing;
    private bool _applyingExplorerSelection;
    private bool _explorerLastFromList;
    private bool _explorerCharactersAllowed;
    private bool _explorerFilterResetting;
    private (string? Module, string? Path, bool Untitled, bool S4)? _explorerSearchContext;
    private bool IsExplorerSearchContext => IsEmptyFileModule || IsOpenFileBrowsing;
    private ThumbnailExplorer? ActiveExplorer => IsOpenFileBrowsing ? _s4Explorer : IsEmptyFileModule ? _thumbnailExplorer : null;

    private void InitializeExplorerSearch()
    {
        _explorerFilterTimer = DispatcherQueue.CreateTimer();
        _explorerFilterTimer.Interval = TimeSpan.FromMilliseconds(100);
        _explorerFilterTimer.IsRepeating = false;
        _explorerFilterTimer.Tick += (_, _) => ApplyExplorerFilter();
        ExplorerFilterBox.TextChanged += (_, _) => QueueExplorerFilter();
        ExplorerFilterBox.TextCompositionStarted += (_, _) =>
        {
            _explorerFilterComposing = true;
            _explorerFilterTimer.Stop();
            ResetExplorerPrefix();
        };
        ExplorerFilterBox.TextCompositionEnded += (_, _) =>
        {
            _explorerFilterComposing = false;
            QueueExplorerFilter();
        };
        ExplorerFilterClear.Click += (_, _) =>
        {
            if (_explorerFilterComposing) return;
            ExplorerFilterBox.Text = string.Empty;
            ApplyExplorerFilter();
            ExplorerFilterBox.Focus(FocusState.Keyboard);
        };
        RootLayout.CharacterReceived += OnExplorerCharacterReceived;
        RootLayout.GotFocus += (_, _) =>
        {
            // Selection can deliver GotFocus after the synchronous selection guard has ended.
            if (!_applyingExplorerSelection &&
                (!TryExplorerOwner(out var fromList) || fromList != _explorerLastFromList))
                ResetExplorerPrefix();
        };
        RootLayout.AddHandler(UIElement.PointerPressedEvent,
            new PointerEventHandler((_, _) => ResetExplorerPrefix()), true);
        RootLayout.SizeChanged += (_, _) => UpdateExplorerSearchLayout();
        Activated += (_, e) => { if (e.WindowActivationState == WindowActivationState.Deactivated) ResetExplorerPrefix(); };
        Closed += (_, _) => _explorerFilterTimer.Stop();
    }

    private void UpdateExplorerSearchContext()
    {
        var context = (CurrentModuleId, _currentFilePath, _untitledContent, IsOpenFileBrowsing);
        if (_explorerSearchContext != context)
        {
            _explorerSearchContext = context;
            ResetExplorerFilter();
        }
        // A shared string on the actual XamlRoot works across source-linked HotkeySupport copies.
        ScaleHost.Tag = IsExplorerSearchContext ? HotkeySupport.ExplorerLetterContextTag : null;
        ExplorerFilterHost.Visibility = IsExplorerSearchContext ? Visibility.Visible : Visibility.Collapsed;
        BottomBar.Visibility = BarVisible ? Visibility.Visible : Visibility.Collapsed;
        UpdateExplorerSearchLayout();
    }

    private void UpdateExplorerSearchLayout()
    {
        var width = RootLayout.ActualWidth >= 1000 ? 180 : 120;
        ExplorerFilterHost.Width = width;
        ExplorerFilterColumn.Width = new GridLength(IsExplorerSearchContext ? width + 6 : 0);
    }

    private void QueueExplorerFilter()
    {
        ResetExplorerPrefix();
        ExplorerFilterClear.Visibility = ExplorerFilterBox.Text.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        if (_explorerFilterResetting || _explorerFilterComposing || !IsExplorerSearchContext) return;
        _explorerFilterTimer?.Stop();
        _explorerFilterTimer?.Start();
    }

    private void ApplyExplorerFilter()
    {
        _explorerFilterTimer?.Stop();
        if (_explorerFilterComposing || !IsExplorerSearchContext) return;
        ListOverlay.SetNameFilter(ExplorerFilterBox.Text);
    }

    private void ResetExplorerFilter()
    {
        ResetExplorerPrefix();
        _explorerFilterTimer?.Stop();
        _explorerFilterResetting = true;
        ExplorerFilterBox.Text = string.Empty;
        _explorerFilterResetting = false;
        ListOverlay.SetNameFilter(string.Empty);
    }

    private void ResetExplorerPrefix() => _explorerTypeAhead.Reset();

    private void RefreshExplorerSelectedMetadata(IReadOnlyList<ExplorerListing.Entry> entries)
    {
        if (_selectedBrowse.Path is not { } selectedPath) return;
        var next = ExplorerNameView.SelectionMetadata(entries, selectedPath, ListOverlay.MetadataGeneration);
        if (next == _selectedBrowse) return;
        _selectedBrowse = next;
    }

    private void ObserveExplorerSelection(bool fromList)
    {
        _explorerLastFromList = fromList;
        if (!_applyingExplorerSelection) ResetExplorerPrefix();
    }

    private static bool ExplorerKeyDown(VirtualKey key) => InputKeyboardSource.GetKeyStateForCurrentThread(key)
        .HasFlag(CoreVirtualKeyStates.Down);

    private bool ExplorerToolsOwnFocus() => RootLayout.XamlRoot is { } root &&
        FocusManager.GetFocusedElement(root) is DependencyObject focused &&
        ActiveExplorer?.OwnsToolFocus(focused) == true;

    private bool ExplorerInputBlocked()
    {
        if (ExplorerToolsOwnFocus()) return true;
        if (ExplorerRenameBox.IsEditing || IsAnyPopupOpen() || IsFocusWithin(JobsLayer) || IsTextInputFocused()) return true;
        var node = RootLayout.XamlRoot is { } root ? FocusManager.GetFocusedElement(root) as DependencyObject : null;
        for (; node is not null; node = VisualTreeHelper.GetParent(node))
            if (node is ComboBox or Slider or NumberBox or AutoSuggestBox or TreeView) return true;
        return false;
    }

    private bool TryExplorerOwner(out bool fromList)
    {
        fromList = false;
        if (ExplorerInputBlocked() || RootLayout.XamlRoot is not { } root ||
            FocusManager.GetFocusedElement(root) is not DependencyObject focused) return false;
        if (ListOverlay.OwnsFileListFocus(focused)) { fromList = true; return true; }
        if (IsWithin(focused, ListOverlay)) return false; // path controls / folder tree retain ownership
        if (!IsExplorerSearchContext || !IsWithin(focused, RootLayout)) return false;
        return ActiveExplorer is not null;
    }

    private bool HandleExplorerPreviewKey(KeyRoutedEventArgs e)
    {
        var control = ExplorerKeyDown(VirtualKey.Control);
        var alt = ExplorerKeyDown(VirtualKey.Menu);
        var shift = ExplorerKeyDown(VirtualKey.Shift);
        _explorerCharactersAllowed = !e.Handled && !control && !alt &&
            !ExplorerKeyDown(VirtualKey.LeftWindows) && !ExplorerKeyDown(VirtualKey.RightWindows) &&
            e.Key != VirtualKey.Space && !(shift && e.Key == VirtualKey.N); // existing check / new-window commands
        if (e.Key is VirtualKey.Left or VirtualKey.Right or VirtualKey.Up or VirtualKey.Down or
            VirtualKey.Home or VirtualKey.End or VirtualKey.PageUp or VirtualKey.PageDown or
            VirtualKey.Tab or VirtualKey.Enter or VirtualKey.Escape or VirtualKey.Back or VirtualKey.Space or VirtualKey.F2 || control || alt)
            ResetExplorerPrefix();
        // A416: consume before GridView's default Space changes selection or generates ItemClick.
        // Only the actual file surface owns this command; footer text/IME, popups and editors do not.
        if (!e.Handled && e.Key == VirtualKey.Space && !control && !alt && !shift &&
            !ExplorerKeyDown(VirtualKey.LeftWindows) && !ExplorerKeyDown(VirtualKey.RightWindows) &&
            !ExplorerInputBlocked() && RootLayout.XamlRoot is { } checkRoot &&
            FocusManager.GetFocusedElement(checkRoot) is DependencyObject checkFocus &&
            (ListOverlay.ToggleFocusedCheck(checkFocus, e.KeyStatus.WasKeyDown) ||
             ActiveExplorer?.ToggleFocusedCheck(checkFocus, e.KeyStatus.WasKeyDown) == true))
        {
            e.Handled = true;
            return true;
        }
        var filterFocused = IsFocusWithin(ExplorerFilterHost);
        if (!e.Handled && filterFocused && IsExplorerSearchContext && !control && !alt && !shift &&
            e.Key is VirtualKey.Escape or VirtualKey.Enter)
        {
            if (_explorerFilterComposing || IsAnyPopupOpen()) return false;
            e.Handled = true;
            if (e.KeyStatus.WasKeyDown) return true;
            if (e.Key == VirtualKey.Escape && ExplorerFilterBox.Text.Length > 0)
            {
                ExplorerFilterBox.Text = string.Empty;
                ApplyExplorerFilter();
            }
            else
            {
                ApplyExplorerFilter();
                if (_explorerLastFromList && ListOverlay.IsOpen) ListOverlay.FocusNameSelection();
                else ActiveExplorer?.FocusNameSelection();
            }
            return true;
        }
        if (!e.Handled && IsExplorerSearchContext && !IsAnyPopupOpen() && !ExplorerRenameBox.IsEditing &&
            e.Key == VirtualKey.F && control && !alt && !shift &&
            (filterFocused || ExplorerToolsOwnFocus() || TryExplorerOwner(out _)))
        {
            e.Handled = true;
            if (!e.KeyStatus.WasKeyDown)
            {
                ExplorerFilterBox.Focus(FocusState.Keyboard);
                ExplorerFilterBox.SelectAll();
            }
            return true;
        }
        return false;
    }

    private void OnExplorerCharacterReceived(UIElement sender, CharacterReceivedRoutedEventArgs e)
    {
        // WinUI does not promise CharacterReceived during IME composition. Never guess virtual keys.
        if (e.Handled || !_explorerCharactersAllowed || !TryExplorerOwner(out var fromList)) return;
        var entries = ListOverlay.CurrentEntries;
        var selected = fromList ? ListOverlay.SelectedEntry : ActiveExplorer?.SelectedEntry;
        var selectedIndex = -1;
        for (var i = 0; i < entries.Count; i++)
            if (selected is not null && string.Equals(entries[i].Path, selected.Path, StringComparison.OrdinalIgnoreCase))
            { selectedIndex = i; break; }
        var index = _explorerTypeAhead.Find(e.Character.ToString(), Environment.TickCount64, entries, selectedIndex);
        e.Handled = true;
        if (index < 0) return;
        _applyingExplorerSelection = true;
        try
        {
            if (fromList) ListOverlay.SelectNameMatch(entries[index].Path);
            else ActiveExplorer?.SelectNameMatch(entries[index].Path);
        }
        finally { _applyingExplorerSelection = false; }
    }
}
