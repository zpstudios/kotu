using System.Text.Json;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Windows.ApplicationModel.DataTransfer;
using Windows.Foundation;

namespace KOTU.Module.Document;

internal sealed partial class VirtualEditorPane
{
    private MenuFlyout? _editMenu;

    private void ShowEditMenu(JsonElement message)
    {
        if (!IsReady || _closed || _failed || !_surfaceLifetime.IsAttached || XamlRoot is null ||
            Visibility != Visibility.Visible || Buffer.IsComposing || _view is not { } view) return;
        _editMenu?.Hide();
        var id = message.GetProperty("id").GetInt64();
        var selected = message.GetProperty("anchor").GetInt32() != message.GetProperty("position").GetInt32();
        var editable = message.GetProperty("editable").GetBoolean() && !IsFrozen;
        var canPaste = false;
        // 우클릭/메뉴 키로 요청한 시점에만 형식을 확인한다. 본문 읽기는 Paste 선택 후에 한다.
        try { canPaste = Clipboard.GetContent().Contains(StandardDataFormats.Text); }
        catch (Exception) { }
        var menu = new MenuFlyout();
        _editMenu = menu;
        string? chosenAction = null;
        menu.Closed += (_, _) =>
        {
            if (chosenAction is null || !ReferenceEquals(_editMenu, menu) || !ReferenceEquals(_view, view) ||
                !IsReady || _closed || _failed || !_surfaceLifetime.IsAttached ||
                XamlRoot is null || Visibility != Visibility.Visible) return;
            try
            {
                FocusNativeEditor();
                // The ordered edit command restores DOM focus before queuing the action. No await may
                // separate native focus and submission: a following Save must remain behind Paste/Cut.
                Post(new { kind = "edit-command", epoch = Buffer.Epoch, ticketId = id, action = chosenAction });
            }
            catch (OperationCanceledException) { }
            catch (Exception ex) { ActionFailed?.Invoke(ex.Message); }
        };
        void Add(string text, string action, bool enabled)
        {
            var item = new MenuFlyoutItem { Text = text, IsEnabled = enabled };
            item.Click += (_, _) =>
            {
                if (!IsReady || _closed || _failed) return;
                chosenAction = action;
            };
            menu.Items.Add(item);
        }
        Add("Undo", "undo", editable && message.GetProperty("canUndo").GetBoolean());
        Add("Redo", "redo", editable && message.GetProperty("canRedo").GetBoolean());
        menu.Items.Add(new MenuFlyoutSeparator());
        Add("Cut", "cut", editable && selected);
        Add("Copy", "copy", selected);
        Add("Paste", "paste", editable && canPaste);
        menu.Items.Add(new MenuFlyoutSeparator());
        Add("Select All", "selectAll", Buffer.Length > 0);
        // client 좌표는 변환 밖의 물리 DIP다. 저배율 잔여 확대를 다시 적용하지 않는다.
        var x = Math.Clamp(message.GetProperty("x").GetDouble(), 0, Math.Max(0, view.ActualWidth));
        var y = Math.Clamp(message.GetProperty("y").GetDouble(), 0, Math.Max(0, view.ActualHeight));
        try { menu.ShowAt(view, new FlyoutShowOptions { Position = new Point(x, y) }); }
        catch (Exception ex) { _editMenu = null; ActionFailed?.Invoke("The edit menu could not be opened. " + ex.Message); }
    }

    private async Task HandleClipboardAsync(JsonElement message)
    {
        var id = message.GetProperty("id").GetInt64();
        var version = message.GetProperty("version").GetInt32();
        var generation = message.GetProperty("selectionGeneration").GetInt64();
        var action = message.GetProperty("action").GetString();
        bool Current() => IsReady && !_closed && !_failed && _surfaceLifetime.IsAttached && XamlRoot is not null &&
            Visibility == Visibility.Visible && !Buffer.IsComposing &&
            Buffer.Version == version && SelectionGeneration == generation &&
            Buffer.Selection.Anchor == message.GetProperty("anchor").GetInt32() &&
            Buffer.Selection.Position == message.GetProperty("position").GetInt32();
        var completed = false;
        try
        {
            if (!Current() || action is not ("copy" or "cut" or "paste")) return;
            string? text = null;
            if (action == "paste")
            {
                var content = Clipboard.GetContent();
                if (!content.Contains(StandardDataFormats.Text)) return;
                text = await content.GetTextAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(3), _lifetime.Token);
                if (!Current()) return;
            }
            else
            {
                var package = new DataPackage { RequestedOperation = DataPackageOperation.Copy };
                package.SetText(message.GetProperty("text").GetString() ?? string.Empty);
                Clipboard.SetContent(package);
            }
            if (Current())
            {
                Post(new { kind = "clipboard-result", epoch = Buffer.Epoch, ticketId = id, ok = true, text });
                completed = true;
            }
        }
        catch (Exception ex)
        {
            if (!_closed && !_failed)
            {
                ActionFailed?.Invoke("The clipboard could not be accessed. " + ex.Message);
            }
        }
        finally
        {
            if (!completed && !_closed && !_failed)
                Post(new { kind = "clipboard-result", epoch = Buffer.Epoch, ticketId = id, ok = false });
        }
    }
}
