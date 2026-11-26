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

internal sealed record ReadOnlySearchSummary(int Count, bool Limited = false, string? Notice = null);

internal interface IReadOnlySearchSource
{
    Task<ReadOnlySearchSummary> SearchAsync(string query, CancellationToken cancellation);
    Task SelectMatchAsync(int index, CancellationToken cancellation);
    void ClearSearch();
    void FocusDocument();
}

/// <summary>A438: shared read-only find UI. One active operation and one latest request per view.</summary>
public sealed partial class ReadOnlyFindBar : UserControl
{
    private FrameworkElement? _owner;
    private FrameworkElement? _bar;
    private IReadOnlySearchSource? _source;
    private CancellationTokenSource? _searchCancellation;
    private CancellationTokenSource? _selectionCancellation;
    private long _revision;
    private long _selectionRevision;
    private bool _pending;
    private bool _running;
    private bool _selectionPending;
    private bool _selectionRunning;
    private bool _suspended;
    private bool _composing;
    private bool _flyoutOpen;
    private bool _interaction;
    private bool _escapePending;
    private bool _returnAfterClose;
    private UIElement? _presenter;
    private ReadOnlySearchSummary? _summary;
    private int _current = -1;
    private bool _previousOnResult;
    private string? _error;
    private bool _initialized;
    private bool _selectOnResult;

    public ReadOnlyFindBar()
    {
        InitializeComponent();
        _initialized = true;
        // Synchronously reject old results, but do not mutate the visual tree inside TextChanging.
        QueryBox.TextChanging += (_, _) => Invalidate(clearSource: false);
        QueryBox.TextCompositionStarted += (_, _) => { _composing = true; Invalidate(); };
        QueryBox.TextCompositionEnded += (_, _) => { _composing = false; QueueSearch(); };
        Unloaded += (_, _) => Suspend();
        Loaded += (_, _) => Resume();
    }

    internal void Bind(FrameworkElement owner, FrameworkElement bar)
    {
        _owner = owner;
        _bar = bar;
        owner.AddHandler(PointerPressedEvent, new PointerEventHandler(OnBodyPointer), true);
        owner.AddHandler(PointerWheelChangedEvent, new PointerEventHandler(OnBodyPointer), true);
    }

    internal void SetSource(IReadOnlySearchSource? source)
    {
        if (ReferenceEquals(_source, source)) return;
        Invalidate();
        _source = source;
        OptionsFlyout.Hide();
        QueryBox.IsEnabled = OptionsButton.IsEnabled = source is not null;
        _error = null;
        QueueSearch();
    }

    internal void SetLayout(double width, bool filteringNames = false)
    {
        QueryBox.Width = width < 700 ? 120 : 180;
        InlineNavigation.Visibility = width >= 1100 ? Visibility.Visible : Visibility.Collapsed;
        Opacity = filteringNames ? 0 : 1;
        IsHitTestVisible = !filteringNames;
        QueryBox.IsTabStop = !filteringNames;
        OptionsButton.IsTabStop = !filteringNames;
    }

    internal void Suspend()
    {
        _suspended = true;
        _pending = false;
        Invalidate();
        _interaction = _escapePending = _returnAfterClose = false;
        OptionsFlyout.Hide();
        DetachPresenter();
    }

    internal void Resume()
    {
        if (!_suspended) return;
        _suspended = false;
        QueueSearch();
    }

    private void Invalidate(bool clearSource = true)
    {
        _revision++;
        _selectionRevision++;
        _searchCancellation?.Cancel();
        _selectionCancellation?.Cancel();
        _selectionPending = false;
        _summary = null;
        _current = -1;
        if (clearSource) _source?.ClearSearch();
    }

    private void OnQueryChanged(object sender, TextChangedEventArgs args)
    {
        if (!_initialized) return;
        _previousOnResult = false;
        QueueSearch();
    }

