using KOTU.Core.Contracts;
using KOTU.DocumentModel;
using KOTU.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.System;

namespace KOTU.Module.Document;

public sealed partial class DocumentView
{
    private TextSearchWorker? _searchWorker;
    private TextSearchResult? _searchResult;
    private long _textRevision;
    private int _searchLoadedGeneration = -1;
    private int _searchCurrent = -1;
    private bool _searchLoaded;
    private bool _searchInitialized;
    private bool _findComposing;
    private bool _replaceComposing;
    private bool _editorComposing;
    private bool _selectingSearchMatch;
    private bool _replaceBusy;
    private bool _focusReplacementOnOpen;
    private bool _searchCompactTools;
    private bool _searchPendingPrevious;
    private bool _searchSelectOnResult;
    private StackPanel? _searchTools;
    private CancellationTokenSource? _replacementCancellation;
    private string? _replaceError;
    private SolidColorBrush? _searchInactiveSelectionBrush;
    private UIElement? _searchFlyoutPresenter;
    private bool _searchFlyoutOpen;
    private bool _searchEscapePending;
    private bool _searchInteractionActive;
    private bool _searchFlyoutDismissed;
    private bool _searchReturnFocusAfterClose;

    private bool CanSearchText => _searchLoaded && _searchLoadedGeneration == _openSeq &&
        (_path is not null || _untitled) && !_htmlMode &&
        !(_truncated && _path is { } path && IsHtmlPath(path)) &&
        (EditorBox.Visibility == Visibility.Visible || _renderMode);

    private bool CanReplaceText => CanSearchText && !_truncated && !_capped && !_loadingText &&
        !_viewMode && !_renderMode && !EditorBox.IsReadOnly && !_documentSession.IsSaving && !_editorComposing;

    private TextSearchSnapshot SearchSnapshot() => new(EditorText, _openSeq, _textRevision,
        !_truncated && !_capped, CanReplaceText);

    private void InitializeTextSearch()
    {
        _searchInactiveSelectionBrush = EditorBox.SelectionHighlightColorWhenNotFocused;
        _searchTools = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        foreach (var element in new FrameworkElement[] { DecorTogglePanel, PageInfoText, ZoomButton })
        {
            StatusBarGrid.Children.Remove(element);
            _searchTools.Children.Add(element);
        }
        SearchToolsInlineHost.Content = _searchTools;
        PageInfoText.MaxWidth = 110;
        PageInfoText.TextTrimming = TextTrimming.CharacterEllipsis;
        FindBox.TextChanging += (_, _) => InvalidateTextSearch();
        ReplaceBox.TextChanging += (_, _) => _replacementCancellation?.Cancel();
        FindBox.TextCompositionStarted += (_, _) => { _findComposing = true; InvalidateTextSearch(); };
        FindBox.TextCompositionEnded += (_, _) => { _findComposing = false; QueueTextSearch(selectMatch: true); };
        ReplaceBox.TextCompositionStarted += (_, _) => _replaceComposing = true;
        ReplaceBox.TextCompositionEnded += (_, _) => _replaceComposing = false;
        EditorBox.TextCompositionStarted += (_, _) => { _editorComposing = true; InvalidateTextSearch(); };
        EditorBox.TextCompositionEnded += (_, _) => { _editorComposing = false; QueueTextSearch(); };
        EditorBox.AddHandler(PointerPressedEvent, new PointerEventHandler(OnSearchBodyPointerPressed), handledEventsToo: true);
        _searchInitialized = true;
        UpdateSearchAvailability();
        UpdateSearchToolbarLayout(StatusBarGrid.ActualWidth);
    }

    private void ResumeTextSearch()
    {
        _searchLoaded = true;
        // A reattached view still owns the same editor buffer, but Unloaded advanced its lifetime generation.
        if (_searchLoadedGeneration >= 0 && (_path is not null || _untitled)) _searchLoadedGeneration = _openSeq;
        _searchWorker ??= new(action => DispatcherQueue.TryEnqueue(() => action()), ApplySearchResult);
        UpdateSearchAvailability();
        QueueTextSearch();
    }

