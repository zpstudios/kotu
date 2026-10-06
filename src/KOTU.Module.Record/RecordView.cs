using KOTU.Core.Contracts;
using KOTU.Core.Settings;
using KOTU.Core.Threading;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.Storage;
using Windows.Storage.Pickers;
using Windows.System;

namespace KOTU.Module.Record;

public sealed class RecordView : UserControl, IBottomBarProvider, ICloseGuard, ITrayStatusProvider
{
    private readonly ISettingsService _settings;
    private readonly ModuleWorker _worker = new("KOTU record worker");
    private readonly CancellationTokenSource _lifetime = new();
    private readonly ComboBox _mode = new() { HorizontalAlignment = HorizontalAlignment.Stretch };
    private readonly ComboBox _source = new() { PlaceholderText = "Choose a screen or window", HorizontalAlignment = HorizontalAlignment.Stretch };
    private readonly ComboBox _microphone = new() { PlaceholderText = "No microphone available", HorizontalAlignment = HorizontalAlignment.Stretch };
    private readonly CheckBox _mix = new() { Content = "Include microphone with system audio" };
    private readonly Button _refresh = new() { Content = "Refresh sources" };
    private readonly Button _start = new() { Content = "Start recording" };
    private readonly Button _stop = new() { Content = "Stop and save", IsEnabled = false };
    private readonly Button _cancel = new() { Content = "Discard", IsEnabled = false };
    private readonly Button _folder = new() { Content = "Open folder", IsEnabled = false };
    private readonly Button _changeFolder = new() { Content = "Change folder" };
    private readonly TextBlock _saveFolder = new() { Text = "Preparing save folder...", TextWrapping = TextWrapping.Wrap, IsTextSelectionEnabled = true };
    private readonly TextBlock _clock = new() { Text = "00:00:00", FontSize = 36 };
    private readonly TextBlock _barClock = new() { Text = "Ready", VerticalAlignment = VerticalAlignment.Center };
    private readonly TextBlock _status = new() { Text = "Choose a mode and recording source.", TextWrapping = TextWrapping.Wrap, IsTextSelectionEnabled = true };
    private readonly TextBlock _path = new() { TextWrapping = TextWrapping.Wrap, IsTextSelectionEnabled = true };
    private readonly TextBlock _description = new() { TextWrapping = TextWrapping.Wrap };
    private readonly StackPanel _screenOptions = new() { Spacing = 8 };
    private readonly StackPanel _bar = new() { Orientation = Orientation.Horizontal, Spacing = 8, VerticalAlignment = VerticalAlignment.Center };
    private IRecordingSession? _session;
    private Task<bool>? _finishTask;
    private TaskCompletionSource<bool>? _closeDecision;
    private System.Threading.Timer? _timer;
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
    private bool _refreshing;
    private bool _stopRequested;
    private volatile bool _discard;
    private volatile bool _disposed;
    private bool _closeDialog;
    private int _refreshSequence;
    private int _tickQueued;

