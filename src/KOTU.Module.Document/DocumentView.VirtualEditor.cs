using KOTU.DocumentModel;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using KOTU.Core.Contracts;

namespace KOTU.Module.Document;

public sealed partial class DocumentView : IContentLifetime, IEditorEscapeSource
{
    public event Action? EditorEscapeRequested;
    private VirtualEditorPane? _virtualEditor;
    private bool _virtualCloseInProgress;
    private bool _virtualSaveBarrierInProgress;
    private bool _viewTransitionInProgress;
    private bool _contentReleased;
    private bool _nativeRecovery;
    private object? _readyFocusOrigin;
    private bool _readyFocusRequested;
    private Button? _recoverEditorButton;
    private bool _recoveryInProgress;
    private sealed class CloseAttempt(VirtualEditorPane pane)
    {
        public VirtualEditorPane Pane { get; } = pane;
        public long? FreezeOwner { get; set; }
        public bool Started { get; set; }
    }
    private CloseAttempt? _closeAttempt;
    private bool VirtualEditorGate => !KOTU.Core.Integration.DistributionPolicy.IsStandalone &&
        Environment.GetEnvironmentVariable("KOTU_NATIVE_TEXT_EDITOR") != "1";

    private async void TryOpenVirtualEditor()
    {
        if (!VirtualEditorGate || _contentReleased || _nativeRecovery || _renderMode || _htmlMode ||
            _capped || _truncated || _virtualEditor is not null || (_path is null && !_untitled)) return;
        var seq = _openSeq;
        var pane = new VirtualEditorPane(NormalizeNewlines(EditorText));
        long selectionGeneration = 0;
        _virtualEditor = pane;
        _editorFocusPending = false;
        _readyFocusRequested = !_viewMode;
        _readyFocusOrigin = XamlRoot is { } root ? FocusManager.GetFocusedElement(root) : null;
        pane.Changed += changes =>
        {
            if (!ReferenceEquals(pane, _virtualEditor)) return;
            _textRevision++;
            _textSnapshot = null;
            QueueTextSearch();
            // 델타는 원본 좌표 내림차순이므로 기존 변경 폭 병합을 그대로 재사용한다.
            foreach (var change in changes) MergeEdit(change.Offset, change.Length, change.Text.Length);
            UpdateChangedText();
            if (pane.Buffer.Length != _documentSession.BaselineText.Length)
            { _dirtyTimer?.Stop(); SetDirty(true); }
            else { DirtyTimer.Stop(); DirtyTimer.Start(); }
        };
        pane.StateChanged += () =>
        {
            if (!ReferenceEquals(pane, _virtualEditor)) return;
            if (selectionGeneration != pane.SelectionGeneration)
            {
                selectionGeneration = pane.SelectionGeneration;
                _searchSelectionRequests.Cancel();
            }
            var wasComposing = _editorComposing;
            _editorComposing = pane.Buffer.IsComposing;
            if (wasComposing != _editorComposing)
            {
                if (_editorComposing) { InvalidateTextSearch(); UpdateSearchPresentation(); }
                else QueueTextSearch();
            }
            UpdateTextPosition();
            SynchronizeSearchSelection();
        };
        pane.Shortcut += key => { if (ReferenceEquals(pane, _virtualEditor)) OnVirtualShortcut(key); };
        pane.ActionFailed += reason => { if (ReferenceEquals(pane, _virtualEditor)) _ = ShowMessageAsync("Could not edit text", reason); };
        pane.Failed += reason =>
        {
            if (!ReferenceEquals(pane, _virtualEditor)) return;
            HandleVirtualFailure(pane);
        };
        pane.ZoomRequested += steps => { if (ReferenceEquals(pane, _virtualEditor)) RequestVirtualZoom(steps); };
        SubscribeVirtualAppearance();
        if (!ReferenceEquals(pane, _virtualEditor) || pane.IsFailed) return;
        object visual;
        try { visual = GetVirtualVisualOptions(); }
        catch (Exception ex) { FailVirtualAppearance(ex); return; }
        RootGrid.Children.Insert(0, pane);
        EditorBox.Visibility = Visibility.Collapsed;
        _decor.Suspend();
        UpdateSearchAvailability();
        UpdateEditorActions();
        var ok = await pane.LoadAsync(EditorBox.IsReadOnly, visual);
        if (seq != _openSeq || !ReferenceEquals(pane, _virtualEditor)) { pane.Close(); return; }
        if (ok)
        {
            RefreshActiveEditorSurface();
            QueueTextSearch();
            CompleteReadyFocus();
            return;
        }
        if (pane.HasAcceptedEdits || pane.HasActivated) return;
        CloseVirtualEditor();
        EditorBox.Visibility = _renderMode || _htmlMode ? Visibility.Collapsed : Visibility.Visible;
        _decor.Resume();
        _decor.Invalidate();
        UpdateSearchAvailability();
        UpdateDecorToggles();
        UpdateViewToggle();
        CompleteReadyFocus();
    }

