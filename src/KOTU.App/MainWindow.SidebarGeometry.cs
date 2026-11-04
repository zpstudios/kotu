using KOTU.Core.Content;
using KOTU.Core.Contracts;
using Microsoft.UI.Xaml;

namespace KOTU.App;

public sealed partial class MainWindow
{
    private bool _sidebarGeometryQueued;

    private void InitializeSidebarGeometry()
    {
        // Actual visibility can change after ApplyOverlayStates returns (folder probes), or when a
        // module replaces its panel provider. Coalesce those changes without restarting any probe.
        foreach (var surface in new FrameworkElement[] { ListOverlay, InfoOverlay, LeftPanelHost, RightPanelHost })
            surface.RegisterPropertyChangedCallback(UIElement.VisibilityProperty, (_, _) => QueueSidebarGeometry());
        ExplorerHost.Loaded += (_, _) => ApplySidebarGeometry();
        S4CenterHost.Loaded += (_, _) => ApplySidebarGeometry();
    }

    private void QueueSidebarGeometry()
    {
        if (_contentClosed || _sidebarGeometryQueued) return;
        _sidebarGeometryQueued = true;
        if (!DispatcherQueue.TryEnqueue(() =>
        {
            _sidebarGeometryQueued = false;
            if (!_contentClosed) ApplySidebarGeometry();
        })) _sidebarGeometryQueued = false;
    }

    // Geometry only: no Show/Hide, navigation, folder I/O, request generation, or focus changes.
    private void ApplySidebarGeometry()
    {
        if (_contentClosed) return;
        var geometry = SidebarGeometry.FromVisiblePanels(LeftPanelIsOpen, RightPanelIsOpen,
            S4SidebarsFloat, SidebarPercent);
        LeftDockColumn.Width = new GridLength(geometry.ContentLeftPercent, GridUnitType.Star);
        RightDockColumn.Width = new GridLength(geometry.ContentRightPercent, GridUnitType.Star);
        CenterColumn.Width = new GridLength(geometry.ContentCenterPercent, GridUnitType.Star);
        (ModuleHost.Content as ISidebarAwareView)?.SetSidebarsState(geometry.DockCount);
        _thumbnailExplorer?.SetColumns(geometry.ContentColumns);

        // S4's content can stay full width, while its separate browser always clears visible panels.
        if (_openFileBrowsing)
        {
            var browser = SidebarGeometry.FromVisiblePanels(ListOverlay.IsOpen, InfoOverlay.IsOpen,
                S4SidebarsFloat, SidebarPercent);
            S4LeftSpacer.Width = new GridLength(browser.LeftPercent, GridUnitType.Star);
            S4RightSpacer.Width = new GridLength(browser.RightPercent, GridUnitType.Star);
            S4CenterColumn.Width = new GridLength(browser.CenterPercent, GridUnitType.Star);
            _s4Explorer?.SetColumns(browser.ExplorerColumns);
        }
        UpdateEdgeButtons();
    }
}
