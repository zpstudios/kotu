using System.Text.Json;
using KOTU.Core.Contracts;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;

namespace KOTU.Module.Document;

internal sealed partial class VirtualEditorPane
{
    public event Action<EditorPanelShortcut>? PanelShortcutRequested;

    public bool OwnsPanelShortcutFocus
    {
        get
        {
            if (!IsReady || _closed || _failed || !_surfaceLifetime.IsAttached || !IsLoaded ||
                Visibility != Visibility.Visible || Buffer.IsComposing || XamlRoot is not { } root ||
                _view is not { IsLoaded: true } view || !ReferenceEquals(Content, view) ||
                !ReferenceEquals(view.XamlRoot, root)) return false;
            for (var node = FocusManager.GetFocusedElement(root) as DependencyObject;
                node is not null; node = VisualTreeHelper.GetParent(node))
                if (ReferenceEquals(node, view)) return true;
            return false;
        }
    }

    private void HandlePanelShortcut(JsonElement message)
    {
        // ProcessMessage has already checked this surface's epoch. Recheck native focus at delivery.
        if (!OwnsPanelShortcutFocus || message.GetProperty("version").GetInt32() != Buffer.Version) return;
        EditorPanelSide? side = message.GetProperty("side").GetString() switch
        {
            "left" => EditorPanelSide.Left,
            "right" => EditorPanelSide.Right,
            _ => null
        };
        if (side is not { } value) return;
        PanelShortcutRequested?.Invoke(new(value, message.GetProperty("repeat").GetBoolean(),
            message.GetProperty("control").GetBoolean(), message.GetProperty("shift").GetBoolean(),
            message.GetProperty("alt").GetBoolean(), message.GetProperty("meta").GetBoolean()));
    }
}