    private void SuspendTextSearch()
    {
        _searchLoaded = false;
        _searchEscapePending = _searchInteractionActive = _searchReturnFocusAfterClose = _searchFlyoutDismissed = false;
        DetachSearchFlyoutPresenter();
        InvalidateTextSearch();
        _searchWorker?.Dispose();
        _searchWorker = null;
        FindFlyout.Hide();
        SearchToolsFlyout.Hide();
        _findComposing = _replaceComposing = _editorComposing = false;
    }

    private void SearchDocumentReady()
    {
        _searchLoadedGeneration = _openSeq;
        UpdateSearchAvailability();
        QueueTextSearch();
    }

    // TextChanged is asynchronous. Invalidate offsets synchronously before any stale result can be used.
    // TextChanging must not modify the visual tree; all control updates happen in TextChanged instead.
    private void OnSearchEditorTextChanging(TextBox sender, TextBoxTextChangingEventArgs args)
    {
        _textRevision++;
        _textSnapshot = null;
        InvalidateTextSearch();
    }

    private void InvalidateTextSearch()
    {
        _searchWorker?.Cancel();
        _replacementCancellation?.Cancel();
        _searchResult = null;
        _searchCurrent = -1;
    }

    private void UpdateSearchAvailability()
    {
        if (!_searchInitialized) return;
        var available = CanSearchText;
        FindBox.IsEnabled = available;
        FindOptionsButton.IsEnabled = available;
        FindBox.PlaceholderText = available ? "Find text" : "Text search unavailable";
        ToolTipService.SetToolTip(FindBox, available
            ? (_capped || _truncated ? "Find in loaded preview only (Ctrl+F)" : "Find text (Ctrl+F); Markdown searches source")
            : "Search is available for loaded text and Markdown source, not PDF or HTML view.");
        if (!available) InvalidateTextSearch();
        else if (_searchResult is { } result && result.Snapshot.CanReplace != CanReplaceText) QueueTextSearch();
        UpdateSearchPresentation();
    }

    private void QueueTextSearch(bool selectMatch = false)
    {
        if (!_searchInitialized) return;
        InvalidateTextSearch();
        _searchSelectOnResult = selectMatch;
        _replaceError = null;
        if (CanSearchText && !_findComposing && !_editorComposing && !_loadingText && FindBox.Text.Length > 0)
            _searchWorker?.Submit(SearchSnapshot(), FindBox.Text);
        UpdateSearchPresentation();
    }

    private void OnFindTextChanged(object sender, TextChangedEventArgs args)
    {
        if (!_searchInitialized) return;
        EnsureSearchSource();
        _searchPendingPrevious = false;
        QueueTextSearch(selectMatch: true);
    }

    private void OnFindGotFocus(object sender, RoutedEventArgs args)
    {
        _searchInteractionActive = true;
        EnsureSearchSource();
        if (_searchResult is null) QueueTextSearch();
    }

    private void EnsureSearchSource()
    {
        if (!_renderMode || !CanSearchText) return;
        // Searching Markdown exposes the source while retaining the existing read-only viewing guard.
        ExitRenderMode();
        EnterViewModeForSearch();
        FindBox.Focus(FocusState.Programmatic);
    }

    private void EnterViewModeForSearch()
    {
        _viewMode = true;
        UpdateEditorReadOnly();
        UpdateViewToggle();
        UpdateDecorToggles();
    }

