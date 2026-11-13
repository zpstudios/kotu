namespace KOTU.Core.Contracts;

/// <summary>A fullscreen-only request whose originating surface must still own input at delivery.</summary>
public sealed class EditorFullscreenShortcut(bool isRepeat, Func<bool> isCurrent)
{
    public bool IsRepeat { get; } = isRepeat;
    public bool IsCurrent => isCurrent();
    public EditorFullscreenShortcut WithLifetime(Func<bool> ownerIsCurrent) =>
        new(IsRepeat, () => ownerIsCurrent() && IsCurrent);
}

public interface IEditorFullscreenShortcutSource
{
    bool OwnsNativeFullscreenShortcutFocus { get; }
    event Action<EditorFullscreenShortcut>? EditorFullscreenShortcutRequested;
}
