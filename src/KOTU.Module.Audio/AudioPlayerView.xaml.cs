using LibVLCSharp.Platforms.Windows;
using LibVLCSharp.Shared;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.Devices.Enumeration;
using Windows.Media.Devices;
using Windows.System;
using KOTU.Core.Contracts;
using KOTU.Core.Integration;
using KOTU.Core.Navigation;
using KOTU.Core.Settings;
using KOTU.Core.Threading;
using KOTU.Input;
using KOTU.Ui;

namespace KOTU.Module.Audio;

/// <summary>
/// 음악 플레이어 화면 (A10 — 비디오 모듈에서 분리). 재생/일시정지, 시킹(슬라이더·←/→ 5초),
/// 볼륨(↑/↓)·음소거(M), 배속, 이어듣기, 이퀄라이저 프리셋(A163)·오디오 장치 선택(A164)을 제공한다. 전체화면은 A151부터 셸의 3단 모드 체계
/// (Enter 순환·Alt+Enter) 몫이다 — 이 뷰에는 진입 코드가 없다.
/// 표면은 libvlc 시각화(A268 스타일 선택 — 기본 scope 파형)가 채우고 상단에 ♪ + 파일명 오버레이를 띄운다.
/// 예외 하나(A304): VU meter 스타일은 libvlc effect가 아니라 자체 렌더다 — WASAPI 루프백
/// 레벨(VuMeterEngine)을 검은 표면 위 VuOverlay에 그린다(수명 판정은 UpdateVuMeter 한곳).
/// 시각화는 인스턴스 옵션으로만 동작하므로(v0.12.0 실기기 확인) 인스턴스는 1회 생성해
/// 재사용하되, 스타일이 바뀌는 순간만 예외로 폐기·재생성한다(RecreatePlayer — A268).
/// 스레드 모델(A42): libvlc 생성·해제는 뷰 전용 워커에서 직렬로. libvlc 이벤트는
/// libvlc 자체 스레드에서 오므로 UI 갱신은 DispatcherQueue로 넘긴다(Dispatch).
/// </summary>
public sealed partial class AudioPlayerView : UserControl, IBottomBarProvider,
    IContentStateSource, IContentInfoProvider, ITrayStatusProvider, IContentInfoChangedSource,
    IBrowseOrderConsumer, ICurrentPathSource
{
    /// <summary>파일 재생을 시작하면 셸에 알린다(빈 상태 탐색기 내림·오버레이 기준 갱신).</summary>
    public event Action<string>? ContentOpened;

    /// <summary>
    /// A349(A348 이식 · 영상과 동형): 뷰 내부 항해(⏮/⏭·키)가 보여 줄 파일을 옮긴 즉시 셸에 알린다 —
    /// 로드 완료를 기다리는 <see cref="ContentOpened"/>보다 앞선 통지라 좌 리스트 하이라이트가
    /// 오토리피트를 1:1로 따라간다. 셸이 연 파일(OpenPath 진입)에서는 쏘지 않는다(셸이 이미 안다).
    /// </summary>
    public event Action<string>? CurrentPathChanged;

    // ---------- 탐색 순서 주입 (A349 — A346 IBrowseOrderConsumer 이식) ----------

    /// <summary>셸이 마지막으로 준 좌 리스트의 폴더. 없으면 주입 목록을 쓰지 않는다.</summary>
    private string? _browseFolder;

    /// <summary>그 폴더의 표시 순서 그대로의 파일 경로 목록(확장자 필터·숨김 표시 반영).</summary>
    private IReadOnlyList<string> _browseFiles = [];

    /// <summary>
    /// A332: libvlc가 파일을 파싱해 길이·트랙 정보를 알게 됐다 — 셸이 정보 패널을 다시 묻는다.
    /// 파일당 1회만 쏜다(_infoNotified) — 계약의 "값이 실제로 갈렸을 때 1회" 규칙.
    /// </summary>
    public event Action? ContentInfoChanged;

    /// <summary>A332: 지금 파일에서 정보 갱신을 이미 알렸는가 — PlayCurrent가 파일마다 리셋한다.</summary>
    private bool _infoNotified;

    // ---------- 트레이 아이콘 내용 (A54, v0.118.0) ----------

    /// <summary>트레이 표시 값이 바뀌었다 — 재생 중에는 1초 타이머가, 그 밖에는 상태 전이가 쏜다.</summary>
    public event Action? TrayStatusChanged;

    /// <summary>
    /// 지금 재생 중인가 — 영상 IsPlaying(A186·A306)과 같은 정의(libvlc IsPlaying 위임).
    /// _player가 없거나(생성 전) 일시정지·정지·미디어 없음은 전부 "재생 중 아님"이다.
    /// A302: 세러모니 게이트·트레이 이퀄라이저 판정이 이 한 곳을 쓴다(판정 일원화).
    /// </summary>
    private bool IsPlaying => _player is { IsPlaying: true };

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

        var playing = IsPlaying; // A302: 판정 일원화(공용 IsPlaying — 종전과 같은 식)
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
    /// 정보 오버레이용 미디어 정보 (A327) — 단일 빌더(AudioQuickInfo)로 옮겼다: 파일 기본 3행 +
    /// 포맷상 정의된 태그·스트림 키 전부(값 없으면 빈칸 행). 셸의 선택 조회(SelectionQuickInfo)와
    /// **같은 빌더**를 써야 열림 축·선택 축 표시가 어긋나지 않는다(A200 원칙 — 이미지 축 선례).
    /// 종전의 libvlc 트랙 한 행("2 ch · 44,100 Hz · mpga")은 폐지 — 같은 정보가 스트림 절의
    /// Channels·Sample rate 행으로 들어갔고, 재생 중에만 나와 두 축 불일치를 만들던 축이다.
    /// 조회는 파일 I/O라 뷰 워커에서 돌린다(A42·규칙 1.8 — UI 스레드 I/O 금지).
    /// </summary>
    public async Task<IReadOnlyList<ContentInfoItem>?> GetContentInfoAsync()
    {
        if (_filePath is not { } path) return null;

        IReadOnlyList<ContentInfoItem> rows;
        try
        {
            rows = await Worker.Run(_ => AudioQuickInfo.BuildRows(path));
        }
        catch
        {
            return null; // 오버레이 정보는 부가 기능(ImageViewerView와 같은 폴백)
        }
        // 항해가 빨라 그새 다른 곡으로 넘어갔으면 버린다 — 오버레이도 seq로 거르지만(A200)
        // 여기서 한 번 더 끊어 낡은 결과가 새 파일 화면에 닿을 길을 남기지 않는다.
        if (_filePath != path) return null;
        return FillFromPlayer(rows);
    }

    /// <summary>
    /// A327 → A332 확장(영상 축 VideoPlayerView.FillFromPlayer와 같은 규격): 셸 속성 핸들러가
    /// 값을 못 주는 컨테이너(설치 코덱에 따라 opus·flac 등, 그리고 파일을 막 연 직후)라도 재생
    /// 중이면 libvlc가 이미 아는 값으로 <b>빈칸인 행만</b> 채운다 — 길이·비트레이트·샘플레이트·
    /// 채널 4종. 종전(A327)은 Duration 한 행뿐이라 영상과 비대칭이었다.
    /// <b>행 집합·순서·라벨은 A327 그대로</b>라 선택 축과 어긋나지 않는다(값만 열림 축에서 더
    /// 채워진다 — 부록 B 98: 행을 늘리거나 라벨을 발명하지 않는다).
    /// Sample size(비트 심도)는 libvlc 오디오 트랙에 없는 값이라 빈칸을 유지한다.
    /// UI 스레드 전용(_durationMs·_player는 UI 상태) — await 복귀 뒤에만 부른다.
    /// </summary>
    private IReadOnlyList<ContentInfoItem> FillFromPlayer(IReadOnlyList<ContentInfoItem> rows)
    {
        string? bitRate = null, sampleRate = null, channels = null;
        try
        {
            // 재생 중이면 libvlc가 파싱한 트랙 정보를 그대로 읽는다 (별도 Parse 불필요).
            // Media 게터는 새 래퍼를 만들어 참조를 늘리므로 쓰고 바로 해제한다(영상 축과 동일).
            using var media = _player?.Media;
            foreach (var track in media?.Tracks ?? [])
            {
                if (track.TrackType != TrackType.Audio) continue;
                var a = track.Data.Audio;
                // 하한(1kbps·1kHz)은 AudioQuickInfo의 속성 축과 같은 값이어야 표기가 일관된다.
                if (track.Bitrate >= 1000) bitRate = $"{track.Bitrate / 1000} kbps";
                if (a.Rate >= 1000) sampleRate = $"{a.Rate / 1000.0:0.#} kHz";
                if (a.Channels > 0) channels = $"{a.Channels}";
                break; // 첫 오디오 트랙이 표시 대상(다중 트랙은 표기 축 밖 — 영상과 같은 규칙)
            }
        }
        catch
        {
            // 트랙 정보 실패는 채우지 않는다 — 속성 조회 결과 그대로 나간다.
        }

        var duration = _durationMs > 0 ? TimeText.Format(_durationMs) : null;
        var filled = new List<ContentInfoItem>(rows.Count);
        foreach (var row in rows)
        {
            string? value = row.Value.Length > 0 ? null : row.Label switch
            {
                AudioQuickInfo.DurationLabel => duration,
                AudioQuickInfo.BitRateLabel => bitRate,
                AudioQuickInfo.SampleRateLabel => sampleRate,
                AudioQuickInfo.ChannelsLabel => channels,
                _ => null,
            };
            filled.Add(value is null ? row : new ContentInfoItem(row.Label, value));
        }
        return filled;
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
    /// 볼륨 슬라이더 96→101로 두 모듈이 나란히 431, A269(v0.267.0)의 우군 재배치로 <b>437</b>이
    /// 됐다(XAML TransportBar 헤더 주석의 재계수 참조 — 종전 394 시절의 "축약 불요" 판정 소멸).
    /// A269분 +6의 실체 = 이 바에서는 정렬 스페이서 26이 비주얼라이저 버튼 32로 바뀐 것이고
    /// (칸 수 불변), 영상 바에서는 우군 끝에 0폭 스페이서 칸이 늘어 간격 6이 붙은 것이다 —
    /// 서로 다른 경로로 같은 +6이라 두 모듈의 합·임계가 계속 같은 값이다.
    /// A349(2026-09-04): 재생 버튼 양옆에 ⏮ ⏭ 칸 2개(각 32 + 간격 6 = 76)가 두 모듈에 대칭으로
    /// 들어가 고정 폭 합 437 → <b>513</b>, 임계 651 → <b>727</b>이다(파생: 축약 시작 창 폭 약 777 →
    /// 약 853). 숨김 대상은 종전 3개 그대로 — 사용자 확정 ⓑ "볼륨·시간이 먼저 빠지고 ⏮⏭은 남는다".
    /// 임계 727(A349 이전 651·A269 이전 645·A250 이전 640)과 산식 계보는 비디오 UpdateCompactTransport 주석이 정본이다.
    /// 숨김 대상(볼륨·시간 텍스트 2개)까지 비디오와 같아야 축약 후에도 클러스터 x가 일치한다.
    /// 숨겨도 기능은 남는다: 볼륨은 ↑/↓·휠·음소거 버튼, 재생 위치는 시크 슬라이더 썸 위치가 대신한다.
    /// </summary>
    private void UpdateCompactTransport(double width)
    {
        // A249 예외: 폭 임계 축약은 "숨김 금지" 정책의 확정 예외다(공간 제약 — 2026-08-27 사용자
        // 답변). 영상과 같은 임계·같은 대상이어야 정렬이 유지된다.
        var visibility = width < 727 ? Visibility.Collapsed : Visibility.Visible;
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

    // ---------- 비주얼라이저 스타일 상태 (A268) ----------
    // 재생 표면의 libvlc 시각화(visual 플러그인) 스타일. 시각화는 인스턴스 옵션 전용이라
    // (v0.12.0 실기기 확인 — 클래스 주석) 스타일 변경 = 인스턴스 폐기·재생성(RecreatePlayer)이다.
    // 저장 키는 전역 1벌·즉시 Set+Save(EQ 선례 — 설정 화면 노출 없음).
    // goom·projectM 등 별도 플러그인 스타일은 배포본 동봉 여부 실기기 확인 후 2차 후보(등재문).
    // ※ 싱크 참고(등재문에 흡수된 "파형과 음악 싱크 안 맞음" 보고): 시각화가 소리를 앞서는
    // 어긋남은 libvlc가 시각화를 디코드 시점에 직접 렌더하면서 aout 버퍼 지연(출력까지의
    // 수백 ms)을 보정하지 않는 알려진 동작이다 — 앱 타이머·폴링과 무관해 앱 측 수리 축이
    // 없다. 스타일별 체감 오프셋은 실기기 계측 포인트(상수로 확인되면 보정 실험이 2차 후보).

    private const string VisualizerKey = "audio.visualizer";
    private const string VisualizerDefault = "scope"; // 현행 상시 파형(v0.12.0)과 같은 기본값

    /// <summary>표기·저장값·libvlc effect-list 값의 단일 원본 — 배열 순서가 곧 플라이아웃 순서다.
    /// Off는 시각화 옵션 자체를 뺀 인스턴스(영상 모듈의 옵션 구성과 동일 — 검은 표면만 남는다).
    /// 저장값은 전부 소문자다. A304: VU meter는 libvlc vuMeter effect("너무 촌스럽다" —
    /// 사용자 확정)를 버리고 자체 렌더로 전환 — Effect가 null이라 인스턴스 옵션은 Off와 같고,
    /// 표시는 VuOverlay + VuMeterEngine(WASAPI 루프백)이 맡는다. off↔vumeter 전환도 키가
    /// 달라 재생성을 타지만(옵션은 동일 — 불필요 재생성 1회) 재생성 경로 무접촉이라는 구조
    /// 최소 변경(구현 시 결정)을 우선했다.</summary>
    private static readonly (string Label, string Key, string? Effect)[] VisualizerStyles =
    [
        ("Off", "off", null),
        ("Scope", "scope", "scope"),
        ("Spectrum", "spectrum", "spectrum"),
        ("Spectrometer", "spectrometer", "spectrometer"),
        ("VU meter", VuMeterStyleKey, null),
    ];

    private string _visualizer;                           // 현재 선택(저장값) — 로컬 소유(_muted 규칙)
    private string _playerVisualizer = VisualizerDefault; // 현재 인스턴스에 구워진 값 — 재생성 필요 판정 기준

    // ---------- A304: 자체 렌더 VU 미터 상태 ----------
    // 경로 선택 근거·스레드 지도는 VuMeterEngine 헤더 주석이 정본이다. 여기는 뷰 쪽 배선만:
    // 오버레이 표시 = VU 스타일 + 파일 열림 + 안내문 없음, 캡처 = 표시 + 재생 중(그 외 즉시
    // 정지 — 배터리·CPU). 모든 전이는 UpdateVuMeter 한곳을 지난다(그곳 주석의 전이표 참고).

    /// <summary>VU meter 스타일의 저장값 키 — 스타일 표(VisualizerStyles)와 판정(IsVuStyle)의 단일 원본.</summary>
    private const string VuMeterStyleKey = "vumeter";

    /// <summary>현재 선택이 VU meter인가 — 자체 렌더 분기(A304)의 단일 판정.</summary>
    private bool IsVuStyle => string.Equals(_visualizer, VuMeterStyleKey, StringComparison.Ordinal);

    private VuMeterEngine? _vuEngine; // 지연 생성 — VU 스타일로 실제 재생해야 만든다
    private bool _vuActive;           // UI 스레드 소유 렌더 게이트 — Stop 뒤 비행 중이던 틱을 걸러낸다
    private readonly RectangleGeometry _vuClipL = new(); // 채움 클립 — 사용처마다 새 인스턴스(v0.174.1 규칙)
    private readonly RectangleGeometry _vuClipR = new();

    // ---------- A301: 교체 직렬화 가드 · 계측 ----------
    // _recreating: RecreatePlayer 진행 중 표시 — 교체 중 스타일 재선택은 조용히 무시한다
    // (구현 시 결정: 마지막 요청 큐잉 불요 — 무시가 단순). _playerGate만으로는 뒤에 줄을 서서
    // "교체 뒤 또 교체"가 되므로 이 플래그가 그 줄서기 자체를 막는다.
    private bool _recreating;

    // 계측(diag.audioSwap — A285 EditorDecor 계측의 최소형): 재생성 시작(RecreatePlayer의
    // 실제 교체 개시)→신 인스턴스 Playing 도달까지 ms를 오버레이에 key=value로 찍는다.
    // 시계는 DateTime.UtcNow(ExplorerFileOps 등 저장소 경과 시간 관례) — MinValue = 계측 없음.
    private bool _diagOn;
    private DateTime _swapStartedUtc = DateTime.MinValue;

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

        // A300: EQ·비주얼라이저 아이콘 — 글리프(E9E9·E8D6)가 기능을 드러내지 못한다는 사용자
        // 보고로 코드 조립 도형으로 교체(Shared/MediaIcons — FitIcons의 A298 공용화·A299 전경
        // 동기 관용구를 그대로 승계. 도형 산술·비활성 회색 근거는 그 파일 주석).
        // 버튼 상자·플라이아웃·핸들러·툴팁은 무접촉 — Content만 갈아 끼운다.
        EqButton.Content = MediaIcons.BuildEqualizerIcon();
        VisualizerButton.Content = MediaIcons.BuildVisualizerIcon();

        _eqPreset = _settings.Get("audio.equalizer", string.Empty);
        _outputDeviceId = _settings.Get("audio.outputDevice", string.Empty);

        // A330 ⓑ: EQ 플라이아웃도 열 때마다 채운다(장치·비주얼라이저와 같은 Opening 재구성).
        // 종전에는 플레이어 생성 워커가 1회 채우며 버튼 활성화까지 겸했는데, 프리셋 열거가
        // 실패하면 버튼이 영구 비활성으로 굳어 "아이콘만 어둡고 흐리다"로 보였다
        // (FillEqualizerFlyout 주석의 판정 근거). 버튼은 이제 항상 활성이다.
        EqFlyout.Opening += (_, _) => FillEqualizerFlyout();

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

        // A268: 비주얼라이저 — 시작 시 로드해 첫 인스턴스 옵션(BuildPlayerOptions)에 굽는다.
        // 알 수 없는 저장값(수동 편집 등)은 기본 scope로 접는다(EQ의 목록 밖 이름 폴백과
        // 같은 규칙 — 설정 파일은 건드리지 않는다). 플라이아웃은 열 때마다 새로 채운다
        // (위 장치 플라이아웃의 Opening 재구성 관례 — 폴백으로도 체크 표시가 어긋날 수 있다).
        _visualizer = _settings.Get(VisualizerKey, VisualizerDefault);
        if (!IsKnownVisualizer(_visualizer)) _visualizer = VisualizerDefault;
        VisualizerFlyout.Opening += (_, _) => FillVisualizerFlyout();

        // A304: VU 미터 채움 클립 배선 + 초기 0 레벨. 오버레이 표시·캡처 여부는 UpdateVuMeter가
        // 상태 전이(재생·일시정지·정지·스타일 변경·안내문)마다 다시 판정한다.
        VuFillL.Clip = _vuClipL;
        VuFillR.Clip = _vuClipR;
        ResetVuBars();
        UpdateVuMeter();

        // A301: 교체 계측 오버레이(diag.audioSwap, 기본 꺼짐) — EditorDecorDiagnostics(A285)
        // 관용구 복제. 초기 1회는 저장값을 바로 읽고, 이후는 설정 화면의 NotifyChanged가 부르는
        // Changed 구독으로 즉시 반영한다. static 이벤트라 Unloaded에서 반드시 해제한다
        // (A88 규칙 — DocumentView의 같은 자리·같은 형태의 해제).
        _diagOn = settings.Get(AudioDiagnostics.SettingKey, false);
        AudioDiagnostics.Changed += ApplyAudioDiagnostics;
        Unloaded += (_, _) => AudioDiagnostics.Changed -= ApplyAudioDiagnostics;

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
            UpdateVuMeter(); // A304: 준비 안내문과 미터가 겹치지 않게(표시 조건이 안내문 유무를 본다)
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

            // A268: 옵션 조립은 BuildPlayerOptions로 일원화 — 시각화 스타일을 인스턴스에 굽는다.
            var style = _visualizer;
            string[] options = BuildPlayerOptions(swapOptions, style);

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
            _playerVisualizer = style; // A268: 이 인스턴스에 구운 스타일 확정(RecreatePlayer의 판정 기준)
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
            // A330 ⓑ: 여기서 플라이아웃을 채우던 호출은 뺐다 — 목록은 열 때마다 이 배열에서
            // 다시 만들어지고(생성자의 EqFlyout.Opening), 버튼 활성화 축도 사라졌다.
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
    /// autoAdvance = <b>목록 진행</b>으로 온 호출 — A255 목록 순환 카운터(_listLoops)를 잇는다.
    /// 그 외(셸 열기·드롭·▶ 재시작·샘플 곡)는 전부 새 재생 단위 = 카운터 재출발.
    /// <b>A349에서 의미가 한 칸 넓어졌다</b>: 종전에는 "EOF 자동 진행(AdvanceAfterEnd 전이 2·3)"
    /// 뿐이었으나 이제 <b>수동 이웃 이동</b>(⏮/⏭·Ctrl+←/→·PageUp/Down — MoveToNeighbor)도 이
    /// 경로로 들어온다. 매개변수 이름은 두되 뜻은 "자동 진행 또는 수동 이웃 이동 = 목록 진행"이다.
    /// 수동 이동을 여기로 보내는 이유는 <b>_listLoops를 리셋하지 않기 위해서</b>다(사용자 확정 ⓒ —
    /// 수동 되감기는 반복 예산을 소모하지도, 재출발시키지도 않는다). 예산을 <b>소모</b>하는 지점은
    /// 종전 그대로 AdvanceAfterEnd의 `_listLoops++` 두 곳뿐이다.
    /// </summary>
    private void PlayCurrent(bool autoAdvance = false)
    {
        if (_player is not { } p || _libVlc is not { } lib || _filePath is null) return;

        _durationMs = 0;
        _infoNotified = false; // A332: 새 파일 = 정보 갱신 통지 1회를 다시 쓸 수 있다
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
        UpdateVuMeter(); // A304: 파일이 열리면 VU 오버레이 표시(캡처는 Playing 전이가 켠다)
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
    /// <para>
    /// A349: 셸이 주입한 좌 리스트 순서(<see cref="SetBrowseOrder"/>)가 이 파일의 폴더 것이면
    /// 그것이 정본이다 — 폴더를 다시 열거하지 않으므로 워커도 대기도 없다(동기 완료).
    /// 폴더가 다르면(명령줄·드래그&amp;드롭·연결 프로그램) 종전 자체 열거 폴백 그대로다.
    /// 영상 EnsurePlaylist의 동형 이식이다.
    /// </para>
    /// </summary>
    private async void EnsurePlaylist()
    {
        var file = _filePath!;
        if (IsSampleTrack(file))
        {
            _playlist = null;
            UpdateNeighborButtons();
            return;
        }
        if (_playlist is { } current &&
            string.Equals(current.Current, file, StringComparison.OrdinalIgnoreCase))
        {
            return; // 목록 진행으로 온 파일 — 스냅샷 유지
        }

        _playlist = null;
        UpdateNeighborButtons(); // 목록이 정해지기 전에는 갈 곳을 알 수 없다 = 둘 다 비활성

        // A349 주입 경로: 화면에서 보는 순서가 곧 ⏮/⏭·오토 넥스트 순서다(A346 이미지 선례).
        if (SameFolder(_browseFolder, Path.GetDirectoryName(file)))
        {
            _playlist = FolderPlaylist.FromOrdered(_browseFiles, file, AudioModule.Extensions);
            UpdateNeighborButtons();
            return;
        }

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

        // A349 두 시점 검사(A346 관용구): 워커를 기다리는 동안 셸의 SetBrowseOrder가 도착했을 수
        // 있다. 그 시점에는 _playlist가 아직 null이라 SetBrowseOrder 쪽 "갈아 끼우기"가 무동작이었다 —
        // 여기서 폴더를 다시 대조해 주입 목록을 채택한다. 빠뜨리면 탐색기에서 연 첫 파일만
        // 옛(자체 열거) 순서가 되고, 그 뒤 정렬을 건드려야 고쳐지는 증상이 된다.
        _playlist = SameFolder(_browseFolder, Path.GetDirectoryName(file))
            ? FolderPlaylist.FromOrdered(_browseFiles, file, AudioModule.Extensions)
            : list;
        UpdateNeighborButtons();
    }

    /// <summary>
    /// A349(A346 이식): 셸이 탐색기 좌 리스트의 표시 순서를 준다(뷰 생성 직후 1회 + 정렬·필터·
    /// 재스캔·폴더 이동마다). 지금 재생 중인 파일이 그 폴더의 것이면 재생 목록을 새 순서로 갈아
    /// 끼운다 — 재생은 건드리지 않고(같은 파일이다) 인덱스만 새 목록에서 경로로 다시 찾는다.
    /// 열린 파일이 없거나 폴더가 다르면 값만 저장해 두고 <see cref="EnsurePlaylist"/>가 꺼내 쓴다.
    /// 샘플 곡은 목록 대상이 아니라 제외한다(_playlist는 null로 남는다 — 같은 규칙).
    /// </summary>
    public void SetBrowseOrder(string folder, IReadOnlyList<string> files)
    {
        _browseFolder = folder;
        _browseFiles = files;

        if (_filePath is not { } current || IsSampleTrack(current)) return;
        if (!SameFolder(folder, Path.GetDirectoryName(current))) return;

        _playlist = FolderPlaylist.FromOrdered(files, current, AudioModule.Extensions);
        UpdateNeighborButtons();
        // A348: 여기서는 CurrentPathChanged를 쏘지 않는다 — 목록만 갈아 끼웠을 뿐
        // 재생 중인 파일은 그대로다(current로 인덱스를 다시 찾았다).
    }

    /// <summary>
    /// 두 폴더 경로가 같은가 — 끝 구분자 유무만 다른 경우를 흡수한다(셸이 주는 폴더 경로와
    /// <c>Path.GetDirectoryName</c> 결과의 표기가 갈릴 수 있다). 셸에 같은 성격의 헬퍼가 있으나
    /// 모듈은 셸(KOTU.App)을 참조할 수 없어 여기에 따로 둔다(이미지·영상 뷰와 같은 사본).
    /// </summary>
    private static bool SameFolder(string? a, string? b) =>
        !string.IsNullOrEmpty(a) && !string.IsNullOrEmpty(b) &&
        string.Equals(Path.TrimEndingDirectorySeparator(a), Path.TrimEndingDirectorySeparator(b),
            StringComparison.OrdinalIgnoreCase);

    // ---------- 이전/다음 파일 이동 (A349 — 영상 MoveToNeighbor의 동형 이식) ----------

    /// <summary>
    /// A349: 재생 목록의 이웃 파일로 이동한다 — 하단 바 ⏮/⏭ · Ctrl+←/→ · PageUp/PageDown 공용.
    /// <para>
    /// 끝 처리는 루프 모드를 따른다: 루프 없음·한 파일 루프면 목록 끝에서 무동작(버튼은 이미
    /// 비활성이라 키로만 닿는 경로다), 목록 루프면 되감기(⏭ = 처음으로 · ⏮ = 마지막으로).
    /// <b>되감기는 <c>_listLoops</c>를 소모하지 않는다</b>(2026-09-04 사용자 확정 ⓒ) — 그 예산은
    /// EOF 자동 진행 전용이고 수동 개입은 세지 않는다. 그래서 이 메서드는 두 루프 카운터를
    /// 어느 쪽으로도 건드리지 않는다(<see cref="PlayCurrent"/>의 autoAdvance=true 경로로 들어가
    /// 리셋을 건너뛴다).
    /// </para>
    /// <para>
    /// 한 파일 루프에서 수동 이동은 이웃으로 간다 — 모드는 그대로 유지되고 새 파일에서 반복한다.
    /// 소실된 파일은 목록에서 빼고 <b>한 번만</b> 시도한다(오토 넥스트의 while 재시도와 다르다 —
    /// 수동 조작은 "한 번 눌렀으니 한 칸"이 예측 가능하고, 연타는 사용자가 한다).
    /// </para>
    /// </summary>
    private void MoveToNeighbor(bool forward)
    {
        if (_playlist is not { } list) return;

        // 목록 루프 + 2개 이상일 때만 양 끝에서 되감을 수 있다(1개짜리 목록은 되감아도 제자리).
        var canWrap = _loopMode == LoopMode.List && list.Count > 1;
        string? target;
        if (forward)
            target = list.HasNext ? list.PeekNext : canWrap ? list.PeekFirst : null;
        else
            target = list.HasPrevious ? list.PeekPrevious : canWrap ? list.PeekLast : null;
        if (target is null) return; // 끝(루프 없음) — 무동작

        if (!File.Exists(target))
        {
            list.Remove(target);
            UpdateNeighborButtons();
            return;
        }

        // 인덱스 이동은 OpenPath보다 먼저다 — 그래야 이어지는 EnsurePlaylist가 "목록 진행으로 온
        // 파일"(list.Current == file)로 보고 스냅샷을 그대로 유지한다. 뒤로 미루면 목록이 매
        // 이동마다 다시 만들어진다.
        if (forward)
        {
            if (list.HasNext) list.MoveNext();
            else list.MoveFirst();
        }
        else
        {
            if (list.HasPrevious) list.MovePrevious();
            else list.MoveLast();
        }

        UpdateNeighborButtons();
        CurrentPathChanged?.Invoke(target);   // A348: 로드 앞 통지 — 좌 리스트 하이라이트 즉시 추종
        OpenPath(target, autoAdvance: true);  // 목록 진행 = 스냅샷 유지 + 루프 카운터 보존(위 ⓒ)
    }

    /// <summary>
    /// A349: ⏮/⏭ 버튼의 활성 상태 — 갈 곳이 있을 때만 살아 있다(사용자 확정 ⓐ. 저장소의
    /// "하단 바 버튼은 항상 살아 있다" 관례의 명시적 예외라 XAML 주석에도 적어 뒀다).
    /// 목록 루프가 켜져 있고 파일이 2개 이상이면 어느 끝에서도 되감을 수 있으므로 둘 다 활성이다.
    /// 재생 목록이 없으면(샘플 곡·스캔 실패·아직 스캔 전) 둘 다 비활성.
    /// </summary>
    private void UpdateNeighborButtons()
    {
        var list = _playlist;
        var canWrap = list is not null && _loopMode == LoopMode.List && list.Count > 1;
        PrevButton.IsEnabled = list is not null && (list.HasPrevious || canWrap);
        NextButton.IsEnabled = list is not null && (list.HasNext || canWrap);
    }

    /// <summary>XAML Click 배선 — ⏮(이전 파일).</summary>
    private void OnPrevClicked(object sender, RoutedEventArgs e) => MoveToNeighbor(forward: false);

    /// <summary>XAML Click 배선 — ⏭(다음 파일).</summary>
    private void OnNextClicked(object sender, RoutedEventArgs e) => MoveToNeighbor(forward: true);

    /// <summary>A349: Ctrl+← · PageUp = 이전 파일.</summary>
    private void OnPreviousFileInvoked(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        args.Handled = true;
        MoveToNeighbor(forward: false);
    }

    /// <summary>A349: Ctrl+→ · PageDown = 다음 파일.</summary>
    private void OnNextFileInvoked(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        args.Handled = true;
        MoveToNeighbor(forward: true);
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
        _ceremonyTimer?.Stop(); // A302: 해체 후 틱 방지(영상 A12·A13과 같은 자리·같은 형태)
        _vuActive = false;
        _vuEngine?.Dispose(); // A304: 캡처·표시 타이머 정지(뒷정리는 스레드풀 — UI 비의존)
        _vuEngine = null;
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
        // A332: 길이가 정해지는 시점 = libvlc가 컨테이너를 파싱해 트랙 정보까지 아는 시점이다.
        // 셸의 정보 패널은 그보다 앞서(PlayCurrent가 ContentOpened를 쏜 프레임에) 이미 물어봐
        // 빈칸을 받았을 수 있다 — 여기서 한 번 알려 다시 묻게 한다. 파일당 1회로 결박해
        // (리핏 재장전은 같은 파일이라 안 쏜다) 잉여 재조회·재진입 고리를 만들지 않는다.
        if (e.Length > 0 && !_infoNotified)
        {
            _infoNotified = true;
            ContentInfoChanged?.Invoke();
        }
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
        UpdateVuMeter(); // A304: 재생 시작 = 캡처 켬(VU 스타일일 때만 실동작 — 전이표)

        // A301: 교체 계측 소비 — RecreatePlayer ⑥이 실어 둔 개시 시각으로 "재생성 시작→
        // Playing 도달" ms를 계산해 오버레이에 찍는다(표시는 diag.audioSwap 켜짐일 때만 —
        // 잰 값은 1회성이라 바로 비운다. 교체가 아닌 일반 재생 시작은 MinValue라 무동작).
        if (_swapStartedUtc != DateTime.MinValue)
        {
            var swapMs = (long)(DateTime.UtcNow - _swapStartedUtc).TotalMilliseconds;
            _swapStartedUtc = DateTime.MinValue;
            if (_diagOn)
            {
                DiagText.Text = $"swap={swapMs}ms";
                DiagPanel.Visibility = Visibility.Visible;
            }
        }
    });

    private void OnPlayerPaused(object? sender, EventArgs e) =>
        Dispatch(() =>
        {
            PlayButton.Content = "▶";
            SetTrayTimer(false); // A54: 멈추면 타이머도 멈춘다 — 막대는 낮게 고정
            TrayStatusChanged?.Invoke();
            UpdateVuMeter(); // A304: 일시정지 = 캡처 즉시 정지(배터리·CPU — 오버레이는 0 레벨로 남는다)
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
    /// A330 ⓒ: 전이 4(목록 루프 + 다음 파일 없음 = 같은 파일 재시작)를 재생 목록 블록 밖으로
    /// 꺼냈다 — 목록이 아직·끝내 만들어지지 않은 회차에도 성립해야 한다(그 자리 주석이 정본).
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
                UpdateNeighborButtons(); // A349: 인덱스가 옮겨졌으니 ⏮/⏭ 활성도 새 자리 기준으로
                return;
            }
        }

        // 전이 4: 목록 루프인데 진행할 다음 파일이 없다 = 지금 파일이 곧 목록 전체다 →
        // 같은 파일 재시작. 이 재시작도 "되감기 1회"로 세어 같은 예산을 소모한다
        // (A255 — 구판의 무한 고정 폐기).
        // **A330 ⓒ 개정**: 종전에는 이 판정이 위 `_playlist is { } list` 블록 **안**에
        // `list.Count == 1` 조건으로 있었다. 그래서 목록이 **만들어지지 않은** 회차에는
        // 목록 루프가 통째로 정지(전이 5)로 떨어졌다 — _playlist가 null이 되는 길은 셋이다:
        //   ⓐ 폴더 스캔 실패(EnsurePlaylist의 catch — 권한·IO 예외가 _playlist를 null로 남긴다)
        //   ⓑ 스캔이 끝나기 전에 EOF(EnsurePlaylist 주석이 "다음 EOF부터 정상"이라고 적어 둔
        //      경합. 그러나 정지 전이는 EOF를 더 만들지 않으므로 그 자가 복구는 성립하지 않는다)
        //   ⓒ 내장 샘플 곡(목록 대상 제외 — PlayCurrent가 _playlist를 null로 둔다)
        // 실기기 보고 = "폴더에 음악 파일 1개인데 전체 반복이 안 된다". 이제 목록이 없거나
        // 항목이 하나뿐이면 같은 뜻으로 본다.
        // 모드 배타는 그대로다: 루프 없음·한 파일 루프는 이 분기에 들어오지 못하므로
        // "루프 없음에서 무한 재생"은 생기지 않고, A258 오토넥스트 게이트도 무관하다
        // (그 게이트는 루프 없음일 때만 유효하고 여기는 목록 루프 전용이다).
        // Count > 1인데 여기까지 왔다면 되감기 예산 소진이므로 종전대로 정지다.
        if (_loopMode == LoopMode.List && _playlist is not { Count: > 1 } &&
            (_listLoopLimit == 0 || _listLoops < _listLoopLimit))
        {
            _listLoops++;
            ReplayCurrent();
            return;
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
        UpdateVuMeter(); // A304: EOF 정지(전이 5) = 캡처 정지(전이 1~4는 Playing 전이가 잇는다)
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
        UpdateVuMeter(); // A304: 안내문과 미터가 겹치지 않게(재생 실패 시 캡처도 여기서 멎는다)
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
        // A268: NothingSpecial도 같은 경로다 — 재생 중이 아닐 때 비주얼라이저를 바꾸면 미디어
        // 없는 새 인스턴스만 남아(Play()가 무동작) ▶가 침묵하게 된다. 위치는 교체 때 이어듣기
        // 저장에 실어 둬 PlayCurrent(_pendingResumeMs)가 복원한다(RecreatePlayer 주석).
        if (p.State is VLCState.Ended or VLCState.NothingSpecial)
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
    /// 라디오 구성).
    /// <b>A330 ⓑ 개정</b>: 종전에는 플레이어 생성 워커가 이 함수를 1회 부르며
    /// <c>EqButton.IsEnabled = 프리셋 있음</c>까지 겸했다. 그래서 프리셋 열거가 실패하면
    /// (EnsurePlayerAsync의 try/catch가 빈 배열을 남긴다) 버튼이 <b>영구 비활성</b>으로
    /// 굳었고, 실기기에서는 "EQ 아이콘만 어둡고 흐리다"로 보였다 — 흐림의 정체는 아이콘
    /// 렌더가 아니라 <b>버튼의 Disabled 전경색</b>이다(옆 비주얼라이저 버튼이 같은
    /// MediaIcons 팩토리·같은 A299 전경 동기를 쓰는데 멀쩡하다는 것이 그 증거이고,
    /// EQ 도형의 잉크량 138px²는 비주얼라이저 88px²보다 오히려 많다).
    /// 이제 버튼은 <b>항상 활성</b>이고(비주얼라이저 버튼 선례 — "선택은 플레이어가 없어도
    /// 저장된다") 목록은 <b>열 때마다</b> 다시 채운다(장치·비주얼라이저 플라이아웃의 Opening
    /// 재구성 관례). 프리셋을 아직·끝내 못 읽은 상태에서 열면 Off 한 줄이 나오고, 그 Off는
    /// 실제로 동작하는 선택이라 막다른 길이 아니다(UnsetEqualizer).
    /// 프리셋 목록은 libvlc 빌드 수명 동안 불변이라 재열거 비용은 없다(문자열 배열 순회).
    /// </summary>
    private void FillEqualizerFlyout()
    {
        EqFlyout.Items.Clear();
        AddEqualizerChoice("Off", string.Empty);
        foreach (var name in _eqPresetNames)
            AddEqualizerChoice(name, name);
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

        // A304: 루프백 캡처는 출력 장치를 미러링하므로 장치 변경을 따라간다(정지 후 재시작 —
        // 엔진의 세대 번호가 비동기 초기화 경합을 정리한다). 비활성이면 다음 시작이 새 값을 쓴다.
        if (_vuActive && _vuEngine is { } engine)
        {
            engine.Stop();
            engine.Start(deviceId);
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

    // ---------- 비주얼라이저 (A268) ----------

    /// <summary>저장값이 스타일 목록에 있는가 — 생성자 로드의 폴백 판정.</summary>
    private static bool IsKnownVisualizer(string style)
    {
        foreach (var (_, key, _) in VisualizerStyles)
            if (string.Equals(key, style, StringComparison.Ordinal)) return true;
        return false;
    }

    /// <summary>저장값 → libvlc effect-list 값(null = 시각화 없음 = Off).</summary>
    private static string? EffectListValue(string style)
    {
        foreach (var (_, key, effect) in VisualizerStyles)
            if (string.Equals(key, style, StringComparison.Ordinal)) return effect;
        return null;
    }

    /// <summary>저장값 → 플라이아웃 표시명 — A302 세러모니가 같은 표기를 재사용한다.
    /// 목록 밖 값은 오지 않지만(호출부가 목록에서 만든 키만 넘긴다) 저장값 폴백으로 방어한다.</summary>
    private static string VisualizerLabel(string style)
    {
        foreach (var (label, key, _) in VisualizerStyles)
            if (string.Equals(key, style, StringComparison.Ordinal)) return label;
        return style;
    }

    /// <summary>
    /// libvlc 인스턴스 옵션 조립 — 스타일을 인스턴스에 굽는 단일 지점(첫 생성·재생성 공용).
    /// --no-video-title-show: 재생 시작 시 파일명이 화면에 오버레이되는 libvlc 기본 동작 끔.
    /// --audio-visual/--effect-list: 시각화(visual 플러그인) — 인스턴스 옵션으로만 동작(A10 이관).
    /// </summary>
    private static string[] BuildPlayerOptions(string[] swapOptions, string style)
    {
        if (EffectListValue(style) is { } effect)
            return [.. swapOptions, "--no-video-title-show", "--audio-visual=visual", $"--effect-list={effect}"];
        return [.. swapOptions, "--no-video-title-show"]; // Off — 영상 모듈과 같은 옵션 구성
    }

    /// <summary>
    /// A330 ⓐ: 표면(vout 스왑체인)에 남은 <b>구 인스턴스의 마지막 프레임</b>을 지운다.
    /// libvlc 시각화는 vout이 스왑체인에 Present한 그림이라 인스턴스를 갈아 끼워도 표면이
    /// 저절로 비워지지 않는다 — 새 인스턴스가 다시 그릴 때 덮일 뿐이다. 그래서 <b>libvlc가
    /// 아예 그리지 않는 스타일</b>(Off · VU meter — 둘 다 Effect가 null이라 인스턴스 옵션이
    /// 같다)로 바꾸면 구 파형이 영구히 남는다(실기기 보고: Scope → VU meter 전환 시 미터
    /// 뒤로 파란·빨간 파형이 그대로 보인다. 사용자 추측대로 다른 전환은 새 그림이 덮어
    /// 가렸던 것뿐이고, <b>Off 전환도 같은 잔상</b>이다).
    /// 지우개는 영상 모듈 A130이 쓰는 것과 같은 관용구다 — VideoView.Clear()(LibVLCSharp
    /// 3.10.0, vlc 이슈 23667 공식 워크어라운드)가 백버퍼를 검정으로 칠해 Present한다.
    /// 표면 배경도 검정(XAML AudioSurface)이라 이음새가 없고, A304 VU 오버레이·A302 세러모니
    /// 칩·A301 진단 판은 전부 XAML 자식이라 이 지우개에 지워지지 않는다(스왑체인 밖).
    /// <b>VideoView를 숨기지 않는 이유</b>: 감추면 Scope·Spectrum·Spectrometer가 함께
    /// 사라진다 — 표면 가시성은 다섯 스타일 전부에서 그대로 두고 내용만 지운다.
    /// </summary>
    private void ClearVisualizerSurface()
    {
        try
        {
            Vlc.Clear();
        }
        catch
        {
            // 표면 정리 실패가 교체를 막으면 안 된다 — 최악이라도 구 프레임이 남을 뿐이다.
        }
    }

    /// <summary>플라이아웃을 스타일 라디오 5택으로 채운다(EQ의 AddEqualizerChoice 관용구).</summary>
    private void FillVisualizerFlyout()
    {
        VisualizerFlyout.Items.Clear();
        foreach (var (label, key, _) in VisualizerStyles)
        {
            var item = new RadioMenuFlyoutItem
            {
                Text = label,
                GroupName = "visualizer",
                IsChecked = string.Equals(key, _visualizer, StringComparison.Ordinal),
            };
            item.Click += (_, _) => SelectVisualizer(key);
            VisualizerFlyout.Items.Add(item);
        }
    }

    /// <summary>
    /// 스타일 선택: 로컬 상태 갱신 + 즉시 저장(EQ 선례) 후 인스턴스 재생성을 건다.
    /// 같은 항목 재선택은 무동작 — 재생성은 소리가 한순간 끊기는 비용이 있어 공짜가 아니다.
    /// A301: 교체 진행 중(_recreating)의 재선택은 조용히 무시한다(구현 시 결정 — 큐잉 불요).
    /// 저장도 하지 않으므로 상태는 일관되고, 플라이아웃은 열 때마다 _visualizer로 다시
    /// 채워져(Opening 재구성) 체크 표시도 어긋나지 않는다.
    /// A302: 정지(일시정지 포함) 상태에서만 세러모니 칩을 띄운다 — 재생 전까지 표면이
    /// 그대로라 이 칩이 유일한 변경 신호다(재생 중 변경은 시각화 자체가 바뀌어 보이므로
    /// 대상 아님 — 사용자 확정). 표시 시점은 선택 직후(RecreatePlayer 완료 대기 없음 —
    /// 누른 순간의 피드백이 목적이고, 재생성 실패 안내는 기존 ShowMessage가 맡는다).
    /// </summary>
    private void SelectVisualizer(string style)
    {
        if (_recreating) return;
        if (string.Equals(style, _visualizer, StringComparison.Ordinal)) return;
        _visualizer = style;
        _settings.Set(VisualizerKey, style);
        _settings.Save();
        if (!IsPlaying) ShowCeremony(VisualizerLabel(style)); // A302: 정지 상태에서만
        UpdateVuMeter(); // A304: VU 진입 = 오버레이 즉시 표시 / 이탈 = 오버레이·캡처 즉시 정리
        RecreatePlayer();
    }

    // ---------- A304: 자체 렌더 VU 미터 ----------

    /// <summary>
    /// VU 미터 수명의 단일 판정점 — 모든 전이가 이 한곳을 지나게 해 누수 축을 없앤다(A306
    /// 전이표 방식). 판정: 오버레이 표시 = VU 스타일 + 파일 열림 + 안내문 없음, 캡처(엔진) =
    /// 표시 + 재생 중. 전이표(호출 지점 → 기대 상태):
    ///   생성자                         → 표시만(파일이 컨텍스트로 왔으면), 캡처 없음
    ///   OnVlcInitialized 준비 안내문   → 안내문 우선 = 오버레이 잠깐 숨김(PlayCurrent가 복원)
    ///   PlayCurrent(열기·드롭·▶ 재시작) → 표시(캡처는 곧 오는 Playing 전이가 켠다)
    ///   Playing(재개·리핏·교체 재장전 포함) → 캡처 켬
    ///   Paused                         → 캡처 끔 + 막대 0 리셋(오버레이는 남는다)
    ///   EOF 정지(AdvanceAfterEnd 전이 5) → 캡처 끔(전이 1~4는 Playing이 다시 켠다)
    ///   ShowMessage(재생 실패 등)       → 오버레이 숨김 + 캡처 끔
    ///   SelectVisualizer(진입/이탈)     → 즉시 표시/정리(재생성 완료 대기 없음)
    ///   OnUnloaded                     → 이 판정을 거치지 않고 엔진을 직접 Dispose(해체 전용)
    /// UI 스레드 전용. 같은 상태 재판정은 무해(엔진 Start/Stop 멱등). 캡처 실패는 엔진이
    /// 조용히 삼켜 빈 미터로 남는다(크래시 금지 요건 — VuMeterEngine 주석).
    /// </summary>
    private void UpdateVuMeter()
    {
        var overlay = !_tornDown && IsVuStyle && _filePath is not null &&
            PlaceholderText.Visibility == Visibility.Collapsed;
        VuOverlay.Visibility = overlay ? Visibility.Visible : Visibility.Collapsed;

        if (overlay && IsPlaying)
        {
            _vuActive = true;
            _vuEngine ??= new VuMeterEngine(levels => Dispatch(() => ApplyVuLevels(levels)));
            _vuEngine.Start(_outputDeviceId);
        }
        else
        {
            _vuActive = false;
            _vuEngine?.Stop();
            ResetVuBars();
        }
    }

    /// <summary>엔진 40ms 틱의 UI 반영(디스패치 후) — 속성 대입뿐이다(계산은 전부 엔진 워커).
    /// 채움은 Clip 폭, 피크는 3px 막대의 X 이동. 트랙 폭은 매 틱 ActualWidth를 읽으므로
    /// 창 리사이즈에 별도 배선 없이 추종한다.</summary>
    private void ApplyVuLevels(VuLevels levels)
    {
        if (!_vuActive) return; // Stop 직후 비행 중이던 틱 — 리셋된 막대를 되살리지 않는다
        ApplyVuChannel(VuTrackL, _vuClipL, VuPeakL, VuPeakShiftL, levels.RmsL, levels.PeakL);
        ApplyVuChannel(VuTrackR, _vuClipR, VuPeakR, VuPeakShiftR, levels.RmsR, levels.PeakR);
    }

    /// <summary>채널 하나 반영. peak는 XAML의 3px Rectangle인데 형식은 FrameworkElement로
    /// 받는다 — Shapes를 using하면 Path가 System.IO.Path와 모호해져서다(Width·Visibility만 쓴다).</summary>
    private static void ApplyVuChannel(Grid track, RectangleGeometry clip,
        FrameworkElement peak, TranslateTransform peakShift, double rms, double peakLevel)
    {
        var width = track.ActualWidth;
        var height = track.ActualHeight;
        if (width <= 0 || height <= 0) return; // 첫 레이아웃 전 — 다음 틱(40ms)이 곧 온다

        clip.Rect = new Windows.Foundation.Rect(0, 0, Math.Clamp(rms, 0, 1) * width, height);

        if (peakLevel <= 0.004)
        {
            peak.Visibility = Visibility.Collapsed; // 무음 — 왼쪽 끝에 피크 조각을 남기지 않는다
            return;
        }
        peak.Visibility = Visibility.Visible;
        peakShift.X = Math.Clamp(peakLevel * width - peak.Width, 0, Math.Max(0, width - peak.Width));
    }

    /// <summary>막대를 0 레벨로 되돌린다(캡처 정지·초기화 공용) — 정지 순간의 레벨이 얼어붙어
    /// 남지 않게. UI 스레드 전용.</summary>
    private void ResetVuBars()
    {
        _vuClipL.Rect = new Windows.Foundation.Rect(0, 0, 0, 0);
        _vuClipR.Rect = new Windows.Foundation.Rect(0, 0, 0, 0);
        VuPeakL.Visibility = Visibility.Collapsed;
        VuPeakR.Visibility = Visibility.Collapsed;
    }

    // ---------- A302: 비주얼라이저 변경 세러모니 ----------

    /// <summary>세러모니 자동 소멸 타이머(1.5초 1회성) — 영상 A12 _startOverlayTimer 관용구
    /// (지연 생성 · Tick에서 Stop · Unloaded에서 Stop).</summary>
    private DispatcherTimer? _ceremonyTimer;

    /// <summary>
    /// A302: "Visualizer: 표시명"을 표면 중앙 하단(CeremonyOverlay — XAML 자리 근거 주석)에
    /// 1.5초 표시한다. 연속 변경은 문구 교체 + 타이머 재시작(영상 A12 "연속 전환 시 표시
    /// 시간 리셋" 그대로 — 이전 표시가 즉시 새 문구로 바뀌고 겹침이 없다). 페이드 없음:
    /// 저장소 오버레이 칩(영상 A12·A13)이 전부 즉시 표시·즉시 숨김이라 같은 관용구를 따른다.
    /// UI 스레드 전용(플라이아웃 Click에서만 호출).
    /// </summary>
    private void ShowCeremony(string label)
    {
        CeremonyText.Text = $"Visualizer: {label}";
        CeremonyOverlay.Visibility = Visibility.Visible;

        if (_ceremonyTimer is not { } timer)
        {
            timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(1500) };
            timer.Tick += (_, _) =>
            {
                timer.Stop();
                CeremonyOverlay.Visibility = Visibility.Collapsed;
            };
            _ceremonyTimer = timer;
        }
        timer.Stop(); // 연속 변경 시 표시 시간 리셋
        timer.Start();
    }

    /// <summary>
    /// A301: 계측 오버레이 토글 반영 — DocumentView.ApplyEditorDecorDiagnostics(A285)와 같은
    /// 형태(설정 화면 스레드에서 올 수 있어 디스패처 경유 가드). 끌 때만 즉시 숨긴다 —
    /// 켤 때는 다음 계측값이 와야 보인다(낡은 값 잔존 금지 — EditorDecor A287 규칙).
    /// </summary>
    private void ApplyAudioDiagnostics()
    {
        if (DispatcherQueue is { } dq && !dq.HasThreadAccess)
        {
            dq.TryEnqueue(ApplyAudioDiagnostics);
            return;
        }
        if (_tornDown) return;
        _diagOn = _settings.Get(AudioDiagnostics.SettingKey, false);
        if (!_diagOn) DiagPanel.Visibility = Visibility.Collapsed;
    }

    /// <summary>
    /// A268: 스타일 반영을 위한 libvlc 인스턴스 재생성 — 시각화는 인스턴스 옵션 전용이라
    /// 이 길밖에 없다. 흐름(전 구간 _playerGate 직렬 — EnsurePlayerAsync·연타 선택과 상호 배제):
    ///   ① 상태 보관: 파일·재생 여부·위치. 위치는 이어듣기 저장에도 실어 둔다(OnUnloaded
    ///      관용구) — 교체 중 창이 닫혀도, 재생 중이 아니어서 재장전을 생략해도 위치가
    ///      살아남는 단일 경로다(샘플 곡은 이어듣기 제외라 0초 재시작 — 18초짜리라 수용).
    ///   ② 교체 중 가드: 이벤트 해제 + _player/_libVlc 비움 — EOF·시크·볼륨·Space 등 모든
    ///      핸들러가 기존 `_player is { } p` 가드에서 무동작으로 접힌다(별도 플래그 불요).
    ///      재생 중이었으면 구 인스턴스를 여기서 일시정지한다 — 새 인스턴스 재장전과 소리가
    ///      겹치면 같은 곡이 이중으로 들린다(이벤트는 이미 떼어 ▶ 표기는 흔들리지 않는다).
    ///   ③ 새 인스턴스 생성(워커 — 첫 생성과 같은 배경 실행. Core.Initialize는 첫 생성이 마쳤다).
    ///      실패하면 구 인스턴스로 원복하고 재생을 되살린다(설정값은 저장돼 있으므로 다음
    ///      생성 기회가 다시 시도한다).
    ///   ④ VideoView 재바인딩(Vlc.MediaPlayer 재대입 — 저장소 선례 0 = 실기기 1순위 확인
    ///      포인트) + 재적용: 볼륨·음소거(_muted 로컬 소유 승계)·EQ. 배속·출력 장치는 기존
    ///      Playing 핸들러가 재적용한다(aout 생성 후가 유효 적용점 — 기존 규칙 그대로).
    ///      A301: _player/_libVlc <b>필드 대입만</b> ⑤ 해제 완료 뒤로 미룬다 — ②의 가드
    ///      (필드 비움 = 핸들러 무동작)가 ⑤의 대기 구간까지 이어져, Space·클릭이 신
    ///      인스턴스를 해제 완료 전에 재생시키는 길을 막는다(재적용 순서 자체는 무변경).
    ///   ⑤ 구 인스턴스 해제 — A301: 종전 Post(fire-and-forget)를 Worker.Run <b>await</b>로
    ///      바꿔 해제 <b>완료</b>를 기다린다(③이 이미 쓰는 await 관용구 — 같은 워커 직렬 큐).
    ///      종전에는 "③ 뒤 실행"(큐 순서)만 보장되고 ⑥의 Play가 UI 스레드에서 먼저 나가
    ///      구 인스턴스가 오디오 장치·vout을 문 채 신 인스턴스가 시작하는 경합 창이 있었다
    ///      (A303 조사 — 교체 버벅임·독립 창 폴백(H1)의 원인 후보). UI 스레드는 await로만
    ///      기다린다(동기 대기 금지 — 교체 중 입력은 ②의 가드가 이미 접는다). 창 닫힘
    ///      경합: OnUnloaded는 ②에서 비워진 필드 때문에 아무것도 해제하지 않으므로 해제
    ///      주체는 항상 이 메서드 한쪽이다(이중 해제 없음). 해제 실패 무시는 종전 Post의
    ///      계약 그대로(람다 안 try/catch — "실패해도 그만"인 정리).
    ///   ⑤-b A330 ⓐ: 표면 잔상 정리(ClearVisualizerSurface) — 구 인스턴스가 스왑체인에
    ///      Present해 둔 마지막 프레임은 인스턴스를 갈아도 남는다. 신 인스턴스가 곧 덮어
    ///      그리는 경우(효과 있는 스타일 + ⑥ 재장전)만 건너뛴다(깜빡임 회피 — 그 자리 주석).
    ///   ⑥ 재생 중이었고 파일이 그대로면 ReplayCurrent로 재장전 + _pendingResumeMs로 위치
    ///      복원(Playing에서 적용 — 기존 이어듣기 관용구. 복원 위치는 ①에서 잡은 값이고 구
    ///      인스턴스는 ②에서 일시정지라 ⑤ 대기 시간만큼 밀리지 않는다). 그새 다른 파일로
    ///      전환됐으면 그 OpenPath가 게이트 뒤에 대기 중이라 여기서는 손대지 않는다.
    ///      ⑤의 대기 중 창이 닫혔으면(_tornDown) 재장전 없이 신 인스턴스를 여기서 해제한다
    ///      — ④의 대입 연기로 필드가 아직 비어 있어 OnUnloaded는 신 인스턴스를 못 봤다
    ///      (③ 실패 경로와 같은 대리 해제 — 이중 해제 없음 원칙은 그대로).
    /// A301 재진입 가드(_recreating): 교체 중 스타일 재선택은 SelectVisualizer가 조용히
    /// 무시한다 — _playerGate만으로는 뒤에 줄을 서서 "교체 뒤 또 교체"가 되기 때문.
    /// A301 계측(diag.audioSwap): 실제 교체 개시(①)→신 인스턴스 Playing 도달 ms를
    /// OnPlayerPlaying이 오버레이에 swap=NNNms로 찍는다(직렬화의 체감 효과는 실기기 판정).
    /// 등재문의 "해제 → 생성" 대신 "생성 → 교체 → 해제" 순서를 택했다(설계 판단): 해제를
    /// 먼저 하면 VideoView가 해제된 플레이어를 무는 구간이 생겨, 그 사이 리사이즈 등이 그
    /// 핸들을 건드리면 죽는다. 산 것끼리 맞바꾸면 그 구간이 없다. 대신 생성 완료까지 구·신
    /// 인스턴스가 같은 스왑체인 포인터(_swapChainOptions)를 잠깐 공유하는데, 구는 일시정지·
    /// 신은 재생 전이라 동시 렌더 축이 없다 — 실기기 확인 포인트.
    /// 최소 복구법(재바인딩이 실기기에서 불발일 때): 이 메서드 본문을 조기 return으로 비워
    /// "다음 재생부터 적용" 다운그레이드 — SelectVisualizer의 저장은 남으므로 모듈 재진입
    /// (뷰 재생성)의 EnsurePlayerAsync가 새 옵션을 쓴다.
    /// </summary>
    private async void RecreatePlayer()
    {
        if (_swapChainOptions is not { } swapOptions) return; // 첫 생성 전 — 생성 시점에 새 값이 구워진다

        _recreating = true; // A301: 여기부터 finally까지 SelectVisualizer 재선택을 조용히 무시
        await _playerGate.WaitAsync();
        try
        {
            if (_tornDown) return;
            if (_player is not { } oldPlayer || _libVlc is not { } oldLib)
                return; // 인스턴스 없음 — 다음 EnsurePlayerAsync가 현재 설정으로 만든다
            var style = _visualizer;
            if (string.Equals(_playerVisualizer, style, StringComparison.Ordinal))
                return; // 연타·경합으로 이미 목표 상태 — 재생성 불요

            // ① 상태 보관 (+ A301: 교체 계측 개시 시각 — 실제 교체가 확정된 이 지점부터 잰다)
            var swapStartUtc = DateTime.UtcNow;
            var file = _filePath;
            var wasPlaying = oldPlayer.IsPlaying;
            var resumeMs = oldPlayer.Time;

            // ② 교체 중 가드 (요약 주석 참고)
            UnhookPlayerEvents(oldPlayer);
            _player = null;
            _libVlc = null;
            if (wasPlaying && oldPlayer.CanPause) oldPlayer.Pause();
            if (file is not null && !IsSampleTrack(file) && _durationMs > 0)
            {
                try { _resumeStore.Report(file, resumeMs, _durationMs); }
                catch { /* 저장 실패가 교체를 막으면 안 된다 */ }
            }

            // ③ 새 인스턴스 생성
            var options = BuildPlayerOptions(swapOptions, style);
            LibVLC libVlc;
            MediaPlayer player;
            try
            {
                (libVlc, player) = await Worker.Run(_ =>
                {
                    var lib = new LibVLC(options);
                    return (lib, new MediaPlayer(lib));
                });
            }
            catch (Exception ex)
            {
                if (_tornDown)
                {
                    // 실패 + 창 닫힘 — 구 인스턴스만 남았다: OnUnloaded 몫을 여기서 대신한다.
                    Worker.Post(() =>
                    {
                        oldPlayer.Stop();
                        oldPlayer.Dispose();
                        oldLib.Dispose();
                    });
                    return;
                }
                // 원복: 재생 유지가 우선 — _playerVisualizer는 구 값 그대로라 재선택하면 재시도된다.
                _player = oldPlayer;
                _libVlc = oldLib;
                HookPlayerEvents(oldPlayer);
                if (wasPlaying) oldPlayer.Play(); // ②의 일시정지 되돌림
                ShowMessage($"Visualizer change failed: {ex.Message}");
                return;
            }

            if (_tornDown)
            {
                // 교체 중 창 닫힘: 구·신 인스턴스 전부 여기서 해제한다(⑤ 요약 주석 — 이중 해제 없음).
                Worker.Post(() =>
                {
                    oldPlayer.Stop();
                    oldPlayer.Dispose();
                    oldLib.Dispose();
                    player.Dispose();
                    libVlc.Dispose();
                });
                return;
            }

            // ④ 재바인딩 + 재적용 — A301: 필드(_player/_libVlc) 대입만 ⑤ 해제 완료 뒤로
            // 미룬다. ②의 가드(필드 비움 = Space·클릭 등 모든 핸들러 무동작)가 ⑤의 대기
            // 구간까지 이어져, 그 사이 사용자 입력이 신 인스턴스를 해제 완료 전에 재생시키는
            // 길이 없다(경합 창 봉쇄의 일부). VideoView 바인딩·볼륨·음소거·EQ·이벤트 훅은
            // 종전 그대로 이 지점 = Play 전 적용이라는 순서가 불변이다.
            Vlc.MediaPlayer = player;

            player.Volume = (int)VolumeSlider.Value; // EnsurePlayerAsync와 같은 적용점
            if (_muted) player.Mute = true;          // A28: 로컬 소유 상태 승계(버튼 아이콘은 이미 맞다)
            HookPlayerEvents(player);
            ApplyEqualizer(player);                  // 프리셋 목록은 libvlc 빌드 불변 — 재열거 불요

            // ⑤ 구 인스턴스 해제 — A301: await로 해제 완료 뒤에만 ⑥이 나가게 직렬화
            // (③의 Worker.Run await 관용구 — 요약 주석 ⑤). 실패 무시는 종전 Post 계약 승계.
            await Worker.Run(_ =>
            {
                try
                {
                    oldPlayer.Stop();
                    oldPlayer.Dispose();
                    oldLib.Dispose();
                }
                catch
                {
                    // 뒷정리 실패는 무시 — Worker.Post의 예외 차단 계약을 여기서 승계한다
                }
            });

            if (_tornDown)
            {
                // ⑤ 대기 중 창 닫힘 — 필드가 아직 비어 있어 OnUnloaded는 신 인스턴스를 못
                // 봤다: ③ 실패 경로처럼 여기서 대신 해제한다(구는 ⑤가 이미 해제했다.
                // 워커가 닫혔으면 Post가 스레드풀로 폴백해 해제 실행은 보장된다).
                UnhookPlayerEvents(player);
                Worker.Post(() =>
                {
                    player.Dispose();
                    libVlc.Dispose();
                });
                return;
            }

            _libVlc = libVlc;
            _player = player;
            _playerVisualizer = style;

            // A330 ⓐ: 잔상 정리 — 구 인스턴스는 ⑤에서 해제됐고 표면에는 그 마지막 프레임이
            // 그대로 남아 있다. 신 인스턴스가 곧 다시 그리는 경우(효과 있는 스타일 + 아래 ⑥
            // 재장전)만 건너뛴다: 어차피 덮이는 자리에 검정을 한 번 끼우면 그 자체가 깜빡임이
            // 된다(A301 계측 기준 교체는 85ms대). 그 밖은 전부 지운다 —
            //   ⓐ Off·VU meter(Effect null) = 신 인스턴스가 영영 그리지 않는다.
            //   ⓑ 정지·일시정지 중 교체 = 재장전이 없어 다음 재생까지 구 그림이 남는다
            //      (A302 세러모니 칩이 "바뀌었다"고 알리는데 화면은 구 스타일이던 어긋남).
            var replays = wasPlaying && file is not null &&
                string.Equals(file, _filePath, StringComparison.Ordinal);
            if (!replays || EffectListValue(style) is null) ClearVisualizerSurface();

            // ⑥ 재장전 + 위치 복원 — ⑤가 끝난 뒤라 구 인스턴스는 장치·vout을 이미 놓았다
            if (replays)
            {
                _swapStartedUtc = swapStartUtc; // A301: Playing 도달 시 OnPlayerPlaying이 소비
                ReplayCurrent();             // 셸 재동기화·카운터 리셋 없는 재장전(A11 변형 재사용)
                _pendingResumeMs = resumeMs; // ReplayCurrent가 -1로 둔 값을 덮어써 위치 복원
            }
        }
        catch (Exception ex)
        {
            ShowMessage($"Visualizer change failed: {ex.Message}");
        }
        finally
        {
            _recreating = false;
            _playerGate.Release();
        }
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
        UpdateNeighborButtons(); // A349: 목록 루프 켬/끔이 목록 양 끝에서 ⏮/⏭ 활성을 뒤집는다
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
    /// 상태에서 만든다). A255 3상태 표지: 루프 없음 / 목록 루프 = E8EE(RepeatAll) /
    /// 한 파일 루프 = E8ED(RepeatOne).
    /// <b>A318 개정</b>: "루프 없음"은 종전엔 같은 E8EE를 Opacity 0.4로 흐리게 그린 것이라
    /// 사용자가 비활성 버튼으로 읽었다(실기기 보고 — "지금 못 누르나?"). 이제 세 상태가 모두
    /// <b>같은 밝기·같은 전경색</b>이고, "반복 안함"만 같은 글리프 위에 <b>금지 사선</b>을 얹어
    /// 구별한다(MediaIcons.BuildLoopOffIcon — 디자인 정본은 그 파일 한 곳, 영상과 공유).
    /// A255가 남긴 "빗금 도형은 v0.174.1 크래시 함정이라 기각"은 <b>인스턴스 공유</b>가 원인이었고,
    /// 그 팩토리는 호출마다 새 Grid·FontIcon·Path·Geometry를 만들어 그 함정에 걸리지 않는다.
    /// 툴팁은 현행 유지 — 3상태 + 횟수 병기에 우클릭 안내를
    /// 덧붙인다(횟수 플라이아웃 진입이 우클릭뿐이라 이 표기가 유일한 발견 경로다).
    /// 표기는 A34 규칙대로 키 상수에서 조립한다.
    /// </summary>
    private void UpdateLoopButton()
    {
        (string glyph, string state) = _loopMode switch
        {
            LoopMode.List => ("\uE8EE", $"Loop list: {CountLabel(_listLoopLimit)}"),
            LoopMode.File => ("\uE8ED", $"Repeat this file: {CountLabel(_fileLoopLimit)}"),
            _ => ("\uE8EE", "Loop: off"),
        };
        // A318: 끔만 합성 아이콘(같은 글리프 + 금지 사선), 나머지 둘은 글리프 그대로 —
        // 셋 다 같은 밝기·같은 도상 가족이라 서로 비교돼 읽힌다(영상과 같은 배선).
        LoopButton.Content = _loopMode == LoopMode.Off
            // 캐스트는 필수다 — Grid와 FontIcon은 서로 변환되지 않아 공통 타입을 명시해야 한다.
            ? (FrameworkElement)MediaIcons.BuildLoopOffIcon(glyph)
            : new FontIcon { Glyph = glyph, FontSize = 18 };
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
        // A349 ⏮/⏭: 툴팁만 직접 대입한다 — 키가 조합 2벌(Ctrl+←/→ 와 PageUp/Down)이라
        // 키 1개용인 HotkeySupport.Bind로는 표기를 만들 수 없고, 가속기는 이미 XAML의
        // UserControl.KeyboardAccelerators에 선언돼 있어 Register로 또 걸면 이중 등록이 된다.
        // 표기 형태(설명 + 괄호 안 키)는 HotkeySupport.Tip 규칙을 그대로 따른다(영상과 같은 문구).
        ToolTipService.SetToolTip(PrevButton, "Previous file (Ctrl+Left, Page Up)");
        ToolTipService.SetToolTip(NextButton, "Next file (Ctrl+Right, Page Down)");
        UpdateLoopButton(); // A11: 루프 아이콘·툴팁 초기값
    }
}
