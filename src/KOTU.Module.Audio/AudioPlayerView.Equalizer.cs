using System.Globalization;
using LibVLCSharp.Shared;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Windows.System;
using KOTU.Core.Settings;
using KOTU.Input;

namespace KOTU.Module.Audio;

public sealed partial class AudioPlayerView
{
    private readonly EqualizerState _equalizer;
    private readonly EqualizerApplyQueue<MediaPlayer> _eqApply;
    private readonly DispatcherTimer _eqSaveTimer = new() { Interval = TimeSpan.FromMilliseconds(180) };
    private bool _eqSavePending;
    private bool _eqFlyoutOpen;
    private bool _updatingEqualizer;
    private bool _eqLayoutQueued;
    private string _eqStatus = string.Empty;
    private XamlRoot? _eqXamlRoot;
    private StackPanel? _eqPanel;
    private ScrollViewer? _eqScroll;
    private ComboBox? _eqChoice;
    private TextBlock? _eqStatusText;
    private readonly List<(Slider Slider, TextBlock Value)> _eqSliders = [];

    private void FillEqualizerFlyout()
    {
        DetachEqualizerRoot();
        _eqFlyoutOpen = true;
        _updatingEqualizer = true;
        _eqSliders.Clear();
        var panel = new StackPanel
        {
            Spacing = 8,
            Tag = HotkeySupport.PassThroughTag,
        };
        var top = new Grid { ColumnSpacing = 8 };
        top.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        top.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        _eqChoice = new ComboBox { HorizontalAlignment = HorizontalAlignment.Stretch, MinWidth = 0 };
        AutomationProperties.SetName(_eqChoice, "Equalizer preset");
        _eqChoice.Items.Add(new ComboBoxItem { Content = "Off", Tag = string.Empty });
        if (_equalizer.Catalog is { } catalog)
        {
            foreach (var preset in catalog.Presets)
                _eqChoice.Items.Add(new ComboBoxItem { Content = preset.Name, Tag = preset.Name });
            _eqChoice.Items.Add(new ComboBoxItem { Content = "Custom", Tag = EqualizerState.CustomSelection });
        }
        _eqChoice.SelectionChanged += (_, _) =>
        {
            if (_updatingEqualizer || _eqChoice.SelectedItem is not ComboBoxItem { Tag: string selection }) return;
            if (!_equalizer.Select(selection)) return;
            EqualizerChanged();
        };
        top.Children.Add(_eqChoice);
        var reset = new Button { Content = "Reset", IsEnabled = _equalizer.Catalog is not null };
        AutomationProperties.SetName(reset, "Reset custom equalizer to flat");
        ToolTipService.SetToolTip(reset, "Reset all custom bands and preamp to 0 dB");
        reset.Click += (_, _) => { _equalizer.Reset(); EqualizerChanged(); };
        Grid.SetColumn(reset, 1);
        top.Children.Add(reset);
        panel.Children.Add(top);

        if (_equalizer.Catalog is { } metadata)
        {
            for (var band = 0; band < metadata.Frequencies.Count; band++)
            {
                var index = band;
                var frequency = FormatFrequency(metadata.Frequencies[band]);
                var row = new Grid { ColumnSpacing = 8 };
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(72) });
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(56) });
                row.Children.Add(new TextBlock { Text = frequency, VerticalAlignment = VerticalAlignment.Center });
                var slider = new Slider
                {
                    Minimum = EqualizerSnapshot.MinimumDb, Maximum = EqualizerSnapshot.MaximumDb,
                    StepFrequency = 0.1, SmallChange = 0.1, LargeChange = 1,
                    MinWidth = 0, HorizontalAlignment = HorizontalAlignment.Stretch,
                    Tag = HotkeySupport.PassThroughTag,
                };
                AutomationProperties.SetName(slider, $"Equalizer {frequency} gain in decibels");
                slider.PreviewKeyDown += OnEqualizerSliderPreviewKeyDown;
                slider.ValueChanged += (_, args) =>
                {
                    if (_updatingEqualizer) return;
                    _equalizer.SetBand(index, (float)args.NewValue);
                    EqualizerChanged();
                };
                Grid.SetColumn(slider, 1);
                row.Children.Add(slider);
                var value = new TextBlock { VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Right };
                Grid.SetColumn(value, 2);
                row.Children.Add(value);
                _eqSliders.Add((slider, value));
                panel.Children.Add(row);
            }
        }
        _eqStatusText = new TextBlock { TextWrapping = TextWrapping.Wrap, FontSize = 12 };
        AutomationProperties.SetLiveSetting(_eqStatusText, Microsoft.UI.Xaml.Automation.Peers.AutomationLiveSetting.Polite);
        panel.Children.Add(_eqStatusText);
        _eqPanel = panel;
        _eqScroll = new ScrollViewer
        {
            Content = panel,
            HorizontalScrollMode = ScrollMode.Disabled, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
        };
        EqFlyout.Content = _eqScroll;
        _eqXamlRoot = XamlRoot;
        if (_eqXamlRoot is not null) _eqXamlRoot.Changed += OnEqualizerRootChanged;
        UpdateEqualizerLayout();
        _updatingEqualizer = false;
        RefreshEqualizerControls();
    }

    private static void OnEqualizerSliderPreviewKeyDown(object sender, KeyRoutedEventArgs args)
    {
        if (args.Handled || sender is not Slider { IsEnabled: true } slider ||
            args.Key is not (VirtualKey.PageUp or VirtualKey.PageDown)) return;

        static bool Down(VirtualKey key) => Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(key)
            .HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down);
        if (Down(VirtualKey.Control) || Down(VirtualKey.Shift) || Down(VirtualKey.Menu) ||
            Down(VirtualKey.LeftWindows) || Down(VirtualKey.RightWindows)) return;

        // WinUI 슬라이더의 기본 방향키는 유지하고, 수식키 없는 큰 폭 조절만 보완한다.
        args.Handled = true;
        var change = args.Key == VirtualKey.PageUp ? slider.LargeChange : -slider.LargeChange;
        slider.Value = Math.Clamp(slider.Value + change, slider.Minimum, slider.Maximum);
    }

    private void OnEqualizerRootChanged(XamlRoot sender, XamlRootChangedEventArgs args)
    {
        if (_tornDown || !_eqFlyoutOpen || _eqLayoutQueued) return;
        _eqLayoutQueued = true;
        // 레이아웃 콜백 안에서 다시 측정하지 않고, 열린 컨트롤과 키보드 포커스를 유지한다.
        if (DispatcherQueue?.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () =>
        {
            _eqLayoutQueued = false;
            if (!_tornDown && _eqFlyoutOpen) UpdateEqualizerLayout();
        }) != true)
            _eqLayoutQueued = false;
    }

    private void UpdateEqualizerLayout()
    {
        // 기본 presenter 여백을 남겨 좁은 창에서도 가로 스크롤 없이 조절한다.
        if (_eqPanel is not null)
            _eqPanel.Width = Math.Min(360, Math.Max(0, (_eqXamlRoot?.Size.Width ?? 440) - 80));
        if (_eqScroll is not null)
            _eqScroll.MaxHeight = Math.Min(560, Math.Max(0, (_eqXamlRoot?.Size.Height ?? 640) - 80));
    }

    private void DetachEqualizerRoot()
    {
        if (_eqXamlRoot is not null) _eqXamlRoot.Changed -= OnEqualizerRootChanged;
        _eqXamlRoot = null;
    }

    private void CloseEqualizerFlyout()
    {
        _eqFlyoutOpen = false;
        DetachEqualizerRoot();
        SaveEqualizer();
    }

    private static string FormatFrequency(float frequency) => frequency >= 1000
        ? $"{(frequency / 1000).ToString("0.##", CultureInfo.InvariantCulture)} kHz"
        : $"{frequency.ToString("0.##", CultureInfo.InvariantCulture)} Hz";

    private void RefreshEqualizerControls()
    {
        if (!_eqFlyoutOpen || _eqChoice is null) return;
        _updatingEqualizer = true;
        try
        {
            var selection = _equalizer.Catalog is null ? string.Empty : _equalizer.Selection;
            _eqChoice.SelectedItem = _eqChoice.Items.OfType<ComboBoxItem>().FirstOrDefault(item => Equals(item.Tag, selection));
            var values = _equalizer.Current ?? _equalizer.Custom;
            for (var band = 0; band < _eqSliders.Count; band++)
            {
                var (slider, label) = _eqSliders[band];
                var gain = values?[band] ?? 0;
                slider.Value = gain;
                slider.IsEnabled = selection.Length != 0;
                label.Text = gain.ToString("+0.0;-0.0;0.0", CultureInfo.InvariantCulture) + " dB";
            }
            if (_eqStatusText is not null)
                _eqStatusText.Text = _equalizer.Catalog is null
                    ? "Equalizer bands are unavailable. Off is available."
                    : _eqStatus.Length != 0 ? _eqStatus
                    : selection.Length == 0 ? "Off. Your custom settings are kept." : string.Empty;
        }
        finally { _updatingEqualizer = false; }
    }

    private void EqualizerChanged()
    {
        _eqStatus = _player is null ? "Saved for the next player." : "Applying equalizer…";
        RefreshEqualizerControls();
        _eqSavePending = true;
        _eqSaveTimer.Stop();
        _eqSaveTimer.Start();
        _ = QueueEqualizer();
    }

    private void SaveEqualizer()
    {
        _eqSaveTimer.Stop();
        if (!_eqSavePending) return;
        _eqSavePending = false;
        _equalizer.WriteSettings((key, value) => _settings.Set(key, value));
        _settings.RequestSave();
    }

    private Task QueueEqualizer()
    {
        if (!_tornDown && _player is { } player) _eqApply.Enqueue(player, _equalizer.Current);
        return _eqApply.Completion;
    }

    private void EqualizerApplied(EqualizerApplyResult result)
    {
        if (_tornDown) return;
        if (result == EqualizerApplyResult.DisabledAfterFailure)
        {
            _equalizer.Select(string.Empty);
            _eqSavePending = true;
            SaveEqualizer();
            _eqStatus = "Equalizer could not be applied. EQ is off; custom settings are kept.";
        }
        else _eqStatus = result == EqualizerApplyResult.Failed
            ? "Equalizer update failed. Its current sound setting could not be confirmed. Select Off or try again."
            : string.Empty;
        ToolTipService.SetToolTip(EqButton, _eqStatus.Length == 0 ? "Equalizer" : _eqStatus);
        RefreshEqualizerControls();
    }
}
