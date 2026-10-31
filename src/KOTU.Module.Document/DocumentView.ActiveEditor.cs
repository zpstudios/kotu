using KOTU.DocumentModel;
using Microsoft.UI.Xaml;

namespace KOTU.Module.Document;

public sealed partial class DocumentView
{
    // 본문 소유자는 하나다. 숨은 TextBox의 선택/포커스/치환을 가상 본문에 적용하지 않는다.
    private bool IsActiveEditorVisible => !_renderMode && !_htmlMode &&
        (_pdfPane is not { Visibility: Visibility.Visible }) &&
        (_virtualEditor is { } pane ? pane.Visibility == Visibility.Visible : EditorBox.Visibility == Visibility.Visible);

    private bool IsActiveEditorAvailable => IsActiveEditorVisible &&
        (_virtualEditor is null || _virtualEditor is { IsReady: true, IsFailed: false });

    private bool IsActiveEditorReadOnly => _truncated || _capped || _viewMode ||
        (_virtualEditor is { } pane ? pane.IsFrozen || !pane.IsReady || pane.IsFailed : EditorBox.IsReadOnly);

    private int ActiveSelectionStart => _virtualEditor is { } pane
        ? Math.Min(pane.Buffer.Selection.Anchor, pane.Buffer.Selection.Position) : EditorBox.SelectionStart;

    private int ActiveSelectionLength => _virtualEditor is { } pane
        ? Math.Abs(pane.Buffer.Selection.Position - pane.Buffer.Selection.Anchor) : EditorBox.SelectionLength;

    private async Task FocusActiveEditorAsync()
    {
        if (!IsActiveEditorAvailable) return;
        if (_virtualEditor is not { } pane) { EditorBox.Focus(FocusState.Programmatic); return; }
        try { await pane.FocusEditorAsync(); }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            if (ReferenceEquals(pane, _virtualEditor) && !_contentReleased && IsLoaded && pane.IsLoaded &&
                XamlRoot is { } root && ReferenceEquals(pane.XamlRoot, root) && IsActiveEditorAvailable)
                await ShowMessageAsync("Editor focus unavailable", ex.Message);
        }
    }

    private async Task SelectActiveEditorAsync(int anchor, int position, int version)
    {
        if (!IsActiveEditorAvailable || _editorComposing) return;
        if (_virtualEditor is { } pane)
        {
            await pane.SelectAsync(anchor, position, reveal: true, version);
            return;
        }
        EditorBox.SelectionStart = Math.Min(anchor, position);
        EditorBox.SelectionLength = Math.Abs(position - anchor);
        BringSearchMatchIntoView(Math.Min(anchor, position));
    }

    private async Task ReplaceActiveEditorAsync(TextReplacement edit, int version, EditorSelection? expectedSelection)
    {
        if (!IsActiveEditorAvailable || IsActiveEditorReadOnly || _editorComposing)
            throw new InvalidOperationException("The document is not available for editing.");
        if (_virtualEditor is { } pane)
        {
            var replacement = VirtualDocumentBuffer.Normalize(edit.Text);
            await pane.ReplaceAsync([new(edit.Start, edit.Length, replacement)], version,
                selectionEnd: edit.Start + replacement.Length, expectedSelection: expectedSelection);
            return;
        }
        EditorBox.SelectionStart = edit.Start;
        EditorBox.SelectionLength = edit.Length;
        EditorBox.SelectedText = edit.Text;
        EditorBox.SelectionStart = Math.Min(edit.Start + edit.Text.Length, EditorText.Length);
        EditorBox.SelectionLength = 0;
        EditorTextChangedCore();
    }
}