    private void ApplySearchResult(TextSearchResult result)
    {
        if (!CanSearchText || _findComposing || _editorComposing || !result.IsCurrent(SearchSnapshot(), FindBox.Text)) return;
        // A mode/save transition can happen while no result has arrived yet.
        if (result.Snapshot.CanReplace != CanReplaceText)
        {
            QueueTextSearch(_searchSelectOnResult);
            return;
        }
        _searchResult = result;
        if (_searchSelectOnResult)
        {
            _searchCurrent = result.AtOrAfter(EditorBox.SelectionStart);
            if (_searchPendingPrevious) _searchCurrent = result.Navigate(_searchCurrent, previous: true);
            SelectSearchMatch();
        }
        else SynchronizeSearchSelection();
        _searchPendingPrevious = false;
        _searchSelectOnResult = false;
    }

    private void SelectSearchMatch()
    {
        if (_searchResult is not { } result || !CanSearchText || !result.IsCurrent(SearchSnapshot(), FindBox.Text)) return;
        if (_searchCurrent >= 0 && _searchCurrent < result.Starts.Count)
        {
            _selectingSearchMatch = true;
            try
            {
                // Only the current selection is highlighted; no per-match rectangle queries are issued.
                EditorBox.SelectionStart = result.Starts[_searchCurrent];
                EditorBox.SelectionLength = result.Query.Length;
                BringSearchMatchIntoView(result.Starts[_searchCurrent]);
            }
            finally { _selectingSearchMatch = false; }
        }
        UpdateSearchPresentation();
    }

    private void BringSearchMatchIntoView(int position)
    {
        if (FindDescendant<ScrollViewer>(EditorBox) is not { } scroll || EditorBox.ActualHeight <= 0) return;
        try
        {
            // The first character is at the scroll origin regardless of native rectangle readiness.
            // Very long lines can return an empty rectangle; that must not prevent returning to start.
            if (position == 0)
            {
                scroll.ChangeView(0, 0, null, disableAnimation: true);
                return;
            }
            // Two rectangles only, independent of the match count. Subtracting the first character
            // removes the runtime's content/viewport origin difference documented by EditorDecor.
            var origin = EditorBox.GetRectFromCharacterIndex(0, false);
            var match = EditorBox.GetRectFromCharacterIndex(position, false);
            if (match.Height <= 0 || !double.IsFinite(match.Y) || !double.IsFinite(origin.Y)) return;
            var padding = EditorBox.Padding;
            var contentY = match.Y - origin.Y;
            var visibleY = contentY + padding.Top - scroll.VerticalOffset;
            var bottom = EditorBox.ActualHeight - padding.Bottom;
            double? vertical = null;
            if (visibleY < padding.Top || visibleY + match.Height > bottom)
                vertical = Math.Clamp(contentY - Math.Max(0, (bottom - padding.Top) / 2), 0, scroll.ScrollableHeight);
            var contentX = match.X - origin.X;
            var visibleX = contentX + padding.Left - scroll.HorizontalOffset;
            double? horizontal = null;
            if (double.IsFinite(contentX) && (visibleX < padding.Left || visibleX > EditorBox.ActualWidth - padding.Right))
                horizontal = Math.Clamp(contentX, 0, scroll.ScrollableWidth);
            if (vertical is not null || horizontal is not null) scroll.ChangeView(horizontal, vertical, null, disableAnimation: true);
        }
        catch (Exception)
        {
            // The native editor may still be laying out. Keep its valid selection without forcing focus.
        }
    }

    private void SynchronizeSearchSelection()
    {
        if (_selectingSearchMatch) return;
        _searchSelectOnResult = false;
        if (_searchResult is not { } result) return;
        var candidate = result.AtOrAfter(EditorBox.SelectionStart);
        _searchCurrent = candidate >= 0 && result.Starts[candidate] == EditorBox.SelectionStart &&
            EditorBox.SelectionLength == result.Query.Length ? candidate : -1;
        UpdateSearchPresentation();
    }