    private void OnVirtualShortcut(string key)
    {
        if (key == "escape-released") { TryTextSearchCommand(TextSearchCommand.EscapeReleased); return; }
        if (_virtualEditor is not { IsReady: true, IsFailed: false } pane || pane.Buffer.IsComposing ||
            !IsActiveEditorAvailable || _contentReleased || !IsLoaded || XamlRoot is null || !pane.IsLoaded) return;
        if (key == "body-interaction") { _searchInteractionActive = _searchFlyoutDismissed = false; return; }
        switch (key)
        {
            case "focus-previous": MoveVirtualFocus(FocusNavigationDirection.Previous); break;
            case "focus-next": MoveVirtualFocus(FocusNavigationDirection.Next); break;
            case "ctrl+s": _ = SaveAsync(); break;
            case "ctrl+f": TryTextSearchCommand(TextSearchCommand.Find); break;
            case "ctrl+h": TryTextSearchCommand(TextSearchCommand.Replace); break;
            case "ctrl+p": if (CanPrintNow) PrintRequested?.Invoke(); break;
            case "next": TryTextSearchCommand(TextSearchCommand.Next); break;
            case "previous": TryTextSearchCommand(TextSearchCommand.Previous); break;
            case "zoom-in": RequestVirtualZoom(1); break;
            case "zoom-out": RequestVirtualZoom(-1); break;
            case "zoom-original": RequestVirtualZoom(0, original: true); break;
            case "escape":
                if (!TryTextSearchCommand(TextSearchCommand.ReturnToDocument)) EditorEscapeRequested?.Invoke();
                break;
        }
    }

    private void MoveVirtualFocus(FocusNavigationDirection direction)
    {
        if (XamlRoot is { } root && !FocusManager.TryMoveFocus(direction, new FindNextElementOptions { SearchRoot = root.Content }))
            FindBox.Focus(FocusState.Keyboard);
    }

    private void RefreshActiveEditorSurface()
    {
        if (_contentReleased) return;
        var visible = !_renderMode && !_htmlMode && !IsTruncatedHtml &&
            (_path is not null || _untitled) && _pdfPane is not { Visibility: Visibility.Visible };
        EditorBox.Visibility = visible && _virtualEditor is null ? Visibility.Visible : Visibility.Collapsed;
        if (_virtualEditor is { } pane) pane.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
        if (_virtualEditor is null) _decor.Resume(); else _decor.Suspend();
        QueueVirtualAppearance();
        UpdateZoomText();
        if (!IsPdfFitBranch) ShowTextFitState();
        UpdateDecorToggles();
        UpdateViewToggle();
        UpdatePrintButton();
        UpdateSearchAvailability();
        UpdateEditorActions();
    }

    private void UpdateEditorActions()
    {
        UpdateSaveAvailability();
        UpdateZoomText();
        if (!IsPdfFitBranch) ShowTextFitState();
        UpdateDecorToggles();
        UpdateViewToggle();
        UpdatePrintButton();
    }

    private void UpdateSaveAvailability()
    {
        SaveButton.IsEnabled = _dirty && !_contentReleased && !_viewTransitionInProgress &&
            (_virtualEditor is null || _virtualEditor is { IsReady: true, IsFailed: false });
    }

    private void CompleteReadyFocus()
    {
        if (!_readyFocusRequested || !IsLoaded || _contentReleased || _viewMode || !IsActiveEditorAvailable) return;
        _readyFocusRequested = false;
        var focused = XamlRoot is { } root ? FocusManager.GetFocusedElement(root) : null;
        if (focused is null || ReferenceEquals(focused, this) || ReferenceEquals(focused, EditorBox) ||
            ReferenceEquals(focused, _readyFocusOrigin)) _ = FocusActiveEditorAsync();
    }