    private void QueueSearch()
    {
        Invalidate();
        _error = null;
        _selectOnResult = true;
        _pending = !_suspended && !_composing && _source is not null && QueryBox.Text.Length > 0;
        UpdatePresentation();
        if (_pending && !_running) _ = RunSearchAsync();
    }

    private async Task RunSearchAsync()
    {
        _running = true;
        try
        {
            while (_pending && !_suspended)
            {
                _pending = false;
                var source = _source;
                var query = QueryBox.Text;
                var revision = _revision;
                using var cancel = new CancellationTokenSource();
                _searchCancellation = cancel;
                try
                {
                    await Task.Delay(150, cancel.Token);
                    if (source is null) continue;
                    var result = await source.SearchAsync(query, cancel.Token);
                    if (cancel.IsCancellationRequested || revision != _revision || !ReferenceEquals(source, _source) || _suspended) continue;
                    _summary = result;
                    _current = result.Count > 0 ? (_previousOnResult ? result.Count - 1 : 0) : -1;
                    _previousOnResult = false;
                    if (_selectOnResult) QueueSelection();
                    UpdatePresentation();
                }
                catch (OperationCanceledException) { }
                catch (Exception)
                {
                    if (revision == _revision && !_suspended)
                    {
                        _error = "Search unavailable for this document.";
                        UpdatePresentation();
                    }
                }
                finally { if (ReferenceEquals(_searchCancellation, cancel)) _searchCancellation = null; }
            }
        }
        finally { _running = false; }
    }

    private void Navigate(bool previous)
    {
        if (_composing || _source is null || QueryBox.Text.Length == 0) return;
        _selectOnResult = true;
        if (_summary is not { Count: > 0 } summary)
        {
            _previousOnResult = previous;
            if (_summary is null && !_running) QueueSearch();
            return;
        }
        _current = previous ? (_current <= 0 ? summary.Count - 1 : _current - 1) : (_current + 1) % summary.Count;
        QueueSelection();
        UpdatePresentation();
    }

    private void QueueSelection()
    {
        _selectionRevision++;
        _selectionCancellation?.Cancel();
        _selectionPending = _current >= 0;
        if (_selectionPending && !_selectionRunning) _ = RunSelectionAsync();
    }

    private async Task RunSelectionAsync()
    {
        _selectionRunning = true;
        try
        {
            while (_selectionPending && !_suspended)
            {
                _selectionPending = false;
                var source = _source;
                var index = _current;
                var revision = _selectionRevision;
                using var cancel = new CancellationTokenSource();
                _selectionCancellation = cancel;
                try
                {
                    if (source is not null) await source.SelectMatchAsync(index, cancel.Token);
                }
                catch (OperationCanceledException) { }
                catch (Exception)
                {
                    if (revision == _selectionRevision && !_suspended)
                    {
                        _error = "Unable to show this match.";
                        UpdatePresentation();
                    }
                }
                finally { if (ReferenceEquals(_selectionCancellation, cancel)) _selectionCancellation = null; }
            }
        }
        finally { _selectionRunning = false; }
    }

    private void UpdatePresentation()
    {
        var count = _summary?.Count ?? 0;
        var suffix = _summary?.Limited == true ? "+" : string.Empty;
        var label = _error ?? (_source is null ? "Text search unavailable" : QueryBox.Text.Length == 0 ? string.Empty :
            _summary is null ? "Searching..." : count == 0 ? _summary.Notice ?? "No matches" : $"{_current + 1:N0} / {count:N0}{suffix}");
        CountText.Text = label;
        CountText.Visibility = label.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
        DetailText.Text = string.Join(" · ", new[] { label, _summary?.Notice, "Literal, ignore case" }.Where(s => !string.IsNullOrEmpty(s)).Distinct());
        ToolTipService.SetToolTip(OptionsButton, "Find options (Ctrl+H) · " + DetailText.Text);
        var canNavigate = count > 0 && _error is null;
        // 결과가 사라져도 팝업 안에 초점을 남긴 뒤 탐색 버튼을 비활성화한다.
        if (!canNavigate && _flyoutOpen && XamlRoot is { } root &&
            FocusManager.GetFocusedElement(root) is DependencyObject focused &&
            (Within(focused, PopupPrevious) || Within(focused, PopupNext)))
            PopupClose.Focus(FocusState.Programmatic);
        PreviousButton.IsEnabled = NextButton.IsEnabled = PopupPrevious.IsEnabled = PopupNext.IsEnabled = canNavigate;
    }