    private void NavigateSearch(bool previous)
    {
        EnsureSearchSource();
        if (_searchResult is not { } result || !result.IsCurrent(SearchSnapshot(), FindBox.Text))
        {
            _searchPendingPrevious = previous;
            QueueTextSearch(selectMatch: true);
            return;
        }
        if (_searchCurrent < 0)
        {
            _searchCurrent = result.AtOrAfter(EditorBox.SelectionStart);
            if (previous) _searchCurrent = result.Navigate(_searchCurrent, previous: true);
        }
        else _searchCurrent = result.Navigate(_searchCurrent, previous);
        _replaceError = null;
        SelectSearchMatch();
    }

    private void UpdateSearchPresentation()
    {
        if (!_searchInitialized) return;
        var count = _searchResult?.Starts.Count ?? 0;
        EditorBox.SelectionHighlightColorWhenNotFocused = _searchCurrent >= 0 && count > 0
            ? EditorBox.SelectionHighlightColor : _searchInactiveSelectionBrush;
        var label = !CanSearchText ? "Text search unavailable" : FindBox.Text.Length == 0 ? string.Empty :
            _searchResult is null ? "Searching..." : count == 0 ? "No matches" : $"{_searchCurrent + 1:N0} / {count:N0}";
        FindCountText.Text = label;
        FindCountText.Visibility = label.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
        var detail = _capped || _truncated ? "Loaded preview only" : "Literal, ignore case";
        FindDetailText.Text = label.Length == 0 ? detail : $"{label} · {detail}";
        ToolTipService.SetToolTip(FindOptionsButton, $"Find and replace (Ctrl+H) · {FindDetailText.Text}");
        var navigate = CanSearchText && count > 0;
        FindPreviousButton.IsEnabled = FindNextButton.IsEnabled = PopupPreviousButton.IsEnabled = PopupNextButton.IsEnabled = navigate;
        var replace = !_replaceBusy && CanReplaceText && _searchResult is { } result &&
            result.CanApplyReplacement(SearchSnapshot(), FindBox.Text) && count > 0;
        ReplaceCurrentButton.IsEnabled = replace && _searchCurrent >= 0;
        ReplaceAllButton.IsEnabled = replace;
        ReplaceBox.IsEnabled = CanReplaceText && !_replaceBusy;
        ReplaceStatusText.Text = _replaceError ?? (_replaceBusy ? "Replacing..." : _truncated || _capped
            ? "Preview only. Replacing is disabled." : !CanReplaceText
            ? "Switch to edit mode to replace. Replacing is unavailable while saving." : "Ctrl+Z in the document undoes a replacement.");
    }

    private void UpdateSearchToolbarLayout(double width)
    {
        if (!_searchInitialized || _searchTools is null) return;
        FindBox.Width = width < 700 ? 120 : 180;
        InlineFindNavigation.Visibility = width >= 1100 ? Visibility.Visible : Visibility.Collapsed;
        var compact = width < 900;
        if (compact == _searchCompactTools) return;
        SearchToolsFlyout.Hide();
        SearchToolsInlineHost.Content = null;
        SearchToolsPopupHost.Content = null;
        _searchCompactTools = compact;
        if (compact) SearchToolsPopupHost.Content = _searchTools;
        else SearchToolsInlineHost.Content = _searchTools;
        SearchToolsInlineHost.Visibility = compact ? Visibility.Collapsed : Visibility.Visible;
        SearchMoreToolsButton.Visibility = compact ? Visibility.Visible : Visibility.Collapsed;
    }

    private static bool IsWithinSearchElement(DependencyObject? focused, DependencyObject ancestor)
    {
        for (var depth = 0; focused is not null && depth < 64; depth++, focused = VisualTreeHelper.GetParent(focused))
        {
            if (focused is FrameworkElement element && Equals(element.Tag, HotkeySupport.PassThroughTag)) return false;
            if (ReferenceEquals(focused, ancestor)) return true;
        }
        return false;
    }

    private static bool IsSearchPassThroughSurface(DependencyObject? focused)
    {
        for (var depth = 0; focused is not null && depth < 64; depth++, focused = VisualTreeHelper.GetParent(focused))
            if (focused is FrameworkElement element && Equals(element.Tag, HotkeySupport.PassThroughTag)) return true;
        return false;
    }