    private async Task ChangeViewModeAsync()
    {
        if (!CanToggleViewMode) return;
        var seq = _openSeq;
        var pane = _virtualEditor;
        var viewing = !_viewMode;
        _viewTransitionInProgress = true;
        UpdateEditorActions();
        UpdateSearchAvailability();
        try
        {
            if (pane is not null)
                await EditorModeTransition.PrepareAsync(_viewMode, viewing,
                    async value => { await pane.OptionsAsync(new { readOnly = value }); },
                    async () => { await CaptureVirtualSnapshotAsync(false); },
                    () => !_contentReleased && seq == _openSeq && ReferenceEquals(pane, _virtualEditor),
                    ex => pane.MarkFailed("Editing could not resume. " + ex.Message));
            if (_contentReleased || seq != _openSeq || !ReferenceEquals(pane, _virtualEditor)) return;
            if (viewing) EnterViewMode(); else ExitViewMode();
            RefreshActiveEditorSurface();
            if (!viewing)
            {
                TryOpenVirtualEditor();
                FocusEditorForEditing();
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { await ShowMessageAsync("Could not switch view", ex.Message); }
        finally
        {
            _viewTransitionInProgress = false;
            UpdateEditorActions();
            UpdateSearchAvailability();
        }
    }

    private void HandleVirtualFailure(VirtualEditorPane pane)
    {
        if (!ReferenceEquals(pane, _virtualEditor) || _contentReleased) return;
        if (!pane.HasAcceptedEdits && !pane.HasActivated)
        {
            CloseVirtualEditor();
            _nativeRecovery = true;
            RefreshActiveEditorSurface();
            CompleteReadyFocus();
            return;
        }
        SetDirty(true);
        UpdateSearchAvailability();
        UpdateViewToggle();
        UpdatePrintButton();
        UpdateEditorActions();
        FileNameText.Text = "Editor unavailable — recover the received text to continue.";
        if (_recoverEditorButton is null)
        {
            _recoverEditorButton = new Button
            {
                Content = "Recover received text", HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            };
            _recoverEditorButton.Click += OnRecoverEditorClick;
            RootGrid.Children.Add(_recoverEditorButton);
        }
    }

    private async void OnRecoverEditorClick(object sender, RoutedEventArgs args)
    {
        if (_recoveryInProgress) return;
        _recoveryInProgress = true;
        if (_recoverEditorButton is { } button) button.IsEnabled = false;
        try { await RecoverVirtualEditorAsync(); }
        catch (Exception) { /* 다른 대화상자가 열려 있으면 복구 버튼으로 다시 시도할 수 있다. */ }
        finally
        {
            _recoveryInProgress = false;
            if (_recoverEditorButton is { } recovery) recovery.IsEnabled = true;
        }
    }

    private async Task RecoverVirtualEditorAsync()
    {
        if (_virtualEditor is not { IsFailed: true } pane || XamlRoot is null ||
            _documentSession.IsSaving || _virtualCloseInProgress || _virtualSaveBarrierInProgress || _viewTransitionInProgress) return;
        var seq = _openSeq;
        var dialog = new ContentDialog
        {
            Title = "Recover received text?",
            Content = "Continue editing the latest text received before the editor stopped. The last pending input may be missing. Undo history will be reset. Nothing will be saved until you choose Save.",
            PrimaryButtonText = "Recover", CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Close, XamlRoot = XamlRoot
        };
        if (await dialog.ShowAsync() != ContentDialogResult.Primary || _contentReleased || seq != _openSeq ||
            !ReferenceEquals(pane, _virtualEditor)) return;
        var text = pane.Buffer.Text;
        CloseVirtualEditor();
        _nativeRecovery = true;
        ResetRenderState(_renderEligible);
        _loadingText = true;
        try { SetEditorText(text); }
        finally { _loadingText = false; }
        _textSnapshot = null;
        _textRevision++;
        ResetChangeTracking();
        RecomputeDirty();
        if (_dirty) TrackEdit(_documentSession.BaselineText, NormalizeNewlines(EditorText));
        ClearHtmlFallbackNotice();
        FileNameText.Text = _path is { } path ? Path.GetFileName(path) : UntitledDisplayName;
        RefreshActiveEditorSurface();
        SearchDocumentReady();
        FocusEditorForEditing();
    }

    public void ReleaseContent()
    {
        if (_contentReleased) return;
        _contentReleased = true;
        _readyFocusRequested = false;
        CloseVirtualEditor();
        _openSeq++;
        _renderSeq++;
        _documentSession.Reset(_documentSession.BaselineText, _documentSession.DiskStamp);
        SuspendTextSearch();
        _worker?.Dispose();
        _worker = null;
        _dirtyTimer?.Stop();
        _performance?.Dispose();
        _performance = null;
        EditorDecorDiagnostics.Changed -= ApplyEditorDecorDiagnostics;
        _decor.Suspend();
        FlushPendingZoomApply();
        _renderSettleTimer?.Stop();
        _zoomSaveTimer?.Stop();
        FlushZoomSave();
        if (_pendingApplyHandler is { } handler)
        {
            Microsoft.UI.Xaml.Media.CompositionTarget.Rendering -= handler;
            _pendingApplyHandler = null;
        }
        StopRenderAppendLoop();
        CloseHtmlPane();
    }

    private void CloseVirtualEditor()
    {
        if (_virtualEditor is not { } pane) return;
        _searchSelectionRequests.Cancel();
        UnsubscribeVirtualAppearance();
        _virtualVisualQueued = false;
        _virtualEditor = null;
        _closeAttempt = null;
        RootGrid.Children.Remove(pane);
        pane.Close();
        _textSnapshot = null;
        _editorComposing = false;
        if (_recoverEditorButton is { } recovery)
        {
            recovery.Click -= OnRecoverEditorClick;
            RootGrid.Children.Remove(recovery);
            _recoverEditorButton = null;
        }
    }

    private async Task<EditorSnapshot?> CaptureVirtualSnapshotAsync(bool freeze)
    {
        if (_virtualEditor is not { } pane) return null;
        var seq = _openSeq;
        var snapshot = await pane.SnapshotAsync(freeze);
        if (seq != _openSeq || !ReferenceEquals(pane, _virtualEditor))
            throw new OperationCanceledException("The document changed while waiting for the editor.");
        RecomputeDirty();
        return snapshot;
    }

    private async Task<bool> ConfirmVirtualCloseAsync()
    {
        if (_virtualCloseInProgress || _virtualSaveBarrierInProgress || _viewTransitionInProgress || _documentSession.IsSaving) return false;
        var pane = _virtualEditor;
        if (pane is null) return false;
        var attempt = _closeAttempt is { Started: false } pending && ReferenceEquals(pending.Pane, pane)
            ? pending : new CloseAttempt(pane);
        attempt.Started = true;
        _closeAttempt = attempt;
        _virtualCloseInProgress = true;
        var accepted = false;
        try
        {
            // 깨끗한 문서도 먼저 브라우저 입력을 잠그고 앞선 입력 메시지까지 확정한다.
            await CaptureVirtualSnapshotAsync(freeze: true);
            attempt.FreezeOwner = pane.FreezeOwner;
            if (!_dirty) return accepted = true;
            return accepted = await ConfirmCloseCoreAsync();
        }
        catch (OperationCanceledException) { return false; }
        catch (Exception ex) { await ShowMessageAsync("Could not close document", ex.Message); return false; }
        finally
        {
            if (!accepted && ReferenceEquals(pane, _virtualEditor))
            {
                try { if (attempt.FreezeOwner is { } owner) await pane.ReleaseFreezeAsync(owner); }
                catch (Exception ex) { await ShowMessageAsync("Could not resume editing", ex.Message); }
            }
            _virtualCloseInProgress = false;
        }
    }

    public Func<Task> CaptureCloseCancellation()
    {
        var pane = _virtualEditor;
        var attempt = pane is null ? null : new CloseAttempt(pane);
        _closeAttempt = attempt;
        return async () =>
        {
            if (attempt is null || !ReferenceEquals(attempt.Pane, _virtualEditor) || attempt.FreezeOwner is not { } owner) return;
            try { await attempt.Pane.ReleaseFreezeAsync(owner); }
            catch (Exception ex) { await ShowMessageAsync("Could not resume editing", ex.Message); }
        };
    }
}