    public RecordView(ISettingsService settings)
    {
        _settings = settings;
        _mode.Items.Add("Screen recording");
        _mode.Items.Add("Microphone recording");
        _mode.SelectedIndex = settings.Get("record.mode", "screen") == "microphone" ? 1 : 0;
        _mix.IsChecked = settings.Get("record.includeMicrophone", true);
        _screenOptions.Children.Add(_source);
        _screenOptions.Children.Add(_mix);
        var panel = new StackPanel { Spacing = 14, MaxWidth = 640, Margin = new Thickness(28, 28, 28, 72), HorizontalAlignment = HorizontalAlignment.Stretch };
        panel.Children.Add(new TextBlock { Text = "Record", FontSize = 28 });
        panel.Children.Add(_mode);
        panel.Children.Add(_description);
        panel.Children.Add(_screenOptions);
        panel.Children.Add(new TextBlock { Text = "Microphone" });
        panel.Children.Add(_microphone);
        panel.Children.Add(_refresh);
        panel.Children.Add(new TextBlock { Text = "Save folder" });
        panel.Children.Add(_saveFolder);
        panel.Children.Add(_changeFolder);
        panel.Children.Add(_clock);
        panel.Children.Add(_status);
        panel.Children.Add(_path);
        panel.Children.Add(_folder);
        Content = new ScrollViewer { Content = panel, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        _bar.Children.Add(_start);
        _bar.Children.Add(_stop);
        _bar.Children.Add(_cancel);
        _bar.Children.Add(_barClock);
        _mode.SelectionChanged += (_, _) =>
        {
            UpdateControls();
            if (!_busy && !_refreshing)
                _status.Text = CurrentFolderError ?? (ScreenMode
                    ? _screenError ?? "Choose a screen or window, then start recording."
                    : _microphone.SelectedItem is MicrophoneDevice ? "Ready to record the selected microphone." : "No microphone found. Connect one and refresh.");
        };
        _source.SelectionChanged += (_, _) => UpdateControls();
        _microphone.SelectionChanged += (_, _) => UpdateControls();
        _refresh.Click += async (_, _) => await RefreshAsync();
        _start.Click += async (_, _) => await StartAsync();
        _stop.Click += async (_, _) => await StopAsync(false);
        _cancel.Click += async (_, _) => await DiscardAsync();
        _folder.Click += async (_, _) => await OpenFolderAsync();
        _changeFolder.Click += async (_, _) => await ChangeFolderAsync();
        Loaded += async (_, _) => { await InitializeFoldersAsync(); if (!_disposed) await RefreshAsync(); };
        Unloaded += OnUnloaded;
        UpdateControls();
    }

    private bool ScreenMode => _mode.SelectedIndex == 0;
    private string? CurrentFolder => ScreenMode ? _videoFolder : _audioFolder;
    private string? CurrentFolderError => ScreenMode ? _videoFolderError : _audioFolderError;
    public bool HasUnsavedChanges => _busy;
    public event Action<bool>? UnsavedChanged;
    public event Action? TrayStatusChanged;
    public object? TakeBottomBar() => _bar;
    public TrayStatus GetTrayStatus() => _busy
        ? TrayStatus.Open("REC", _barClock.Text, 0xFFFF5050)
        : TrayStatus.Idle("REC");

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
            ? "MP4 video (H.264/AAC), 30 fps. System audio from the default output is always included, including sounds from other apps. Keep the selected window open and visible."
            : "WAV audio, 48 kHz / 16-bit mono. Only the selected microphone is recorded. Windows must allow microphone access for desktop apps.";
        var selectable = !_busy && !_refreshing && !_folderInitializing && !_choosingFolder;
        _mode.IsEnabled = selectable;
        _source.IsEnabled = selectable;
        _microphone.IsEnabled = selectable;
        _mix.IsEnabled = selectable && _microphone.SelectedItem is MicrophoneDevice;
        _refresh.IsEnabled = selectable;
        _changeFolder.IsEnabled = selectable;
        _saveFolder.Text = _folderInitializing ? "Preparing save folder..." : CurrentFolder ?? CurrentFolderError ?? "Choose a save folder.";
        _start.IsEnabled = selectable && !_closeDialog && CurrentFolder is not null && CurrentFolderError is null && (screen ? _source.SelectedItem is CaptureSource : _microphone.SelectedItem is MicrophoneDevice);
        _stop.IsEnabled = _session is not null && !_stopRequested;
        _cancel.IsEnabled = _session is not null && !_stopRequested;
        _folder.IsEnabled = !_busy && _savedPath is not null;
    }

