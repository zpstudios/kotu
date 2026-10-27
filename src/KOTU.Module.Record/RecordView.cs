using System.Collections.ObjectModel;
using KOTU.Core.Contracts;
using KOTU.Core.Settings;
using KOTU.Core.Threading;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Markup;
using Ellipse = Microsoft.UI.Xaml.Shapes.Ellipse;
using Windows.Storage;
using Windows.Storage.Pickers;
using Windows.System;

namespace KOTU.Module.Record;

public sealed class RecordView : UserControl, IBottomBarProvider, ICloseGuard, ITrayStatusProvider
{
    private readonly ISettingsService _settings;
    private readonly RecordingFolderCoordinator _folders;
    private RecordingFolderState _folderState;
    private readonly ModuleWorker _worker = new("KOTU record worker");
    private readonly ModuleWorker _discoveryWorker = new("KOTU record source discovery", ThreadPriority.BelowNormal);
    private readonly CancellationTokenSource _lifetime = new();
    private readonly GridView _mode = CreateSourceGrid("\uE714", "Recording source settings", "", "{Binding}");
    private readonly GridView _source = CreateSourceGrid("\uE7F4", "Screens", "SCREEN");
    private readonly GridView _camera = CreateSourceGrid("\uE722", "Cameras", "CAMERA");
    private readonly TextBlock _cameraStatus = new() { Text = "Finding cameras...", TextWrapping = TextWrapping.Wrap };
    private readonly GridView _windows = CreateSourceGrid("\uE737", "Windows", "WINDOW");
    private readonly GridView _microphone = CreateSourceGrid("\uE720", "Microphones", "MICROPHONE");
    private readonly GridView _output = CreateSourceGrid("\uE767", "System audio outputs", "SYSTEM AUDIO");
    private readonly ObservableCollection<CaptureSource> _sources = [];
    private readonly ObservableCollection<CaptureSource> _windowSources = [];
    private readonly ObservableCollection<CaptureSource> _cameras = [];
    private readonly ObservableCollection<MicrophoneDevice> _microphones = [];
    private readonly ObservableCollection<OutputDevice> _outputs = [];
    private readonly CheckBox _includeOutput = new() { Content = "Include system audio from the selected output" };
    private readonly CheckBox _mix = new() { Content = "Include the selected microphone" };
    private readonly TextBlock _discoveryStatus = new() { Text = "Finding recording sources...", TextWrapping = TextWrapping.Wrap };
    private readonly Button _refresh = new() { Content = "Refresh sources" };
    private readonly Button _audioStart = new() { Content = "Audio record" };
    private readonly Button _videoStart = new() { Content = "Video record" };
    private readonly Button _cameraStart = new() { Content = "Camera record" };
    private readonly TextBlock _audioReady = new() { TextWrapping = TextWrapping.Wrap };
    private readonly TextBlock _videoReady = new() { TextWrapping = TextWrapping.Wrap };
    private readonly TextBlock _cameraReady = new() { TextWrapping = TextWrapping.Wrap };
    private readonly Button _stop = new() { Content = "Stop and save", IsEnabled = false };
    private readonly Button _cancel = new() { Content = "Discard", IsEnabled = false };
    private readonly Button _folder = new() { Content = "Open folder", IsEnabled = false };
    private readonly Button _changeFolder = new() { Content = "Change save folder" };
    private readonly Button _retryFolder = new() { Content = "Retry save folder", Visibility = Visibility.Collapsed };
    private readonly TextBlock _folderPath = new() { TextWrapping = TextWrapping.Wrap, IsTextSelectionEnabled = true };
    private readonly TextBlock _folderNotice = new() { TextWrapping = TextWrapping.Wrap, Visibility = Visibility.Collapsed };
    private readonly TextBlock _barClock = new() { Text = "Ready", VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis };
    private readonly TextBlock _captureState = new() { Text = "Ready", TextWrapping = TextWrapping.Wrap };
    private readonly TextBlock _captureSources = new() { TextWrapping = TextWrapping.Wrap, IsTextSelectionEnabled = true };
    private readonly Ellipse _captureDot = CreateRecordingDot();
    private readonly Ellipse _barDot = CreateRecordingDot();
    private readonly TextBlock _status = new() { Text = "Choose recording sources, then use a record button below.", TextWrapping = TextWrapping.Wrap, IsTextSelectionEnabled = true };
    private readonly TextBlock _path = new() { TextWrapping = TextWrapping.Wrap, IsTextSelectionEnabled = true };
    private readonly TextBlock _description = new() { TextWrapping = TextWrapping.Wrap };
    private readonly StackPanel _screenOptions = new() { Spacing = 8 };
    private readonly Grid _bar = new() { ColumnSpacing = 6, MinWidth = 228, HorizontalAlignment = HorizontalAlignment.Stretch, VerticalAlignment = VerticalAlignment.Center };
    private IRecordingSession? _session;
    private Task<bool>? _finishTask;
    private TaskCompletionSource<bool>? _closeDecision;
    private System.Threading.Timer? _timer;
    private System.Threading.Timer? _watchTimer;
    private CancellationToken _discoveryCancellation;
    private nint _ownWindow;
    private RecordingDiscoverySelection _discoverySelection = new(null, null, null, null);
    private RecordingSelection? _activeSelection;
    private string? _selectedVideoKey;
    private string? _selectedCameraKey;
    private string? _selectedMicrophoneId;
    private string? _selectedOutputId;
    private string? _sourceLostReason;
    private bool _sourcesInitialized, _camerasInitialized, _microphonesInitialized, _outputsInitialized;
    private bool _applyingSources;
    private bool _watchStarted;
    private string? _savedPath;
    private string? _cameraError;
    private bool _folderInitializing = true;
    private bool _folderInitializationStarted;
    private bool _choosingFolder;
    private bool _busy;
    private bool _starting;
    private object? _startAttempt;
    private bool _catalogInitializing = true;
    private bool _stopRequested;
    private volatile bool _discard;
    private volatile bool _disposed;
    private bool _closeDialog;
    private int _refreshSequence;
    private int _tickQueued;
    private int _discoveryPending;
    private RecordingPhase _phase = RecordingPhase.Ready;
    private RecordingPresentation _presentation = RecordingPresentation.Create(RecordingPhase.Ready, true, false, false, false, TimeSpan.Zero, "");
    private RecordingKind _activeKind;
    private bool _captureObserved;
    private TimeSpan _captureElapsed;
    private string _activeSourceLabels = "";

