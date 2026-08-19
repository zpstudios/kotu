using LibVLCSharp.Platforms.Windows;
using LibVLCSharp.Shared;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.System;
using KOTU.Core.Contracts;
using KOTU.Core.Settings;
using KOTU.Core.Threading;
using KOTU.Input;

namespace KOTU.Module.Video;

/// <summary>
/// 동영상 플레이어 화면. 재생/일시정지, 시킹(슬라이더·←/→ 5초), 볼륨(↑/↓)·음소거(M),
/// 배속, 자막(자동 탐지 + CP949 자동 변환), 이어보기를 제공한다. 전체화면은 A151부터
/// 셸의 3단 모드 체계(Enter 순환·Alt+Enter) 몫이다 — 이 뷰에는 진입 코드가 없다.
/// 더블클릭 전체화면은 제거(v0.23.0) — 클릭(재생/일시정지)과 겹쳐 의도치 않은 전환이 잦았다.
/// 음악 재생은 오디오 모듈(KOTU-audio)로 분리(A10, v0.75.0) — 파형 시각화 인스턴스 교체 로직도 함께 이관.
/// 스레드 모델(A42): libvlc 생성·해제와 자막 탐지·변환은 뷰 전용 워커에서 직렬로 —
/// 생성/해제가 같은 큐라 순서가 구조적으로 보장된다. libvlc 이벤트는 libvlc 자체 스레드에서
/// 오므로 UI 갱신은 DispatcherQueue로 넘긴다(Dispatch).
/// </summary>
public sealed partial class VideoPlayerView : UserControl, IBottomBarProvider,
    IContentStateSource, IContentInfoProvider, ITrayStatusProvider
{
    /// <summary>파일 재생을 시작하면 셸에 알린다(v0.25.0 — 빈 상태 탐색기 내림·오버레이 기준 갱신).</summary>
    public event Action<string>? ContentOpened;

    /// <summary>트레이 아이콘 표시 값이 바뀌었다(A54) — 재생 시작·길이 확정·트랙 파싱 시점.</summary>
    public event Action? TrayStatusChanged;

    /// <summary>
    /// 트레이 아이콘 내용(A54): 열림 = 해상도("1080p") · 비트레이트("4.2M"), 유휴 = "VID".
    /// 비트레이트는 libvlc 통계 대신 <b>파일 크기 ÷ 재생 길이</b>의 평균값이다 —
    /// 16px 표기에는 순간값보다 안정적이고, 이미 쓰는 값(_durationMs·FileInfo)만으로 구해진다(구현 시 결정).
    /// 아직 파싱 전이라 값이 없으면 그 줄만 "—"가 된다.
    /// </summary>
    public TrayStatus GetTrayStatus()
    {
        if (_filePath is not { } path) return TrayStatus.Idle("VID");

        var (_, height) = VideoPixelSize();
        long bytes = -1;
        try
        {
            bytes = new FileInfo(path).Length;
        }
        catch
        {
            // 크기를 못 읽으면 비트레이트 줄만 "—"가 된다.
        }
        return TrayStatus.Open(TrayFormat.Resolution((int)height), TrayFormat.BitrateOf(bytes, _durationMs));
    }

    /// <summary>
    /// 정보 오버레이(v0.25.0)용 미디어 정보: 파일·시간·비디오/오디오 트랙. A150에서 라벨·값
    /// 행 목록으로 이식 — 비디오 한 줄을 Resolution/Frame rate/Video codec으로 분해했다
    /// (값 포맷은 유지). 값이 없는 행은 생략한다.
    /// </summary>
    public Task<IReadOnlyList<ContentInfoItem>?> GetContentInfoAsync()
    {
        if (_filePath is not { } path) return Task.FromResult<IReadOnlyList<ContentInfoItem>?>(null);

        var rows = new List<ContentInfoItem> { new("File", Path.GetFileName(path)) };
        try
        {
            var info = new FileInfo(path);
            rows.Add(new ContentInfoItem("Size", $"{info.Length / 1024.0 / 1024.0:0.##} MB"));
            rows.Add(new ContentInfoItem("Modified", $"{info.LastWriteTime:yyyy-MM-dd HH:mm}"));
        }
        catch
        {
            // 크기·날짜는 없어도 된다.
        }

        if (_durationMs > 0)
            rows.Add(new ContentInfoItem("Duration", TimeText.Format(_durationMs)));

        try
        {
            // 재생 중이면 libvlc가 파싱한 트랙 정보를 그대로 읽는다 (별도 Parse 불필요).
            // Media 게터는 새 래퍼를 만들어 참조를 늘리므로 쓰고 바로 해제한다.
            using var media = _player?.Media;
            foreach (var track in media?.Tracks ?? [])
            {
                if (track.TrackType == TrackType.Video)
                {
                    var v = track.Data.Video;
                    rows.Add(new ContentInfoItem("Resolution", $"{v.Width}×{v.Height}"));
                    if (v.FrameRateDen > 0)
                        rows.Add(new ContentInfoItem("Frame rate",
                            $"{(double)v.FrameRateNum / v.FrameRateDen:0.##} fps"));
                    rows.Add(new ContentInfoItem("Video codec", FourCc(track.Codec)));
                }
                else if (track.TrackType == TrackType.Audio)
                {
                    var a = track.Data.Audio;
                    rows.Add(new ContentInfoItem("Audio",
                        $"{a.Channels} ch · {a.Rate:N0} Hz · {FourCc(track.Codec)}"));
                }
            }
        }
        catch
        {
            // 트랙 정보 실패는 기본 정보만 보여준다.
        }

        return Task.FromResult<IReadOnlyList<ContentInfoItem>?>(rows);
    }

    /// <summary>libvlc 코덱 FourCC(uint) → 사람이 읽는 문자열.</summary>
    private static string FourCc(uint codec)
    {
        Span<char> chars = stackalloc char[4];
        for (var i = 0; i < 4; i++)
        {
            var c = (char)((codec >> (8 * i)) & 0xFF);
            chars[i] = char.IsLetterOrDigit(c) ? c : '?';
        }
        return new string(chars);
    }

    /// <summary>
    /// 트랜스포트 바를 뷰에서 떼어 셸 하단 바 한 줄에 얹는다(v0.21.0, 실기기 피드백:
    /// 재생줄과 하단 바가 두 줄로 중복). 컨트롤 필드 참조는 그대로 유효하다.
    /// </summary>
    public object? TakeBottomBar()
    {
        RootGrid.Children.Remove(TransportBar);
        return TransportBar;
    }

    /// <summary>
    /// A40: 좁은 하단 바에서 우선순위 낮은 요소를 숨겨 잘림을 막는다. 당시 실측: 고정 요소 합
    /// (버튼·콤보·볼륨 96 + 칸 간격)이 시간 텍스트가 길 때("1:23:45") 약 740px —
    /// 최소 창 폭 720(바 폭 약 656)에서는 시크 슬라이더가 0으로 밀리고 우측 ⛶가 잘린다.
    /// 임계값 이력: A40 760(실측 오차 여유 포함) → A99에서 열기 버튼 제거로 42px(버튼 36 + 간격 6)
    /// 감소 → **718** → A106(v0.132.0)에서 1칸 버튼이 36→32가 되어 **698** →
    /// A111(v0.133.0)에서 1:1 버튼이 사라져 **660** → A144에서 Fit이 84→64가 되어 **640**.
    /// A144분 −20의 근거(TransportBar 요소 직접 계수 — 다른 요소의 폭은 무변경):
    ///   Fit 칸 c8이 SplitButton 84 → 본체 32 + 화살표 32(같은 칸의 StackPanel, 간격 0) = 64.
    ///   지금 남은 고정 폭: 재생 c0(32) · 음소거 c4(32) · 볼륨 c5(96) · 배속 c6(84) · 자막 c7(32) ·
    ///   Fit c8(64) + 시간 텍스트 c1/c3 + 간격 6×8.
    /// ⚠️ A151이 전체화면 c9(버튼 32 + 간격 6 = 38)를 제거했을 때 이 임계는 내리지 않았다
    ///   (660 유지 — 그 38은 지금도 여유분으로 남아 있다). 이번 A144는 자기 몫 −20만 반영한다 —
    ///   추가 인하(−38)는 별도 판단 대상(등재 후보)으로 보고만 해 둔다.
    /// 숨겨도 기능은 남는다: 볼륨은 ↑/↓·휠·음소거 버튼, 재생 위치는 시크 슬라이더 썸 위치가 대신한다.
    /// </summary>
    private void UpdateCompactTransport(double width)
    {
        var visibility = width < 640 ? Visibility.Collapsed : Visibility.Visible;
        VolumeSlider.Visibility = visibility;
        PositionText.Visibility = visibility;
        DurationText.Visibility = visibility;
    }

    private const long SeekStepMs = 5_000;
    private const int VolumeStep = 5;
    private const long ResumeReportIntervalMs = 10_000;
    private static readonly float[] Speeds = [0.5f, 0.75f, 1.0f, 1.25f, 1.5f, 2.0f];

    /// <summary>내장 테스트 클립(화면 색감 + 스피커 테스트 음악) — 배포본 Assets에 동봉.</summary>
    private static readonly string TestClipPath =
        Path.Combine(AppContext.BaseDirectory, "Assets", "test-clip.mp4");

    /// <summary>테스트 클립은 이어보기 대상에서 뺀다 (32초 점검용 클립에 이어보기는 무의미).</summary>
    private static bool IsTestClip(string? path) =>
        string.Equals(path, TestClipPath, StringComparison.OrdinalIgnoreCase);

    private readonly ISettingsService _settings;
    private readonly PlaybackResumeStore _resumeStore;
    private string? _filePath;

    private LibVLC? _libVlc;
    private MediaPlayer? _player;
    private string[]? _swapChainOptions;   // 스왑체인 준비 전 OpenPath 대비 (Vlc.Initialized에서 1회 저장)
    private readonly SemaphoreSlim _playerGate = new(1, 1); // 생성 직렬화 (Initialized·OpenPath 경합 대비)
    private List<string> _subtitleFiles = [];
    private int _subtitleIndex; // 0 = 끔, 1부터 _subtitleFiles[i-1] (v0.53.0 콤보 → 플라이아웃)
    private long _durationMs;
    private long _lastReportedMs;
    private long _pendingResumeMs = -1;
    private bool _pendingAutoSubtitle;
    private bool _suppressSeekEvent;
    private bool _suppressVolumeEvent;
    private bool _muted; // A28: 음소거 상태 로컬 소유 — libvlc Mute 게터의 스테일 값 회피
    private bool _tornDown;
    private bool _pendingStartOverlay; // A12: 다음 Playing에서 시작 오버레이 표시(일시정지 해제와 구분)
    private DispatcherTimer? _startOverlayTimer;
    private DispatcherTimer? _feedbackTimer; // A13: 전체화면 조작 피드백 칩
    private ModuleWorker? _worker; // libvlc 생성·해제/자막 탐지·변환 전용(A42) — 뷰별 분리

    /// <summary>지연 생성. 이 뷰는 Unloaded가 곧 최종 해체(_tornDown)라 재생성될 일은 없다.</summary>
    private ModuleWorker Worker => _worker ??= new ModuleWorker("KOTU video worker");

    public VideoPlayerView(OpenContext context, ISettingsService settings)
    {
        InitializeComponent();
        _settings = settings;
        _resumeStore = new PlaybackResumeStore(settings);
        _filePath = context.FilePath is { } p && File.Exists(p) ? p : null;

        foreach (var s in Speeds)
            SpeedBox.Items.Add($"{s:0.##}×");
        SpeedBox.SelectedIndex = Array.IndexOf(Speeds, 1.0f);
        FillSubtitleFlyout(); // "No subtitles"만 있는 초기 상태
        SetupHotkeys();       // A34: 하단 바 버튼 핫키 + 툴팁 표기

        _suppressVolumeEvent = true;
        VolumeSlider.Value = Math.Clamp(_settings.Get("video.volume", 80), 0, 100);
        _suppressVolumeEvent = false;

        if (_filePath is null)
            PlaceholderText.Visibility = Visibility.Visible;

        Vlc.Initialized += OnVlcInitialized;
        Loaded += (_, _) => Focus(FocusState.Programmatic);
        Unloaded += OnUnloaded;

        // 휠 = 볼륨 (플레이어 관례), Ctrl+휠 = 줌 (A98). 자식 요소가 소비해도 받도록 handledEventsToo.
        VideoSurface.AddHandler(PointerWheelChangedEvent,
            new PointerEventHandler(OnSurfaceWheel), handledEventsToo: true);

        // Fit width/height는 표면 크기에 따라 배율이 달라지므로 크기 변화에 추종한다 (v0.41.0).
        // A83: Contain도 추종 대상 — "축소만"이라 창 크기에 따라 Scale 1(원본)과 0(자동 축소)이
        // 갈리므로, 재판정하지 않으면 작은 창에서 100%로 굳는다(전체화면 전환도 이 경로로 온다).
        // Manual(수동 줌, A98)·ActualSize는 종전대로 추종하지 않는다.
        // A130: EOF(Ended)에서는 이 재적용이 화면에 닿지 않으므로(원인은 ClearEndedFrameOnResize
        // 주석 참조) 잔상 프레임을 지우는 것으로 갈음한다 — 최대화 전용이 아니라 경계 드래그·복원·
        // 창 스냅(Win+화살표)·전체화면 전환까지 모든 크기 변경 공통.
        VideoSurface.SizeChanged += (_, _) =>
        {
            if (_fitMode is VideoFitMode.Contain or VideoFitMode.FitWidth or VideoFitMode.FitHeight)
                ApplyFitMode();
            ClearEndedFrameOnResize();
        };

        // A40: 바 폭이 좁으면 볼륨 슬라이더·시간 텍스트를 숨긴다(셸 하단 바로 옮겨진 뒤에도 유효)
        TransportBar.SizeChanged += (_, e) => UpdateCompactTransport(e.NewSize.Width);

        // 시크 슬라이더 스크럽 감지: 드래그 중에는 시킹하지 않고 놓을 때 1회만 시킹한다.
        // (드래그 틱마다 p.Time을 설정하면 시킹이 폭주해 드래그를 멈춰도 재생이 멎는 실기기 버그)
        // Slider가 포인터 이벤트를 내부에서 소비하므로 handledEventsToo 필수.
        SeekSlider.AddHandler(PointerPressedEvent,
            new PointerEventHandler(OnSeekPointerPressed), handledEventsToo: true);
        SeekSlider.AddHandler(PointerReleasedEvent,
            new PointerEventHandler(OnSeekPointerReleased), handledEventsToo: true);
        SeekSlider.AddHandler(PointerCaptureLostEvent,
            new PointerEventHandler(OnSeekPointerReleased), handledEventsToo: true);
    }

    // ---------- libvlc 초기화 / 해제 ----------

    /// <summary>
    /// VideoView의 D3D 스왑체인이 준비되면 호출된다. 여기서만 스왑체인 옵션을 얻을 수 있다.
    /// 파일 없이 열렸어도 플레이어는 만들어 둔다 — 이후 열기 버튼/드롭으로 파일이 올 수 있다.
    /// </summary>
    private async void OnVlcInitialized(object? sender, InitializedEventArgs e)
    {
        if (_tornDown) return;
        _swapChainOptions = e.SwapChainOptions;

        if (_filePath is not null)
        {
            PlaceholderText.Text = "Preparing playback...";
            PlaceholderText.Visibility = Visibility.Visible;
        }

        await EnsurePlayerAsync();
        if (!_tornDown && _filePath is not null && _player is not null) PlayCurrent();
    }

    /// <summary>
    /// 플레이어를 1회 생성한다(이미 있으면 그대로). 음악용 파형 시각화 인스턴스 교체는
    /// 오디오 모듈로 이관(A10) — 이 뷰는 동영상용(시각화 끔) 인스턴스만 쓴다.
    /// 중요: libvlc 생성은 첫 실행 시 플러그인 캐시 생성으로 수 초가 걸린다(v0.10.1 실기기 버그).
    /// 생성은 전부 백그라운드에서 하고, 뷰 연결만 UI 스레드에서 한다.
    /// </summary>
    private async Task EnsurePlayerAsync()
    {
        if (_swapChainOptions is not { } swapOptions) return; // 스왑체인 준비 전 — OnVlcInitialized가 다시 부른다

        await _playerGate.WaitAsync();
        try
        {
            if (_tornDown || _player is not null) return;

            // --no-video-title-show: 재생 시작 시 파일명이 화면에 오버레이되는 libvlc 기본 동작 끔 (사용자 요청)
            string[] options = [.. swapOptions, "--no-video-title-show"];

            var (libVlc, player) = await Worker.Run(_ =>
            {
                // libvlc 네이티브 dll은 libvlc\win-x64\ 하위에 배포되므로 검색 경로 등록이 선행돼야 한다.
                // 주의: 그냥 Core라고 쓰면 KOTU.Core 네임스페이스로 해석된다(상위 네임스페이스 우선).
                LibVLCSharp.Shared.Core.Initialize();
                var lib = new LibVLC(options);
                return (lib, new MediaPlayer(lib));
            });

            if (_tornDown)
            {
                // 생성이 끝나기 전에 뷰가 내려갔다 — 연결하지 않고 워커에서 해제만.
                // (워커가 이미 닫혔으면 Post가 스레드풀로 폴백해 해제 실행은 보장된다)
                Worker.Post(() =>
                {
                    player.Dispose();
                    libVlc.Dispose();
                });
                return;
            }

            _libVlc = libVlc;
            _player = player;
            Vlc.MediaPlayer = player;

            player.Volume = (int)VolumeSlider.Value;
            _muted = false; // 새 인스턴스는 음소거 해제 상태 (A28: 로컬 상태도 동기)
            MuteButton.Content = "🔊";
            HookPlayerEvents(player);
        }
        catch (Exception ex)
        {
            ShowMessage($"Playback initialization failed: {ex.Message}");
        }
        finally
        {
            _playerGate.Release();
        }
    }

    /// <summary>현재 _filePath를 처음부터(또는 이어보기 지점부터) 재생한다. 플레이어 준비 후에만 호출.</summary>
    private void PlayCurrent()
    {
        if (_player is not { } p || _libVlc is not { } lib || _filePath is null) return;

        _durationMs = 0;
        _lastReportedMs = 0;
        _pendingResumeMs = IsTestClip(_filePath)
            ? -1
            : _resumeStore.GetResumePositionMs(_filePath) ?? -1;
        LoadSubtitleList();

        using var media = new Media(lib, new Uri(_filePath));
        _pendingStartOverlay = true; // A12: 실제 재생이 시작되면(Playing) 오버레이 표시
        p.Play(media);
        PlaceholderText.Visibility = Visibility.Collapsed;
        ContentOpened?.Invoke(_filePath); // 셸 동기화 (v0.25.0)
        TrayStatusChanged?.Invoke();      // A54: 유휴("VID") → 열림. 값은 파싱되는 대로 다시 올라온다
    }

    /// <summary>
    /// A12: 재생 시작 시 좌상단에 "파일명 · 1080p"를 3초 표시.
    /// 해상도는 Playing 시점의 트랙 정보에서 읽는다 — 아직 파싱 전이면 파일명만.
    /// </summary>
    private void ShowStartOverlay()
    {
        if (_filePath is null) return;

        var (_, h) = VideoPixelSize();
        StartOverlayText.Text = h > 0
            ? $"{Path.GetFileName(_filePath)} · {(int)h}p"
            : Path.GetFileName(_filePath);
        StartOverlay.Visibility = Visibility.Visible;

        if (_startOverlayTimer is not { } timer)
        {
            timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
            timer.Tick += (_, _) =>
            {
                timer.Stop();
                StartOverlay.Visibility = Visibility.Collapsed;
            };
            _startOverlayTimer = timer;
        }
        timer.Stop(); // 연속 전환 시 표시 시간 리셋
        timer.Start();
    }

    // ---------- 파일 열기 (버튼/드래그&드롭/초기 컨텍스트) ----------

    private async void OpenPath(string path)
    {
        if (!File.Exists(path)) return;

        // 보던 파일이 있으면 위치를 저장하고 전환한다.
        if (_player is { } p && _filePath is not null && !IsTestClip(_filePath) && _durationMs > 0)
        {
            try { _resumeStore.Report(_filePath, p.Time, _durationMs); }
            catch { /* 저장 실패가 전환을 막으면 안 된다 */ }
        }

        _filePath = path;

        // A30: 재생 영상이 바뀌면 핏 옵션은 Contain으로 회귀 — 이번 실행 내에서도 기억하지 않는다
        // (사용자 확정, A83에서 재확인). Manual(Ctrl+휠 줌, A98)·100%도 같은 규칙으로 회귀한다 —
        // 실제 Scale 리셋은 Playing 핸들러의 ApplyFitMode() 재적용이 수행한다.
        if (_fitMode != VideoFitMode.Contain || _lastFitOption != VideoFitMode.Contain)
        {
            _lastFitOption = VideoFitMode.Contain;
            _fitMode = VideoFitMode.Contain;
            UpdateFitButton();
        }

        await EnsurePlayerAsync(); // 이미 있으면 즉시 반환 — 인스턴스 교체 없음(A10 이후 동영상 전용)
        if (_tornDown || _filePath != path) return; // 그새 또 다른 파일로 전환됨

        if (_player is not null) PlayCurrent();
        // 플레이어가 아직 없으면(스왑체인 준비 전) OnVlcInitialized에서 PlayCurrent()가 이어받는다.
    }

    // A99: 모듈 열기 버튼·O 키·파일 대화상자(PickAndOpenAsync)는 제거 — 파일 열기는
    // 셸 S4 'Open file'(A90)로 일원화됐다.
    // 드래그&드롭은 종전대로 창 수준(MainWindow)에서 확장자 라우팅으로 일괄 처리한다.

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
                if (_filePath is not null && !IsTestClip(_filePath) && _durationMs > 0)
                    _resumeStore.Report(_filePath, player.Time, _durationMs);
            }
            catch
            {
                // 저장 실패가 해제를 막으면 안 된다.
            }

            UnhookPlayerEvents(player);
            Worker.Post(() =>
            {
                player.Stop();
                player.Dispose();
                libVlc?.Dispose();
            });
        }

        _startOverlayTimer?.Stop(); // A12: 해체 후 틱 방지
        _feedbackTimer?.Stop();     // A13

        _settings.Set("video.volume", (int)VolumeSlider.Value);
        _settings.Save();

        // 마지막 해제 작업까지 큐에 넣었으니 워커를 닫는다(남은 작업은 워커가 마저 실행).
        // null로 되돌리지 않는다 — 해체된 뷰에서 게터가 새 워커를 만들면 스레드가 샌다.
        _worker?.Dispose();
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
        if (_filePath is not null && !IsTestClip(_filePath) && _durationMs > 0 &&
            Math.Abs(e.Time - _lastReportedMs) >= ResumeReportIntervalMs)
        {
            _lastReportedMs = e.Time;
            _resumeStore.Report(_filePath, e.Time, _durationMs);
        }

        Dispatch(() =>
        {
            if (_isScrubbing) return; // 드래그 중에는 사용자의 손 위치를 지키고 미리보기 텍스트 유지

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
        TrayStatusChanged?.Invoke(); // A54: 길이가 정해져야 평균 비트레이트가 나온다
    }

    private void OnPlayerPlaying(object? sender, EventArgs e) => Dispatch(() =>
    {
        PlayButton.Content = "❚❚";

        // A12: 새 미디어의 첫 Playing에서만 (일시정지 해제 Playing 제외)
        if (_pendingStartOverlay)
        {
            _pendingStartOverlay = false;
            ShowStartOverlay();
        }
        TrayStatusChanged?.Invoke(); // A54: 이 시점에 트랙(해상도)이 파싱돼 있다 — A12 오버레이와 같은 근거

        // 재생이 실제로 시작된 뒤에만 시킹·자막 선택이 적용된다.
        if (_pendingResumeMs > 0 && _player is { } p)
        {
            p.Time = _pendingResumeMs;
            _pendingResumeMs = -1;
        }
        if (_pendingAutoSubtitle)
        {
            _pendingAutoSubtitle = false;
            ApplySubtitle(_subtitleIndex);
        }

        // 배속은 미디어가 바뀌면 초기화되므로 콤보의 현재 선택을 다시 적용한다(같은 값 재적용은 무해).
        if (_player is { } player &&
            SpeedBox.SelectedIndex is >= 0 and var i && i < Speeds.Length)
        {
            player.SetRate(Speeds[i]);
        }

        // 보기 모드도 미디어·플레이어 교체 후 다시 적용한다 (v0.41.0 — 해상도가 달라질 수 있다)
        ApplyFitMode();
    });

    private void OnPlayerPaused(object? sender, EventArgs e) =>
        Dispatch(() => PlayButton.Content = "▶");

    private void OnPlayerEndReached(object? sender, EventArgs e)
    {
        // 끝까지 봤으면 이어보기 기록을 지운다. (이 콜백 안에서 Stop()을 부르면 교착 — 금지)
        // A11 승계 메모: 루프 옵션이 EOF 동작을 재정의하면(목록/현재 영상 루프) 다음 재생을 잇는
        // 자리가 이 핸들러다. A130의 잔상 정리는 여기가 아니라 "Ended 상태 + 크기 변경" 조건에
        // 걸려 있어(ClearEndedFrameOnResize), 루프가 Ended에 머물지 않게 되면 자연히 비활성이다.
        if (_filePath is not null && !IsTestClip(_filePath) && _durationMs > 0)
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

    /// <summary>
    /// A130: EOF(Ended) 상태에서 뷰포트 크기가 바뀌면 마지막 프레임 잔상을 지운다(검정 Present).
    /// 원인(libvlc 3.0 소스 판독으로 확정): 재생이 끝나면 libvlc는 vout을 재사용 대비로 살려 두되
    /// flush가 재표시 시계를 무효화해(video_output.c ThreadFlush에서 displayed.date 무효) 80ms
    /// 재표시 펌프가 멎고, Scale/zoom 쓰기도 활성 vout 목록이 비어(resource.c에서 제거) 어떤
    /// vout에도 닿지 않는다. 즉 EOF에서는 무엇을 재적용해도 화면이 다시 그려질 수 없다 —
    /// SizeChanged의 ApplyFitMode 재적용이 EOF에서만 무력했던 이유이자, 재생·수동 일시정지
    /// 중에는 같은 펌프가 살아 있어 경계 드래그든 최대화든 정상 추종하는 이유다.
    /// 그래서 유일하게 일관된 표시인 잔상 제거를 택한다: VideoView.Clear()(LibVLCSharp 3.10.0,
    /// vlc 이슈 23667 공식 워크어라운드)가 백버퍼를 검정으로 칠해 Present한다. 표면 배경도
    /// 검정(XAML)이라 어떤 크기에서도 이음새가 없고, 종료 상태는 하단 바(▶·끝 위치)가 알린다.
    /// 재생을 되살리면(TogglePlayPause의 Ended 분기 → PlayCurrent) Playing 핸들러의
    /// ApplyFitMode가 새 크기로 다시 그리므로 별도 재적용은 필요 없다.
    /// </summary>
    private void ClearEndedFrameOnResize()
    {
        if (_player is { } p && p.State == VLCState.Ended)
            Vlc.Clear();
    }

    private void OnPlayerError(object? sender, EventArgs e) =>
        ShowMessage($"Playback failed: {Path.GetFileName(_filePath ?? string.Empty)}");

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

    /// <summary>
    /// 같은 폴더의 자막 후보를 찾아 콤보에 채우고, 있으면 자동 선택한다.
    /// 폴더 스캔은 네트워크 드라이브에서 느릴 수 있어 백그라운드에서 수행하고,
    /// 결과가 오기 전에 파일이 바뀌었으면 버린다.
    /// </summary>
    private async void LoadSubtitleList()
    {
        var file = _filePath!;
        _subtitleFiles = [];
        _pendingAutoSubtitle = false;
        FillSubtitleFlyout();

        List<string> found;
        try
        {
            found = await Worker.Run(_ => SubtitleFileLocator.Find(file).ToList());
        }
        catch
        {
            return; // 자막 탐지 실패가 재생을 방해하면 안 된다.
        }

        if (_tornDown || file != _filePath) return; // 그새 다른 파일로 전환됨

        _subtitleFiles = found;
        FillSubtitleFlyout();

        if (found.Count > 0)
        {
            // 이미 재생 중이면 바로 적용, 아니면 Playing 이벤트에서 적용하도록 예약.
            if (_player is { IsPlaying: true }) ApplySubtitle(_subtitleIndex);
            else _pendingAutoSubtitle = true;
        }
    }

    /// <summary>
    /// 현재 _subtitleFiles로 자막 플라이아웃을 다시 채운다(첫 후보 자동 선택).
    /// v0.53.0: 넓은 콤보 대신 아이콘 버튼 + 라디오 플라이아웃으로 공간 절약.
    /// </summary>
    private void FillSubtitleFlyout()
    {
        _subtitleIndex = _subtitleFiles.Count > 0 ? 1 : 0;
        SubtitleFlyout.Items.Clear();
        AddSubtitleChoice("No subtitles", 0);
        for (var i = 0; i < _subtitleFiles.Count; i++)
            AddSubtitleChoice(Path.GetFileName(_subtitleFiles[i]), i + 1);
        SubtitleButton.IsEnabled = _subtitleFiles.Count > 0;
    }

    private void AddSubtitleChoice(string label, int index)
    {
        var item = new RadioMenuFlyoutItem
        {
            Text = label,
            GroupName = "subtitles",
            IsChecked = index == _subtitleIndex,
        };
        item.Click += (_, _) =>
        {
            _subtitleIndex = index;
            ApplySubtitle(index);
        };
        SubtitleFlyout.Items.Add(item);
    }

    /// <summary>
    /// 콤보 인덱스(0=끔, 1부터 파일)를 플레이어에 적용. CP949 자막은 UTF-8 사본으로 변환.
    /// 변환은 파일 읽기+쓰기이므로 백그라운드에서 하고 적용만 이어서 한다.
    /// </summary>
    private async void ApplySubtitle(int index)
    {
        if (_player is not { } p) return;

        if (index <= 0 || index > _subtitleFiles.Count)
        {
            p.SetSpu(-1);
            return;
        }

        var source = _subtitleFiles[index - 1];
        try
        {
            var utf8Path = await Worker.Run(_ => SubtitleCharset.EnsureUtf8File(source));
            if (_tornDown || _player is not { } player) return;
            player.AddSlave(MediaSlaveType.Subtitle, new Uri(utf8Path).AbsoluteUri, true);
        }
        catch (OperationCanceledException)
        {
            return; // 뷰가 내려가며 워커가 닫힘
        }
        catch (Exception ex)
        {
            ShowMessage($"Failed to load subtitles: {ex.Message}");
        }
    }

    // ---------- 조작 ----------

    /// <summary>
    /// A13: 전체화면에서만 중앙에 조작 피드백 칩을 0.9초 표시.
    /// 하단 바(시크·볼륨 슬라이더)가 숨어 있어 조작 결과가 안 보이는 상태를 보완한다.
    /// 창 모드에서는 슬라이더가 그대로 보이므로 띄우지 않는다.
    /// </summary>
    private void ShowFeedback(string text)
    {
        if (!IsFullScreen()) return;

        FeedbackText.Text = text;
        FeedbackOverlay.Visibility = Visibility.Visible;

        if (_feedbackTimer is not { } timer)
        {
            timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(900) };
            timer.Tick += (_, _) =>
            {
                timer.Stop();
                FeedbackOverlay.Visibility = Visibility.Collapsed;
            };
            _feedbackTimer = timer;
        }
        timer.Stop(); // 연타 시 표시 시간 리셋
        timer.Start();
    }

    private bool IsFullScreen() =>
        XamlRoot?.ContentIslandEnvironment is { } environment &&
        AppWindow.GetFromWindowId(environment.AppWindowId).Presenter.Kind
            == AppWindowPresenterKind.FullScreen;

    private void TogglePlayPause()
    {
        if (_player is not { } p) return;

        // 아무것도 열지 않은 상태의 ▶ = 내장 테스트 클립 재생 (화면 색감 + 스피커 점검).
        if (_filePath is null)
        {
            if (File.Exists(TestClipPath)) OpenPath(TestClipPath);
            else ShowMessage(@"Test clip not found (Assets\test-clip.mp4)");
            return;
        }

        // 끝까지 재생된(Ended) 상태에서는 Play()만으로 재시작이 안 되므로 미디어를 다시 건다.
        if (p.State == VLCState.Ended)
        {
            PlayCurrent();
            ShowFeedback("▶"); // A13
            return;
        }

        if (p.CanPause && p.IsPlaying)
        {
            p.Pause();
            ShowFeedback("❚❚"); // A13: 멎었음을 표시
        }
        else
        {
            p.Play();
            ShowFeedback("▶"); // A13: 재생 재개
        }
    }

    private void SeekBy(long deltaMs)
    {
        if (_player is not { } p || _durationMs <= 0) return;
        var target = Math.Clamp(p.Time + deltaMs, 0, _durationMs);
        p.Time = target;
        ShowFeedback($"{TimeText.Format(target)} / {TimeText.Format(_durationMs)}"); // A13
    }

    private void ChangeVolume(int delta)
    {
        VolumeSlider.Value = Math.Clamp(VolumeSlider.Value + delta, 0, 100);
        ShowFeedback($"Volume {(int)VolumeSlider.Value}%"); // A13
    }

    /// <summary>
    /// 음소거 상태는 로컬(_muted)이 소유한다(A28). libvlc의 Mute 게터는 설정 직후
    /// 이전 값을 돌려줄 수 있어(비동기 반영), 토글 직후 읽어 아이콘을 정하면 반전돼 보였다.
    /// </summary>
    private void ToggleMute()
    {
        if (_player is not { } p) return;
        _muted = !_muted;
        p.Mute = _muted;
        MuteButton.Content = _muted ? "🔇" : "🔊";
        ShowFeedback(_muted ? "Muted" : $"Volume {(int)VolumeSlider.Value}%"); // A13
    }

    // ---------- 보기 모드 (A83: 100% / Contain / Fit width / Fit height — 3모듈 공통) ----------

    /// <summary>
    /// Contain = 한 번에 다 보이는 레터박스, 단 <b>축소만</b> 한다(A83 확정) — 뷰포트보다 클 때만
    /// 줄이고 작은 영상은 원본 크기(Scale 1)로 둔다. 구 Auto-fit(항상 libvlc Scale 0)에서
    /// 바뀐 이 배치의 유일한 실동작 변경이다.
    /// FitWidth/FitHeight = 표면의 해당 축을 꽉 채우는 배율(반대 축은 잘리거나 남는다. 확대·축소 양방향).
    /// ActualSize = 원본 픽셀 1:1. 파일을 바꿔도 선택은 유지된다.
    /// Manual = Ctrl+휠 수동 줌(A98)이 정한 명시 배율 — Fit 추종 없음(이미지·PDF의 A49 UX와 동일).
    /// </summary>
    private enum VideoFitMode { Contain, FitWidth, FitHeight, ActualSize, Manual }

    private VideoFitMode _fitMode = VideoFitMode.Contain;

    /// <summary>
    /// A30: Fit 버튼이 표시·실행할 마지막 핏 옵션. A83 이후 100%도 플라이아웃 옵션이라
    /// ActualSize까지 들어온다(1:1 별도 버튼은 A111에서 없어졌다. Manual은 옵션이 아니라 제외).
    /// 기억하지 않는다 — 재생 영상이 바뀌면 Contain으로 회귀(사용자 확정, A83에서 재확인).
    /// </summary>
    private VideoFitMode _lastFitOption = VideoFitMode.Contain;

    /// <summary>
    /// A30: Fit 버튼 본체 내용(4옵션 아이콘)과 툴팁을 마지막 옵션에 맞춘다.
    /// A144: 본체가 SplitButton에서 일반 Button(32×32)이 됐다 — 화살표는 별도
    /// DropDownButton(FitOptionsButton, 플라이아웃 전담·A34 키 없음·툴팁은 XAML 고정)이라
    /// 이 메서드는 종전대로 본체(FitButton)만 만진다.
    /// A143: 100%도 아이콘이 됐다 — 종전 "1:1" 텍스트(FontSize 13) 대신 PathIcon(부록 B 69).
    /// ⚠️ v0.174.1: PathIcon 인스턴스(UIElement)만이 아니라 **Geometry도 공유 금지** — WinUI
    /// Geometry는 부모가 하나뿐이라 공유 인스턴스를 PathIcon.Data에 걸면 XamlParseException
    /// ("Failed to assign to property")으로 앱이 죽는다(실기기 크래시 실사례 — 종전엔 App.xaml
    /// 공유 리소스를 봤다). 호출마다 BuildActualSizeIconGeometry()로 새로 만든다.
    /// </summary>
    private void UpdateFitButton()
    {
        (object content, string tip) = _lastFitOption switch
        {
            VideoFitMode.FitWidth =>
                ((object)new FontIcon { Glyph = "\uE8AB", FontSize = 18 }, "Fit width"),
            VideoFitMode.FitHeight =>
                (new FontIcon { Glyph = "\uE8CB", FontSize = 18 }, "Fit height"),
            VideoFitMode.ActualSize => (new PathIcon
            {
                Data = BuildActualSizeIconGeometry(),
            }, "Actual size"),
            _ => (new FontIcon { Glyph = "\uE9A6", FontSize = 18 },
                "Contain - the whole video fits, never enlarged"),
        };
        FitButton.Content = content;
        ToolTipService.SetToolTip(FitButton, FitTip(tip)); // A34: 표기는 키 상수에서
    }

    /// <summary>
    /// A143/v0.174.1: 100% 아이콘 도형(16x16 좌표계 — PathIcon은 스케일하지 않는다). 도형 6개 =
    /// 왼쪽 1(깃발+기둥/밑변)·콜론 점 2개·오른쪽 1(깃발+기둥/밑변). 호출마다 새 인스턴스를 만든다
    /// (Geometry 공유 금지 — 위 UpdateFitButton 주석). 좌표를 바꾸면 이 파일 XAML의 인라인 Data
    /// 문자열과 형제 두 모듈(이미지·문서)의 같은 두 곳까지 총 6곳을 함께 고칠 것.
    /// </summary>
    private static Geometry BuildActualSizeIconGeometry()
    {
        static PathFigure Fig(double sx, double sy, params (double X, double Y)[] points)
        {
            var figure = new PathFigure
            {
                StartPoint = new Windows.Foundation.Point(sx, sy),
                IsClosed = true,
                IsFilled = true,
                Segments = new PathSegmentCollection(),
            };
            foreach ((double x, double y) in points)
                figure.Segments.Add(new LineSegment { Point = new Windows.Foundation.Point(x, y) });
            return figure;
        }

        var geometry = new PathGeometry { Figures = new PathFigureCollection() };
        geometry.Figures.Add(Fig(2.3, 5.0, (4.2, 3.0), (5.0, 3.0), (5.0, 11.4), (3.5, 11.4), (3.5, 5.0)));
        geometry.Figures.Add(Fig(1.8, 11.4, (6.7, 11.4), (6.7, 13.0), (1.8, 13.0)));
        geometry.Figures.Add(Fig(7.4, 6.0, (8.8, 6.0), (8.8, 7.4), (7.4, 7.4)));
        geometry.Figures.Add(Fig(7.4, 9.6, (8.8, 9.6), (8.8, 11.0), (7.4, 11.0)));
        geometry.Figures.Add(Fig(10.5, 5.0, (12.4, 3.0), (13.2, 3.0), (13.2, 11.4), (11.7, 11.4), (11.7, 5.0)));
        geometry.Figures.Add(Fig(10.0, 11.4, (14.9, 11.4), (14.9, 13.0), (10.0, 13.0)));
        return geometry;
    }

    /// <summary>
    /// 본체 툴팁 = "지금 표시 중인 옵션 (F) · 100% (A)" — 1:1 버튼이 사라져도 A 키 표기가
    /// 남게 병합한다(A111). 두 표기 모두 키 상수에서 조립한다(A34 표기 규칙).
    /// </summary>
    private static string FitTip(string description) =>
        $"{HotkeySupport.Tip(description, FitKey)} · {HotkeySupport.Tip("100%", ActualSizeKey)}";

    /// <summary>A30: 플라이아웃에서 옵션 선택 — 즉시 적용하고 버튼 표시를 그 옵션으로 바꾼다.</summary>
    private void SelectFitOption(VideoFitMode option)
    {
        _lastFitOption = option;
        _fitMode = option;
        ApplyFitMode();
        UpdateFitButton();
    }

    /// <summary>현재 보기 모드를 플레이어 Scale에 적용한다(0 = 자동 맞춤, 1 = 원본 1:1).</summary>
    private void ApplyFitMode()
    {
        if (_player is not { } p) return;

        switch (_fitMode)
        {
            case VideoFitMode.Contain:
                // A83 "축소만": 레터박스 유효 배율이 1 이상(= 영상이 표면보다 작다)이면 원본 크기로
                // 고정하고, 1 미만일 때만 libvlc 자동 맞춤(Scale 0 = 딱 그 축소 배율)에 맡긴다.
                // 트랙·표면 정보가 아직 없으면(파싱 전) 판정할 수 없으므로 종전대로 자동 맞춤 —
                // 크기를 알게 되는 Playing·SizeChanged에서 이 경로가 다시 돈다(A98 주석과 같은 안전 규칙).
                var contain = ContainScale();
                p.Scale = contain >= 1 ? 1 : 0;
                break;
            case VideoFitMode.ActualSize:
                p.Scale = 1;
                break;
            case VideoFitMode.Manual:
                break; // 수동 줌 배율은 ZoomBy가 직접 설정한다 — 여기서 재적용할 것 없음

            case VideoFitMode.FitWidth:
            case VideoFitMode.FitHeight:
                var (videoW, videoH) = VideoPixelSize();
                var (surfaceW, surfaceH) = SurfacePixelSize();
                if (videoW <= 0 || videoH <= 0 || surfaceW <= 0 || surfaceH <= 0)
                {
                    p.Scale = 0; // 트랙 정보가 아직 없으면(파싱 전) 자동 맞춤으로 대체
                    break;
                }
                p.Scale = (float)(_fitMode == VideoFitMode.FitWidth
                    ? surfaceW / videoW
                    : surfaceH / videoH);
                break;
        }
    }

    /// <summary>
    /// 레터박스(Contain) 유효 배율 = 두 축 비율 중 작은 쪽. 트랙·표면 크기를 아직 모르면 0
    /// (= 판정 불가 — 호출부가 자동 맞춤으로 안전하게 대체한다). A98의 계산식을 공유한다.
    /// </summary>
    private double ContainScale()
    {
        var (videoW, videoH) = VideoPixelSize();
        var (surfaceW, surfaceH) = SurfacePixelSize();
        if (videoW <= 0 || videoH <= 0 || surfaceW <= 0 || surfaceH <= 0) return 0;
        return Math.Min(surfaceW / videoW, surfaceH / videoH);
    }

    /// <summary>표시 표면의 물리 픽셀 크기(모니터 배율 반영) — 트랙 해상도와 같은 단위로 맞춘다.</summary>
    private (double W, double H) SurfacePixelSize()
    {
        var rs = XamlRoot?.RasterizationScale ?? 1.0;
        return (VideoSurface.ActualWidth * rs, VideoSurface.ActualHeight * rs);
    }

    /// <summary>현재 미디어의 비디오 트랙 해상도. 없으면 (0, 0).</summary>
    private (double W, double H) VideoPixelSize()
    {
        try
        {
            // Media 게터는 새 래퍼를 만들어 참조를 늘리므로 쓰고 바로 해제한다.
            using var media = _player?.Media;
            foreach (var track in media?.Tracks ?? [])
            {
                if (track.TrackType == TrackType.Video)
                {
                    var v = track.Data.Video;
                    return (v.Width, v.Height);
                }
            }
        }
        catch
        {
            // 트랙 조회 실패는 자동 맞춤으로 대체된다.
        }
        return (0, 0);
    }

    // ---------- Ctrl+휠 줌 (A98 — 영상 줌 신규, A30 Scale 메커니즘 위에) ----------

    /// <summary>Manual 줌(A98)이 마지막으로 정한 배율 — _fitMode == Manual일 때만 읽는다.</summary>
    private double _manualScale;

    /// <summary>
    /// A98: Ctrl+휠 줌 — 현재 유효 배율에서 노치당 ×1.1(양방향), 0.1~8 클램프.
    /// VideoView는 스왑체인이라 XAML transform 줌이 불가 — libvlc Scale(A30)로만 구현한다.
    /// 수동 줌이 들어오면 Fit 추종은 해제된다(SizeChanged 재적용 대상에서 빠진다 — A49 UX와 동일).
    /// 새 미디어로 바뀌면 OpenPath의 Fit 회귀 + Playing 핸들러의 ApplyFitMode 재적용이 줌을 리셋한다.
    /// </summary>
    private void ZoomBy(int delta)
    {
        if (_player is not { } p) return;
        var current = CurrentEffectiveScale();
        if (current <= 0) return; // 트랙·표면 정보가 아직 없다 — 줌 시작 불가(안전)

        var next = Math.Clamp(current * Math.Pow(1.1, delta / 120.0), 0.1, 8.0);
        _fitMode = VideoFitMode.Manual;
        _manualScale = next;
        p.Scale = (float)next;
        ShowFeedback($"Zoom {(int)Math.Round(next * 100)}%"); // A13: 볼륨 칩과 같은 패턴(전체화면 한정)
    }

    /// <summary>
    /// 현재 화면에 걸린 유효 배율. Scale == 0(자동 맞춤) 계열은 libvlc가 배율 값을 주지 않으므로
    /// 뷰포트·트랙 크기로 직접 계산한다(ApplyFitMode의 FitWidth/FitHeight 계산식 재활용). 계산 불가면 0.
    /// </summary>
    private double CurrentEffectiveScale()
    {
        switch (_fitMode)
        {
            case VideoFitMode.Manual: return _manualScale;
            case VideoFitMode.ActualSize: return 1;
        }

        var (videoW, videoH) = VideoPixelSize();
        var (surfaceW, surfaceH) = SurfacePixelSize();
        if (videoW <= 0 || videoH <= 0 || surfaceW <= 0 || surfaceH <= 0) return 0;

        return _fitMode switch
        {
            VideoFitMode.FitWidth => surfaceW / videoW,
            VideoFitMode.FitHeight => surfaceH / videoH,
            // Contain = 레터박스 배율, 단 축소만이라 1을 넘지 않는다(A83 — ApplyFitMode와 같은 판정)
            _ => Math.Min(Math.Min(surfaceW / videoW, surfaceH / videoH), 1.0),
        };
    }

    /// <summary>A30: 본체 클릭 = 버튼에 표시된 마지막 옵션 적용.</summary>
    private void OnFitClicked(object sender, RoutedEventArgs e) => ApplyLastFitOption();

    /// <summary>Fit 본체 클릭·F 키(A34) 공용 경로 — 플라이아웃이 아니라 마지막 옵션 재적용이다.</summary>
    private void ApplyLastFitOption()
    {
        _fitMode = _lastFitOption;
        ApplyFitMode();
    }

    /// <summary>플라이아웃 100%·A 키(A34) 공용 경로 — 구 1:1 버튼 자리(A111).</summary>
    private void OnFitActualSizeClicked(object sender, RoutedEventArgs e) =>
        SelectFitOption(VideoFitMode.ActualSize);

    private void OnFitContainClicked(object sender, RoutedEventArgs e) =>
        SelectFitOption(VideoFitMode.Contain);

    private void OnFitWidthClicked(object sender, RoutedEventArgs e) =>
        SelectFitOption(VideoFitMode.FitWidth);

    private void OnFitHeightClicked(object sender, RoutedEventArgs e) =>
        SelectFitOption(VideoFitMode.FitHeight);

    // A151: 전체화면 토글(ToggleFullScreen·⛶ 버튼·F11/Enter/Esc 액셀러레이터)은 전부 제거 —
    // 전체화면은 셸의 3단 모드 체계(MainWindow — Enter 순환·Alt+Enter·Esc·모드 버튼)가 담당한다.
    // 이 뷰는 상태 플래그 없이 매번 Presenter.Kind를 읽으므로(IsFullScreen — 피드백 칩 판정)
    // 셸이 프레젠터를 바꿔도 어긋날 상태가 없다.

    // ---------- 입력 핸들러 ----------

    private void OnPlayClicked(object sender, RoutedEventArgs e) => TogglePlayPause();

    private void OnMuteClicked(object sender, RoutedEventArgs e) => ToggleMute();

    /// <summary>영상 클릭 = 재생/일시정지 (플레이어 관례).</summary>
    private void OnSurfaceTapped(object sender, TappedRoutedEventArgs e)
    {
        e.Handled = true;
        TogglePlayPause();
    }

    /// <summary>영상 위 휠 = 볼륨 조절(유지), Ctrl+휠 = 줌(A98 — 영상 줌 신규).</summary>
    private void OnSurfaceWheel(object sender, PointerRoutedEventArgs e)
    {
        var delta = e.GetCurrentPoint(this).Properties.MouseWheelDelta;
        if (delta == 0) return;
        e.Handled = true;
        if (e.KeyModifiers.HasFlag(Windows.System.VirtualKeyModifiers.Control))
            ZoomBy(delta); // 트랙 정보 전이면 무동작 — 볼륨으로 새지 않는다
        else
            ChangeVolume(delta > 0 ? VolumeStep : -VolumeStep);
    }

    private bool _isScrubbing;
    private bool _resumeAfterScrub;

    private void OnSeekPointerPressed(object sender, PointerRoutedEventArgs e)
    {
        if (_player is not { } p || _durationMs <= 0) return;
        _isScrubbing = true;
        _resumeAfterScrub = p.IsPlaying;
    }

    private void OnSeekPointerReleased(object sender, PointerRoutedEventArgs e)
    {
        if (!_isScrubbing) return;
        _isScrubbing = false;
        if (_player is not { } p || _durationMs <= 0) return;

        p.Time = (long)(SeekSlider.Value / SeekSlider.Maximum * _durationMs);

        // 드래그 중 멈췄던(또는 시킹으로 멎어버린) 재생을 되살린다
        if (_resumeAfterScrub && !p.IsPlaying) p.Play();
    }

    private void OnSeekSliderChanged(object sender, RangeBaseValueChangedEventArgs e)
    {
        if (_suppressSeekEvent || _player is not { } p || _durationMs <= 0) return;

        var targetMs = (long)(e.NewValue / SeekSlider.Maximum * _durationMs);
        if (_isScrubbing)
        {
            // 드래그 중에는 위치 미리보기만 — 실제 시킹은 놓을 때(OnSeekPointerReleased) 1회
            PositionText.Text = TimeText.Format(targetMs);
            return;
        }
        p.Time = targetMs; // 키보드 조작·트랙 클릭 등 단발 변경은 즉시 시킹
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

    /// <summary>
    /// Space = 재생/일시정지. A157(v0.168.0)에서 탐색기 표면의 Space(선택 토글)와 충돌하므로,
    /// 포커스가 통과 표면(탐색기 리스트·트리·썸네일)이나 텍스트 입력에 있으면 삼키지 않는다 —
    /// 액셀러레이터는 표면 KeyDown보다 먼저 돌아 여기서 흘려 주지 않으면 표면이 받을 방법이 없다.
    /// 가드 형태는 구 Enter 액셀러레이터(A151에서 제거)의 것을 승계한 공용 통과 판정 한 벌이다.
    /// </summary>
    private void OnTogglePlayInvoked(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        if (HotkeySupport.ShouldPassThrough(this))
        {
            args.Handled = false;
            return;
        }
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

    // ---------- 하단 바 버튼 핫키 (A34) ----------

    /// <summary>Fit 키 — 툴팁 표기(UpdateFitButton)와 액셀러레이터가 이 한 값을 함께 쓴다.</summary>
    private const VirtualKey FitKey = VirtualKey.F;

    /// <summary>
    /// 100%(1:1) 키 — A111에서 1:1 버튼이 사라진 뒤로도 A는 그대로 100% 적용이다(A107 확정:
    /// 문자 핫키 전부 유지). 대상만 Fit 버튼의 100% 옵션 적용 액션으로 옮겼다.
    /// </summary>
    private const VirtualKey ActualSizeKey = VirtualKey.A;

    /// <summary>
    /// A34: 하단 바 버튼에 단독 문자 키를 걸고 툴팁 "(키)" 표기까지 같은 호출에서 만든다.
    /// 텍스트 입력·탐색기 파일 리스트 포커스에서는 HotkeySupport가 키를 통과시킨다(A32/A84 규칙).
    /// M(음소거)은 v0.21.0부터 있던 키를 XAML 액셀러레이터에서 여기로 옮긴 것 — 의미는 그대로다.
    /// 플라이아웃형(S 배속·C 자막)은 누르면 목록이 열리고, Fit(F)·100%(A)는 즉시 적용이다.
    /// A111부터 A·F 둘 다 Fit 버튼에 건다(1:1 버튼이 없어졌을 뿐, 키 동작은 무변경) —
    /// 툴팁은 상태를 따라가므로 UpdateFitButton()이 두 키 표기를 함께 만든다.
    /// 자막이 S가 아니라 C인 것은 S를 배속(Speed)이 먼저 쓰기 때문 — 캡션 관습 키를 따랐다.
    /// </summary>
    private void SetupHotkeys()
    {
        HotkeySupport.Bind(this, MuteButton, VirtualKey.M, "Mute", ToggleMute);
        HotkeySupport.Bind(this, SpeedBox, VirtualKey.S,
            "Playback speed", () => SpeedBox.IsDropDownOpen = true);
        HotkeySupport.Bind(this, SubtitleButton, VirtualKey.C,
            "Subtitles", () => SubtitleFlyout.ShowAt(SubtitleButton));
        HotkeySupport.Register(this, FitButton, ActualSizeKey,
            () => SelectFitOption(VideoFitMode.ActualSize));
        HotkeySupport.Register(this, FitButton, FitKey, ApplyLastFitOption);
        UpdateFitButton(); // Fit 툴팁은 표시 상태를 따라가므로 초기값도 여기서 만든다
    }
}
