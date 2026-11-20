using KOTU.Input;
using KOTU.Ui;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.System;

namespace KOTU.Module.Document;

public sealed partial class DocumentFitControls : UserControl
{
    private const VirtualKey FitKey = VirtualKey.F;
    private const VirtualKey OriginalKey = VirtualKey.A;
    internal event Action<PdfFitMode>? Selected;
    internal event Action? Reapplied;

    public DocumentFitControls()
    {
        InitializeComponent();
        FitIcons.ConfigureMenu(OriginalItem, ContainItem, WidthItem, HeightItem);
        Show(null, allowHeight: false);
    }

    internal void RegisterHotkeys(UIElement scope)
    {
        HotkeySupport.Register(scope, FitButton, FitKey, () => Reapplied?.Invoke());
        HotkeySupport.Register(scope, FitButton, OriginalKey, () => Selected?.Invoke(PdfFitMode.ActualSize));
    }

    internal void Show(PdfFitMode? mode, bool allowHeight)
    {
        FitButton.IsEnabled = FitOptionsButton.IsEnabled = mode.HasValue;
        HeightItem.IsEnabled = allowHeight;
        var (kind, tip) = mode switch
        {
            PdfFitMode.ActualSize => (ToolbarIconKind.OriginalRatio, "Original size"),
            PdfFitMode.FitWidth => (ToolbarIconKind.FitWidth, "Fit width"),
            PdfFitMode.FitHeight => (ToolbarIconKind.FitHeight, "Fit height"),
            _ => (ToolbarIconKind.FitContain, "Contain - the whole page fits, never enlarged"),
        };
        FitButton.Content = ToolbarIcons.Build(kind);
        ToolTipService.SetToolTip(FitButton, mode.HasValue
            ? $"{HotkeySupport.Tip(tip, FitKey)} · {HotkeySupport.Tip("Original", OriginalKey)}"
            : "No document open");
        ToolTipService.SetToolTip(FitOptionsButton, mode.HasValue ? "Fit options" : "No document open");
    }

    private void Reapply(object sender, RoutedEventArgs e) => Reapplied?.Invoke();

    private void Choose(object sender, RoutedEventArgs e) => Selected?.Invoke(
        sender == OriginalItem ? PdfFitMode.ActualSize :
        sender == WidthItem ? PdfFitMode.FitWidth :
        sender == HeightItem ? PdfFitMode.FitHeight : PdfFitMode.Contain);
}