    public bool TryTextSearchCommand(TextSearchCommand command, bool fromEnter = false, bool repeat = false)
    {
        if (command == TextSearchCommand.EscapeReleased)
        {
            var dismissedSearch = _searchInteractionActive && _searchFlyoutDismissed;
            _searchEscapePending = false;
            _searchFlyoutDismissed = false;
            var releasedFocus = XamlRoot is { } releasedRoot ? FocusManager.GetFocusedElement(releasedRoot) as DependencyObject : null;
            if (dismissedSearch && CanSearchText && !HasUnrelatedSearchPopup() &&
                !IsSearchPassThroughSurface(releasedFocus) &&
                (releasedFocus is not TextBox || ReferenceEquals(releasedFocus, FindBox) ||
                    ReferenceEquals(releasedFocus, ReplaceBox) || ReferenceEquals(releasedFocus, EditorBox)))
            {
                _searchInteractionActive = false;
                EditorBox.Focus(FocusState.Programmatic);
            }
            return false; // KeyUp is observed, never consumed.
        }
        if (!CanSearchText || XamlRoot is not { } root) return false;
        if (HasUnrelatedSearchPopup()) return false;
        // Retain consumption until physical KeyUp, across native dismissal, focus restoration and bubbling.
        if (command == TextSearchCommand.ReturnToDocument && _searchEscapePending)
        {
            if (!repeat) EditorBox.Focus(FocusState.Programmatic);
            return true;
        }
        var focused = FocusManager.GetFocusedElement(root) as DependencyObject;
        if (IsSearchPassThroughSurface(focused)) return false;
        var findField = ReferenceEquals(focused, FindBox);
        var replaceField = ReferenceEquals(focused, ReplaceBox);
        var inOptions = IsWithinSearchElement(focused, FindOptionsPanel) ||
            (_searchFlyoutPresenter is { } presenter && IsWithinSearchElement(focused, presenter));
        var inDocument = IsWithinSearchElement(focused, this) || IsWithinSearchElement(focused, StatusBar);
        var inSearch = IsWithinSearchElement(focused, SearchPanel) || inOptions ||
            (command == TextSearchCommand.ReturnToDocument &&
                (_searchFlyoutOpen || _searchInteractionActive));
        if (fromEnter && !findField && !replaceField) return false;
        if (command == TextSearchCommand.ReturnToDocument && !inSearch) return false;
        if (!inSearch && !inDocument) return false;
        if (focused is TextBox && !findField && !replaceField && !ReferenceEquals(focused, EditorBox)) return false;
        if (_findComposing || _replaceComposing || _editorComposing) return false;
        if (repeat) return true;
        switch (command)
        {
            case TextSearchCommand.Find:
                _searchInteractionActive = true;
                FindFlyout.Hide();
                EnsureSearchSource();
                FindBox.Focus(FocusState.Programmatic);
                FindBox.SelectionStart = 0;
                FindBox.SelectionLength = FindBox.Text.Length;
                break;
            case TextSearchCommand.Replace:
                _searchInteractionActive = true;
                EnsureSearchSource();
                _focusReplacementOnOpen = true;
                FindFlyout.ShowAt(FindOptionsButton);
                break;
            case TextSearchCommand.Next: NavigateSearch(previous: false); break;
            case TextSearchCommand.Previous: NavigateSearch(previous: true); break;
            case TextSearchCommand.ReturnToDocument:
                _searchEscapePending = true;
                _searchInteractionActive = false;
                _searchReturnFocusAfterClose = _searchFlyoutOpen;
                FindFlyout.Hide();
                EditorBox.Focus(FocusState.Programmatic);
                break;
        }
        return true;
    }

