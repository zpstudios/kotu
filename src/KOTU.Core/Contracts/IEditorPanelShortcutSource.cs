namespace KOTU.Core.Contracts;

public enum EditorPanelSide { Left, Right }

/// <summary>A panel key down from an editor that owns its native keyboard input.</summary>
public readonly record struct EditorPanelShortcut(EditorPanelSide Side, bool IsRepeat,
    bool Control, bool Shift, bool Alt, bool Meta);

public interface IEditorPanelShortcutSource
{
    event Action<EditorPanelShortcut>? EditorPanelShortcutRequested;
}
