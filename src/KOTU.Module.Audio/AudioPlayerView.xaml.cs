using LibVLCSharp.Platforms.Windows;
using LibVLCSharp.Shared;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Windows.Devices.Enumeration;
using Windows.Media.Devices;
using Windows.System;
using KOTU.Core.Contracts;
using KOTU.Core.Integration;
using KOTU.Core.Navigation;
using KOTU.Core.Settings;
using KOTU.Core.Threading;
using KOTU.Input;

namespace KOTU.Module.Audio;

/// <summary>
/// 음악 플레이어 화면 (A10 — 비디오 모듈에서 분리). 재생/일시정지, 시킹(슬라이더·←/→ 5초),
/// 볼륨(↑/↓)·음소거(M), 배속, 이어듣기, 이퀄라이저 프리셋(A163)·오디오 장치 선택(A164)을 제공한다. 전체화면은 A151부터 셸의 3단 모드 체계
/// (Enter 순환·Alt+Enter) 몫이다 — 이 뷰에는 진입 코드가 없다.
/// 표면은 libvlc 파형 시각화(scope)가 채우고 상단에 ♪ + 파일명 오버레이를 띄운다.
/// 파형 시각화는 인스턴스 옵션으로만 동작하므로(v0.12.0 실기기 확인) 항상 켠 인스턴스를
/// 1회 생성해 재사용한다 — 비디오처럼 음악↔영상 교체 재생성이 필요 없다.
/// 스레드 모델(A42): libvlc 생성·해제는 뷰 전용 워커에서 직렬로. libvlc 이벤트는
/// libvlc 자체 스레드에서 오므로 UI 갱신은 DispatcherQueue로 넘긴다(Dispatch).
/// </summary>
public sealed partial class AudioPlayerView : UserControl, IBottomBarProvider,
    IContentStateSource, IContentInfoProvider, ITrayStatusProvider
{
    /// <summary>파일 재생을 시작하면 셸에 알린다(빈 상태 탐색기 내림·오버레이 기준 갱신).</summary>
    public event Action<string>? ContentOpened;

    // ---------- 트레이 아이콘 내용 (A54, v0.118.0) ----------

    /// <summary>트레이 표시 값이 바뀌었다 — 재생 중에는 1초 타이머가, 그 밖에는 상태 전이가 쏜다.</summary>
    public event Action? TrayStatusChanged;

    /// <summary>이퀄라이저 막대 개수(16px 아래 줄에 들어가는 한계).</summary>
    private const int EqualizerBars = 4;

    /// <summary>재생 중일 때만 도는 트레이 갱신 타이머(1초) — 그 이상 자주 하면 아이콘 재합성이 낭비다.</summary>
    private DispatcherTimer? _trayTimer;

    /// <summary>이퀄라이저 의사 패턴의 위상. 1초마다 1 증가한다.</summary>
    private int _trayPhase;

    /// <summary>
    /// 트레이 아이콘 내용(A54): 열림 = 재생 위치 · 이퀄라이저 막대, 유휴 = "AUD".
    /// <b>이퀄라이저는 실제 주파수 분석이 아니다</b>(구현 시 결정) — libvlc 오디오 콜백을 붙이는
    /// 비용이 16px 장식에 비해 과해서, 재생 중임을 알리는 <b>의사 패턴</b>(위상 기반 결정론적 값)을 그린다.
    /// 일시정지·정지면 막대가 낮게 고정되고 타이머도 멈춘다.
    /// </summary>
    public TrayStatus GetTrayStatus()
    {
        if (_filePath is null) return TrayStatus.Idle("AUD");

        var playing = _player is { IsPlaying: true };
        var position = _player?.Time ?? 0;
        var bars = new double[EqualizerBars];
        for (var i = 0; i < bars.Length; i++)
        {
            bars[i] = playing
                ? 0.35 + 0.6 * Math.Abs(Math.Sin(_trayPhase * 0.9 + i * 1.7))
                : 0.15; // 멈춘 상태 = 낮게 고정
        }
        return TrayStatus.OpenWithBars(TimeText.Format(position), bars);
    }

    /// <summary>재생 상태에 맞춰 1초 타이머를 켜고 끈다(UI 스레드에서만 호출).</summary>
    private void SetTrayTimer(bool running)
    {
        if (_tornDown || !running)
        {
            _trayTimer?.Stop();
            return;
        }
        if (_trayTimer is null)
        {
            _trayTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
            _trayTimer.Tick += (_, _) =>
            {
                _trayPhase++;
                TrayStatusChanged?.Invoke();
            };
        }
        _trayTimer.Start();
    }

    /// <summary>
    /// 정보 오버레이용 미디어 정보: 파일·시간·오디오 트랙. A150에서 라벨·값 행 목록으로
    /// 이식했다(값 포맷은 유지). 값이 없는 행은 생략한다.
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
                if (track.TrackType == TrackType.Audio)
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

    /// <summary>트랜스포트 바를 뷰에서 떼어 셸 하단 바 한 줄에 얹는다(비디오와 동일 규격).</summary>
    public object? TakeBottomBar()
    {
        RootGrid.Children.Remove(TransportBar);
        return TransportBar;
    }

    /// <summary>
    /// A217(v0.229.0): 좁은 하단 바 축약 — 비디오 A40 로직의 동형 이식(임계·숨김 대상 동일).
    /// A217의 공통 클러스터 정렬로 이 바의 고정 폭 합이 비디오와 같은 426이 됐고, A250(v0.246.0)의
    /// 볼륨 슬라이더 96→101로 두 모듈이 나란히 <b>431</b>이 됐다(XAML TransportBar 헤더 주석의
    /// 재계수 참조 — 종전 394 시절의 "축약 불요" 판정 소멸).
    /// 임계 645(A250 이전 640)와 산식 계보는 비디오 UpdateCompactTransport 주석이 정본이다.
    /// 숨김 대상(볼륨·시간 텍스트 2개)까지 비디오와 같아야 축약 후에도 클러스터 x가 일치한다.
    /// 숨겨도 기능은 남는다: 볼륨은 ↑/↓·휠·음소거 버튼, 재생 위치는 시크 슬라이더 썸 위치가 대신한다.
    /// </summary>
    private void UpdateCompactTransport(double width)
    {
        // A249 예외: 폭 임계 축약은 "숨김 금지" 정책의 확정 예외다(공간 제약 — 2026-08-27 사용자
        // 답변). 영상과 같은 임계·같은 대상이어야 정렬이 유지된다.
        var visibility = width < 645 ? Visibility.Collapsed : Visibility.Visible;
        VolumeSlider.Visibility = visibility;
        PositionText.Visibility = visibility;
        DurationText.Visibility = visibility;
    }

    private const long SeekStepMs = 5_000;
    private const int VolumeStep = 5;
    private const long ResumeReportIntervalMs = 10_000;
    private static readonly float[] Speeds = [0.5f, 0.75f, 1.0f, 1.25f, 1.5f, 2.0f];

    /// <summary>내장 샘플 곡(A66: 펜타토닉 멜로디 18초) — 배포본 Assets에 동봉 (비디오 테스트 클립과 동일 패턴).</summary>
    private static readonly string SampleTrackPath =
        Path.Combine(AppContext.BaseDirectory, "Assets", "sample.mp3");

    /// <summary>샘플 곡은 이어듣기 대상에서 뺀다 (18초 샘플에 이어듣기는 무의미 — 비디오 IsTestClip과 동일).</summary>
    private static bool IsSampleTrack(string? path) =>
        string.Equals(path, SampleTrackPath, StringComparison.OrdinalIgnoreCase);

    private readonly ISettingsService _settings;
    private readonly PlaybackResumeStore _resumeStore;
    private string? _filePath;

    private LibVLC? _libVlc;
    private MediaPlayer? _player;
    private string[]? _swapChainOptions;   // 스왑체인 준비 전 OpenPath 대비 (Vlc.Initialized에서 1회 저장)
    private readonly SemaphoreSlim _playerGate = new(1, 1); // 생성 직렬화 (Initialized·OpenPath 경합 대비)
    private long _durationMs;
    private long _lastReportedMs;
    private long _pendingResumeMs = -1;
    private bool _suppressSeekEvent;
    private bool _suppressVolumeEvent;
    private bool _muted; // A28: 음소거 상태 로컬 소유 — libvlc Mute 게터의 스테일 값 회피
    private bool _tornDown;

    // ---------- 이퀄라이저 · 오디오 장치 상태 (A163 · A164) ----------
    // 저장 키는 전역 1벌(설정 화면 노출 없음): audio.equalizer = 프리셋 이름("" = Off),
    // audio.outputDevice = libvlc 장치 ID("" = 시스템 기본 — 이름이 아니라 ID인 이유는
    // 장치 이름이 OS 로캘 문자열이라 재부팅·언어 변경으로 바뀔 수 있어서다).
    private string[] _eqPresetNames = []; // libvlc 내장 프리셋 이름 — 플레이어 생성 워커에서 1회 열거
    private string _eqPreset;             // 현재 프리셋 이름("" = Off) — 상태는 로컬 소유(_muted와 동일 규칙)
    private string _outputDeviceId;       // 현재 출력 장치 ID("" = 시스템 기본)
    private readonly MenuFlyoutSubItem _outputMenu = new() { Text = "Output device" };
    private readonly MenuFlyoutSubItem _inputMenu = new() { Text = "Input device" };
    private ModuleWorker? _worker; // libvlc 생성·해제 전용(A42) — 뷰별 분리

    /// <summary>지연 생성. 이 뷰는 Unloaded가 곧 최종 해체(_tornDown)라 재생성될 일은 없다.</summary>
    private ModuleWorker Worker => _worker ??= new ModuleWorker("KOTU audio worker");

    // ---------- A11(v0.212.0) 재생 목록 루프 상태 (설계 docs/A11-playlist-design.md §3) ----------
    // 영상(v0.211.0)에 먼저 구현한 구조의 동형 이식이다 — 키 접두사만 audio.* 로 다르고
    // 의미·기본값·우선순위는 한 글자도 다르지 않다(설계 §7 배치 ③).
    // 설정 3축(부록 B 76 확정): 목록 루프(기본 켬·무한 고정) / 현재 파일 루프(기본 끔) /
    // 루프 횟수(현재 파일 루프 전용 — "1" = 한 번 더 = 총 2회 재생, 확정 ⓐ 해석).
    // 저장은 전역 1벌·즉시 Set+Save(EQ 선례), 창 간 실시간 전파 없음 — 상태는 로컬 소유(_muted 규칙).

    private const string LoopListKey = "audio.loopList";
    private const string LoopCurrentKey = "audio.loopCurrent";
    private const string LoopCountKey = "audio.loopCount"; // 문자열 enum "1"·"3"·"infinite" — explorer.sort 관례

    private FolderPlaylist? _playlist; // 같은 폴더 스냅샷 목록 — EnsurePlaylist가 워커에서 만든다
    private bool _loopList;
    private bool _loopCurrent;
    private int _loopCountLimit; // 0 = 무한, 1·3 = "그만큼 한 번 더"(리핏 허용 횟수)
    private int _loopPlays;      // 현재 파일에서 소진한 리핏 횟수 — PlayCurrent가 리셋, ReplayCurrent만 증가

    /// <summary>"1"·"3"은 그 횟수, 그 외(기본 "infinite"·구버전 잔값 포함)는 전부 무한(0)으로 읽는다.</summary>
    private static int ParseLoopCount(string value) => value switch
    {
        "1" => 1,
        "3" => 3,
        _ => 0,
    };

    public AudioPlayerView(OpenContext context, ISettingsService settings)
    {
        InitializeComponent();
        _settings = settings;
        _resumeStore = new PlaybackResumeStore(settings);
        _filePath = context.FilePath is { } p && File.Exists(p) ? p : null;

        foreach (var s in Speeds)
            SpeedBox.Items.Add($"{s:0.##}×");
        SpeedBox.SelectedIndex = Array.IndexOf(Speeds, 1.0f);

        // A11: 루프 설정 읽기(생성자 1회 — _muted 규칙)와 플라이아웃 구성은 SetupHotkeys보다
        // 먼저다 — UpdateLoopButton(툴팁 초기값)이 상태를 읽는다(영상과 같은 순서).
        _loopList = _settings.Get(LoopListKey, true);
        _loopCurrent = _settings.Get(LoopCurrentKey, false);
        _loopCountLimit = ParseLoopCount(_settings.Get(LoopCountKey, "infinite"));
        BuildLoopFlyout();

        SetupHotkeys(); // A34: 하단 바 버튼 핫키 + 툴팁 표기

        _eqPreset = _settings.Get("audio.equalizer", string.Empty);
        _outputDeviceId = _settings.Get("audio.outputDevice", string.Empty);

        // A164: 장치 플라이아웃 뼈대 — 서브메뉴 2개(출력 / Windows 기본 입력)는 코드로 만든다
        // (MenuFlyoutSubItem은 XAML 선례가 없어 코드 구성만 쓴다 — 탐색기 우클릭 메뉴와 같은 방식).
        // 목록은 열 때마다 새로 채운다: 오디오 장치는 꽂힘·뽑힘이 잦아 시점 캐시가 무의미하다
        // (Opening 재구성은 이미지 모듈 우클릭 플라이아웃 선례). 입력 변경은 앱 밖(시스템 전역)에
        // 영향을 주므로 툴팁으로 병기한다(부록 B 70 확정 문구).
        ToolTipService.SetToolTip(_inputMenu, "Sets the Windows default input device");
        DevicesFlyout.Items.Add(_outputMenu);
        DevicesFlyout.Items.Add(_inputMenu);
        DevicesFlyout.Opening += (_, _) =>
        {
            FillOutputDeviceMenu();
            FillInputDeviceMenu();
        };

        _suppressVolumeEvent = true;
        VolumeSlider.Value = Math.Clamp(_settings.Get("audio.volume", 80), 0, 100);
        _suppressVolumeEvent = false;

        if (_filePath is null)
            PlaceholderText.Visibility = Visibility.Visible;

        Vlc.Initialized += OnVlcInitialized;
        Loaded += (_, _) => Focus(FocusState.Programmatic);
        Unloaded += OnUnloaded;

        // 휠 = 볼륨 (플레이어 관례). 자식 요소가 소비해도 받도록 handledEventsToo.
        AudioSurface.AddHandler(PointerWheelChangedEvent,
            new PointerEventHandler(OnSurfaceWheel), handledEventsToo: true);

        // A217: 바 폭이 좁으면 볼륨 슬라이더·시간 텍스트를 숨긴다(비디오 A40 훅의 동형 —
        // 셸 하단 바로 옮겨진 뒤에도 유효).
        TransportBar.SizeChanged += (_, e) => UpdateCompactTransport(e.NewSize.Width);

        // 시크 슬라이더 스크럽 감지: 드래그 중에는 시킹하지 않고 놓을 때 1회만 시킹한다.
        // (드래그 틱마다 p.Time을 설정하면 시킹이 폭주하는 실기기 버그 — 비디오와 동일 대응)
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
    /// 파형 시각화를 켠 libvlc 인스턴스를 1회 생성한다(이미 있으면 그대로).
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

            // --no-video-title-show: 재생 시작 시 파일명이 화면에 오버레이되는 libvlc 기본 동작 끔.
            // --audio-visual/--effect-list: 파형 시각화(scope) — 인스턴스 옵션으로만 동작(A10 이관).
            string[] options =
                [.. swapOptions, "--no-video-title-show", "--audio-visual=visual", "--effect-list=scope"];

            var (libVlc, player, presetNames) = await Worker.Run(_ =>
            {
                // libvlc 네이티브 dll은 libvlc\win-x64\ 하위에 배포되므로 검색 경로 등록이 선행돼야 한다.
                // 주의: 그냥 Core라고 쓰면 KOTU.Core 네임스페이스로 해석된다(상위 네임스페이스 우선).
                LibVLCSharp.Shared.Core.Initialize();
                var lib = new LibVLC(options);
                var mp = new MediaPlayer(lib);

                // A163: 내장 프리셋 이름 열거 — LibVLCSharp 3.x의 PresetCount·PresetName은
                // 인스턴스 멤버라(4.x 문서와 다름) 빈 인스턴스를 하나 만들어 읽고 바로 해제한다.
                // libvlc 네이티브 호출이므로 Core.Initialize 이후 이 워커에서 함께 처리한다.
                var names = Array.Empty<string>();
                try
                {
                    using var eq = new Equalizer();
                    var count = eq.PresetCount;
                    names = new string[count];
                    for (var i = 0u; i < count; i++)
                        names[i] = eq.PresetName(i) ?? string.Empty;
                }
                catch
                {
                    // 프리셋 열거 실패 — EQ 버튼만 비활성으로 남고 재생은 정상 진행한다.
                }

                return (lib, mp, names);
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

            // A163: 프리셋 목록 확정 + 저장값 적용(볼륨과 같은 UI 스레드 적용 경로).
            // 이퀄라이저는 한 번 걸면 이후 미디어에도 유지되므로(libvlc 문서) 이 1회가
            // "재생 시작 시 재적용"을 충족한다. 출력 장치는 aout이 생겨야 걸리는 값이라
            // Playing 이벤트에서 재적용한다(OnPlayerPlaying 주석 참고).
            _eqPresetNames = presetNames;
            if (_eqPreset.Length > 0 && Array.IndexOf(_eqPresetNames, _eqPreset) < 0)
                _eqPreset = string.Empty; // 저장 이름이 이 libvlc 목록에 없다 — Off 폴백(설정 파일은 유지)
            FillEqualizerFlyout();
            ApplyEqualizer(player);
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

    /// <summary>현재 _filePath를 처음부터(또는 이어듣기 지점부터) 재생한다. 플레이어 준비 후에만 호출.</summary>
    private void PlayCurrent()
    {
        if (_player is not { } p || _libVlc is not { } lib || _filePath is null) return;

        _durationMs = 0;
        _lastReportedMs = 0;
        _loopPlays = 0; // A11: 재생 단위가 새로 시작되면 리핏 카운터 리셋 — ReplayCurrent만 증가시킨다
        _pendingResumeMs = IsSampleTrack(_filePath)
            ? -1
            : _resumeStore.GetResumePositionMs(_filePath) ?? -1;
        EnsurePlaylist(); // A11: 폴더 재생 목록 준비(워커행 — libvlc 생성과 같은 Worker.Run 관용구)

        using var media = new Media(lib, new Uri(_filePath));
        p.Play(media);
        PlaceholderText.Visibility = Visibility.Collapsed;
        TitleOverlay.Visibility = Visibility.Visible;
        TitleText.Text = Path.GetFileNameWithoutExtension(_filePath);
        ContentOpened?.Invoke(_filePath); // 셸 동기화
        TrayStatusChanged?.Invoke();      // A54: 유휴("AUD") → 열림(시간 · 이퀄라이저)
    }

    /// <summary>
    /// A11(설계 §2.3): 리핏 전용 재장전. Ended에서는 Play()만으로 재시작이 안 되므로
    /// (TogglePlayPause Ended 분기의 실검증 선례) PlayCurrent처럼 미디어를 다시 걸되,
    /// 매 루프마다 나면 소음인 부작용을 뺀 변형이다 — 원형(PlayCurrent)은 고치지 않는다:
    /// ① TitleOverlay 재대입 생략 — 같은 파일이라 이미 같은 이름이 떠 있다.
    /// ② ContentOpened 생략 — 셸 동기화(S4 종료·오버레이·아이콘)는 전부 무의미한 재계산.
    /// 이어듣기 조회도 생략(_pendingResumeMs = -1 고정) — EndReached가 기록을 이미 지웠고
    /// (97% 정책) 리핏은 항상 0초 시작이다. 배속·출력 장치 재적용은 기존 Playing 핸들러가 잇고,
    /// EQ는 인스턴스에 걸린 채 유지된다(EnsurePlayerAsync 주석). 트레이 1초 타이머도 같은
    /// Playing 핸들러가 다시 켠다.
    /// 영상 원본과 다른 점: A12 시작 오버레이·자막(_pendingAutoSubtitle)이 없어 더 단순하다.
    /// </summary>
    private void ReplayCurrent()
    {
        if (_player is not { } p || _libVlc is not { } lib || _filePath is null) return;

        _durationMs = 0;
        _lastReportedMs = 0;
        _pendingResumeMs = -1;

        using var media = new Media(lib, new Uri(_filePath));
        p.Play(media);
    }

    /// <summary>
    /// A11: 현재 파일의 폴더 재생 목록(같은 폴더 스냅샷 — 감시 없음, 이미지·영상 선례와 동일)을
    /// 준비한다. 폴더 스캔은 파일당 속성 읽기가 있어 UI 스레드 금지(§11.1) — libvlc 생성과 같은
    /// Worker.Run 관용구로 돌리고, 결과가 오기 전에 파일이 바뀌었으면 버린다.
    /// 목록 진행(OpenPath)으로 온 파일은 이미 목록의 현재 항목이라 재스캔하지 않는다.
    /// 내장 샘플 곡은 목록 대상이 아니다(Assets 폴더 순회는 무의미 — 이어듣기 제외와 같은 성질,
    /// 영상의 테스트 클립 판정과 동형).
    /// 스캔이 끝나기 전에 EOF가 오면 그 회차는 "목록 없음"으로 판정된다(아주 짧은 트랙 +
    /// 느린 네트워크 폴더의 희귀 경합 — 수용, 다음 EOF부터 정상).
    /// </summary>
    private async void EnsurePlaylist()
    {
        var file = _filePath!;
        if (IsSampleTrack(file))
        {
            _playlist = null;
            return;
        }
        if (_playlist is { } current &&
            string.Equals(current.Current, file, StringComparison.OrdinalIgnoreCase))
        {
            return; // 목록 진행으로 온 파일 — 스냅샷 유지
        }

        _playlist = null;
        FolderPlaylist list;
        try
        {
            list = await Worker.Run(_ => FolderPlaylist.Create(file, AudioModule.Extensions));
        }
        catch
        {
            return; // 목록 생성 실패가 재생을 방해하면 안 된다(libvlc 부수 작업과 같은 규칙)
        }

        if (_tornDown || file != _filePath) return; // 그새 다른 파일로 전환됨
        _playlist = list;
    }

    // ---------- 파일 열기 (버튼/드래그&드롭/초기 컨텍스트) ----------

    private async void OpenPath(string path)
    {
        if (!File.Exists(path)) return;

        // 듣던 파일이 있으면 위치를 저장하고 전환한다 (샘플 곡은 이어듣기 제외).
        if (_player is { } p && _filePath is not null && !IsSampleTrack(_filePath) && _durationMs > 0)
        {
            try { _resumeStore.Report(_filePath, p.Time, _durationMs); }
            catch { /* 저장 실패가 전환을 막으면 안 된다 */ }
        }

        _filePath = path;

        await EnsurePlayerAsync(); // 이미 있으면 즉시 반환 — 인스턴스 교체 없음(항상 시각화 켬)
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
        SetTrayTimer(false); // A54: 뷰가 내려가면 1초 타이머도 반드시 멈춘다
        var player = _player;
        var libVlc = _libVlc;
        _player = null;
        _libVlc = null;

        if (player is not null)
        {
            // 이어듣기 저장 후 해제. Stop/Dispose는 UI 스레드에서 부르면
            // libvlc 콜백과 교착할 수 있어 백그라운드로 넘긴다.
            try
            {
                if (_filePath is not null && !IsSampleTrack(_filePath) && _durationMs > 0)
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

        _settings.Set("audio.volume", (int)VolumeSlider.Value);
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
        // 이어듣기 위치 보고는 UI와 무관하므로 이벤트 스레드에서 바로 처리(스토어는 스레드 안전).
        if (_filePath is not null && !IsSampleTrack(_filePath) && _durationMs > 0 &&
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
    }

    private void OnPlayerPlaying(object? sender, EventArgs e) => Dispatch(() =>
    {
        PlayButton.Content = "❚❚";

        // 재생이 실제로 시작된 뒤에만 시킹이 적용된다.
        if (_pendingResumeMs > 0 && _player is { } p)
        {
            p.Time = _pendingResumeMs;
            _pendingResumeMs = -1;
        }

        // 배속은 미디어가 바뀌면 초기화되므로 콤보의 현재 선택을 다시 적용한다(같은 값 재적용은 무해).
        if (_player is { } player &&
            SpeedBox.SelectedIndex is >= 0 and var i && i < Speeds.Length)
        {
            player.SetRate(Speeds[i]);
        }

        // A164: 저장된 출력 장치 재적용. libvlc 3의 장치 지정(module=NULL)은 살아 있는
        // aout에만 걸리므로(재생 전 호출은 무동작) 재생이 실제로 시작된 이 시점이 가장 이른
        // 유효 적용점이다. 같은 플레이어로 다음 곡을 이어 재생해도 같은 값 재적용은 무해하다
        // (배속과 동일 규칙). 빈 값 = 시스템 기본 — 새 aout의 기본 동작이므로 아예 부르지 않는다.
        if (_outputDeviceId.Length > 0 && _player is { } dp)
        {
            try { dp.SetOutputDevice(_outputDeviceId); }
            catch { /* 장치 유실(분리 등) — 시스템 기본으로 재생은 계속된다 */ }
        }

        SetTrayTimer(true); // A54: 재생 중에만 1초마다 트레이(시간·이퀄라이저)를 갱신한다
        TrayStatusChanged?.Invoke();
    });

    private void OnPlayerPaused(object? sender, EventArgs e) =>
        Dispatch(() =>
        {
            PlayButton.Content = "▶";
            SetTrayTimer(false); // A54: 멈추면 타이머도 멈춘다 — 막대는 낮게 고정
            TrayStatusChanged?.Invoke();
        });

    private void OnPlayerEndReached(object? sender, EventArgs e)
    {
        // 끝까지 들었으면 이어듣기 기록을 지운다. (이 콜백 안에서 Stop()을 부르면 교착 — 금지)
        // A11: 이 삭제는 루프 전이와 무관하게 유지한다 — 다 들은 파일의 기록 청소는 별개 사실이고,
        // 바로 이 삭제가 목록 진행·리핏 후 이 파일을 다시 열 때 0초 시작을 보장한다(설계 §3.3).
        if (_filePath is not null && !IsSampleTrack(_filePath) && _durationMs > 0)
            _resumeStore.Report(_filePath, _durationMs, _durationMs);

        // A11(v0.212.0): EOF 전이가 여기 얹힌다. 전이 판정·재생 API는 전부 UI 스레드에서
        // (libvlc 콜백 안 재생 API 직접 호출 금지 — Dispatch 경유), Dispatch 사이에 사용자가
        // 개입했을 수 있어 AdvanceAfterEnd가 파일·상태를 재검사한다.
        var endedFile = _filePath;
        Dispatch(() => AdvanceAfterEnd(endedFile));
    }

    /// <summary>
    /// A11: EOF 전이(설계 §3.3 전이표 — 위에서부터 첫 일치). 우선순위 = 현재 루프(횟수 내) >
    /// 목록 진행 > 목록 루프(처음으로) > 정지(부록 B 76 확정 — repeat one이 repeat all을 가리는
    /// 음악 플레이어 통례). 전이 1~4는 Ended에 머물지 않으므로 종전 EndReached UI 갱신
    /// (▶ 표기·시크바 끝·트레이 타이머 정지)을 생략한다 — 곧 Playing이 덮어써 깜빡임만 만든다.
    /// 정지(전이 5)만 종전 갱신 그대로다. EncounteredError는 전이 트리거가 아니다(실패 파일
    /// 자동 스킵은 무한 실패 루프 위험 — 별도 설계 대상, §3.3). UI 스레드 전용.
    /// 영상 원본과 다른 점: 오디오에는 IPlaybackStateSource·PlaybackStateChanged가 없어
    /// (설계 §5.2 — A186 확대는 이번 범위 밖) 그 발화 줄이 통째로 빠지고, 대신 오디오만의
    /// 트레이 1초 타이머 정지(SetTrayTimer(false))·TrayStatusChanged가 정지 전이에 남는다.
    /// </summary>
    private void AdvanceAfterEnd(string? endedFile)
    {
        // 재진입 가드(설계 §2.3): _tornDown은 Dispatch가 이중 검사했다. 여기서는
        // ② 그새 다른 파일로 전환되지 않았는지 ③ 사용자가 ▶로 이미 재시작하지 않았는지(Ended 유지)만.
        if (_player is not { } p || _filePath is null || _filePath != endedFile) return;
        if (p.State != VLCState.Ended) return;

        // 전이 1: 현재 파일 루프 — 횟수 내면 같은 파일 재시작(0 = 무한. "1" = 한 번 더 = 총 2회).
        if (_loopCurrent && (_loopCountLimit == 0 || _loopPlays < _loopCountLimit))
        {
            _loopPlays++;
            ReplayCurrent();
            return;
        }

        // 전이 2·3: 다음 파일로 / 목록 끝이면 첫 파일로(목록 루프는 무한 고정 — 부록 B 76).
        // 그새 소실된 파일은 Remove로 목록에서 빼고 그다음 후보로 재시도한다(구현 시 결정).
        // OpenPath = 기존 완결 경로 재사용(설계 §2.2 경로 B) — 이어듣기 저장·PlayCurrent·
        // ContentOpened 셸 동기화(트레이·A174)까지 전부 따라온다. 신규 셸 배선 0(설계 §5).
        if (_playlist is { } list)
        {
            while (true)
            {
                var next = list.HasNext ? list.PeekNext
                    : _loopList && list.Count > 1 ? list.PeekFirst
                    : null;
                if (next is null) break;

                if (!File.Exists(next))
                {
                    list.Remove(next);
                    continue;
                }

                if (list.HasNext) list.MoveNext();
                else list.MoveFirst();
                OpenPath(next);
                return;
            }

            // 전이 4: 목록 루프 켬 + 단일 파일 목록 = 같은 파일 재시작.
            // _loopPlays와 무관하다 — 이것은 횟수 축이 없는 "목록 루프"의 축이다(설계 §3.3).
            if (_loopList && list.Count == 1)
            {
                ReplayCurrent();
                return;
            }
        }

        // 전이 5: 정지 — 종전 EndReached UI 갱신 그대로(유일하게 Ended에 머무는 경로).
        PlayButton.Content = "▶";
        PositionText.Text = TimeText.Format(_durationMs);
        _suppressSeekEvent = true;
        SeekSlider.Value = SeekSlider.Maximum;
        _suppressSeekEvent = false;
        SetTrayTimer(false); // A54: 멈추면 타이머도 멈춘다 — 막대는 낮게 고정
        TrayStatusChanged?.Invoke();
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

    // ---------- 조작 ----------

    private void TogglePlayPause()
    {
        // 아무것도 열지 않은 상태의 ▶ = 내장 샘플 곡 재생 (A66 — 비디오의 테스트 클립과 동일 UX).
        // A207: 반드시 _player 가드보다 앞이다 — 빈 모듈 상태(S1 중앙 탐색기)에서는 스왑체인
        // 초기화(OnVlcInitialized → _player 생성)가 아직 안 끝났을 수 있어, 종전 가드 순서로는
        // ▶가 침묵 무동작이었다(회귀 원인, 비디오와 동형). OpenPath는 플레이어가 없으면
        // _filePath만 걸어 두고 OnVlcInitialized 말미의 PlayCurrent()가 잇는다(파일 열기 정상
        // 경로와 동일 훅) — 스왑체인 없이 Play를 직접 부르는 경로는 생기지 않는다.
        if (_filePath is null)
        {
            if (File.Exists(SampleTrackPath)) OpenPath(SampleTrackPath);
            else ShowMessage(@"Sample track not found (Assets\sample.mp3)");
            return;
        }

        if (_player is not { } p) return;

        // 끝까지 재생된(Ended) 상태에서는 Play()만으로 재시작이 안 되므로 미디어를 다시 건다.
        if (p.State == VLCState.Ended)
        {
            PlayCurrent();
            return;
        }

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
    }

    // A151: 전체화면 토글(ToggleFullScreen·⛶ 버튼·F11/Esc 액셀러레이터)은 전부 제거 —
    // 전체화면은 셸의 3단 모드 체계(MainWindow — Enter 순환·Alt+Enter·Esc·모드 버튼)가 담당한다.

    // ---------- 이퀄라이저 (A163) ----------

    /// <summary>
    /// EQ 플라이아웃을 Off + libvlc 내장 프리셋 목록으로 채운다(자막 플라이아웃과 같은
    /// 라디오 구성). 프리셋 목록은 libvlc 수명 동안 불변이라 플레이어 생성 후 1회면 된다.
    /// </summary>
    private void FillEqualizerFlyout()
    {
        EqFlyout.Items.Clear();
        AddEqualizerChoice("Off", string.Empty);
        foreach (var name in _eqPresetNames)
            AddEqualizerChoice(name, name);
        EqButton.IsEnabled = _eqPresetNames.Length > 0;
    }

    private void AddEqualizerChoice(string label, string presetName)
    {
        var item = new RadioMenuFlyoutItem
        {
            Text = label,
            GroupName = "equalizer",
            IsChecked = string.Equals(presetName, _eqPreset, StringComparison.Ordinal),
        };
        item.Click += (_, _) => SelectEqualizerPreset(presetName);
        EqFlyout.Items.Add(item);
    }

    /// <summary>프리셋 선택: 로컬 상태 갱신 + 즉시 저장(이어듣기 Report와 같은 즉시 기록) + 적용.</summary>
    private void SelectEqualizerPreset(string presetName)
    {
        _eqPreset = presetName;
        _settings.Set("audio.equalizer", presetName);
        _settings.Save();
        if (_player is { } p) ApplyEqualizer(p);
    }

    /// <summary>
    /// 현재 프리셋(_eqPreset)을 플레이어에 적용한다. 재생 중이든 아니든 즉시 적용되고
    /// 이후 미디어에도 유지된다(libvlc 문서). 이름이 목록에 없으면 Off로 폴백.
    /// </summary>
    private void ApplyEqualizer(MediaPlayer p)
    {
        try
        {
            var index = Array.IndexOf(_eqPresetNames, _eqPreset);
            if (_eqPreset.Length == 0 || index < 0)
            {
                p.UnsetEqualizer();
                return;
            }
            // 플레이어가 값을 복사하므로 핸들은 바로 해제해도 안전하다(libvlc 문서).
            using var eq = new Equalizer((uint)index);
            p.SetEqualizer(eq);
        }
        catch
        {
            // EQ 적용 실패가 재생을 막으면 안 된다 — 소리는 프리셋 없이 계속 나온다.
        }
    }

    // ---------- 오디오 장치 (A164) ----------

    /// <summary>
    /// 출력 장치 서브메뉴를 "System default" + libvlc 열거 목록으로 다시 채운다.
    /// 열거는 MediaPlayer 단위(libvlc 3) — 플레이어가 아직 없으면 System default 한 줄만 남는다.
    /// </summary>
    private void FillOutputDeviceMenu()
    {
        _outputMenu.Items.Clear();
        AddOutputChoice("System default", string.Empty);
        if (_player is not { } p) return;
        try
        {
            foreach (var device in p.AudioOutputDeviceEnum)
                AddOutputChoice(device.Description, device.DeviceIdentifier);
        }
        catch
        {
            // 열거 실패 — System default만 남는다(재생에는 영향 없음).
        }
    }

    private void AddOutputChoice(string label, string deviceId)
    {
        var item = new RadioMenuFlyoutItem
        {
            Text = label,
            GroupName = "audio-output",
            IsChecked = string.Equals(deviceId, _outputDeviceId, StringComparison.Ordinal),
        };
        item.Click += (_, _) => SelectOutputDevice(deviceId);
        _outputMenu.Items.Add(item);
    }

    /// <summary>출력 장치 선택: 로컬 상태 갱신 + 즉시 저장 + 재생 중이면 무재시작 이동.</summary>
    private void SelectOutputDevice(string deviceId)
    {
        _outputDeviceId = deviceId;
        _settings.Set("audio.outputDevice", deviceId);
        _settings.Save();
        if (_player is not { } p) return;
        try
        {
            // 재생 중 무재시작 이동: module=NULL 지정(기본 오버로드)은 살아 있는 aout을 즉시
            // 새 장치로 옮긴다(libvlc 3 권장 사용법 — 재시작 불요). aout이 아직 없으면(재생 전)
            // 무동작이지만 Playing 재적용이 잇는다. System default(빈 문자열) 복귀의 즉시 이동
            // 여부는 실기기 확인 포인트 — 무동작이어도 다음 재생부터는 지정을 생략해 기본 장치다.
            p.SetOutputDevice(deviceId);
        }
        catch
        {
            // 장치 유실 등 — 재생은 기존 장치로 계속된다.
        }
    }

    /// <summary>
    /// 입력(캡처) 장치 목록을 서브메뉴에 채운다 — 여기서의 "선택"은 Windows 기본 입력 장치
    /// 변경이다(A164, 부록 B 70 ⓒ 확정 — 출력과 대칭인 단일 선택 UI). 열거는 WinRT
    /// DeviceInformation(AudioCapture), 현재 기본 표시는 MediaDevice의 기본 캡처 ID.
    /// 비동기 결과가 열린 메뉴에 늦게 꽂혀도 무해하고, 다음 열기에서 항상 새로 채운다.
    /// </summary>
    private async void FillInputDeviceMenu()
    {
        try
        {
            _inputMenu.Items.Clear();

            var devices = await DeviceInformation.FindAllAsync(DeviceClass.AudioCapture);
            if (_tornDown) return;

            string? defaultId = null;
            try { defaultId = MediaDevice.GetDefaultAudioCaptureId(AudioDeviceRole.Default); }
            catch { /* 기본 장치 없음(장치 0개 등) — 체크 표시만 빠진다 */ }

            foreach (var device in devices)
            {
                var id = device.Id; // 클로저 캡처용 — WinRT 장치 인터페이스 ID
                var item = new RadioMenuFlyoutItem
                {
                    Text = device.Name,
                    GroupName = "audio-input",
                    IsChecked = string.Equals(id, defaultId, StringComparison.OrdinalIgnoreCase),
                };
                item.Click += (_, _) => SetDefaultInputDevice(id);
                _inputMenu.Items.Add(item);
            }
        }
        catch
        {
            // 열거 실패 — 빈 서브메뉴는 아래에서 비활성으로 접는다.
        }
        _inputMenu.IsEnabled = _inputMenu.Items.Count > 0;
    }

    /// <summary>
    /// 선택 장치를 Windows 기본 입력으로 지정한다(앱 밖 전역 변경 — 서브메뉴 툴팁에 병기).
    /// WinRT 장치 ID(SWD#MMDEVAPI 경로)에서 IPolicyConfig가 받는 MMDevice 엔드포인트 ID
    /// ("{0.0.1.00000000}.{guid}" 꼴)를 잘라 셸 훅에 넘긴다. 실패는 조용히 접고 플라이아웃
    /// 안내만 띄운다(부록 B 70 확정 폴백 — 훅·셸 계층은 예외를 새지 않는다).
    /// </summary>
    private void SetDefaultInputDevice(string deviceInterfaceId)
    {
        var endpointId = ExtractEndpointId(deviceInterfaceId);
        if (endpointId is null || !DefaultAudioInputHook.TrySetDefault(endpointId))
            ShowDeviceNotice("Could not change the default device");
    }

    /// <summary>
    /// WinRT 장치 인터페이스 ID에서 MMDevice 엔드포인트 ID를 추출한다.
    /// 캡처 엔드포인트는 "{0.0.1.00000000}.{guid}" 꼴이 ID 중간에 '#' 구분자로 끼어 있다.
    /// 형태가 예상과 다르면 null — 호출부가 안내 폴백으로 접는다.
    /// </summary>
    private static string? ExtractEndpointId(string deviceInterfaceId)
    {
        var start = deviceInterfaceId.IndexOf("{0.0.", StringComparison.Ordinal);
        if (start < 0) return null;
        var end = deviceInterfaceId.IndexOf('#', start);
        return end < 0 ? deviceInterfaceId[start..] : deviceInterfaceId[start..end];
    }

    /// <summary>장치 조작 실패 안내 — 장치 버튼 위에 작은 플라이아웃으로 띄운다(자동 닫힘).</summary>
    private void ShowDeviceNotice(string text)
    {
        var flyout = new Flyout
        {
            Content = new TextBlock { Text = text },
            Placement = FlyoutPlacementMode.Top,
        };
        flyout.ShowAt(DevicesButton);
    }

    // ---------- A11 루프 플라이아웃 (c9 — A163 EQ 칸과 같은 "버튼 + 라디오 플라이아웃" 규격) ----------

    /// <summary>
    /// 루프 플라이아웃 구성(1회 — 항목이 정적이라 EQ·장치처럼 다시 채울 일이 없다).
    /// 줄 1 = "Loop list" 토글(ToggleMenuFlyoutItem), 구분선 아래 = "Repeat this file" 라디오
    /// 4택(끔/1×/3×/무한 — 현재 파일 루프와 그 횟수는 한 축으로 고른다. 끔을 골라도 저장된
    /// 횟수는 남겨 다음 켬 때 되살아난다). 문구·구성은 영상과 동일하고 설정 키만 audio.* 다.
    /// </summary>
    private void BuildLoopFlyout()
    {
        LoopFlyout.Items.Clear();

        var listToggle = new ToggleMenuFlyoutItem { Text = "Loop list", IsChecked = _loopList };
        listToggle.Click += (_, _) =>
        {
            // ToggleMenuFlyoutItem은 Click 시점에 IsChecked가 이미 뒤집혀 있다(A160 선례와 같은 성질).
            _loopList = listToggle.IsChecked;
            _settings.Set(LoopListKey, _loopList);
            _settings.Save(); // 즉시 저장 — EQ 선례(전역 1벌)
            UpdateLoopButton();
        };
        LoopFlyout.Items.Add(listToggle);
        LoopFlyout.Items.Add(new MenuFlyoutSeparator());

        AddLoopCurrentChoice("Repeat this file: Off", repeat: false, limit: 0);
        AddLoopCurrentChoice("Repeat this file: 1×", repeat: true, limit: 1);
        AddLoopCurrentChoice("Repeat this file: 3×", repeat: true, limit: 3);
        AddLoopCurrentChoice("Repeat this file: Infinite", repeat: true, limit: 0);
    }

    /// <summary>"Repeat this file" 라디오 1개 추가 — EQ의 AddEqualizerChoice 관용구.</summary>
    private void AddLoopCurrentChoice(string label, bool repeat, int limit)
    {
        var item = new RadioMenuFlyoutItem
        {
            Text = label,
            GroupName = "loop-current",
            IsChecked = repeat == _loopCurrent && (!repeat || limit == _loopCountLimit),
        };
        item.Click += (_, _) =>
        {
            _loopCurrent = repeat;
            _settings.Set(LoopCurrentKey, _loopCurrent);
            if (repeat)
            {
                _loopCountLimit = limit;
                _settings.Set(LoopCountKey, limit == 0 ? "infinite" : limit.ToString());
            }
            _settings.Save();
            UpdateLoopButton();
        };
        LoopFlyout.Items.Add(item);
    }

    /// <summary>
    /// 루프 버튼 본체를 상태형으로 갱신 — 영상 UpdateLoopButton과 같은 규칙(아이콘 + 툴팁을
    /// 상태에서 만든다). 글리프 E8EE(RepeatAll)/E8ED(RepeatOne)는 영상 c9와 같은 값이다.
    /// 현재 파일 루프가 켜져 있으면 RepeatOne(우선순위 그대로 — 목록 루프를 가린다), 아니면
    /// RepeatAll이고 끔 상태는 툴팁이 알린다. 툴팁 표기는 A34 규칙대로 키 상수에서 조립한다.
    /// </summary>
    private void UpdateLoopButton()
    {
        (string glyph, string state) = _loopCurrent
            ? ("\uE8ED", _loopCountLimit switch
            {
                1 => "Repeat this file: 1×",
                3 => "Repeat this file: 3×",
                _ => "Repeat this file: Infinite",
            })
            : _loopList
                ? ("\uE8EE", "Loop list")
                : ("\uE8EE", "Loop: off");
        LoopButton.Content = new FontIcon { Glyph = glyph, FontSize = 18 };
        ToolTipService.SetToolTip(LoopButton, HotkeySupport.Tip(state, LoopKey));
    }

    // ---------- 입력 핸들러 ----------

    private void OnPlayClicked(object sender, RoutedEventArgs e) => TogglePlayPause();

    private void OnMuteClicked(object sender, RoutedEventArgs e) => ToggleMute();

    /// <summary>표면 클릭 = 재생/일시정지 (플레이어 관례).</summary>
    private void OnSurfaceTapped(object sender, TappedRoutedEventArgs e)
    {
        e.Handled = true;
        TogglePlayPause();
    }

    /// <summary>표면 위 휠 = 볼륨 조절.</summary>
    private void OnSurfaceWheel(object sender, PointerRoutedEventArgs e)
    {
        var delta = e.GetCurrentPoint(this).Properties.MouseWheelDelta;
        if (delta == 0) return;
        e.Handled = true;
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
    /// 가드 형태는 영상 모듈 Space(OnTogglePlayInvoked)와 같은 공용 통과 판정 한 벌이다
    /// (원형이던 영상 Enter 액셀러레이터는 A151에서 제거됐다).
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

    /// <summary>
    /// A11 루프 키(설계 §4.1) — 플라이아웃 열기. 툴팁 표기(UpdateLoopButton)와 액셀러레이터가
    /// 이 한 값을 함께 쓴다. 오디오 기사용 문자 키 M·S와 충돌 없고, 영상 모듈의 L과 같은 뜻이다
    /// (같은 동작에 같은 키 규칙 — 두 뷰가 동시에 살아 있지 않으므로 스코프 충돌도 없다).
    /// </summary>
    private const VirtualKey LoopKey = VirtualKey.L;

    /// <summary>
    /// A34: 하단 바 버튼에 단독 문자 키를 걸고 툴팁 "(키)" 표기까지 같은 호출에서 만든다.
    /// 텍스트 입력·탐색기 파일 리스트 포커스에서는 HotkeySupport가 키를 통과시킨다(A32/A84 규칙).
    /// M(음소거)은 v0.75.0부터 있던 키를 XAML 액셀러레이터에서 여기로 옮긴 것 — 의미는 그대로다.
    /// 키 배정은 같은 뜻의 동작에 같은 키를 쓰는 규칙에 따라 영상 모듈과 일치시켰다(M·S·A11의 L).
    /// </summary>
    private void SetupHotkeys()
    {
        HotkeySupport.Bind(this, MuteButton, VirtualKey.M, "Mute", ToggleMute);
        HotkeySupport.Bind(this, SpeedBox, VirtualKey.S,
            "Playback speed", () => SpeedBox.IsDropDownOpen = true);
        // A11: L = 루프 플라이아웃 열기(영상 c9와 같은 배선). 툴팁이 상태형이라 Bind가 아닌
        // Register — 표기는 UpdateLoopButton()이 같은 키 상수(LoopKey)로 조립한다.
        HotkeySupport.Register(this, LoopButton, LoopKey, () => LoopFlyout.ShowAt(LoopButton));
        UpdateLoopButton(); // A11: 루프 아이콘·툴팁 초기값
    }
}