    private bool HasUnrelatedSearchPopup()
    {
        if (XamlRoot is not { } root) return true;
        try
        {
            foreach (var popup in VisualTreeHelper.GetOpenPopupsForXamlRoot(root))
                if (popup.Child is not ToolTip &&
                    !(_searchFlyoutOpen && popup.Child is { } child && IsWithinSearchElement(FindOptionsPanel, child))) return true;
            return false;
        }
        catch { return true; }
    }

    private static bool SearchKeyDown(VirtualKey key) => Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(key)
        .HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down);

    private void OnFindPanelKeyDown(object sender, KeyRoutedEventArgs args)
    {
        if (args.Handled || SearchKeyDown(VirtualKey.Menu)) return;
        var control = SearchKeyDown(VirtualKey.Control);
        var shift = SearchKeyDown(VirtualKey.Shift);
        TextSearchCommand? command = args.Key switch
        {
            VirtualKey.F when control && !shift => TextSearchCommand.Find,
            VirtualKey.H when control && !shift => TextSearchCommand.Replace,
            VirtualKey.F3 when !control => shift ? TextSearchCommand.Previous : TextSearchCommand.Next,
            VirtualKey.Enter when !control => shift ? TextSearchCommand.Previous : TextSearchCommand.Next,
            VirtualKey.Escape when !control && !shift => TextSearchCommand.ReturnToDocument,
            _ => null
        };
        if (command is { } value && TryTextSearchCommand(value, args.Key == VirtualKey.Enter, args.KeyStatus.WasKeyDown)) args.Handled = true;
    }

    private void OnFindPanelKeyUp(object sender, KeyRoutedEventArgs args)
    {
        if (args.Key == VirtualKey.Escape) TryTextSearchCommand(TextSearchCommand.EscapeReleased);
    }

    private void OnSearchBodyPointerPressed(object sender, PointerRoutedEventArgs args)
        => _searchInteractionActive = _searchFlyoutDismissed = false;

    private void ObserveSearchBodyKey(KeyRoutedEventArgs args)
    {
        if (args.Key is VirtualKey.Escape or VirtualKey.F3 or VirtualKey.Control or VirtualKey.Shift or VirtualKey.Menu) return;
        if ((args.Key is VirtualKey.F or VirtualKey.H) && SearchKeyDown(VirtualKey.Control)) return;
        _searchInteractionActive = _searchFlyoutDismissed = false;
    }

    private void OnFindFlyoutOpened(object sender, object args)
    {
        _searchFlyoutOpen = true;
        _searchInteractionActive = true;
        _searchFlyoutDismissed = false;
        _searchReturnFocusAfterClose = false;
        DetachSearchFlyoutPresenter();
        // Disabling a focused Replace button may focus the presenter, outside FindOptionsPanel.
        try
        {
            if (XamlRoot is { } root)
            {
                foreach (var popup in VisualTreeHelper.GetOpenPopupsForXamlRoot(root))
                {
                    if (popup.Child is not { } child || !IsWithinSearchElement(FindOptionsPanel, child)) continue;
                    _searchFlyoutPresenter = child;
                    child.PreviewKeyDown += OnFindPanelKeyDown;
                    child.AddHandler(KeyUpEvent, new KeyEventHandler(OnFindPanelKeyUp), handledEventsToo: true);
                    break;
                }
            }
        }
        catch (Exception)
        {
            // Retain the content handler and shell dismissal guard if the popup tree is changing.
        }
        UpdateSearchPresentation();
        if (_focusReplacementOnOpen && ReplaceBox.IsEnabled)
        {
            ReplaceBox.Focus(FocusState.Programmatic);
            ReplaceBox.SelectionStart = 0;
            ReplaceBox.SelectionLength = ReplaceBox.Text.Length;
        }
        _focusReplacementOnOpen = false;
    }

    private void OnFindFlyoutClosing(FlyoutBase sender, FlyoutBaseClosingEventArgs args)
    {
        if (!_searchLoaded || !SearchKeyDown(VirtualKey.Escape) || SearchKeyDown(VirtualKey.Control) ||
            SearchKeyDown(VirtualKey.Shift) || SearchKeyDown(VirtualKey.Menu)) return;
        _searchReturnFocusAfterClose = true;
        _searchInteractionActive = false;
        _searchEscapePending = true;
        // This is an additional observation only. Explicit search-interaction ownership also covers
        // native dismissal when Closing cannot observe the keyboard state. KeyUp ends the gesture.
    }

    private void OnFindFlyoutClosed(object sender, object args)
    {
        _searchFlyoutOpen = false;
        _searchFlyoutDismissed = _searchInteractionActive;
        DetachSearchFlyoutPresenter();
        var returnFocus = _searchReturnFocusAfterClose;
        _searchReturnFocusAfterClose = false;
        if (returnFocus && CanSearchText) EditorBox.Focus(FocusState.Programmatic);
    }

    private void DetachSearchFlyoutPresenter()
    {
        if (_searchFlyoutPresenter is not { } presenter) return;
        presenter.PreviewKeyDown -= OnFindPanelKeyDown;
        presenter.RemoveHandler(KeyUpEvent, new KeyEventHandler(OnFindPanelKeyUp));
        _searchFlyoutPresenter = null;
    }

    private void OnFindPreviousClick(object sender, RoutedEventArgs args) => NavigateSearch(previous: true);
    private void OnFindNextClick(object sender, RoutedEventArgs args) => NavigateSearch(previous: false);
    private void OnReplaceCurrentClick(object sender, RoutedEventArgs args) => _ = ReplaceSearchAsync(all: false);
    private void OnReplaceAllClick(object sender, RoutedEventArgs args) => _ = ReplaceSearchAsync(all: true);

    private async Task ReplaceSearchAsync(bool all)
    {
        if (_replaceBusy || !CanReplaceText || _findComposing || _replaceComposing ||
            _searchResult is not { } result || !result.CanApplyReplacement(SearchSnapshot(), FindBox.Text)) return;
        var current = _searchCurrent;
        if (!all && (current < 0 || EditorBox.SelectionStart != result.Starts[current] || EditorBox.SelectionLength != result.Query.Length)) return;
        var replacement = ReplaceBox.Text;
        using var cancellation = new CancellationTokenSource();
        _replacementCancellation = cancellation;
        _replaceError = null;
        _replaceBusy = true;
        UpdateSearchPresentation();
        try
        {
            var edit = await Task.Run(() => TextSearch.Replace(result, replacement, current, all, cancellation.Token));
            if (edit is null || cancellation.IsCancellationRequested || !CanReplaceText ||
                !result.CanApplyReplacement(SearchSnapshot(), FindBox.Text)) return;
            // Selection may have moved without changing the text while the worker computed this edit.
            if (!all && (EditorBox.SelectionStart != result.Starts[current] || EditorBox.SelectionLength != result.Query.Length)) return;
            _selectingSearchMatch = true;
            try
            {
                EditorBox.SelectionStart = edit.Start;
                EditorBox.SelectionLength = edit.Length;
                EditorBox.SelectedText = edit.Text;
                EditorBox.SelectionStart = Math.Min(edit.Start + edit.Text.Length, EditorText.Length);
                EditorBox.SelectionLength = 0;
            }
            finally { _selectingSearchMatch = false; }
            // TextChanging invalidated the cache synchronously. Preserve the existing dirty/save machinery.
            EditorTextChangedCore();
            SettlePendingDirtyCheck();
        }
        catch (OperationCanceledException) { }
        catch (Exception error) { _replaceError = "Could not replace text. " + error.Message; }
        finally
        {
            if (ReferenceEquals(_replacementCancellation, cancellation)) _replacementCancellation = null;
            _replaceBusy = false;
            if (_searchLoaded) UpdateSearchPresentation();
        }
    }
}
