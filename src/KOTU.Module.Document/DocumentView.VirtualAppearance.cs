using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.System;
using Windows.UI;
using Windows.UI.ViewManagement;

namespace KOTU.Module.Document;

public sealed partial class DocumentView
{
    private readonly AccessibilitySettings _virtualAccessibility = new();
    private readonly UISettings _virtualColors = new();
    private ThemeSettings? _virtualWindowTheme;
    private bool _virtualThemeSubscribed;
    private long _virtualAppearanceEpoch;
    private bool _virtualVisualQueued;
    private bool _virtualVisualSending;

    private object GetVirtualVisualOptions()
    {
        var highContrast = _virtualWindowTheme?.HighContrast ?? _virtualAccessibility.HighContrast;
        var light = ActualTheme == ElementTheme.Light;
        var foreground = highContrast ? _virtualColors.UIElementColor(UIElementType.ButtonText) :
            EditorBox.Foreground is SolidColorBrush brush ? brush.Color : light ? Microsoft.UI.Colors.Black : Microsoft.UI.Colors.White;
        var background = highContrast ? _virtualColors.UIElementColor(UIElementType.ButtonFace) :
            light ? Microsoft.UI.Colors.White : Microsoft.UI.Colors.Transparent;
        var selection = highContrast ? _virtualColors.UIElementColor(UIElementType.Highlight) : (EditorBox.SelectionHighlightColor?.Color ?? _virtualColors.UIElementColor(UIElementType.Highlight));
        return new
        {
            zoomPercent = _zoomPercent, gutterDigits = _gutterDigits,
            // 큰 파일의 네이티브 장식 억제는 유지한다. 실험용 명시적 재정의만 예외다.
            decorEnabled = (_gutterDigits > 0 || _performance?.Options.Decor == 1) && _performance?.Options.Decor != 0,
            showGuides = _showGuides, showMarks = _showMarks, viewSuppressed = _viewMode,
            highContrast, light, foreground = CssColor(foreground), background = CssColor(background),
            selectionBackground = CssColor(selection),
            selectionForeground = CssColor(highContrast ? _virtualColors.UIElementColor(UIElementType.HighlightText) : Microsoft.UI.Colors.White)
        };
    }

    private static string CssColor(Color color) => $"#{color.R:X2}{color.G:X2}{color.B:X2}{color.A:X2}";

    private void SubscribeVirtualAppearance()
    {
        if (_virtualThemeSubscribed || _contentReleased || _virtualEditor is null || !IsLoaded ||
            XamlRoot?.ContentIslandEnvironment is not { } environment) return;
        ThemeSettings? windowTheme = null;
        var windowSubscribed = false;
        var colorsSubscribed = false;
        try
        {
            // 데스크톱 창에는 CoreWindow 기반 AccessibilitySettings 이벤트를 연결할 수 없다.
            windowTheme = ThemeSettings.CreateForWindowId(environment.AppWindowId);
            windowTheme.Changed += OnVirtualHighContrastChanged;
            windowSubscribed = true;
            _virtualColors.ColorValuesChanged += OnVirtualColorsChanged;
            colorsSubscribed = true;
            ActualThemeChanged += OnVirtualThemeChanged;
            _virtualWindowTheme = windowTheme;
            _virtualAppearanceEpoch++;
            _virtualThemeSubscribed = true;
        }
        catch (Exception ex)
        {
            // 연결이 끝나기 전에 실패해도 이미 붙은 이벤트만 정확히 해제한다.
            try { if (colorsSubscribed) _virtualColors.ColorValuesChanged -= OnVirtualColorsChanged; }
            finally { if (windowSubscribed) windowTheme!.Changed -= OnVirtualHighContrastChanged; }
            FailVirtualAppearance(ex);
        }
    }

    private void UnsubscribeVirtualAppearance()
    {
        if (!_virtualThemeSubscribed) return;
        _virtualThemeSubscribed = false;
        _virtualAppearanceEpoch++;
        var windowTheme = _virtualWindowTheme;
        _virtualWindowTheme = null;
        ActualThemeChanged -= OnVirtualThemeChanged;
        try { _virtualColors.ColorValuesChanged -= OnVirtualColorsChanged; }
        finally { if (windowTheme is not null) windowTheme.Changed -= OnVirtualHighContrastChanged; }
    }

    private void OnVirtualThemeChanged(FrameworkElement sender, object args) => QueueVirtualAppearance();
    private void OnVirtualHighContrastChanged(ThemeSettings sender, object args)
    {
        if (ReferenceEquals(sender, _virtualWindowTheme)) DispatchVirtualAppearance();
    }
    private void OnVirtualColorsChanged(UISettings sender, object args) => DispatchVirtualAppearance();
    private void DispatchVirtualAppearance()
    {
        var epoch = _virtualAppearanceEpoch;
        DispatcherQueue.TryEnqueue(() =>
        {
            if (_virtualThemeSubscribed && epoch == _virtualAppearanceEpoch) QueueVirtualAppearance();
        });
    }

    private void FailVirtualAppearance(Exception ex)
    {
        if (_virtualEditor is not { } pane) return;
        pane.MarkFailed("The editor appearance could not be initialized. " + ex.Message);
        _ = ShowMessageAsync("Editor appearance unavailable", ex.Message);
    }

    // 마지막 목표만 남겨 한 요청씩 전달한다. 휠 연타가 요청 레지스트리를 채우지 않는다.
    private void QueueVirtualAppearance()
    {
        if (_contentReleased || _virtualEditor is not { IsReady: true, IsFailed: false }) return;
        _virtualVisualQueued = true;
        if (!_virtualVisualSending) SendVirtualAppearanceAsync();
    }

    private async void SendVirtualAppearanceAsync()
    {
        _virtualVisualSending = true;
        try
        {
            while (_virtualVisualQueued && !_contentReleased && _virtualEditor is { IsReady: true, IsFailed: false } pane)
            {
                _virtualVisualQueued = false;
                try { await pane.OptionsAsync(new { visual = GetVirtualVisualOptions() }); }
                catch (Exception ex)
                {
                    if (ReferenceEquals(pane, _virtualEditor)) pane.MarkFailed("The editor appearance could not be updated. " + ex.Message);
                }
            }
        }
        finally { _virtualVisualSending = false; }
    }

    // 키보드 소유권 배치는 이 진입점을 쓴다. 휠·툴바와 같은 저장/범위/합치기 축이다.
    private void RequestVirtualZoom(int steps, bool original = false)
    {
        if (_virtualEditor is not { IsReady: true, IsFailed: false, Visibility: Visibility.Visible }) return;
        if (original) SetZoom(DefaultZoomPercent);
        else RequestZoom(ZoomTargetPercent + steps * ZoomStepPercent);
    }

    private bool CanUseDocumentZoom => (_path is not null || _untitled) &&
        (_virtualEditor is null || _renderMode || _htmlMode || _virtualEditor is { IsReady: true, IsFailed: false });
}
