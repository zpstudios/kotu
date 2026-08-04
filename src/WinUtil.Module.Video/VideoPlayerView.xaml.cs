using LibVLCSharp.Platforms.Windows;
using LibVLCSharp.Shared;
using Microsoft.UI;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Windows.ApplicationModel.DataTransfer;
using Windows.Storage.Pickers;
using WinRT.Interop;
using WinUtil.Core.Contracts;
using WinUtil.Core.Settings;

namespace WinUtil.Module.Video;

/// <summary>
/// 동영상 플레이어 화면. 재생/일시정지, 시킹(슬라이더·←/→ 5초), 볼륨(↑/↓)·음소거(M),
/// 배속, 자막(자동 탐지 + CP949 자동 변환), 이어보기, 전체화면(F11/더블클릭)을 제공한다.
/// libvlc 이벤트는 백그라운드 스레드에서 오므로 UI 갱신은 DispatcherQueue로 넘긴다.
/// </summary>
public sealed partial class VideoPlayerView : UserControl
{
    private const long SeekStepMs = 5_000;
    private const int VolumeStep = 5;
    private const long ResumeReportIntervalMs = 10_000;
    private static readonly float[] Speeds = [0.5f, 0.75f, 1.0f, 1.25f, 1.5f, 2.0f];

    private readonly ISettingsService _settings;
    private readonly PlaybackResumeStore _resumeStore;
    private string? _filePath;

    private LibVLC? _libVlc;
    private MediaPlayer? _player;
    private List<string> _subtitleFiles = [];
    private long _durationMs;
    private long _lastReportedMs;
    private long _pendingResumeMs = -1;
    private bool _pendingAutoSubtitle;
    private bool _suppressSeekEvent;
    private bool _suppressVolumeEvent;
    private bool _suppressSubtitleEvent;
    private bool _tornDown;

    public VideoPlayerView(OpenContext context, ISettingsService settings)
    {
        InitializeComponent();
        _settings = settings;
        _resumeStore = new PlaybackResumeStore(settings);
        _filePath = context.FilePath is { } p && File.Exists(p) ? p : null;

        foreach (var s in Speeds)
            SpeedBox.Items.Add($"{s:0.##}×");
        _suppressSubtitleEvent = true;
        SpeedBox.SelectedIndex = Array.IndexOf(Speeds, 1.0f);
        SubtitleBox.Items.Add("자막 없음");
        SubtitleBox.SelectedIndex = 0;
        SubtitleBox.IsEnabled = false;
        _suppressSubtitleEvent = false;

        _suppressVolumeEvent = true;
        VolumeSlider.Value = Math.Clamp(_settings.Get("video.volume", 80), 0, 100);
        _suppressVolumeEvent = false;

        if (_filePath is null)
            PlaceholderText.Visibility = Visibility.Visible;

        Vlc.Initialized += OnVlcInitialized;
        Loaded += (_, _) => Focus(FocusState.Programmatic);
        Unloaded += OnUnloaded;
    }

    // ---------- libvlc 초기화 / 해제 ----------

    /// <summary>
    /// VideoView의 D3D 스왑체인이 준비되면 호출된다. 여기서만 LibVLC를 만들 수 있다.
    /// 파일 없이 열렸어도 플레이어는 만들어 둔다 — 이후 열기 버튼/드롭으로 파일이 올 수 있다.
    /// </summary>
    private void OnVlcInitialized(object? sender, InitializedEventArgs e)
    {
        if (_tornDown) return;

        try
        {
            // libvlc 네이티브 dll은 libvlc\win-x64\ 하위에 배포되므로 검색 경로 등록이 선행돼야 한다.
            // 주의: 그냥 Core라고 쓰면 WinUtil.Core 네임스페이스로 해석된다(상위 네임스페이스 우선).
            LibVLCSharp.Shared.Core.Initialize();

            _libVlc = new LibVLC(e.SwapChainOptions);
            _player = new MediaPlayer(_libVlc);
            Vlc.MediaPlayer = _player;

            _player.Volume = (int)VolumeSlider.Value;
            HookPlayerEvents(_player);

            if (_filePath is not null) PlayCurrent();
        }
        catch (Exception ex)
        {
            ShowMessage($"재생 초기화 실패: {ex.Message}");
        }
    }

