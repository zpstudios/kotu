using System.Text.Json;
using KOTU.Core.Contracts;

namespace KOTU.Module.Document;

internal sealed partial class VirtualEditorPane
{
    public event Action<EditorFullscreenShortcut>? FullscreenShortcutRequested;

    private void HandleFullscreenShortcut(JsonElement message)
    {
        var version = message.GetProperty("version").GetInt32();
        var attachment = _surfaceLifetime.AttachmentGeneration;
        if (!OwnsPanelShortcutFocus || IsFrozen || version != Buffer.Version ||
            message.GetProperty("control").GetBoolean() || message.GetProperty("shift").GetBoolean() ||
            !message.GetProperty("alt").GetBoolean() || message.GetProperty("meta").GetBoolean()) return;
        FullscreenShortcutRequested?.Invoke(new(message.GetProperty("repeat").GetBoolean(),
            () => OwnsPanelShortcutFocus && !IsFrozen && version == Buffer.Version &&
                attachment == _surfaceLifetime.AttachmentGeneration));
    }
}
