using KOTU.Core.Appearance;
using Microsoft.UI.System;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.UI.ViewManagement;

namespace KOTU.App.Appearance;

/// <summary>
/// Isolated appearance scope. Live appearance walkers must skip this entire subtree (the Tag
/// carries Core's shared marker). Every instance renders its own immutable definition, never
/// DesignRuntime.Current. No application controls, files or players are placed in this tree.
/// </summary>
internal sealed class DesignPreviewRoot : UserControl
{
    private readonly DesignDefinition _design;
    private readonly Viewbox _viewport = new() { Stretch = Stretch.Uniform };
    private readonly AccessibilitySettings _accessibility = new();
    private ThemeSettings? _windowTheme;

    internal DesignPreviewRoot(DesignDefinition design)
    {
        _design = design;
        Tag = DesignRuntime.PreviewScopeMarker;
        IsHitTestVisible = false;
        IsTabStop = false;
        Content = _viewport;
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
        ActualThemeChanged += (_, _) => { if (IsLoaded) Refresh(); };
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (_windowTheme is null && XamlRoot?.ContentIslandEnvironment is { } environment)
        {
            _windowTheme = ThemeSettings.CreateForWindowId(environment.AppWindowId);
            _windowTheme.Changed += OnWindowThemeChanged;
        }
        Refresh();
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        if (_windowTheme is not null) _windowTheme.Changed -= OnWindowThemeChanged;
        _windowTheme = null;
    }

    private void OnWindowThemeChanged(ThemeSettings sender, object args)
    {
        DispatcherQueue.TryEnqueue(() =>
        {
            if (!IsLoaded || !ReferenceEquals(sender, _windowTheme)) return;
            AppDesignService.RefreshSystemColors();
            Refresh();
        });
    }

    private void Refresh() => _viewport.Child = DesignPreviewRenderer.Create(AppDesignService.PreviewDesign(_design),
        ActualTheme == ElementTheme.Dark, _windowTheme?.HighContrast ?? _accessibility.HighContrast);
}
