using System.Collections.ObjectModel;
using KOTU.Core.Contracts;
using KOTU.Core.Settings;
using KOTU.Core.Threading;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Ellipse = Microsoft.UI.Xaml.Shapes.Ellipse;
using Windows.Storage;
using Windows.Storage.Pickers;
using Windows.System;

namespace KOTU.Module.Record;

public sealed class RecordView : UserControl, IBottomBarProvider, ICloseGuard, ITrayStatusProvider
{
    private readonly ISettingsService _settings;
    private readonly ModuleWorker _worker = new("KOTU record worker");
    private readonly ModuleWorker _discoveryWorker = new("KOTU record source discovery", ThreadPriority.BelowNormal);
    private readonly CancellationTokenSource _lifetime = new();
    private readonly ListView _mode = new() { SelectionMode = ListViewSelectionMode.Single, MaxHeight = 100 };
    private readonly ListView _source = new() { SelectionMode = ListViewSelectionMode.Single, MaxHeight = 260, DisplayMemberPath = "Label" };
    private readonly ListView _microphone = new() { SelectionMode = ListViewSelectionMode.Single, MaxHeight = 180, DisplayMemberPath = "Label" };
    private readonly ListView _output = new() { SelectionMode = ListViewSelectionMode.Single, MaxHeight = 180, DisplayMemberPath = "Label" };
    private readonly ObservableCollection<CaptureSource> _sources = [];
    private readonly ObservableCollection<MicrophoneDevice> _microphones = [];
    private readonly ObservableCollection<OutputDevice> _outputs = [];
    private readonly CheckBox _includeOutput = new() { Content = "Include system audio from the selected output" };
    private readonly CheckBox _mix = new() { Content = "Include the selected microphone" };
    private readonly TextBlock _discoveryStatus = new() { Text = "Finding recording sources...", TextWrapping = TextWrapping.Wrap };
    private readonly Button _refresh = new() { Content = "Refresh sources" };
    private readonly Button _start = new() { Content = "Start recording" };
    private readonly Button _stop = new() { Content = "Stop and save", IsEnabled = false };
    private readonly Button _cancel = new() { Content = "Discard", IsEnabled = false };
    private readonly Button _folder = new() { Content = "Open folder", IsEnabled = false };
    private readonly Button _changeFolder = new() { Content = "Change folder" };
    private readonly TextBlock _saveFolder = new() { Text = "Preparing save folder...", TextWrapping = TextWrapping.Wrap, IsTextSelectionEnabled = true };
    private readonly TextBlock _barClock = new() { Text = "Ready", VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis };
    private readonly TextBlock _captureState = new() { Text = "Ready", TextWrapping = TextWrapping.Wrap };
    private readonly TextBlock _captureSources = new() { TextWrapping = TextWrapping.Wrap, IsTextSelectionEnabled = true };
    private readonly Ellipse _captureDot = CreateRecordingDot();
    private readonly Ellipse _barDot = CreateRecordingDot();
    private readonly TextBlock _status = new() { Text = "Choose a mode and recording source.", TextWrapping = TextWrapping.Wrap, IsTextSelectionEnabled = true };
    private readonly TextBlock _path = new() { TextWrapping = TextWrapping.Wrap, IsTextSelectionEnabled = true };
    private readonly TextBlock _description = new() { TextWrapping = TextWrapping.Wrap };
    private readonly StackPanel _screenOptions = new() { Spacing = 8 };
    private readonly Grid _bar = new() { ColumnSpacing = 6, MinWidth = 152, HorizontalAlignment = HorizontalAlignment.Stretch, VerticalAlignment = VerticalAlignment.Center };
    private IRecordingSession? _session;
    private Task<bool>? _finishTask;
    private TaskCompletionSource<bool>? _closeDecision;
    private System.Threading.Timer? _timer;
    private System.Threading.Timer? _watchTimer;
    private CancellationToken _discoveryCancellation;
    private nint _ownWindow;
    private RecordingSelection _discoverySelection = new(null, null, null);
    private RecordingSelection? _activeSelection;
    private string? _selectedSourceKey;
    private string? _selectedMicrophoneId;
    private string? _selectedOutputId;
    private string? _sourceLostReason;
    private bool _sourcesInitialized, _microphonesInitialized, _outputsInitialized;
    private bool _applyingSources;
    private bool _watchStarted;
    private string? _savedPath;
    private string? _screenError;
    private string? _videoFolder;
    private string? _audioFolder;
    private string? _videoFolderError;
    private string? _audioFolderError;
    private bool _folderInitializing = true;
    private bool _folderInitializationStarted;
    private bool _choosingFolder;
    private bool _busy;
    private bool _starting;
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
    private bool _activeScreen;
    private bool _captureObserved;
    private TimeSpan _captureElapsed;
    private string _activeSourceLabels = "";