    internal bool TryCommand(TextSearchCommand command, bool fromEnter = false, bool repeat = false,
        bool fromBody = false, object? focusScope = null, object? neutralFocus = null)
    {
        if (command == TextSearchCommand.EscapeReleased) { _escapePending = false; return false; }
        if (_source is null || _suspended || XamlRoot is not { } root || Opacity == 0) return false;
        if (HasOtherPopup(root)) return false;
        if (command == TextSearchCommand.ReturnToDocument && _escapePending) return true;
        var focused = FocusManager.GetFocusedElement(root) as DependencyObject;
        if (IsPassThrough(focused)) return false;
        var field = ReferenceEquals(focused, QueryBox);
        var inSearch = Within(focused, this) || Within(focused, OptionsPanel) || Within(focused, _presenter);
        // 중립 호스트는 검색을 여는 키만 허용한다. 형제 입력이나 임의 조상의 초점까지 넓히지 않는다.
        var neutralEntry = (command is TextSearchCommand.Find or TextSearchCommand.Replace) &&
            (focused is null || (focusScope is DependencyObject scope && ReferenceEquals(focused, scope) && Within(_owner, scope)) ||
                (neutralFocus is FrameworkElement neutral && ReferenceEquals(focused, neutral) && Within(neutral, root.Content)));
        if (!fromBody && !inSearch && !neutralEntry && !Within(focused, _owner) && !Within(focused, _bar)) return false;
        if (HasOtherTextInput(focused)) return false;
        if (_composing || (fromEnter && !field)) return false;
        if (command == TextSearchCommand.ReturnToDocument && !inSearch && !_interaction && !_flyoutOpen) return false;
        if (repeat) return true;
        switch (command)
        {
            case TextSearchCommand.Find:
                _interaction = true;
                OptionsFlyout.Hide();
                QueryBox.Focus(FocusState.Programmatic);
                QueryBox.SelectAll();
                break;
            case TextSearchCommand.Replace:
                _interaction = true;
                OptionsFlyout.ShowAt(OptionsButton);
                break;
            case TextSearchCommand.Next: Navigate(false); break;
            case TextSearchCommand.Previous: Navigate(true); break;
            case TextSearchCommand.ReturnToDocument:
                _escapePending = true;
                _interaction = false;
                _returnAfterClose = _flyoutOpen;
                OptionsFlyout.Hide();
                _source.FocusDocument();
                break;
        }
        return true;
    }

    private static bool Within(DependencyObject? element, DependencyObject? ancestor)
    {
        if (ancestor is null) return false;
        for (var depth = 0; element is not null && depth < 64; depth++, element = VisualTreeHelper.GetParent(element))
            if (ReferenceEquals(element, ancestor)) return true;
        return false;
    }

    private static bool IsPassThrough(DependencyObject? element)
    {
        for (var depth = 0; element is not null && depth < 64; depth++, element = VisualTreeHelper.GetParent(element))
            if (element is FrameworkElement frame && Equals(frame.Tag, HotkeySupport.PassThroughTag)) return true;
        return false;
    }

    private bool HasOtherTextInput(DependencyObject? element)
    {
        for (var depth = 0; element is not null && depth < 64; depth++, element = VisualTreeHelper.GetParent(element))
        {
            if (ReferenceEquals(element, QueryBox)) return false;
            if (element is TextBox or RichEditBox or PasswordBox) return true;
        }
        return false;
    }

