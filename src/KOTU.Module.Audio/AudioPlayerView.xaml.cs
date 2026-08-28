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

    // ---------- A11(v0.212.0) → A255(v0.255.0) 재생 목록 루프 상태 (설계 docs/A11-playlist-design.md §3) ----------
    // 영상에 먼저 구현한 구조의 동형 이식이다 — 키 접두사만 audio.* 로 다르고
    // 의미·기본값·우선순위는 한 글자도 다르지 않다(설계 §7 배치 ③).
    // A255(2026-08-27 사용자 확정): 구 2축(Loop list 체크 × Repeat this file 라디오 — 우선순위
    // 결합)을 단일 모드(상호 배타)로 개편. 본체 클릭·L 키 = 루프 없음 → 목록 루프 → 한 파일
    // 루프 순환(진입 시 횟수는 기본 ∞), 두 모드 모두 반복 횟수(1×/3×/∞)를 우클릭 플라이아웃에서
    // 가진다. 기본값 = 루프 없음(구 "목록 루프 기본 켬" 폐기 — 일반 플레이어 관례).
    // 저장 = 신 키 loopMode(off/list/file) + 모드별 횟수(loopCount = 한 파일 · loopListCount =
    // 목록). 구 키 loopList·loopCurrent는 소비처만 제거하고 값은 무해 잔존(A174 선례) —
    // 신 키가 없을 때만 생성자에서 1회 해석해 이행한다(그곳의 매핑 주석 참조).
    // 저장은 전역 1벌·즉시 Set+Save(EQ 선례), 창 간 실시간 전파 없음 — 상태는 로컬 소유(_muted 규칙).

    private const string LoopModeKey = "audio.loopMode";           // "off"·"list"·"file" — A255 신설
    private const string LoopCountKey = "audio.loopCount";         // 한 파일 루프 횟수 "1"·"3"·"infinite" — 구 Repeat 횟수 키를 의미 그대로 재사용
    private const string LoopListCountKey = "audio.loopListCount"; // 목록 루프 횟수 — A255 신설(같은 문자열 enum)
    private const string LegacyLoopListKey = "audio.loopList";     // 구 키 — 이행 해석 전용(쓰기 없음)
    private const string LegacyLoopCurrentKey = "audio.loopCurrent"; // 구 키 — 이행 해석 전용(쓰기 없음)

    /// <summary>A255 단일 루프 모드(상호 배타) — 버튼 순환 순서 그대로 Off → List → File.</summary>
    private enum LoopMode { Off, List, File }

    private FolderPlaylist? _playlist; // 같은 폴더 스냅샷 목록 — EnsurePlaylist가 워커에서 만든다
    private LoopMode _loopMode;
    private int _fileLoopLimit; // 0 = 무한, 1·3 = "그만큼 한 번 더"(리핏 허용 횟수 — 구 Repeat 의미 그대로)
    private int _listLoopLimit; // 0 = 무한, 1·3 = 목록 끝→처음 되감기 허용 횟수(같은 어휘 — "1×" = 한 번 더 = 목록 총 2회)
    private int _loopPlays;     // 현재 파일에서 소진한 리핏 횟수 — PlayCurrent가 리셋, AdvanceAfterEnd 전이 1만 증가
    private int _listLoops;     // 소진한 목록 되감기 횟수 — EOF 자동 진행만 소모하고, 수동 개입(파일 열기·모드 변경)이 리셋

    // A255: 횟수 플라이아웃은 코드로 만들어 ContextFlyout으로 건다(이미지 표면 메뉴 관례 —
    // 본체 클릭이 Button.Flyout이 아닌 모드 순환이 됐기 때문). Placement Top = 구 XAML 값 유지.
    private readonly MenuFlyout _loopFlyout = new() { Placement = FlyoutPlacementMode.Top };

    /// <summary>"1"·"3"은 그 횟수, 그 외(기본 "infinite"·구버전 잔값 포함)는 전부 무한(0)으로 읽는다.</summary>
    private static int ParseLoopCount(string value) => value switch
    {
        "1" => 1,
        "3" => 3,
        _ => 0,
    };

    /// <summary>횟수 저장값 — ParseLoopCount의 역방향(0 = 무한).</summary>
    private static string CountKeyValue(int limit) => limit == 0 ? "infinite" : limit.ToString();

    /// <summary>횟수 표기(UI 문자열) — 플라이아웃 라벨·툴팁이 같은 값에서 나온다.</summary>
    private static string CountLabel(int limit) => limit switch
    {
        1 => "1×",
        3 => "3×",
        _ => "Infinite",
    };

    /// <summary>모드 저장값 — 생성자 로드 switch의 역방향.</summary>
    private static string ModeKeyValue(LoopMode mode) => mode switch
    {
        LoopMode.List => "list",
        LoopMode.File => "file",
        _ => "off",
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

        // A255: 루프 설정 읽기(생성자 1회 — _muted 규칙)와 플라이아웃 배선은 SetupHotkeys보다
        // 먼저다 — UpdateLoopButton(툴팁 초기값)이 상태를 읽는다(영상과 같은 순서).
        // 신 키(loopMode)가 없으면 구 2키를 여기서 1회 해석해 이행한다(별도 마이그레이션 없음):
        //   구 loopCurrent 켬(loopList 무관) → 한 파일 루프(구 전이표 ①>③ 우선순위 승계 · 횟수는
        //                                     loopCount 키 재사용으로 자동 유지)
        //   구 loopList 켬 단독             → 목록 루프(횟수 키 부재 = ∞)
        //   둘 다 꺼짐 또는 미저장           → 루프 없음(신 기본값 — 구 기본값 "켬"은 폐기라
        //                                     구 키를 일부러 false 기본으로 읽는다)
        _loopMode = _settings.Get(LoopModeKey, string.Empty) switch
        {
            "list" => LoopMode.List,
            "file" => LoopMode.File,
            "off" => LoopMode.Off,
            _ => _settings.Get(LegacyLoopCurrentKey, false) ? LoopMode.File
                : _settings.Get(LegacyLoopListKey, false) ? LoopMode.List
                : LoopMode.Off,
        };
        _fileLoopLimit = ParseLoopCount(_settings.Get(LoopCountKey, "infinite"));
        _listLoopLimit = ParseLoopCount(_settings.Get(LoopListCountKey, "infinite"));

        // A255: 횟수 플라이아웃은 우클릭(ContextFlyout — 이미지 표면 메뉴 관례)으로 연다. 본체
        // 클릭은 모드 순환이 가져갔다. 체크 상태가 버튼 순환으로도 바뀌므로 항목은 열 때마다
        // 새로 채운다(아래 장치 플라이아웃의 Opening 재구성 관례).
        LoopButton.ContextFlyout = _loopFlyout;
        _loopFlyout.Opening += (_, _) => BuildLoopFlyout();

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

    /// <summary>
    /// 현재 _filePath를 처음부터(또는 이어듣기 지점부터) 재생한다. 플레이어 준비 후에만 호출.
    /// autoAdvance = EOF 자동 진행(AdvanceAfterEnd 전이 2·3)에서 온 호출 — A255 목록 순환
    /// 카운터(_listLoops)를 잇는다. 그 외(셸 열기·드롭·▶ 재시작·샘플 곡)는 전부 수동 개입 =
    /// 카운터 재출발(확정: 자동 진행만 순환 예산을 소모한다).
    /// </summary>
    private void PlayCurrent(bool autoAdvance = false)
    {
        if (_player is not { } p || _libVlc is not { } lib || _filePath is null) return;

        _durationMs = 0;
        _lastReportedMs = 0;
        _loopPlays = 0; // A11: 재생 단위가 새로 시작되면 리핏 카운터 리셋 — AdvanceAfterEnd 전이 1만 증가시킨다
        if (!autoAdvance) _listLoops = 0; // A255: 수동 개입 = 목록 순환 카운터 리셋(위 요약 주석)
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

    /// <summary>autoAdvance는 PlayCurrent로 중계만 한다(A255 — EOF 자동 진행 표시).</summary>
    private async void OpenPath(string path, bool autoAdvance = false)
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

        if (_player is not null) PlayCurrent(autoAdvance);
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
    /// A255(v0.255.0): EOF 전이 재작성 — A11 전이표(설계 §3.3)·부록 B 76 "우선순위 결합"의
    /// 공식 개정. 구 2축 결합 대신 단일 모드(상호 배타)로 판정한다:
    ///   루프 없음    = 다음 파일 → 목록 끝이면 정지.
    ///   목록 루프    = 다음 파일 → 목록 끝이면 처음으로(되감기 1회 소모 — 예산 소진 시 정지).
    ///   한 파일 루프 = 같은 파일 재시작(횟수 소진 시 다음 파일로 진행 — 구 Repeat 소진 후
    ///                  "목록 진행으로 낙하" 규칙 승계. 단 상호 배타라 목록 끝 되감기는 없다).
    /// 횟수 어휘는 구 Repeat와 동일("1×" = 한 번 더): 한 파일 1× = 총 2회 재생, 목록 1× =
    /// 끝→처음 되감기 1회 = 목록 총 2회 재생. 매 회 0:00 시작(이어듣기 무시 — ReplayCurrent와
    /// EndReached의 기록 삭제 규칙 그대로 유지).
    /// 전이 1~4는 Ended에 머물지 않으므로 종전 EndReached UI 갱신
    /// (▶ 표기·시크바 끝·트레이 타이머 정지)을 생략한다 — 곧 Playing이 덮어써 깜빡임만 만든다.
    /// 정지(전이 5)만 종전 갱신 그대로다. EncounteredError는 전이 트리거가 아니다(실패 파일
    /// 자동 스킵은 무한 실패 루프 위험 — 별도 설계 대상, §3.3). UI 스레드 전용.
    /// A258(v0.258.0): 위 "루프 없음 = 다음 파일"에 설정 게이트가 하나 붙었다 — 설정의
    /// "Auto-play next file"을 끄면 <b>루프 없음일 때만</b> 목록 진행 대신 정지(전이 5)한다.
    /// 루프 모드가 켜져 있으면 옵션과 무관하게 종전 전이 그대로다(아래 게이트 주석 참고).
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

        // 전이 1: 한 파일 루프 — 횟수 내면 같은 파일 재시작(0 = 무한. "1" = 한 번 더 = 총 2회).
        // 소진하면 아래 목록 진행으로 낙하한다(구 규칙 승계) — 다음 파일에서는 PlayCurrent가
        // _loopPlays를 리셋하므로 새 파일도 같은 횟수만큼 돈다.
        if (_loopMode == LoopMode.File && (_fileLoopLimit == 0 || _loopPlays < _fileLoopLimit))
        {
            _loopPlays++;
            ReplayCurrent();
            return;
        }

        // A258(v0.258.0): 오토 넥스트 게이트 — 설정의 "Auto-play next file"(player.autoNext,
        // 영상·오디오 공용 한 벌)을 끄면 목록 진행을 막고 정지(전이 5)로 보낸다. 유효 조건은
        // **루프 모드가 '없음'일 때뿐**이다(확정): 목록 루프·한 파일 루프가 선택돼 있으면 그
        // 모드가 이긴다. 특히 한 파일 루프의 "횟수 소진 후 목록 진행 낙하"는 이 지점에서도
        // _loopMode가 여전히 File이라 게이트를 그냥 통과한다 — 소진 낙하는 종전대로 다음 파일로
        // 간다(A255 규칙 불변). 값은 캐시하지 않고 EOF마다 라이브로 읽는다(설정 변경 즉시 반영·
        // 이벤트 배선 0). 전이 1·4(같은 파일 재시작)는 이 게이트 앞뒤라 영향 없다.
        var autoNext = _loopMode != LoopMode.Off
            || _settings.Get(PlaybackSettings.AutoNextKey, PlaybackSettings.AutoNextDefault);

        // 전이 2: 다음 파일로(모든 모드 공통 — 루프 없음·한 파일 횟수 소진 포함).
        // 전이 3: 목록 루프 + 목록 끝 = 처음으로. 이 되감기가 "목록 1회 순환" 소모 시점이다
        // (A255 확정: 마지막 파일 EOF에서 처음으로 갈 때 1회. 수동 개입은 PlayCurrent가
        // _listLoops를 리셋하므로 자동 진행만 예산을 소모한다 — 카운트 기준점).
        // 그새 소실된 파일은 Remove로 목록에서 빼고 그다음 후보로 재시도한다(구현 시 결정).
        // OpenPath = 기존 완결 경로 재사용(설계 §2.2 경로 B) — 이어듣기 저장·PlayCurrent·
        // ContentOpened 셸 동기화(트레이·A174)까지 전부 따라온다. 신규 셸 배선 0(설계 §5).
        if (autoNext && _playlist is { } list)
        {
            while (true)
            {
                var wrapping = !list.HasNext; // Remove가 목록을 줄일 수 있어 매 회 재판정
                var next = !wrapping ? list.PeekNext
                    : _loopMode == LoopMode.List && list.Count > 1 &&
                      (_listLoopLimit == 0 || _listLoops < _listLoopLimit) ? list.PeekFirst
                    : null;
                if (next is null) break;

                if (!File.Exists(next))
                {
                    list.Remove(next);
                    continue;
                }

                if (wrapping)
                {
                    _listLoops++; // 되감기 확정 시점에만 소모(소실 파일 재시도는 소모 없음)
                    list.MoveFirst();
                }
                else
                {
                    list.MoveNext();
                }
                OpenPath(next, autoAdvance: true);
                return;
            }

            // 전이 4: 목록 루프 + 단일 파일 목록 = 같은 파일 재시작. 파일 1개가 곧 목록 전체라
            // 이 재시작도 "되감기 1회"로 세어 같은 예산을 소모한다(A255 — 구판의 무한 고정 폐기).
            if (_loopMode == LoopMode.List && list.Count == 1 &&
                (_listLoopLimit == 0 || _listLoops < _listLoopLimit))
            {
                _listLoops++;
                ReplayCurrent();
                return;
            }
        }

        // 전이 5: 정지 — 종전 EndReached UI 갱신 그대로(유일하게 Ended에 머무는 경로).
        // A255: 루프 없음·목록 끝 외에 "횟수 소진"(목록 루프 되감기 예산 소진, 한 파일 소진 후
        // 다음 파일 없음)도 이 경로로 온다.
        // A258: "루프 없음 + Auto-play next file 끔"도 이 경로다 — 목록 중간 파일이어도 여기서
        // 멈추므로 아래 네 줄(▶ 표기·시크바 끝) + 오디오 전용 트레이 두 줄을 반드시 거쳐야 한다.
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

    // ---------- A255 루프 모드·횟수 (본체 클릭 = 모드 순환 · 우클릭 플라이아웃 = 횟수) ----------

    /// <summary>
    /// 루프 플라이아웃 구성 — A255: 본체 클릭이 모드 순환으로 바뀌어 플라이아웃은 우클릭
    /// (ContextFlyout)으로 연다. 구 "Loop list 토글 + Repeat 라디오 4택"을 라디오 7택 한
    /// 그룹(끔 1 + 모드 2 × 횟수 3)으로 바꿔 체크 하나가 곧 현재 상태다(상호 배타 그대로).
    /// 버튼 순환·구 키 이행으로도 상태가 바뀌므로 매 열림(Opening)마다 다시 채운다 —
    /// 장치 플라이아웃 관례(1회 구성이던 구판과 달라진 점). 문구·구성은 영상과 동일하고
    /// 설정 키만 audio.* 다.
    /// </summary>
    private void BuildLoopFlyout()
    {
        _loopFlyout.Items.Clear();

        AddLoopChoice("Loop: off", LoopMode.Off, 0);
        _loopFlyout.Items.Add(new MenuFlyoutSeparator());
        AddLoopChoice("Loop list: 1×", LoopMode.List, 1);
        AddLoopChoice("Loop list: 3×", LoopMode.List, 3);
        AddLoopChoice("Loop list: Infinite", LoopMode.List, 0);
        _loopFlyout.Items.Add(new MenuFlyoutSeparator());
        AddLoopChoice("Repeat this file: 1×", LoopMode.File, 1);
        AddLoopChoice("Repeat this file: 3×", LoopMode.File, 3);
        AddLoopChoice("Repeat this file: Infinite", LoopMode.File, 0);
    }

    /// <summary>
    /// 라디오 1개 추가 — EQ의 AddEqualizerChoice 관용구. 횟수를 고르면 그 모드로의 전환을
    /// 겸한다(구현 시 결정 — 플라이아웃에서 "Loop list: 3×"를 고른 의도는 그 모드의 사용이다).
    /// </summary>
    private void AddLoopChoice(string label, LoopMode mode, int limit)
    {
        var item = new RadioMenuFlyoutItem
        {
            Text = label,
            GroupName = "loop",
            IsChecked = mode == _loopMode &&
                (mode == LoopMode.Off ||
                 limit == (mode == LoopMode.List ? _listLoopLimit : _fileLoopLimit)),
        };
        item.Click += (_, _) => SetLoopState(mode, limit);
        _loopFlyout.Items.Add(item);
    }

    /// <summary>
    /// A255: 모드(+그 모드의 횟수)를 확정하고 저장·아이콘 갱신까지 한곳에서 한다.
    /// 모드·횟수 변경은 전부 사용자 개입이므로 두 카운터를 재출발시킨다(PlayCurrent의
    /// 수동 리셋과 같은 취지). 끔은 횟수 축이 없어 저장된 두 횟수를 건드리지 않는다.
    /// </summary>
    private void SetLoopState(LoopMode mode, int limit)
    {
        _loopMode = mode;
        if (mode == LoopMode.List)
        {
            _listLoopLimit = limit;
            _settings.Set(LoopListCountKey, CountKeyValue(limit));
        }
        else if (mode == LoopMode.File)
        {
            _fileLoopLimit = limit;
            _settings.Set(LoopCountKey, CountKeyValue(limit));
        }
        _loopPlays = 0;
        _listLoops = 0;
        _settings.Set(LoopModeKey, ModeKeyValue(mode));
        _settings.Save(); // 즉시 저장 — EQ 선례(전역 1벌)
        UpdateLoopButton();
    }

    /// <summary>
    /// A255: 본체 클릭·L 키 = 모드 순환(루프 없음 → 목록 루프 → 한 파일 루프 → 처음으로).
    /// 순환으로 진입한 모드의 횟수는 기본값 ∞로 되돌린다(사용자 확정 — 세밀한 횟수는
    /// 우클릭 플라이아웃의 몫이라 버튼만 쓰는 손에는 항상 "무한 루프"가 잡힌다).
    /// </summary>
    private void CycleLoopMode() => SetLoopState(
        _loopMode switch
        {
            LoopMode.Off => LoopMode.List,
            LoopMode.List => LoopMode.File,
            _ => LoopMode.Off,
        },
        limit: 0);

    /// <summary>XAML Click 배선(OnPlayClicked 관용구) — 본체 클릭 = 모드 순환.</summary>
    private void OnLoopClicked(object sender, RoutedEventArgs e) => CycleLoopMode();

    /// <summary>
    /// 루프 버튼 본체를 상태형으로 갱신 — 영상 UpdateLoopButton과 같은 규칙(아이콘 + 툴팁을
    /// 상태에서 만든다). A255 3상태 표지: 루프 없음 = E8EE 흐림(Opacity 0.4 — 끔 표지 확정값.
    /// 빗금 도형 안은 v0.174.1 Geometry 공유 크래시 함정이라 기각) / 목록 루프 = E8EE(RepeatAll)
    /// 불투명 / 한 파일 루프 = E8ED(RepeatOne). 툴팁도 3상태 + 횟수 병기에 우클릭 안내를
    /// 덧붙인다(횟수 플라이아웃 진입이 우클릭뿐이라 이 표기가 유일한 발견 경로다).
    /// 표기는 A34 규칙대로 키 상수에서 조립한다.
    /// </summary>
    private void UpdateLoopButton()
    {
        (string glyph, double opacity, string state) = _loopMode switch
        {
            LoopMode.List => ("\uE8EE", 1.0, $"Loop list: {CountLabel(_listLoopLimit)}"),
            LoopMode.File => ("\uE8ED", 1.0, $"Repeat this file: {CountLabel(_fileLoopLimit)}"),
            _ => ("\uE8EE", 0.4, "Loop: off"),
        };
        LoopButton.Content = new FontIcon { Glyph = glyph, FontSize = 18, Opacity = opacity };
        ToolTipService.SetToolTip(LoopButton,
            HotkeySupport.Tip($"{state} · right-click for count", LoopKey));
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
    /// A11 루프 키(설계 §4.1) → A255: 플라이아웃 열기에서 모드 순환(본체 클릭과 동일)으로 개정.
    /// 툴팁 표기(UpdateLoopButton)와 액셀러레이터가 이 한 값을 함께 쓴다. 오디오 기사용 문자 키
    /// M·S와 충돌 없고, 영상 모듈의 L과 같은 뜻이다
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
        // A255: L = 루프 모드 순환(본체 클릭과 동일 — 구 "플라이아웃 열기"에서 개정. 횟수
        // 플라이아웃은 우클릭 전용이 됐다). 툴팁이 상태형이라 Bind가 아닌 Register —
        // 표기는 UpdateLoopButton()이 같은 키 상수(LoopKey)로 조립한다(영상과 같은 배선).
        HotkeySupport.Register(this, LoopButton, LoopKey, CycleLoopMode);
        UpdateLoopButton(); // A11: 루프 아이콘·툴팁 초기값
    }
}