    private async Task RefreshAsync()
    {
        if (_disposed || _busy || _refreshing || _choosingFolder || _folderInitializing) return;
        var sequence = ++_refreshSequence;
        _refreshing = true;
        UpdateControls();
        _status.Text = "Finding screens, windows and microphones...";
        try
        {
            var window = GetHwnd();
            var previousId = (_microphone.SelectedItem as MicrophoneDevice)?.Id
                ?? _settings.Get("record.microphoneId", "");
            var result = await _worker.Run(_ =>
            {
                IReadOnlyList<CaptureSource> sources = [];
                IReadOnlyList<MicrophoneDevice> microphones = [];
                string? screenError = null;
                string? audioError = null;
                try { sources = RecordingBackend.GetScreenSources(window); }
                catch (Exception ex) { screenError = DescribeError(ex); }
                try { microphones = MicrophoneRecordingSession.GetDevices(); }
                catch (Exception ex) { audioError = DescribeError(ex); }
                return (sources, microphones, screenError, audioError);
            }, _lifetime.Token);
            if (_disposed || sequence != _refreshSequence) return;
            _screenError = result.screenError ?? (result.sources.Count == 0 ? "No screen or window is available. Connect a display and refresh." : null);
            _source.ItemsSource = result.sources;
            _source.SelectedIndex = result.sources.Count > 0 ? 0 : -1;
            _microphone.ItemsSource = result.microphones;
            _microphone.SelectedItem = result.microphones.FirstOrDefault(d => d.Id == previousId)
                ?? result.microphones.FirstOrDefault();
            _status.Text = CurrentFolderError ?? (ScreenMode
                ? _screenError ?? "Choose a screen or window, then start recording."
                : result.audioError ?? (result.microphones.Count > 0 ? "Ready to record the selected microphone." : "No microphone found. Connect one and refresh."));
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { if (!_disposed) _status.Text = DescribeError(ex); }
        finally
        {
            _refreshing = false;
            if (!_disposed) UpdateControls();
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
        if (_disposed || _busy || _refreshing || _folderInitializing || _choosingFolder) return;
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
        if (_disposed || _busy || _refreshing || _closeDialog || _choosingFolder || _folderInitializing || CurrentFolderError is not null || CurrentFolder is not { } saveFolder) return;
        var screen = ScreenMode;
        var source = _source.SelectedItem as CaptureSource;
        var microphone = _microphone.SelectedItem as MicrophoneDevice;
        if (screen && source is null || !screen && microphone is null) return;
        var includeMicrophone = screen && _mix.IsChecked == true && microphone is not null;
        var mixPreference = _mix.IsChecked == true;
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
                    _settings.Set("record.microphoneId", microphone?.Id ?? "");
                    _settings.Save();
                }
                catch { /* A settings failure must not prevent recording. */ }
                context.ThrowIfCancelled();
                return screen
                    ? RecordingBackend.StartScreen(source!, includeMicrophone ? microphone!.Id : null, output)
                    : new MicrophoneRecordingSession(microphone!.Id, output, _worker.Post);
            }, _lifetime.Token);
            _session = session;
            _starting = false;
            _clock.Text = "00:00:00";
            _barClock.Text = "Starting";
            _finishTask = FinishAsync(session, output, destination);
            temporaryPath = null; // FinishAsync owns the file from here.
            if (_disposed)
            {
                _discard = true;
                await StopAsync(true);
                return;
            }
            _status.Text = screen
                ? includeMicrophone ? "Recording system audio and microphone. Stop to save the MP4." : "Recording system audio. Stop to save the MP4."
                : "Recording microphone. Stop to save the WAV.";
            _timer = new System.Threading.Timer(_ => QueueTick(session), null, 0, 250);
            UpdateControls();
        }
        catch (OperationCanceledException) { if (!_disposed) _status.Text = "Recording canceled."; }
        catch (Exception ex)
        {
            keepTemporary = true;
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
                if (!_disposed) SetBusy(false);
                else _worker.Dispose();
            }
        }
    }

    private void QueueTick(IRecordingSession session)
    {
        // At most one UI update is pending, even if rendering is temporarily delayed.
        if (Interlocked.Exchange(ref _tickQueued, 1) != 0) return;
        var elapsed = session.Elapsed;
        var label = $"{(int)elapsed.TotalHours:00}:{elapsed.Minutes:00}:{elapsed.Seconds:00}";
        if (!DispatcherQueue.TryEnqueue(() =>
        {
            Interlocked.Exchange(ref _tickQueued, 0);
            if (_disposed || _session != session) return;
            _clock.Text = label;
            _barClock.Text = _stopRequested ? "Saving..." : session.IsRecording ? label : "Starting...";
            TrayStatusChanged?.Invoke();
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
            // A device failure or WAV size limit may stop capture while a close/discard
            // dialog is open. Wait for that choice before publishing the recording.
            if (_closeDecision is { } decision) await decision.Task;
            _stopRequested = true;
            _timer?.Dispose();
            _timer = null;
            if (!_disposed) { _status.Text = "Finalizing recording..."; _barClock.Text = "Saving..."; UpdateControls(); }
            // Capture callbacks have stopped. Release native handles before moving/deleting.
            var discard = await _worker.Run(_ =>
            {
                session.Dispose();
                // Read the current disposition after releasing the native handles:
                // an unload may have requested discard while this work was queued.
                if (_discard || _disposed) { RecordingOutput.Discard(temporaryPath); return true; }
                if (result.Error is not null) return false; // Keep a partial file for recovery.
                RecordingOutput.Publish(temporaryPath, destination, overwrite: false);
                return false;
            });
            discarded = discard;
            if (!_disposed)
            {
                if (discard)
                {
                    _status.Text = "Recording discarded.";
                    _path.Text = "";
                }
                else if (result.Error is not null)
                {
                    _status.Text = "Recording failed. " + result.Error + " A partial file may be available at the path below.";
                    _path.Text = temporaryPath;
                    _savedPath = temporaryPath;
                }
                else
                {
                    saved = true;
                    _savedPath = destination;
                    _path.Text = destination;
                    _status.Text = result.Notice ?? "Recording saved.";
                }
            }
        }
        catch (Exception ex)
        {
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
            _stopRequested = false;
            if (!_disposed) { _barClock.Text = "Ready"; SetBusy(false); }
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
            if (!_disposed) { _status.Text = discard ? "Discarding recording..." : "Stopping and saving..."; UpdateControls(); }
            try { await _worker.Run(_ => { if (!session.Completion.IsCompleted) session.Stop(); }); }
            catch (Exception ex)
            {
                _stopRequested = false;
                if (!_disposed) { _status.Text = "Could not stop recording. " + DescribeError(ex); UpdateControls(); }
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