    /// <summary>현재 _filePath를 처음부터(또는 이어보기 지점부터) 재생한다. 플레이어 준비 후에만 호출.</summary>
    private void PlayCurrent()
    {
        if (_player is not { } p || _libVlc is not { } lib || _filePath is null) return;

        _durationMs = 0;
        _lastReportedMs = 0;
        _pendingResumeMs = _resumeStore.GetResumePositionMs(_filePath) ?? -1;
        LoadSubtitleList();

        using var media = new Media(lib, new Uri(_filePath));
        p.Play(media);
        PlaceholderText.Visibility = Visibility.Collapsed;
    }

    // ---------- 파일 열기 (버튼/드래그&드롭/초기 컨텍스트) ----------

    private void OpenPath(string path)
    {
        if (!File.Exists(path)) return;

        // 보던 파일이 있으면 위치를 저장하고 전환한다.
        if (_player is { } p && _filePath is not null && _durationMs > 0)
        {
            try { _resumeStore.Report(_filePath, p.Time, _durationMs); }
            catch { /* 저장 실패가 전환을 막으면 안 된다 */ }
        }

        _filePath = path;
        if (_player is not null) PlayCurrent();
        // 플레이어가 아직 없으면 OnVlcInitialized에서 PlayCurrent()가 이어받는다.
    }

    private async Task PickAndOpenAsync()
    {
        var environment = XamlRoot?.ContentIslandEnvironment;
        if (environment is null) return;
        var hwnd = Win32Interop.GetWindowFromWindowId(environment.AppWindowId);

        var picker = new FileOpenPicker { SuggestedStartLocation = PickerLocationId.VideosLibrary };
        foreach (var ext in VideoModule.Extensions)
            picker.FileTypeFilter.Add(ext);
        InitializeWithWindow.Initialize(picker, hwnd);

        if (await picker.PickSingleFileAsync() is { } file)
            OpenPath(file.Path);
    }

    private void OnOpenClicked(object sender, RoutedEventArgs e) => _ = PickAndOpenAsync();

    private void OnDragOver(object sender, Microsoft.UI.Xaml.DragEventArgs e)
    {
        if (e.DataView.Contains(StandardDataFormats.StorageItems))
            e.AcceptedOperation = DataPackageOperation.Copy;
    }

    private async void OnDrop(object sender, Microsoft.UI.Xaml.DragEventArgs e)
    {
        if (!e.DataView.Contains(StandardDataFormats.StorageItems)) return;
        var items = await e.DataView.GetStorageItemsAsync();
        var path = items.OfType<Windows.Storage.StorageFile>()
            .Select(f => f.Path)
            .FirstOrDefault(p => VideoModule.Extensions
                .Contains(Path.GetExtension(p), StringComparer.OrdinalIgnoreCase));
        if (path is not null) OpenPath(path);
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        _tornDown = true;
        var player = _player;
        var libVlc = _libVlc;
        _player = null;
        _libVlc = null;

        if (player is not null)
        {
            // 이어보기 저장 후 해제. Stop/Dispose는 UI 스레드에서 부르면
            // libvlc 콜백과 교착할 수 있어 백그라운드로 넘긴다.
            try
            {
                if (_filePath is not null && _durationMs > 0)
                    _resumeStore.Report(_filePath, player.Time, _durationMs);
            }
            catch
            {
                // 저장 실패가 해제를 막으면 안 된다.
            }

            UnhookPlayerEvents(player);
            Task.Run(() =>
            {
                try
                {
                    player.Stop();
                    player.Dispose();
                    libVlc?.Dispose();
                }
                catch
                {
                    // 해제 중 예외는 무시.
                }
            });
        }

        _settings.Set("video.volume", (int)VolumeSlider.Value);
        _settings.Save();
    }

    // ---------- 플레이어 이벤트 (백그라운드 스레드 → 디스패치) ----------

    private void HookPlayerEvents(MediaPlayer p)
    {
        p.TimeChanged += OnPlayerTimeChanged;
        p.LengthChanged += OnPlayerLengthChanged;
        p.Playing += OnPlayerPlaying;
        p.Paused += OnPlayerPaused;
        p.EndReached += OnPlayerEndReached;
        p.EncounteredError += OnPlayerError;
    }