    internal RecordView(ISettingsService settings, RecordingFolderCoordinator folders)
    {
        _settings = settings;
        _folders = folders;
        _folderState = folders.Current;
        _folders.Changed += OnFolderChanged;
        _discoveryCancellation = _lifetime.Token;
        var preferences = RecordingSourcePreferences.Read(settings);
        _selectedVideoKey = preferences.VideoKey;
        _selectedCameraKey = preferences.CameraKey;
        _selectedMicrophoneId = settings.Get("record.microphoneId", "");
        _selectedOutputId = settings.Get("record.outputId", "");
        _mode.ItemTemplate = null;
        _mode.Items.Add(new GridViewItem { Content = CreateModeTile("\uE714", "Video and camera") });
        _mode.Items.Add(new GridViewItem { Content = CreateModeTile("\uE720", "Audio (microphone)") });
        foreach (GridViewItem item in _mode.Items) HookTileSelection(item);
        _mode.SelectedIndex = settings.Get("record.mode", "screen") == "microphone" ? 1 : 0;
        _mix.IsChecked = settings.Get("record.includeMicrophone", true);
        _includeOutput.IsChecked = settings.Get("record.includeSystemAudio", true);
        _source.ItemsSource = _sources;
        _windows.ItemsSource = _windowSources;
        _camera.ItemsSource = _cameras;
        _microphone.ItemsSource = _microphones;
        _output.ItemsSource = _outputs;
        _screenOptions.Children.Add(new TextBlock { Text = "Video record · Choose one screen or window", FontSize = 16, TextWrapping = TextWrapping.Wrap });
        _screenOptions.Children.Add(new TextBlock { Text = "Screens · Entire display" });
        _screenOptions.Children.Add(_source);
        _screenOptions.Children.Add(new TextBlock { Text = "Windows · Individual app", FontSize = 16, Margin = new Thickness(0, 8, 0, 0) });
        _screenOptions.Children.Add(_windows);
        _screenOptions.Children.Add(new TextBlock { Text = "Camera record · Choose a camera", FontSize = 16, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 8, 0, 0) });
        _screenOptions.Children.Add(_camera);
        _screenOptions.Children.Add(_cameraStatus);
        _screenOptions.Children.Add(_includeOutput);
        _screenOptions.Children.Add(new TextBlock { Text = "System audio output" });
        _screenOptions.Children.Add(_output);
        _screenOptions.Children.Add(_mix);
        var panel = new StackPanel { Spacing = 14, MaxWidth = 640, Margin = new Thickness(28, 28, 28, 72), HorizontalAlignment = HorizontalAlignment.Stretch };
        panel.Children.Add(new TextBlock { Text = "Record", FontSize = 28 });
        var banner = new Grid { ColumnSpacing = 6, RowSpacing = 4 };
        banner.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        banner.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        banner.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        banner.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        Grid.SetColumn(_captureState, 1);
        Grid.SetRow(_captureSources, 1);
        Grid.SetColumnSpan(_captureSources, 2);
        banner.Children.Add(_captureDot);
        banner.Children.Add(_captureState);
        banner.Children.Add(_captureSources);
        panel.Children.Add(banner);
        panel.Children.Add(_mode);
        panel.Children.Add(new TextBlock { Text = "Choose source settings here. Use a record button below to start.", TextWrapping = TextWrapping.Wrap });
        panel.Children.Add(_description);
        panel.Children.Add(_screenOptions);
        panel.Children.Add(new TextBlock { Text = "Audio record always records only the selected microphone to WAV. The audio checkboxes apply to Video record and Camera record. To select a microphone when its checkbox is off, open Audio (microphone).", TextWrapping = TextWrapping.Wrap });
        panel.Children.Add(new TextBlock { Text = "Microphone" });
        panel.Children.Add(_microphone);
        ConfigureActionButton(_refresh);
        ConfigureActionButton(_changeFolder);
        ConfigureActionButton(_retryFolder);
        panel.Children.Add(CreateSettingsCard("Sources", "\uE72C", _discoveryStatus, _refresh));
        panel.Children.Add(CreateSettingsCard("Record actions", "\uE7C8", _audioReady, _videoReady, _cameraReady));
        panel.Children.Add(CreateSettingsCard("Save folder", "\uE8B7", _folderPath, _folderNotice, _changeFolder, _retryFolder));
        panel.Children.Add(CreateSettingsCard("Recording status", "\uE946", _status, _path));
        _path.Visibility = Visibility.Collapsed;
        _path.RegisterPropertyChangedCallback(TextBlock.TextProperty, (_, _) =>
            _path.Visibility = string.IsNullOrEmpty(_path.Text) ? Visibility.Collapsed : Visibility.Visible);
        Content = new ScrollViewer { Content = panel, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        ConfigureBarButton(_audioStart, "\uE720", "Audio record", "Audio record");
        ConfigureBarButton(_videoStart, "\uE714", "Video record", "Video record");
        ConfigureBarButton(_cameraStart, "\uE722", "Camera record", "Camera record");
        ConfigureBarButton(_stop, ((char)Symbol.Stop).ToString(), "Stop and save", "Stop and save");
        ConfigureBarButton(_cancel, "\uE74D", "Discard recording", "Discard recording");
        ConfigureBarButton(_folder, "\uE8B7", "Open result folder (last saved or partial recording)", "Open result folder");
        // A389: 셸이 받는 하단 줄에만 버튼을 배치한다. 상태 칸은 좁은 폭에서 먼저 줄어든다.
        var barStatus = new Grid { ColumnSpacing = 6, HorizontalAlignment = HorizontalAlignment.Stretch };
        barStatus.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        barStatus.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        Grid.SetColumn(_barClock, 1);
        barStatus.Children.Add(_barDot);
        barStatus.Children.Add(_barClock);
        var barControls = new FrameworkElement[] { _audioStart, _videoStart, _cameraStart, _stop, _cancel, _folder, barStatus };
        for (var i = 0; i < barControls.Length; i++)
        {
            _bar.ColumnDefinitions.Add(new ColumnDefinition { Width = i < 6 ? GridLength.Auto : new GridLength(1, GridUnitType.Star) });
            Grid.SetColumn(barControls[i], i);
            _bar.Children.Add(barControls[i]);
        }
        _mode.SelectionChanged += (_, _) => UpdateControls();
        _source.SelectionChanged += (_, _) => CaptureSelectionChanged(_source);
        _windows.SelectionChanged += (_, _) => CaptureSelectionChanged(_windows);
        _camera.SelectionChanged += (_, _) => CaptureSelectionChanged(_camera);
        _microphone.SelectionChanged += (_, _) =>
        {
            if (!_applyingSources && _microphone.SelectedItem is MicrophoneDevice microphone) _selectedMicrophoneId = microphone.Id;
            UpdateDiscoverySelection(); UpdateControls();
        };
        _output.SelectionChanged += (_, _) =>
        {
            if (!_applyingSources && _output.SelectedItem is OutputDevice output) _selectedOutputId = output.Id;
            UpdateDiscoverySelection(); UpdateControls();
        };
        _mix.Checked += (_, _) => UpdateControls();
        _mix.Unchecked += (_, _) => UpdateControls();
        _includeOutput.Checked += (_, _) => UpdateControls();
        _includeOutput.Unchecked += (_, _) => UpdateControls();
        _refresh.Click += (_, _) => QueueDiscovery();
        _audioStart.Click += async (_, _) => await StartAsync(RecordingKind.Microphone);
        _videoStart.Click += async (_, _) => await StartAsync(RecordingKind.Screen);
        _cameraStart.Click += async (_, _) => await StartAsync(RecordingKind.Camera);
        _stop.Click += async (_, _) => await StopAsync(false);
        _cancel.Click += async (_, _) => await DiscardAsync();
        _folder.Click += async (_, _) => await OpenFolderAsync();
        _changeFolder.Click += async (_, _) => await ChangeFolderAsync();
        _retryFolder.Click += async (_, _) => await RetryFolderAsync();
        Loaded += async (_, _) => { StartDiscovery(); await InitializeFoldersAsync(); };
        Unloaded += OnUnloaded;
        UpdateDiscoverySelection();
        UpdateControls();
    }

