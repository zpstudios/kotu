using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace KOTU.Module.Document;

public sealed partial class HwpPageControls : UserControl
{
    private HwpPagePane? _pane;

    public HwpPageControls()
    {
        InitializeComponent();
        Fit.Selected += mode => _pane?.ApplyFit(mode);
        Fit.Reapplied += () => _pane?.ReapplyFit();
        Fit.RegisterHotkeys(this);
        RegisterPropertyChangedCallback(VisibilityProperty, (_, _) => Refresh());
    }

    internal void Attach(HwpPagePane pane)
    {
        Detach();
        _pane = pane;
        pane.StateChanged += Refresh;
        Refresh();
    }

    internal void Detach()
    {
        if (_pane is { } pane) pane.StateChanged -= Refresh;
        _pane = null;
        Refresh();
    }

    private void Refresh()
    {
        var pane = _pane;
        var ready = pane is { PageCount: > 0 } && Visibility == Visibility.Visible;
        Fit.Show(ready ? pane!.FitMode : null, allowHeight: ready);
        Position.Text = ready ? $"{pane!.PageIndex + 1} / {pane.PageCount}" : "";
        Previous.IsEnabled = ready && !pane!.IsPagePending && pane.PageIndex > 0;
        Next.IsEnabled = ready && !pane!.IsPagePending && pane.PageIndex + 1 < pane.PageCount;
    }

    private void PreviousClicked(object sender, RoutedEventArgs e) => _pane?.MovePage(-1);
    private void NextClicked(object sender, RoutedEventArgs e) => _pane?.MovePage(1);
}