    private bool HasOtherPopup(XamlRoot root)
    {
        foreach (var popup in VisualTreeHelper.GetOpenPopupsForXamlRoot(root))
            if (popup.Child is not ToolTip && !(_flyoutOpen && Within(OptionsPanel, popup.Child))) return true;
        return false;
    }

    private static bool Down(VirtualKey key) => Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(key)
        .HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down);

    private void OnKeyDown(object sender, KeyRoutedEventArgs args)
    {
        if (args.Handled || Down(VirtualKey.Menu)) return;
        var control = Down(VirtualKey.Control);
        var shift = Down(VirtualKey.Shift);
        TextSearchCommand? command = args.Key switch
        {
            VirtualKey.F when control && !shift => TextSearchCommand.Find,
            VirtualKey.H when control && !shift => TextSearchCommand.Replace,
            VirtualKey.F3 when !control => shift ? TextSearchCommand.Previous : TextSearchCommand.Next,
            VirtualKey.Enter when !control => shift ? TextSearchCommand.Previous : TextSearchCommand.Next,
            VirtualKey.Escape when !control && !shift => TextSearchCommand.ReturnToDocument,
            _ => null
        };
        if (command is { } value && TryCommand(value, args.Key == VirtualKey.Enter, args.KeyStatus.WasKeyDown)) args.Handled = true;
    }

    private void OnKeyUp(object sender, KeyRoutedEventArgs args)
    {
        if (args.Key == VirtualKey.Escape) _escapePending = false;
    }

    private void OnBodyPointer(object sender, PointerRoutedEventArgs args)
    {
        if (!Within(args.OriginalSource as DependencyObject, this))
            ObserveBodyInteraction();
    }

    internal void ObserveBodyInteraction()
    {
        _interaction = false;
        _selectOnResult = false;
        _selectionRevision++;
        _selectionPending = false;
        _selectionCancellation?.Cancel();
    }
    private void OnPrevious(object sender, RoutedEventArgs args) => Navigate(true);
    private void OnNext(object sender, RoutedEventArgs args) => Navigate(false);
    private void OnCloseOptions(object sender, RoutedEventArgs args)
    {
        _interaction = false;
        _returnAfterClose = true;
        OptionsFlyout.Hide();
    }
    private void OnFlyoutOpened(object sender, object args)
    {
        _flyoutOpen = _interaction = true;
        _returnAfterClose = false;
        DetachPresenter();
        if (XamlRoot is { } root)
        {
            _presenter = VisualTreeHelper.GetOpenPopupsForXamlRoot(root).Select(p => p.Child).FirstOrDefault(c => Within(OptionsPanel, c));
            if (_presenter is { } presenter)
            {
                presenter.PreviewKeyDown += OnKeyDown;
                presenter.AddHandler(KeyUpEvent, new KeyEventHandler(OnKeyUp), true);
            }
        }
        // 빈 검색·검색 중·실패에는 탐색 버튼이 모두 꺼져 있어도 안내를 읽을 수 있어야 한다.
        if (!PopupPrevious.IsEnabled && !PopupNext.IsEnabled)
            PopupClose.Focus(FocusState.Programmatic);
    }
    private void OnFlyoutClosing(FlyoutBase sender, FlyoutBaseClosingEventArgs args)
    {
        if (_interaction && Down(VirtualKey.Escape))
        {
            _escapePending = true;
            _returnAfterClose = true;
            _interaction = false;
        }
    }
    private void OnFlyoutClosed(object sender, object args)
    {
        _flyoutOpen = false;
        DetachPresenter();
        if (_returnAfterClose && !_suspended) _source?.FocusDocument();
        _returnAfterClose = false;
    }
    private void DetachPresenter()
    {
        if (_presenter is not { } presenter) return;
        presenter.PreviewKeyDown -= OnKeyDown;
        presenter.RemoveHandler(KeyUpEvent, new KeyEventHandler(OnKeyUp));
        _presenter = null;
    }
}