    private bool ScreenMode => _mode.SelectedIndex == 0;
    private CaptureSource? SelectedVideoSource => _source.SelectedItem as CaptureSource ?? _windows.SelectedItem as CaptureSource;
    public bool HasUnsavedChanges => _busy;
    public event Action<bool>? UnsavedChanged;
    public event Action? TrayStatusChanged;
    public object? TakeBottomBar() => _bar;
    public TrayStatus GetTrayStatus() => _presentation.Tray;

    private static Border CreateSettingsCard(string title, string glyph, params UIElement[] controls)
    {
        var heading = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10 };
        heading.Children.Add(new FontIcon { Glyph = glyph, FontSize = 18 });
        heading.Children.Add(new TextBlock { Text = title, FontSize = 16, TextWrapping = TextWrapping.Wrap });
        var content = new StackPanel { Spacing = 12 };
        content.Children.Add(heading);
        foreach (var control in controls) content.Children.Add(control);
        var card = new Border
        {
            Child = content, Padding = new Thickness(16), CornerRadius = new CornerRadius(8), BorderThickness = new Thickness(1),
        };
        void ApplyColors()
        {
            var dark = card.ActualTheme == ElementTheme.Dark;
            card.Background = new SolidColorBrush(dark ? ColorHelper.FromArgb(255, 32, 32, 32) : ColorHelper.FromArgb(255, 243, 243, 243));
            card.BorderBrush = new SolidColorBrush(dark ? ColorHelper.FromArgb(255, 98, 98, 98) : ColorHelper.FromArgb(255, 138, 138, 138));
        }
        card.Loaded += (_, _) => ApplyColors();
        card.ActualThemeChanged += (_, _) => ApplyColors();
        ApplyColors();
        return card;
    }

    private static void ConfigureActionButton(Button button)
    {
        button.Padding = new Thickness(14, 8, 14, 8);
        button.MinHeight = 36;
        button.BorderThickness = new Thickness(1);
        button.CornerRadius = new CornerRadius(6);
        button.HorizontalAlignment = HorizontalAlignment.Left;
        AutomationProperties.SetName(button, button.Content.ToString()!);
        void ApplyColors()
        {
            var dark = button.ActualTheme == ElementTheme.Dark;
            button.Background = new SolidColorBrush(dark ? ColorHelper.FromArgb(255, 52, 52, 52) : ColorHelper.FromArgb(255, 224, 224, 224));
            button.BorderBrush = new SolidColorBrush(dark ? ColorHelper.FromArgb(255, 133, 133, 133) : ColorHelper.FromArgb(255, 119, 119, 119));
        }
        button.Loaded += (_, _) => ApplyColors();
        button.ActualThemeChanged += (_, _) => ApplyColors();
        ApplyColors();
    }

    private static GridView CreateSourceGrid(string glyph, string name, string category, string labelBinding = "{Binding Label}")
    {
        // Native GridView containers retain selection, focus, checkmarks and arrow-key navigation.
        var grid = new GridView
        {
            SelectionMode = ListViewSelectionMode.Single, MaxHeight = 300,
            IsMultiSelectCheckBoxEnabled = false,
            ItemTemplate = (DataTemplate)XamlReader.Load($$"""
                <DataTemplate xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation">
                    <Border Width="132" Height="132" Padding="10" CornerRadius="8"
                            BorderThickness="2" BorderBrush="{ThemeResource ControlStrokeColorDefaultBrush}">
                        <Grid RowSpacing="6">
                            <Grid.RowDefinitions>
                                <RowDefinition Height="*"/><RowDefinition Height="Auto"/><RowDefinition Height="36"/>
                            </Grid.RowDefinitions>
                            <FontIcon Glyph="{{glyph}}" FontSize="30" HorizontalAlignment="Center" VerticalAlignment="Center"/>
                            <TextBlock Grid.Row="1" Text="{{category}}" FontSize="10" Opacity="0.65" HorizontalAlignment="Center"/>
                            <TextBlock Grid.Row="2" Text="{{labelBinding}}" FontSize="12" TextWrapping="Wrap"
                                       TextTrimming="CharacterEllipsis" MaxLines="2" TextAlignment="Center"/>
                            <Border Name="SelectionBadge" Grid.RowSpan="3" Width="22" Height="22" CornerRadius="11"
                                    HorizontalAlignment="Right" VerticalAlignment="Top" Visibility="Collapsed"
                                    Background="{ThemeResource SystemControlHighlightAccentBrush}" IsHitTestVisible="False">
                                <FontIcon Glyph="&#xE73E;" FontSize="12" Foreground="White"/>
                            </Border>
                        </Grid>
                    </Border>
                </DataTemplate>
                """),
            ItemsPanel = (ItemsPanelTemplate)XamlReader.Load("""
                <ItemsPanelTemplate xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation">
                    <ItemsWrapGrid Orientation="Horizontal"/>
                </ItemsPanelTemplate>
                """),
            ItemContainerStyle = (Style)XamlReader.Load("""
                <Style xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation" TargetType="GridViewItem">
                    <Setter Property="Margin" Value="0,0,8,8"/>
                    <Setter Property="Padding" Value="0"/>
                    <Setter Property="HorizontalContentAlignment" Value="Stretch"/>
                    <Setter Property="VerticalContentAlignment" Value="Stretch"/>
                </Style>
                """),
        };
        ScrollViewer.SetHorizontalScrollBarVisibility(grid, ScrollBarVisibility.Disabled);
        ScrollViewer.SetHorizontalScrollMode(grid, ScrollMode.Disabled);
        ScrollViewer.SetVerticalScrollBarVisibility(grid, ScrollBarVisibility.Auto);
        AutomationProperties.SetName(grid, name);
        var hookedContainers = new HashSet<GridViewItem>();
        grid.ContainerContentChanging += (_, args) =>
        {
            if (args.InRecycleQueue) return;
            if (args.ItemContainer is GridViewItem container)
            {
                if (hookedContainers.Add(container) && container.Content is not Border)
                {
                    // Bind the decoration to the container's actual selection, including keyboard
                    // selection, restored preferences and deselection in the other capture group.
                    HookTileSelection(container);
                }
                ApplyTileSelection(container);
                if (args.Phase == 0)
                    args.RegisterUpdateCallback((_, updated) => ApplyTileSelection((GridViewItem)updated.ItemContainer));
            }
            var label = args.Item switch
            {
                CaptureSource source => source.Label,
                MicrophoneDevice microphone => microphone.Label,
                OutputDevice output => output.Label,
                _ => null,
            };
            if (label is null) return;
            ToolTipService.SetToolTip(args.ItemContainer, label);
            AutomationProperties.SetName(args.ItemContainer, label);
        };
        return grid;
    }

    private static void HookTileSelection(GridViewItem container)
    {
        container.RegisterPropertyChangedCallback(ListViewItem.IsSelectedProperty, (_, _) => ApplyTileSelection(container));
        container.Loaded += (_, _) => ApplyTileSelection(container);
        container.ActualThemeChanged += (_, _) => ApplyTileSelection(container);
    }

    private static void ApplyTileSelection(GridViewItem container)
    {
        if ((container.Content as Border ?? container.ContentTemplateRoot as Border) is not { Child: Grid content } tile) return;
        var accent = (SolidColorBrush)Application.Current.Resources["SystemControlHighlightAccentBrush"];
        tile.BorderBrush = container.IsSelected ? accent : (Brush)Application.Current.Resources["ControlStrokeColorDefaultBrush"];
        tile.Background = container.IsSelected
            ? new SolidColorBrush(accent.Color) { Opacity = 0.22 }
            : new SolidColorBrush(Colors.Transparent);
        foreach (var badge in content.Children.OfType<Border>().Where(b => b.Name == "SelectionBadge"))
            badge.Visibility = container.IsSelected ? Visibility.Visible : Visibility.Collapsed;
    }

    private static FrameworkElement CreateModeTile(string glyph, string label)
    {
        var panel = new StackPanel { Spacing = 12, VerticalAlignment = VerticalAlignment.Center };
        panel.Children.Add(new FontIcon { Glyph = glyph, FontSize = 30 });
        panel.Children.Add(new TextBlock { Text = label, FontSize = 12, TextWrapping = TextWrapping.Wrap, TextAlignment = TextAlignment.Center });
        var content = new Grid();
        content.Children.Add(panel);
        content.Children.Add(new Border
        {
            Name = "SelectionBadge", Width = 22, Height = 22, CornerRadius = new CornerRadius(11),
            HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Top,
            Background = (Brush)Application.Current.Resources["SystemControlHighlightAccentBrush"],
            Visibility = Visibility.Collapsed, IsHitTestVisible = false,
            Child = new FontIcon { Glyph = "\uE73E", FontSize = 12, Foreground = new SolidColorBrush(Colors.White) },
        });
        var tile = new Border
        {
            Width = 132, Height = 132, Padding = new Thickness(10), Child = content,
            CornerRadius = new CornerRadius(8), BorderThickness = new Thickness(2),
            BorderBrush = (Brush)Application.Current.Resources["ControlStrokeColorDefaultBrush"],
        };
        AutomationProperties.SetName(tile, label);
        ToolTipService.SetToolTip(tile, label);
        return tile;
    }

    private void CaptureSelectionChanged(GridView selected)
    {
        if (_applyingSources) return;
        if (selected.SelectedItem is CaptureSource source)
        {
            _applyingSources = true;
            try
            {
                if (selected == _source) _windows.SelectedItem = null;
                else if (selected == _windows) _source.SelectedItem = null;
            }
            finally { _applyingSources = false; }
            if (selected == _camera) _selectedCameraKey = RecordingSourceCatalog.Key(source);
            else _selectedVideoKey = RecordingSourceCatalog.Key(source);
        }
        UpdateDiscoverySelection();
        UpdateControls();
    }

    private static Ellipse CreateRecordingDot() => new()
    {
        Width = 8, Height = 8, Fill = new SolidColorBrush(Colors.Red),
        VerticalAlignment = VerticalAlignment.Center, Visibility = Visibility.Collapsed, IsHitTestVisible = false,
    };

    private void UpdateRecordingPresentation(bool recording = false, bool completed = false)
    {
        if (_disposed) return;
        var previousPhase = _presentation.Phase;
        _presentation = RecordingPresentation.Create(_phase, _activeKind, recording, completed, _captureObserved, _captureElapsed, _activeSourceLabels);
        _captureState.Text = _presentation.Text;
        _captureSources.Text = _activeSourceLabels;
        _captureSources.Visibility = string.IsNullOrEmpty(_activeSourceLabels) ? Visibility.Collapsed : Visibility.Visible;
        _barClock.Text = _presentation.Detail;
        _captureDot.Visibility = _barDot.Visibility = _presentation.IsRecording ? Visibility.Visible : Visibility.Collapsed;
        ToolTipService.SetToolTip(_barClock, _presentation.Detail);
        AutomationProperties.SetName(_captureState, _presentation.Text);
        AutomationProperties.SetName(_barClock, _presentation.Detail);
        if (_presentation.Phase != previousPhase) UpdateControls();
        TrayStatusChanged?.Invoke();
    }

    private static void ConfigureBarButton(Button button, string glyph, string tooltip, string name)
    {
        try
        {
            if (Application.Current?.Resources["BottomBarButtonStyle"] is Style style) button.Style = style;
        }
        catch { /* 공통 스타일이 없는 호스트에서도 기본 버튼 템플릿으로 표시한다. */ }
        // 기본 스타일의 최소 크기가 명시 크기를 이기지 않도록 두 값 모두 지정한다.
        button.Width = button.Height = 32;
        button.MinWidth = button.MinHeight = 0;
        button.Padding = new Thickness(2);
        button.BorderThickness = new Thickness(1);
        button.CornerRadius = new CornerRadius(4);
        button.HorizontalContentAlignment = HorizontalAlignment.Center;
        button.VerticalContentAlignment = VerticalAlignment.Center;
        button.VerticalAlignment = VerticalAlignment.Center;
        button.Content = new FontIcon { Glyph = glyph, FontSize = 18 };
        ToolTipService.SetToolTip(button, tooltip);
        AutomationProperties.SetName(button, name);
    }

    private void SetBusy(bool value)
    {
        _busy = value;
        UpdateControls();
        UnsavedChanged?.Invoke(value);
        TrayStatusChanged?.Invoke();
    }

    private void UpdateControls()
    {
        if (_disposed) return;
        var screen = ScreenMode;
        _screenOptions.Visibility = screen ? Visibility.Visible : Visibility.Collapsed;
        _description.Text = screen
            ? "MP4 video (H.264), 30 fps. Video record uses the selected screen or window; Camera record uses the independently selected camera. Camera format is automatic. Windows must allow camera access for desktop apps. System audio includes other apps on the selected output. Uncheck both audio choices for silent video. Keep the selected window open and visible."
            : "WAV audio, 48 kHz / 16-bit mono. Only the selected microphone is recorded. Windows must allow microphone access for desktop apps.";
        var selectable = !_busy && !_starting && !_folderInitializing && !_choosingFolder && !_closeDialog;
        _mode.IsEnabled = selectable;
        _source.IsEnabled = selectable;
        _windows.IsEnabled = selectable;
        _camera.IsEnabled = selectable;
        _microphone.IsEnabled = selectable && (!screen || _mix.IsChecked == true);
        _output.IsEnabled = selectable && _includeOutput.IsChecked == true;
        _mix.IsEnabled = selectable;
        _includeOutput.IsEnabled = selectable;
        _refresh.IsEnabled = !_disposed;
        var canChangeFolder = !_disposed && !_folderInitializing && !_choosingFolder && !_closeDialog;
        _changeFolder.IsEnabled = _retryFolder.IsEnabled = canChangeFolder;
        _folderPath.Text = _folderInitializing ? "Preparing save folder..." : _folderState.DisplayPath ?? "Choose a save folder.";
        _folderNotice.Text = string.Join("\n", new[] { _folderState.Error, _folderState.Notice, _folderState.PersistenceWarning }
            .Where(text => !string.IsNullOrEmpty(text)).Distinct());
        _folderNotice.Visibility = _folderNotice.Text.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
        _retryFolder.Visibility = _folderState.Error is null ? Visibility.Collapsed : Visibility.Visible;
        var input = CaptureStartInput();
        ApplyStartControl(_audioStart, _audioReady, "Audio record", RecordingStartPolicy.Evaluate(RecordingKind.Microphone, input));
        ApplyStartControl(_videoStart, _videoReady, "Video record", RecordingStartPolicy.Evaluate(RecordingKind.Screen, input));
        ApplyStartControl(_cameraStart, _cameraReady, "Camera record", RecordingStartPolicy.Evaluate(RecordingKind.Camera, input));
        _stop.IsEnabled = _session is not null && !_stopRequested && !_closeDialog;
        _cancel.IsEnabled = _session is not null && !_stopRequested && !_closeDialog;
        _folder.IsEnabled = !_busy && _savedPath is not null;
    }

    private RecordingStartInput CaptureStartInput() => new()
    {
        Disposed = _disposed, Busy = _busy, Starting = _starting, Phase = _presentation.Phase,
        CloseDialog = _closeDialog, ChoosingFolder = _choosingFolder, FolderInitializing = _folderInitializing,
        CatalogInitializing = _catalogInitializing, Folder = _folders.CaptureCommitted(),
        Video = SelectedVideoSource, Camera = _camera.SelectedItem as CaptureSource,
        Microphone = _microphone.SelectedItem as MicrophoneDevice, Output = _output.SelectedItem as OutputDevice,
        IncludeMicrophone = _mix.IsChecked == true, IncludeOutput = _includeOutput.IsChecked == true,
        CamerasObserved = _camerasInitialized, CameraCount = _cameras.Count, CameraError = _cameraError,
        Preferences = new(_selectedVideoKey ?? "", _selectedCameraKey ?? ""),
        MicrophonePreference = _selectedMicrophoneId ?? "", OutputPreference = _selectedOutputId ?? "",
    };

    private static void ApplyStartControl(Button button, TextBlock row, string name, RecordingStartDecision decision)
    {
        button.IsEnabled = decision.CanStart;
        row.Text = name + ": " + decision.Detail;
        ToolTipService.SetToolTip(button, row.Text);
    }

    private void UpdateDiscoverySelection()
    {
        if (_applyingSources) return;
        // Immutable preferences are the only view state read by the discovery thread.
        Volatile.Write(ref _discoverySelection, new(SelectedVideoSource, _camera.SelectedItem as CaptureSource,
            (_microphone.SelectedItem as MicrophoneDevice)?.Id ?? _selectedMicrophoneId,
            (_output.SelectedItem as OutputDevice)?.Id ?? _selectedOutputId));
    }

    private void StartDiscovery()
    {
        if (_disposed || _watchStarted) return;
        _watchStarted = true;
        try
        {
            _ownWindow = GetHwnd();
            _watchTimer = new System.Threading.Timer(_ => QueueDiscovery(), null, 0, 2000);
        }
        catch (Exception ex) { _catalogInitializing = false; _discoveryStatus.Text = "Could not watch sources. " + DescribeError(ex); UpdateControls(); }
    }

    private void QueueDiscovery()
    {
        if (_disposed || !_watchStarted || Interlocked.CompareExchange(ref _discoveryPending, 1, 0) != 0) return;
        _ = DiscoverAsync(Volatile.Read(ref _refreshSequence));
    }

    private async Task DiscoverAsync(int sequence)
    {
        var posted = false;
        try
        {
            var preferred = Volatile.Read(ref _discoverySelection).WithActive(Volatile.Read(ref _activeSelection));
            var snapshot = await _discoveryWorker.Run(context =>
                RecordingSourceDiscovery.Read(_ownWindow, preferred, context.Cancellation), _discoveryCancellation).ConfigureAwait(false);
            if (_disposed || _discoveryCancellation.IsCancellationRequested || sequence != Volatile.Read(ref _refreshSequence)) return;
            posted = DispatcherQueue.TryEnqueue(() =>
            {
                var retryObservation = false;
                try
                {
                    if (!_disposed && !_discoveryCancellation.IsCancellationRequested && sequence == _refreshSequence)
                    {
                        ApplySourceSnapshot(snapshot, preferred);
                        retryObservation = _busy && Volatile.Read(ref _activeSelection) is { } active && !RecordingSourceCatalog.CoversObservation(active, preferred);
                    }
                }
                catch (Exception ex) { if (!_disposed) _discoveryStatus.Text = "Could not update sources. " + DescribeError(ex); }
                finally
                {
                    Interlocked.Exchange(ref _discoveryPending, 0);
                    if (retryObservation) QueueDiscovery();
                }
            });
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            if (!_disposed && !_discoveryCancellation.IsCancellationRequested)
                posted = DispatcherQueue.TryEnqueue(() =>
                {
                    try { if (!_disposed && sequence == _refreshSequence) _discoveryStatus.Text = "Could not refresh sources. " + DescribeError(ex); }
                    finally { Interlocked.Exchange(ref _discoveryPending, 0); }
                });
        }
        finally { if (!posted) Interlocked.Exchange(ref _discoveryPending, 0); }
    }

    private void ApplySources<T>(ListViewBase list, ObservableCollection<T> current, IReadOnlyList<T>? snapshot,
        ref bool initialized, ref string? selectedKey, Func<T, string> key, Func<T, bool> isDefault) where T : class
    {
        if (snapshot is null) return;
        var selected = RecordingSourceCatalog.Select(snapshot, selectedKey, key, isDefault, !initialized);
        initialized = true;
        if (selected is not null) selectedKey = key(selected);
        RecordingSourceCatalog.Apply(current, snapshot, key);
        var row = selected is null ? null : current.FirstOrDefault(item => key(item) == key(selected));
        if (!ReferenceEquals(list.SelectedItem, row)) list.SelectedItem = row;
    }

    private void ApplySourceSnapshot(RecordingSourceSnapshot snapshot, RecordingDiscoverySelection observed)
    {
        _applyingSources = true;
        try
        {
            var firstScreenObservation = snapshot.Sources is not null && !_sourcesInitialized;
            if (snapshot.Sources is { } sources)
            {
                _sourcesInitialized = true;
                RecordingSourceCatalog.Apply(_sources, sources.Where(s => s.Kind == CaptureSourceKind.Display).ToArray(), RecordingSourceCatalog.Key);
                RecordingSourceCatalog.Apply(_windowSources, sources.Where(s => s.Kind == CaptureSourceKind.Window).ToArray(), RecordingSourceCatalog.Key);
            }
            if (snapshot.Cameras is { } cameras)
            {
                _camerasInitialized = true;
                RecordingSourceCatalog.Apply(_cameras, cameras, RecordingSourceCatalog.Key);
                _cameraStatus.Text = cameras.Count == 0 ? "No cameras available." : "Camera format: Automatic";
            }
            if (snapshot.CameraError is { } cameraError) _cameraStatus.Text = cameraError;
            var selected = RecordingSourceCatalog.SelectVisual(_sources.Concat(_windowSources).ToArray(),
                _selectedVideoKey, camera: false, firstScreenObservation);
            var selectedCamera = RecordingSourceCatalog.SelectVisual(_cameras, _selectedCameraKey, camera: true, firstObservation: false);
            if (selected is not null) _selectedVideoKey = RecordingSourceCatalog.Key(selected);
            if (selectedCamera is not null) _selectedCameraKey = RecordingSourceCatalog.Key(selectedCamera);
            _source.SelectedItem = selected?.Kind == CaptureSourceKind.Display ? selected : null;
            _windows.SelectedItem = selected?.Kind == CaptureSourceKind.Window ? selected : null;
            _camera.SelectedItem = selectedCamera;
            ApplySources(_microphone, _microphones, snapshot.Microphones, ref _microphonesInitialized, ref _selectedMicrophoneId, d => d.Id, d => d.IsDefault);
            ApplySources(_output, _outputs, snapshot.Outputs, ref _outputsInitialized, ref _selectedOutputId, d => d.Id, d => d.IsDefault);
        }
        finally { _applyingSources = false; }
        _catalogInitializing = false;
        _cameraError = snapshot.CameraError;
        var errors = new[] { snapshot.CameraError is { } camera ? "Cameras: " + camera : null, snapshot.ScreenError is { } screen ? "Screens/windows: " + screen : null,
            snapshot.MicrophoneError is { } mic ? "Microphones: " + mic : null,
            snapshot.OutputError is { } output ? "System outputs: " + output : null }.Where(s => s is not null);
        var message = string.Join("\n", errors);
        if (message.Length == 0) message = "Sources update automatically. Select the sources to record.";
        if (!_busy) message += " Missing selections are not replaced automatically.";
        if (_busy && _sourceLostReason is { } lost) message = lost + " Stopping and saving the current recording.";
        if (_discoveryStatus.Text != message) _discoveryStatus.Text = message;
        UpdateDiscoverySelection();
        UpdateControls();
        if (_busy && _sourceLostReason is null && Volatile.Read(ref _activeSelection) is { } active &&
            RecordingSourceCatalog.CoversObservation(active, observed) &&
            RecordingSourceCatalog.Missing(active, snapshot) is { } reason)
        {
            _sourceLostReason = reason;
            _discoveryStatus.Text = reason + " Stopping and saving the current recording.";
            if (_session is not null && !_stopRequested) _ = StopAsync(false);
        }
    }

    private void OnFolderChanged()
    {
        if (_disposed) return;
        DispatcherQueue.TryEnqueue(() =>
        {
            if (!_disposed) ApplyFolderState();
        });
    }

    private void ApplyFolderState()
    {
        var state = _folders.Current;
        if (_disposed || state.Revision < _folderState.Revision) return;
        _folderState = state;
        UpdateControls();
    }

    private async Task InitializeFoldersAsync()
    {
        if (_disposed || _folderInitializationStarted) return;
        _folderInitializationStarted = true;
        try { await _folders.EnsureInitializedAsync().WaitAsync(_lifetime.Token); }
        catch (OperationCanceledException) { }
        catch (Exception ex) { if (!_disposed) _status.Text = "Could not prepare the save folder. " + DescribeError(ex); }
        finally
        {
            _folderInitializing = false;
            if (!_disposed) ApplyFolderState();
        }
    }

    private async Task RetryFolderAsync()
    {
        if (_disposed || _folderInitializing || _choosingFolder || _closeDialog) return;
        _folderInitializing = true;
        UpdateControls();
        try { await _folders.RetryAsync(_lifetime.Token); }
        catch (OperationCanceledException) { }
        catch (Exception ex) { if (!_disposed) _status.Text = "Could not prepare the save folder. " + DescribeError(ex); }
        finally
        {
            _folderInitializing = false;
            if (!_disposed) ApplyFolderState();
        }
    }

    private async Task ChangeFolderAsync()
    {
        if (_disposed || _folderInitializing || _choosingFolder || _closeDialog) return;
        _choosingFolder = true;
        UpdateControls();
        try
        {
            var picker = new FolderPicker { SuggestedStartLocation = PickerLocationId.DocumentsLibrary };
            picker.FileTypeFilter.Add("*");
            WinRT.Interop.InitializeWithWindow.Initialize(picker, GetHwnd());
            var selection = await picker.PickSingleFolderAsync();
            if (_disposed || selection is null) return;
            await _folders.ChangeAsync(selection.Path, _lifetime.Token);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { if (!_disposed) _status.Text = "Could not change the save folder. " + DescribeError(ex); }
        finally { _choosingFolder = false; if (!_disposed) ApplyFolderState(); }
    }

    private async Task StartAsync(RecordingKind requestedKind)
    {
        // 모든 진입점은 디스패처에서 요청 종류를 검증하고 첫 알림과 대기 전에 함께 예약한다.
        if (_disposed || _busy || _starting) return;
        var decision = RecordingStartPolicy.Evaluate(requestedKind, CaptureStartInput());
        if (decision.Request is not { } request) { _status.Text = decision.Detail; UpdateControls(); return; }
        var attempt = new object();
        _startAttempt = attempt;
        _starting = true;
        _busy = true;
        var folderSnapshot = request.Folder;
        string? temporaryPath = null;
        var keepTemporary = false;
        var destinationPrepared = false;
        var folderPreparationAttempted = false;
        try
        {
            _sourceLostReason = null;
            _activeKind = request.Kind;
            _activeSourceLabels = request.Labels;
            _captureObserved = false;
            _captureElapsed = TimeSpan.Zero;
            _phase = RecordingPhase.Starting;
            Volatile.Write(ref _activeSelection, request.Selection);
            _discard = false;
            _stopRequested = false;
            _mode.SelectedIndex = request.IsVisual ? 0 : 1;
            UpdateRecordingPresentation();
            SetBusy(true);
            _status.Text = "Checking save folder...";
            folderPreparationAttempted = true;
            var destination = await _worker.Run(context =>
            {
                var folder = _folders.PrepareCaptured(folderSnapshot.Spec!);
                context.ThrowIfCancelled();
                return RecordingOutput.CreateDestinationPath(folder, request.Kind, DateTime.Now);
            }, _lifetime.Token);
            if (_disposed) return;
            destinationPrepared = true;
            // A sibling temporary file preserves an existing destination on cancel/failure.
            temporaryPath = RecordingOutput.CreateTemporaryPath(destination, request.Extension);
            _status.Text = "Starting recording...";
            _savedPath = null;
            _path.Text = "Save to: " + destination;
            var output = temporaryPath;
            var session = await _worker.Run<IRecordingSession>(context =>
            {
                context.ThrowIfCancelled();
                // Settings Save is disk I/O, so it shares the module worker.
                try
                {
                    request.SavePreferences(_settings);
                }
                catch { /* A settings failure must not prevent recording. */ }
                context.ThrowIfCancelled();
                return request.IsVisual
                    ? RecordingBackend.StartScreen(request.Source!, request.MicrophoneId, request.OutputId, output, _worker)
                    : new MicrophoneRecordingSession(request.MicrophoneId!, output, _worker.Post);
            }, _lifetime.Token);
            _session = session;
            _finishTask = FinishAsync(session, output, destination, attempt);
            temporaryPath = null; // FinishAsync owns the file before callbacks can request Stop.
            _starting = false;
            _phase = RecordingPhase.Active;
            UpdateRecordingPresentation();
            if (_disposed)
            {
                _discard = true;
                await StopAsync(true);
                return;
            }
            if (_sourceLostReason is not null || _stopRequested)
            {
                await StopAsync(false);
                return;
            }
            _status.Text = request.IsVisual ? "Stop to save the MP4 recording." : "Stop to save the WAV recording.";
            _timer = new System.Threading.Timer(_ => QueueTick(session), null, 0, 250);
            UpdateControls();
        }
        catch (OperationCanceledException) when (ReferenceEquals(_startAttempt, attempt))
        {
            _phase = RecordingPhase.Finished;
            if (!_disposed) _status.Text = "Recording canceled.";
        }
        catch (Exception ex) when (ReferenceEquals(_startAttempt, attempt))
        {
            keepTemporary = true;
            _phase = RecordingPhase.Error;
            if (folderPreparationAttempted && !destinationPrepared && !_disposed)
            {
                var error = "Could not prepare the save folder. " + DescribeError(ex);
                await _folders.ReportPreparationFailureAsync(folderSnapshot, error);
            }
            if (!_disposed) _status.Text = "Recording could not start. " + DescribeError(ex);
        }
        catch { /* 이미 끝난 시작 요청의 늦은 오류는 새 녹화 상태를 바꾸지 않는다. */ }
        finally
        {
            if (temporaryPath is { } abandonedPath)
            {
                try
                {
                    var retained = await _worker.Run(_ =>
                    {
                        if (keepTemporary && !_disposed) return File.Exists(abandonedPath);
                        RecordingOutput.Discard(abandonedPath);
                        return false;
                    });
                    if (retained && !_disposed)
                    {
                        _savedPath = abandonedPath;
                        _path.Text = abandonedPath;
                        _status.Text += " A partial recording has been kept at the path below.";
                    }
                }
                catch (Exception ex)
                {
                    if (!_disposed) _status.Text += " Could not clean up the temporary recording. " + DescribeError(ex);
                }
            }
            // 이전 Start가 Stop을 기다리는 사이 다음 요청이 시작되어도 그 상태를 바꾸지 않는다.
            if (ReferenceEquals(_startAttempt, attempt))
            {
                _starting = false;
                if (_session is null)
                {
                    _startAttempt = null;
                    Volatile.Write(ref _activeSelection, null);
                    if (!_disposed) { UpdateRecordingPresentation(); SetBusy(false); }
                    else _worker.Dispose();
                }
            }
        }
    }

    private void QueueTick(IRecordingSession session)
    {
        // At most one UI update is pending, even if rendering is temporarily delayed.
        if (Interlocked.Exchange(ref _tickQueued, 1) != 0) return;
        var elapsed = session.Elapsed;
        var recording = session.IsRecording;
        var completed = session.Completion.IsCompleted;
        var pendingStopReason = session.PendingStopReason;
        if (!DispatcherQueue.TryEnqueue(() =>
        {
            Interlocked.Exchange(ref _tickQueued, 0);
            if (_disposed || _session != session || _phase is not (RecordingPhase.Active or RecordingPhase.Stopping)) return;
            if (pendingStopReason is not null && !session.Completion.IsCompleted)
            {
                _phase = RecordingPhase.Stopping;
                _status.Text = pendingStopReason + " Waiting for the camera to stop. Use Stop and save to retry stopping.";
                UpdateControls();
            }
            _captureElapsed = elapsed;
            _captureObserved |= recording;
            // 완료가 UI 대기 중 발생했다면 이전 녹화 스냅샷을 활성 표식으로 쓰지 않는다.
            UpdateRecordingPresentation(recording, completed || session.Completion.IsCompleted);
        })) Interlocked.Exchange(ref _tickQueued, 0);
    }

    private async Task<bool> FinishAsync(IRecordingSession session, string temporaryPath, string destination, object attempt)
    {
        // Always attach the session, timer and finish task before an immediate native
        // failure can finalize the view (including startup failure callbacks).
        await Task.Yield();
        var saved = false;
        var discarded = false;
        try
        {
            var result = await session.Completion;
            _phase = RecordingPhase.Saving;
            UpdateRecordingPresentation();
            // A device failure or WAV size limit may stop capture while a close/discard
            // dialog is open. Wait for that choice before publishing the recording.
            if (_closeDecision is { } decision) await decision.Task;
            _stopRequested = true;
            _timer?.Dispose();
            _timer = null;
            if (!_disposed) { _status.Text = "Finalizing recording..."; UpdateControls(); }
            // Capture callbacks have stopped. Release native handles before moving/deleting.
            var finalized = await _worker.Run(_ =>
            {
                var elapsed = session.Elapsed;
                session.Dispose();
                // Read the current disposition after releasing the native handles:
                // an unload may have requested discard while this work was queued.
                if (_discard || _disposed) { RecordingOutput.Discard(temporaryPath); return (Discard: true, Elapsed: elapsed); }
                if (result.Error is not null) return (Discard: false, Elapsed: elapsed); // Keep a partial file for recovery.
                RecordingOutput.Publish(temporaryPath, destination, overwrite: false);
                return (Discard: false, Elapsed: elapsed);
            });
            var discard = finalized.Discard;
            _captureElapsed = finalized.Elapsed;
            discarded = discard;
            if (!_disposed)
            {
                if (discard)
                {
                    _status.Text = "Recording discarded.";
                    _phase = RecordingPhase.Finished;
                    _path.Text = "";
                }
                else if (result.Error is not null)
                {
                    _status.Text = (_sourceLostReason is { } lost ? lost + " " : "") + "Recording failed. " + result.Error + " A partial file may be available at the path below.";
                    _path.Text = temporaryPath;
                    _savedPath = temporaryPath;
                    _phase = RecordingPhase.Error;
                }
                else
                {
                    saved = true;
                    _phase = RecordingPhase.Finished;
                    _savedPath = destination;
                    _path.Text = destination;
                    _status.Text = (_sourceLostReason is { } lost ? lost + " " : "") + (result.Notice ?? "Recording saved.");
                }
            }
        }
        catch (Exception ex)
        {
            _phase = RecordingPhase.Error;
            if (!_disposed)
            {
                _savedPath = temporaryPath;
                _path.Text = temporaryPath;
                _status.Text = "Could not finish saving. " + DescribeError(ex) + " The temporary recording has been kept at the path below.";
            }
        }
        finally
        {
            if (ReferenceEquals(_startAttempt, attempt))
            {
                _timer?.Dispose();
                _timer = null;
                _session = null;
                _startAttempt = null;
                _starting = false;
                Volatile.Write(ref _activeSelection, null);
                _stopRequested = false;
                if (!_disposed) { UpdateRecordingPresentation(); SetBusy(false); }
                else _worker.Dispose();
            }
        }
        return saved || discarded;
    }

    private async Task<bool> StopAsync(bool discard)
    {
        if (discard) _discard = true;
        if (_session is not { } session) return !_busy;
        var finish = _finishTask;
        if (!_stopRequested)
        {
            _stopRequested = true;
            _phase = RecordingPhase.Stopping;
            UpdateRecordingPresentation();
            if (!_disposed) { _status.Text = discard ? "Discarding recording..." : "Stopping and saving..."; UpdateControls(); }
            try { await _worker.Run(_ => { if (!session.Completion.IsCompleted) session.Stop(); }); }
            catch (Exception ex)
            {
                // 동시에 완료된 세션의 저장/종료 상태를 늦은 정지 오류가 되돌리면 안 된다.
                if (_session == session && _phase == RecordingPhase.Stopping && !session.Completion.IsCompleted)
                {
                    _stopRequested = false;
                    _phase = RecordingPhase.Active;
                    UpdateRecordingPresentation();
                    if (!_disposed) { _status.Text = "Could not stop recording. " + DescribeError(ex); UpdateControls(); }
                }
                return false;
            }
        }
        return finish is not null && await finish;
    }

    private async Task DiscardAsync()
    {
        if (_closeDialog || _session is null || _stopRequested) return;
        _closeDialog = true;
        _closeDecision = new(TaskCreationOptions.RunContinuationsAsynchronously);
        UpdateControls();
        try
        {
            var dialog = new ContentDialog
            {
                XamlRoot = XamlRoot, Title = "Discard this recording?",
                Content = "The current recording will be deleted. Existing files are not changed.",
                PrimaryButtonText = "Discard", CloseButtonText = "Keep recording", DefaultButton = ContentDialogButton.Close,
            };
            var discard = await dialog.ShowAsync() == ContentDialogResult.Primary;
            ResolveCloseDecision(discard);
            if (discard) await StopAsync(true);
        }
        catch (Exception ex) { if (!_disposed) _status.Text = "Could not confirm discard. " + DescribeError(ex); }
        finally { EndCloseDecision(); }
    }

    public async Task<bool> ConfirmCloseAsync()
    {
        if (!_busy) return true;
        if (_starting || _closeDialog) return false;
        if (_stopRequested) return _finishTask is not null && await _finishTask;
        _closeDialog = true;
        _closeDecision = new(TaskCreationOptions.RunContinuationsAsynchronously);
        UpdateControls();
        try
        {
            var dialog = new ContentDialog
            {
                XamlRoot = XamlRoot, Title = "Recording is in progress",
                Content = "Stop and save before leaving, or discard this recording.",
                PrimaryButtonText = "Stop and save", SecondaryButtonText = "Discard", CloseButtonText = "Keep recording",
                DefaultButton = ContentDialogButton.Close,
            };
            var choice = await dialog.ShowAsync();
            ResolveCloseDecision(choice == ContentDialogResult.Secondary);
            if (choice == ContentDialogResult.None) return false;
            return await StopAsync(choice == ContentDialogResult.Secondary);
        }
        catch (Exception ex)
        {
            if (!_disposed) _status.Text = "Could not confirm stopping the recording. " + DescribeError(ex);
            return false;
        }
        finally { EndCloseDecision(); }
    }

    private void ResolveCloseDecision(bool discard)
    {
        if (discard) _discard = true;
        _closeDecision?.TrySetResult(discard);
    }

    private void EndCloseDecision()
    {
        _closeDecision?.TrySetResult(false);
        _closeDecision = null;
        _closeDialog = false;
        if (!_disposed) UpdateControls();
    }

    private async Task OpenFolderAsync()
    {
        if (_savedPath is not { } path) return;
        try
        {
            var folder = await StorageFolder.GetFolderFromPathAsync(Path.GetDirectoryName(path)!);
            if (!await Launcher.LaunchFolderAsync(folder)) _status.Text = "Windows could not open the recording folder.";
        }
        catch (Exception ex) { if (!_disposed) _status.Text = "Could not open the folder. " + ex.Message; }
    }

    private nint GetHwnd()
    {
        var environment = XamlRoot?.ContentIslandEnvironment
            ?? throw new InvalidOperationException("Cannot determine the window handle.");
        return Win32Interop.GetWindowFromWindowId(environment.AppWindowId);
    }

    private async void OnUnloaded(object sender, RoutedEventArgs args)
    {
        if (_disposed) return;
        _disposed = true;
        _folders.Changed -= OnFolderChanged;
        Interlocked.Increment(ref _refreshSequence);
        _watchTimer?.Dispose();
        _watchTimer = null;
        _discoveryWorker.Dispose();
        ResolveCloseDecision(true);
        _lifetime.Cancel();
        _timer?.Dispose();
        _timer = null;
        if (_session is not null) await StopAsync(true);
        else if (!_starting) _worker.Dispose();
        _lifetime.Dispose();
    }

    private static string DescribeError(Exception error)
    {
        if (error is DllNotFoundException or FileNotFoundException or BadImageFormatException or TypeInitializationException)
            return "Screen recording components could not load. Install Microsoft Visual C++ 2015-2022 Redistributable (x64), and the Media Feature Pack on Windows N/KN, then restart KOTU. " + error.Message;
        if (error is UnauthorizedAccessException)
            return "Access was denied. Check the save location and Windows microphone privacy settings.";
        return error.Message;
    }
}