    public RecordView(ISettingsService settings)
    {
        _settings = settings;
        _discoveryCancellation = _lifetime.Token;
        _selectedSourceKey = settings.Get("record.sourceKey", "");
        _selectedMicrophoneId = settings.Get("record.microphoneId", "");
        _selectedOutputId = settings.Get("record.outputId", "");
        _mode.Items.Add("Screen recording");
        _mode.Items.Add("Microphone recording");
        _mode.SelectedIndex = settings.Get("record.mode", "screen") == "microphone" ? 1 : 0;
        _mix.IsChecked = settings.Get("record.includeMicrophone", true);
        _includeOutput.IsChecked = settings.Get("record.includeSystemAudio", true);
        _source.ItemsSource = _sources;
        _microphone.ItemsSource = _microphones;
        _output.ItemsSource = _outputs;
        _screenOptions.Children.Add(new TextBlock { Text = "Screen or window" });
        _screenOptions.Children.Add(_source);
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
        panel.Children.Add(_description);
        panel.Children.Add(_screenOptions);
        panel.Children.Add(new TextBlock { Text = "Microphone" });
        panel.Children.Add(_microphone);
        panel.Children.Add(_refresh);
        panel.Children.Add(_discoveryStatus);
        panel.Children.Add(new TextBlock { Text = "Save folder" });
        panel.Children.Add(_saveFolder);
        panel.Children.Add(_changeFolder);
        panel.Children.Add(_status);
        panel.Children.Add(_path);
        Content = new ScrollViewer { Content = panel, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        ConfigureBarButton(_start, "\uE7C8", "Start recording", "Start recording");
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
        var barControls = new FrameworkElement[] { _start, _stop, _cancel, _folder, barStatus };
        for (var i = 0; i < barControls.Length; i++)
        {
            _bar.ColumnDefinitions.Add(new ColumnDefinition { Width = i < 4 ? GridLength.Auto : new GridLength(1, GridUnitType.Star) });
            Grid.SetColumn(barControls[i], i);
            _bar.Children.Add(barControls[i]);
        }
        _mode.SelectionChanged += (_, _) =>
        {
            UpdateControls();
            if (!_busy && !_catalogInitializing)
                _status.Text = CurrentFolderError ?? (ScreenMode
                    ? _screenError ?? "Choose a screen or window, then start recording."
                    : _microphone.SelectedItem is MicrophoneDevice ? "Ready to record the selected microphone." : "Choose an available microphone.");
        };
        _source.SelectionChanged += (_, _) =>
        {
            if (!_applyingSources && _source.SelectedItem is CaptureSource source) _selectedSourceKey = RecordingSourceCatalog.Key(source);
            UpdateDiscoverySelection(); UpdateControls();
        };
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
        _start.Click += async (_, _) => await StartAsync();
        _stop.Click += async (_, _) => await StopAsync(false);
        _cancel.Click += async (_, _) => await DiscardAsync();
        _folder.Click += async (_, _) => await OpenFolderAsync();
        _changeFolder.Click += async (_, _) => await ChangeFolderAsync();
        Loaded += async (_, _) => { StartDiscovery(); await InitializeFoldersAsync(); };
        Unloaded += OnUnloaded;
        UpdateDiscoverySelection();
        UpdateControls();
    }

    private bool ScreenMode => _mode.SelectedIndex == 0;
    private string? CurrentFolder => ScreenMode ? _videoFolder : _audioFolder;
    private string? CurrentFolderError => ScreenMode ? _videoFolderError : _audioFolderError;
    public bool HasUnsavedChanges => _busy;
    public event Action<bool>? UnsavedChanged;
    public event Action? TrayStatusChanged;
    public object? TakeBottomBar() => _bar;
    public TrayStatus GetTrayStatus() => _presentation.Tray;

    private static Ellipse CreateRecordingDot() => new()
    {
        Width = 8, Height = 8, Fill = new SolidColorBrush(Colors.Red),
        VerticalAlignment = VerticalAlignment.Center, Visibility = Visibility.Collapsed, IsHitTestVisible = false,
    };

    private void UpdateRecordingPresentation(bool recording = false, bool completed = false)
    {
        if (_disposed) return;
        _presentation = RecordingPresentation.Create(_phase, _activeScreen, recording, completed, _captureObserved, _captureElapsed, _activeSourceLabels);
        _captureState.Text = _presentation.Text;
        _captureSources.Text = _activeSourceLabels;
        _captureSources.Visibility = string.IsNullOrEmpty(_activeSourceLabels) ? Visibility.Collapsed : Visibility.Visible;
        _barClock.Text = _presentation.Detail;
        _captureDot.Visibility = _barDot.Visibility = _presentation.IsRecording ? Visibility.Visible : Visibility.Collapsed;
        ToolTipService.SetToolTip(_barClock, _presentation.Detail);
        AutomationProperties.SetName(_captureState, _presentation.Text);
        AutomationProperties.SetName(_barClock, _presentation.Detail);
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
        var screen = ScreenMode;
        _screenOptions.Visibility = screen ? Visibility.Visible : Visibility.Collapsed;
        _description.Text = screen
            ? "MP4 video (H.264), 30 fps. Choose a screen or window and optional audio sources. System audio includes other apps on the selected output. Uncheck both audio choices for silent video. Keep the selected window open and visible."
            : "WAV audio, 48 kHz / 16-bit mono. Only the selected microphone is recorded. Windows must allow microphone access for desktop apps.";
        var selectable = !_busy && !_folderInitializing && !_choosingFolder;
        _mode.IsEnabled = selectable;
        _source.IsEnabled = selectable;
        _microphone.IsEnabled = selectable && (!screen || _mix.IsChecked == true);
        _output.IsEnabled = selectable && _includeOutput.IsChecked == true;
        _mix.IsEnabled = selectable;
        _includeOutput.IsEnabled = selectable;
        _refresh.IsEnabled = !_disposed;
        _changeFolder.IsEnabled = selectable;
        _saveFolder.Text = _folderInitializing ? "Preparing save folder..." : CurrentFolder ?? CurrentFolderError ?? "Choose a save folder.";
        _start.IsEnabled = selectable && !_catalogInitializing && !_closeDialog && CurrentFolder is not null && CurrentFolderError is null &&
            (screen ? _source.SelectedItem is CaptureSource && (_includeOutput.IsChecked != true || _output.SelectedItem is OutputDevice) &&
                (_mix.IsChecked != true || _microphone.SelectedItem is MicrophoneDevice) : _microphone.SelectedItem is MicrophoneDevice);
        _stop.IsEnabled = _session is not null && !_stopRequested;
        _cancel.IsEnabled = _session is not null && !_stopRequested;
        _folder.IsEnabled = !_busy && _savedPath is not null;
    }

    private void UpdateDiscoverySelection()
    {
        if (_applyingSources) return;
        // Immutable preferences are the only view state read by the discovery thread.
        Volatile.Write(ref _discoverySelection, new(_source.SelectedItem as CaptureSource,
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
            var preferred = Volatile.Read(ref _activeSelection) ?? Volatile.Read(ref _discoverySelection);
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
                        retryObservation = _busy && Volatile.Read(ref _activeSelection) is { } active && !RecordingSourceCatalog.Covers(active, preferred);
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

    private void ApplySources<T>(ListView list, ObservableCollection<T> current, IReadOnlyList<T>? snapshot,
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

    private void ApplySourceSnapshot(RecordingSourceSnapshot snapshot, RecordingSelection observed)
    {
        _applyingSources = true;
        try
        {
            ApplySources(_source, _sources, snapshot.Sources, ref _sourcesInitialized, ref _selectedSourceKey, RecordingSourceCatalog.Key, _ => false);
            ApplySources(_microphone, _microphones, snapshot.Microphones, ref _microphonesInitialized, ref _selectedMicrophoneId, d => d.Id, d => d.IsDefault);
            ApplySources(_output, _outputs, snapshot.Outputs, ref _outputsInitialized, ref _selectedOutputId, d => d.Id, d => d.IsDefault);
        }
        finally { _applyingSources = false; }
        _catalogInitializing = false;
        _screenError = snapshot.ScreenError;
        var errors = new[] { snapshot.ScreenError is { } screen ? "Screens/windows: " + screen : null,
            snapshot.MicrophoneError is { } mic ? "Microphones: " + mic : null,
            snapshot.OutputError is { } output ? "System outputs: " + output : null }.Where(s => s is not null);
        var message = string.Join("\n", errors);
        if (message.Length == 0) message = "Sources update automatically. Select the sources to record.";
        if (!_busy && (ScreenMode && _source.SelectedItem is null ||
            (ScreenMode && _includeOutput.IsChecked == true && _output.SelectedItem is null) ||
            ((!ScreenMode || _mix.IsChecked == true) && _microphone.SelectedItem is null)))
            message += " Choose an available source; missing selections are not replaced automatically.";
        if (_busy && _sourceLostReason is { } lost) message = lost + " Stopping and saving the current recording.";
        if (_discoveryStatus.Text != message) _discoveryStatus.Text = message;
        UpdateDiscoverySelection();
        UpdateControls();
        if (_busy && _sourceLostReason is null && Volatile.Read(ref _activeSelection) is { } active &&
            RecordingSourceCatalog.Covers(active, observed) &&
            RecordingSourceCatalog.Missing(active, snapshot) is { } reason)
        {
            _sourceLostReason = reason;
            _discoveryStatus.Text = reason + " Stopping and saving the current recording.";
            if (_session is not null && !_stopRequested) _ = StopAsync(false);
        }
    }

    // A391: 기본 위치 복원, 폴더 생성 및 권한 확인은 장치 조회와 분리된 워커 초기화다.
    private async Task InitializeFoldersAsync()
    {
        if (_disposed || _folderInitializationStarted) return;
        _folderInitializationStarted = true;
        try
        {
            var result = await _worker.Run(_ =>
            {
                (string? Folder, string? Error) Resolve(bool screen)
                {
                    try { return (RecordingOutput.PrepareFolder(_settings.Get(screen ? "record.videoFolder" : "record.audioFolder", ""), screen), null); }
                    catch (Exception ex) { return (null, "Could not prepare the save folder. " + DescribeError(ex)); }
                }
                return (Video: Resolve(true), Audio: Resolve(false));
            }, _lifetime.Token);
            if (_disposed) return;
            (_videoFolder, _videoFolderError) = result.Video;
            (_audioFolder, _audioFolderError) = result.Audio;
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            if (!_disposed) _videoFolderError = _audioFolderError = "Could not prepare the save folder. " + DescribeError(ex);
        }
        finally
        {
            _folderInitializing = false;
            if (!_disposed) UpdateControls();
        }
    }

    private async Task ChangeFolderAsync()
    {
        if (_disposed || _busy || _folderInitializing || _choosingFolder) return;
        var screen = ScreenMode;
        _choosingFolder = true;
        UpdateControls();
        try
        {
            var picker = new FolderPicker { SuggestedStartLocation = screen ? PickerLocationId.VideosLibrary : PickerLocationId.MusicLibrary };
            picker.FileTypeFilter.Add("*");
            WinRT.Interop.InitializeWithWindow.Initialize(picker, GetHwnd());
            var selection = await picker.PickSingleFolderAsync();
            if (_disposed || selection is null) return;
            var selectedPath = selection.Path;
            var folder = await _worker.Run(context =>
            {
                var prepared = RecordingOutput.PrepareFolder(selectedPath, screen);
                context.Cancellation.ThrowIfCancellationRequested();
                var key = screen ? "record.videoFolder" : "record.audioFolder";
                var previous = _settings.Get(key, "");
                _settings.Set(key, prepared);
                try { _settings.Save(); }
                catch { _settings.Set(key, previous); throw; }
                return prepared;
            }, _lifetime.Token);
            if (_disposed) return;
            if (screen) { _videoFolder = folder; _videoFolderError = null; }
            else { _audioFolder = folder; _audioFolderError = null; }
            _status.Text = "Save folder updated.";
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { if (!_disposed) _status.Text = "Could not change the save folder. " + DescribeError(ex); }
        finally { _choosingFolder = false; if (!_disposed) UpdateControls(); }
    }

    private async Task StartAsync()
    {
        if (_disposed || _busy || _catalogInitializing || _closeDialog || _choosingFolder || _folderInitializing || CurrentFolderError is not null || CurrentFolder is not { } saveFolder) return;
        var screen = ScreenMode;
        var source = _source.SelectedItem as CaptureSource;
        var microphone = _microphone.SelectedItem as MicrophoneDevice;
        var outputDevice = _output.SelectedItem as OutputDevice;
        if (screen && source is null || !screen && microphone is null) return;
        if (screen && (_mix.IsChecked == true && microphone is null || _includeOutput.IsChecked == true && outputDevice is null)) return;
        var includeMicrophone = screen && _mix.IsChecked == true;
        var includeSystemAudio = screen && _includeOutput.IsChecked == true;
        var mixPreference = _mix.IsChecked == true;
        var systemPreference = _includeOutput.IsChecked == true;
        _sourceLostReason = null;
        _activeScreen = screen;
        _activeSourceLabels = string.Join(" · ", new[]
        {
            screen ? source!.Label : null,
            includeSystemAudio ? "System audio: " + outputDevice!.Label : null,
            !screen || includeMicrophone ? "Microphone: " + microphone!.Label : null,
        }.Where(label => label is not null));
        _captureObserved = false;
        _captureElapsed = TimeSpan.Zero;
        _phase = RecordingPhase.Starting;
        UpdateRecordingPresentation();
        Volatile.Write(ref _activeSelection, new(screen ? source : null,
            !screen || includeMicrophone ? microphone!.Id : null, includeSystemAudio ? outputDevice!.Id : null));
        _starting = true;
        _discard = false;
        _stopRequested = false;
        SetBusy(true);
        string? temporaryPath = null;
        var keepTemporary = false;
        var destinationPrepared = false;
        try
        {
            _status.Text = "Checking save folder...";
            var destination = await _worker.Run(context =>
            {
                var folder = RecordingOutput.PrepareFolder(saveFolder, screen);
                context.ThrowIfCancelled();
                return RecordingOutput.CreateDestinationPath(folder, screen, DateTime.Now);
            }, _lifetime.Token);
            if (_disposed) return;
            destinationPrepared = true;
            // A sibling temporary file preserves an existing destination on cancel/failure.
            temporaryPath = RecordingOutput.CreateTemporaryPath(destination, screen ? ".mp4" : ".wav");
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
                    _settings.Set("record.mode", screen ? "screen" : "microphone");
                    _settings.Set("record.includeMicrophone", mixPreference);
                    _settings.Set("record.includeSystemAudio", systemPreference);
                    _settings.Set("record.sourceKey", source is null ? "" : RecordingSourceCatalog.Key(source));
                    _settings.Set("record.outputId", outputDevice?.Id ?? "");
                    _settings.Set("record.microphoneId", microphone?.Id ?? "");
                    _settings.Save();
                }
                catch { /* A settings failure must not prevent recording. */ }
                context.ThrowIfCancelled();
                return screen
                    ? RecordingBackend.StartScreen(source!, includeMicrophone ? microphone!.Id : null, includeSystemAudio ? outputDevice!.Id : null, output)
                    : new MicrophoneRecordingSession(microphone!.Id, output, _worker.Post);
            }, _lifetime.Token);
            _session = session;
            _starting = false;
            _phase = RecordingPhase.Active;
            UpdateRecordingPresentation();
            _finishTask = FinishAsync(session, output, destination);
            temporaryPath = null; // FinishAsync owns the file from here.
            if (_disposed)
            {
                _discard = true;
                await StopAsync(true);
                return;
            }
            if (_sourceLostReason is not null)
            {
                await StopAsync(false);
                return;
            }
            _status.Text = screen ? "Stop to save the MP4 recording." : "Stop to save the WAV recording.";
            _timer = new System.Threading.Timer(_ => QueueTick(session), null, 0, 250);
            UpdateControls();
        }
        catch (OperationCanceledException) { _phase = RecordingPhase.Finished; if (!_disposed) _status.Text = "Recording canceled."; }
        catch (Exception ex)
        {
            keepTemporary = true;
            _phase = RecordingPhase.Error;
            if (!destinationPrepared && !_disposed)
            {
                var error = "Could not prepare the save folder. " + DescribeError(ex);
                if (screen) _videoFolderError = error;
                else _audioFolderError = error;
            }
            if (!_disposed) _status.Text = "Recording could not start. " + DescribeError(ex);
        }
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
            _starting = false;
            if (_session is null)
            {
                Volatile.Write(ref _activeSelection, null);
                if (!_disposed) { UpdateRecordingPresentation(); SetBusy(false); }
                else _worker.Dispose();
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
        if (!DispatcherQueue.TryEnqueue(() =>
        {
            Interlocked.Exchange(ref _tickQueued, 0);
            if (_disposed || _session != session || _phase != RecordingPhase.Active) return;
            _captureElapsed = elapsed;
            _captureObserved |= recording;
            // 완료가 UI 대기 중 발생했다면 이전 녹화 스냅샷을 활성 표식으로 쓰지 않는다.
            UpdateRecordingPresentation(recording, completed || session.Completion.IsCompleted);
        })) Interlocked.Exchange(ref _tickQueued, 0);
    }

    private async Task<bool> FinishAsync(IRecordingSession session, string temporaryPath, string destination)
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
            _timer?.Dispose();
            _timer = null;
            _session = null;
            Volatile.Write(ref _activeSelection, null);
            _stopRequested = false;
            if (!_disposed) { UpdateRecordingPresentation(); SetBusy(false); }
            else _worker.Dispose();
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