    private void UnhookPlayerEvents(MediaPlayer p)
    {
        p.TimeChanged -= OnPlayerTimeChanged;
        p.LengthChanged -= OnPlayerLengthChanged;
        p.Playing -= OnPlayerPlaying;
        p.Paused -= OnPlayerPaused;
        p.EndReached -= OnPlayerEndReached;
        p.EncounteredError -= OnPlayerError;
    }

    private void OnPlayerTimeChanged(object? sender, MediaPlayerTimeChangedEventArgs e)
    {
        // 이어보기 위치 보고는 UI와 무관하므로 이벤트 스레드에서 바로 처리(스토어는 스레드 안전).
        if (_filePath is not null && _durationMs > 0 &&
            Math.Abs(e.Time - _lastReportedMs) >= ResumeReportIntervalMs)
        {
            _lastReportedMs = e.Time;
            _resumeStore.Report(_filePath, e.Time, _durationMs);
        }

        Dispatch(() =>
        {
            PositionText.Text = TimeText.Format(e.Time);
            if (_durationMs > 0)
            {
                _suppressSeekEvent = true;
                SeekSlider.Value = (double)e.Time / _durationMs * SeekSlider.Maximum;
                _suppressSeekEvent = false;
            }
        });
    }

    private void OnPlayerLengthChanged(object? sender, MediaPlayerLengthChangedEventArgs e)
    {
        _durationMs = e.Length;
        Dispatch(() => DurationText.Text = TimeText.Format(e.Length));
    }

    private void OnPlayerPlaying(object? sender, EventArgs e) => Dispatch(() =>
    {
        PlayButton.Content = "❚❚";

        // 재생이 실제로 시작된 뒤에만 시킹·자막 선택이 적용된다.
        if (_pendingResumeMs > 0 && _player is { } p)
        {
            p.Time = _pendingResumeMs;
            _pendingResumeMs = -1;
        }
        if (_pendingAutoSubtitle)
        {
            _pendingAutoSubtitle = false;
            ApplySubtitle(SubtitleBox.SelectedIndex);
        }
    });

    private void OnPlayerPaused(object? sender, EventArgs e) =>
        Dispatch(() => PlayButton.Content = "▶");

    private void OnPlayerEndReached(object? sender, EventArgs e)
    {
        // 끝까지 봤으면 이어보기 기록을 지운다. (이 콜백 안에서 Stop()을 부르면 교착 — 금지)
        if (_filePath is not null && _durationMs > 0)
            _resumeStore.Report(_filePath, _durationMs, _durationMs);

        Dispatch(() =>
        {
            PlayButton.Content = "▶";
            PositionText.Text = TimeText.Format(_durationMs);
            _suppressSeekEvent = true;
            SeekSlider.Value = SeekSlider.Maximum;
            _suppressSeekEvent = false;
        });
    }

    private void OnPlayerError(object? sender, EventArgs e) =>
        ShowMessage($"재생 실패: {Path.GetFileName(_filePath ?? string.Empty)}");

    private void Dispatch(Action action)
    {
        if (_tornDown) return;
        DispatcherQueue?.TryEnqueue(() =>
        {
            if (!_tornDown) action();
        });
    }

    private void ShowMessage(string text) => Dispatch(() =>
    {
        PlaceholderText.Text = text;
        PlaceholderText.Visibility = Visibility.Visible;
    });

    // ---------- 자막 ----------

    /// <summary>같은 폴더의 자막 후보를 콤보에 채우고, 있으면 첫 후보를 자동 선택 예약한다.</summary>
    private void LoadSubtitleList()
    {
        _subtitleFiles = SubtitleFileLocator.Find(_filePath!).ToList();

        _suppressSubtitleEvent = true;
        SubtitleBox.Items.Clear();
        SubtitleBox.Items.Add("자막 없음");
        foreach (var s in _subtitleFiles)
            SubtitleBox.Items.Add(Path.GetFileName(s));
        SubtitleBox.IsEnabled = _subtitleFiles.Count > 0;
        SubtitleBox.SelectedIndex = _subtitleFiles.Count > 0 ? 1 : 0;
        _suppressSubtitleEvent = false;

        _pendingAutoSubtitle = _subtitleFiles.Count > 0;
    }

    /// <summary>콤보 인덱스(0=끔, 1부터 파일)를 플레이어에 적용. CP949 자막은 UTF-8 사본으로 변환.</summary>
    private void ApplySubtitle(int index)
    {
        if (_player is not { } p) return;

        if (index <= 0 || index > _subtitleFiles.Count)
        {
            p.SetSpu(-1);
            return;
        }

        try
        {
            var utf8Path = SubtitleCharset.EnsureUtf8File(_subtitleFiles[index - 1]);
            p.AddSlave(MediaSlaveType.Subtitle, new Uri(utf8Path).AbsoluteUri, true);
        }
        catch (Exception ex)
        {
            ShowMessage($"자막 로드 실패: {ex.Message}");
        }
    }

    // ---------- 조작 ----------

    private void TogglePlayPause()
    {
        if (_player is not { } p) return;
        if (p.CanPause && p.IsPlaying) p.Pause();
        else p.Play();
    }

    private void SeekBy(long deltaMs)
    {
        if (_player is not { } p || _durationMs <= 0) return;
        p.Time = Math.Clamp(p.Time + deltaMs, 0, _durationMs);
    }

    private void ChangeVolume(int delta) =>
        VolumeSlider.Value = Math.Clamp(VolumeSlider.Value + delta, 0, 100);

    private void ToggleMute()
    {
        if (_player is not { } p) return;
        p.Mute = !p.Mute;
        MuteButton.Content = p.Mute ? "🔇" : "🔊";
    }

    private void ToggleFullScreen()
    {
        // Window 객체 없이 XamlRoot 경유로 AppWindow에 접근한다 (이미지 뷰어와 동일 패턴).
        var environment = XamlRoot?.ContentIslandEnvironment;
        if (environment is null) return;

        var appWindow = AppWindow.GetFromWindowId(environment.AppWindowId);
        appWindow.SetPresenter(appWindow.Presenter.Kind == AppWindowPresenterKind.FullScreen
            ? AppWindowPresenterKind.Default
            : AppWindowPresenterKind.FullScreen);
    }

    // ---------- 입력 핸들러 ----------

    private void OnPlayClicked(object sender, RoutedEventArgs e) => TogglePlayPause();

    private void OnMuteClicked(object sender, RoutedEventArgs e) => ToggleMute();

    private void OnFullScreenClicked(object sender, RoutedEventArgs e) => ToggleFullScreen();

    private void OnDoubleTapped(object sender, DoubleTappedRoutedEventArgs e) => ToggleFullScreen();

    private void OnSeekSliderChanged(object sender, RangeBaseValueChangedEventArgs e)
    {
        if (_suppressSeekEvent || _player is not { } p || _durationMs <= 0) return;
        p.Time = (long)(e.NewValue / SeekSlider.Maximum * _durationMs);
    }

    private void OnVolumeSliderChanged(object sender, RangeBaseValueChangedEventArgs e)
    {
        if (_suppressVolumeEvent) return;
        if (_player is { } p) p.Volume = (int)e.NewValue;
    }

    private void OnSpeedChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_player is { } p && SpeedBox.SelectedIndex is >= 0 and var i && i < Speeds.Length)
            p.SetRate(Speeds[i]);
    }

    private void OnSubtitleChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressSubtitleEvent) return;
        ApplySubtitle(SubtitleBox.SelectedIndex);
    }

    private void OnTogglePlayInvoked(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        args.Handled = true;
        TogglePlayPause();
    }

    private void OnSeekBackInvoked(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        args.Handled = true;
        SeekBy(-SeekStepMs);
    }

    private void OnSeekForwardInvoked(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        args.Handled = true;
        SeekBy(SeekStepMs);
    }

    private void OnVolumeUpInvoked(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        args.Handled = true;
        ChangeVolume(VolumeStep);
    }

    private void OnVolumeDownInvoked(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        args.Handled = true;
        ChangeVolume(-VolumeStep);
    }

    private void OnMuteInvoked(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        args.Handled = true;
        ToggleMute();
    }

    private void OnFullScreenInvoked(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        args.Handled = true;
        ToggleFullScreen();
    }
}
