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
using KOTU.Core.Integration;
using KOTU.Core.Navigation;
using KOTU.Core.Settings;
using KOTU.Core.Threading;
using KOTU.Input;
using KOTU.Ui;

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
    IContentStateSource, IContentInfoProvider, ITrayStatusProvider, IPlaybackStateSource
{
    /// <summary>파일 재생을 시작하면 셸에 알린다(v0.25.0 — 빈 상태 탐색기 내림·오버레이 기준 갱신).</summary>
    public event Action<string>? ContentOpened;

    /// <summary>재생/일시정지/종료 전이를 셸에 알린다(A186 — 하단 바 자동 숨김의 입력).</summary>
    public event Action? PlaybackStateChanged;

    /// <summary>영상 뷰 자신이 곧 재생 표면이다(A186 — IPlaybackStateSource).</summary>
    public bool HasPlaybackSurface => true;

    /// <summary>
    /// 지금 재생 중인가(A186). libvlc의 IsPlaying을 그대로 쓴다 — 발화 시점
    /// (Playing/Paused/EndReached 디스패치 뒤)에는 상태가 반영돼 있다.
    /// </summary>
    public bool IsPlaying => _player is { IsPlaying: true };

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
    /// A111(v0.133.0)에서 1:1 버튼이 사라져 **660** → A144에서 Fit이 84→64가 되어 **640** →
    /// A250(v0.246.0)에서 볼륨 슬라이더가 96→101이 되어 **645** →
    /// A269(v0.267.0)에서 우군 끝 0폭 스페이서 칸이 생겨 간격 6이 늘어 **651**.
    /// A144분 −20의 근거(TransportBar 요소 직접 계수 — 다른 요소의 폭은 무변경):
    ///   Fit 칸이 SplitButton 84 → 본체 32 + 화살표 32(같은 칸의 StackPanel, 간격 0) = 64.
    ///   지금 남은 고정 폭(칸 번호는 A216 재배열 이후): 루프 c0(32) · 재생 c1(32) ·
    ///   음소거 c5(32) · 볼륨 c6(101) · 배속 c7(84) · 자막 c8(32) · Fit c9(64) ·
    ///   0폭 스페이서 c10(0, A269) + 시간 텍스트 c2/c4 + 간격 6×10 — 합 437(시간 텍스트 제외).
    /// A250분 +5의 근거: 볼륨 슬라이더 96 → 101(0~100 = 값 101개. WinUI Slider 썸·패딩 때문에
    ///   "1px = 1단위"는 명목치다 — XAML VolumeSlider 주석 참조). 다른 요소·간격은 무변경이라
    ///   고정 폭 합과 임계가 나란히 +5다. 오디오 바도 같은 +5를 함께 받았다(아래 A217 항).
    /// A269분 +6의 근거(A217 정렬 등식의 첫 개정): 오디오 우군이 비주얼라이저 버튼 신설(A268)로
    ///   108 → 114가 되어, 이 바의 우군 끝에 0폭 스페이서 칸 c10을 더해 114로 맞췄다.
    ///   칸은 요소가 없어 0폭이고 늘어난 것은 ColumnSpacing 6 하나뿐이라 두 모듈이 나란히
    ///   431 → 437, 임계 645 → 651이다(오디오도 같은 값 — 스페이서를 두는 쪽만 반대로 바뀌었다).
    /// ⚠️ A151이 전체화면 칸(버튼 32 + 간격 6 = 38)을 제거했을 때 이 임계는 내리지 않았고
    ///   (660 유지) A144도 자기 몫 −20만 반영해 그 38이 여유분으로 남아 있었는데,
    ///   **A11(v0.211.0)의 루프 칸(32 + 간격 6 = 38)이 그 여유를 정확히 소진**했다 —
    ///   그래서 새 칸을 넣고도 당시 임계는 640 그대로였다(설계 docs/A11-playlist-design.md §4.1 판정).
    ///   A216(칸 재배열)·A217(오디오 정렬)은 이 바의 폭을 안 바꿨다 — 당시 임계 640 유지.
    /// A217(v0.229.0): 이 산식·임계·숨김 대상이 오디오 바의 정본이기도 하다 — 오디오도 고정 폭
    ///   합이 같은 값(A269 이후 437)으로 맞춰졌고(스페이서 회계는 오디오 XAML 헤더 주석)
    ///   동형 축약을 이식했다.
    ///   임계나 숨김 대상을 바꾸면 오디오 UpdateCompactTransport도 함께 바꿀 것 — 두 모듈이
    ///   다르게 숨기면 좁은 창에서 공통 클러스터 정렬이 다시 어긋난다.
    /// 숨겨도 기능은 남는다: 볼륨은 ↑/↓·휠·음소거 버튼, 재생 위치는 시크 슬라이더 썸 위치가 대신한다.
    /// ⚠️ 임계는 <b>TransportBar 자신의 폭</b> 기준이라(SizeChanged의 NewSize.Width) 셸의
    /// ModuleBarHost Margin은 이 값에 들어가지 않는다 — 여백이 바뀌어도 임계는 재계수 대상이 아니고,
    /// 바뀌는 것은 "창 폭 얼마에서 축약이 시작되는가"라는 파생 사실뿐이다.
    /// A305 배치 2 검산: 셸 모드 버튼이 2개가 되어 우측 여백 44→82 → 파일 모듈의 여백 합 82+82=164 →
    /// 축약 시작 창 폭이 약 777(651+126)에서 약 <b>815</b>(651+164)로 올라간다.
    /// 최소 창 720에서는 바 폭이 약 556이라 전후 모두 축약 상태다(동작 변화 없음).
    /// 위 첫 문단의 "최소 창 폭 720(바 폭 약 656)"은 여백 합이 64이던 A40 당시의 실측 기록이다.
    /// </summary>
    private void UpdateCompactTransport(double width)
    {
        // A249 예외: 폭 임계 축약은 "숨김 금지" 정책의 확정 예외다(공간 제약 — 2026-08-27 사용자
        // 답변). 볼륨은 숨어도 ↑/↓·휠·음소거 버튼으로 접근이 유지된다.
        var visibility = width < 651 ? Visibility.Collapsed : Visibility.Visible;
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

    // ---------- A11(v0.211.0) → A255(v0.255.0) 재생 목록 루프 상태 (설계 docs/A11-playlist-design.md §3) ----------
    // A255(2026-08-27 사용자 확정): 구 2축(Loop list 체크 × Repeat this file 라디오 — 우선순위
    // 결합)을 단일 모드(상호 배타)로 개편. 본체 클릭·L 키 = 루프 없음 → 목록 루프 → 한 파일
    // 루프 순환(진입 시 횟수는 기본 ∞), 두 모드 모두 반복 횟수(1×/3×/∞)를 우클릭 플라이아웃에서
    // 가진다. 기본값 = 루프 없음(구 "목록 루프 기본 켬" 폐기 — 일반 플레이어 관례).
    // 저장 = 신 키 loopMode(off/list/file) + 모드별 횟수(loopCount = 한 파일 · loopListCount =
    // 목록). 구 키 loopList·loopCurrent는 소비처만 제거하고 값은 무해 잔존(A174 선례) —
    // 신 키가 없을 때만 생성자에서 1회 해석해 이행한다(그곳의 매핑 주석 참조).
    // 저장은 전역 1벌·즉시 Set+Save(EQ 선례), 창 간 실시간 전파 없음 — 상태는 로컬 소유(_muted 규칙).
    // [오디오 동형 이식 완료 — v0.255.0] 키 접두사만 audio.* 로 바꿔 그대로 복제한다.

    private const string LoopModeKey = "video.loopMode";           // "off"·"list"·"file" — A255 신설
    private const string LoopCountKey = "video.loopCount";         // 한 파일 루프 횟수 "1"·"3"·"infinite" — 구 Repeat 횟수 키를 의미 그대로 재사용
    private const string LoopListCountKey = "video.loopListCount"; // 목록 루프 횟수 — A255 신설(같은 문자열 enum)
    private const string LegacyLoopListKey = "video.loopList";     // 구 키 — 이행 해석 전용(쓰기 없음)
    private const string LegacyLoopCurrentKey = "video.loopCurrent"; // 구 키 — 이행 해석 전용(쓰기 없음)

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

    public VideoPlayerView(OpenContext context, ISettingsService settings)
    {
        InitializeComponent();
        // A310: 플라이아웃 "Original" 항목의 아이콘도 본체 1:1 상자와 같은 파일(Shared/FitIcons)이
        // 그린다 — 종전에는 XAML 인라인 도형이라 본체와 미세하게 다른 그림이었다(사용자 보고).
        // MenuFlyoutItem.Icon은 IconElement만 받아 본체의 Border를 못 꽂으므로, 같은 치수표로
        // 그린 PathIcon 판본을 받는다. 호출마다 새 인스턴스라 v0.174.1의 공유 크래시와 무관하다.
        FitOriginalItem.Icon = FitIcons.BuildOriginalRatioIcon();
        _settings = settings;
        _resumeStore = new PlaybackResumeStore(settings);
        _filePath = context.FilePath is { } p && File.Exists(p) ? p : null;

        foreach (var s in Speeds)
            SpeedBox.Items.Add($"{s:0.##}×");
        SpeedBox.SelectedIndex = Array.IndexOf(Speeds, 1.0f);
        FillSubtitleFlyout(); // "No subtitles"만 있는 초기 상태

        // A255: 루프 설정 읽기(생성자 1회 — _muted 규칙)와 플라이아웃 배선은 SetupHotkeys보다
        // 먼저다 — UpdateLoopButton(툴팁 초기값)이 상태를 읽는다.
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
        // 새로 채운다(오디오 장치 플라이아웃의 Opening 재구성 관례).
        LoopButton.ContextFlyout = _loopFlyout;
        _loopFlyout.Opening += (_, _) => BuildLoopFlyout();

        SetupHotkeys();       // A34: 하단 바 버튼 핫키 + 툴팁 표기

        _suppressVolumeEvent = true;
        VolumeSlider.Value = Math.Clamp(_settings.Get("video.volume", 80), 0, 100);
        _suppressVolumeEvent = false;

        if (_filePath is null)
            PlaceholderText.Visibility = Visibility.Visible;

        // A230 → A249: 빈 상태(_filePath 없음)면 Fit 조절기를 비활성으로 시작한다 — XAML 초기값이
        // 이미 IsEnabled=False라 파일 인자가 없으면 무동작이고, 있으면 여기서 곧바로 켠다
        // (첫 프레임 정합. 버튼은 두 경우 모두 보인다).
        UpdateFitEnabled();

        Vlc.Initialized += OnVlcInitialized;
        Loaded += (_, _) => Focus(FocusState.Programmatic);
        Unloaded += OnUnloaded;

        // A306: 화면 꺼짐 억제 설정이 바뀌면 재생 중이어도 그 자리에서 반영한다(다음 재생부터가
        // 아니다). 알림은 다른 창의 설정 화면에서 오지만 UI 스레드가 하나라 그대로 이 스레드다
        // (SettingsView의 UiScale.Changed 구독과 같은 형태 — 해제는 OnUnloaded에서).
        PlaybackSettings.KeepDisplayAwakeChanged += UpdateDisplayAwake;

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

    /// <summary>
    /// 현재 _filePath를 처음부터(또는 이어보기 지점부터) 재생한다. 플레이어 준비 후에만 호출.
    /// autoAdvance = EOF 자동 진행(AdvanceAfterEnd 전이 2·3)에서 온 호출 — A255 목록 순환
    /// 카운터(_listLoops)를 잇는다. 그 외(셸 열기·드롭·▶ 재시작·테스트 클립)는 전부 수동 개입 =
    /// 카운터 재출발(확정: 자동 진행만 순환 예산을 소모한다).
    /// </summary>
    private void PlayCurrent(bool autoAdvance = false)
    {
        if (_player is not { } p || _libVlc is not { } lib || _filePath is null) return;

        _durationMs = 0;
        _lastReportedMs = 0;
        _loopPlays = 0; // A11: 재생 단위가 새로 시작되면 리핏 카운터 리셋 — AdvanceAfterEnd 전이 1만 증가시킨다
        if (!autoAdvance) _listLoops = 0; // A255: 수동 개입 = 목록 순환 카운터 리셋(위 요약 주석)
        _pendingResumeMs = IsTestClip(_filePath)
            ? -1
            : _resumeStore.GetResumePositionMs(_filePath) ?? -1;
        LoadSubtitleList();
        EnsurePlaylist(); // A11: 폴더 재생 목록 준비(워커행 — 자막 탐지와 같은 관용구)

        using var media = new Media(lib, new Uri(_filePath));
        _pendingStartOverlay = true; // A12: 실제 재생이 시작되면(Playing) 오버레이 표시
        p.Play(media);
        PlaceholderText.Visibility = Visibility.Collapsed;
        ContentOpened?.Invoke(_filePath); // 셸 동기화 (v0.25.0)
        TrayStatusChanged?.Invoke();      // A54: 유휴("VID") → 열림. 값은 파싱되는 대로 다시 올라온다
    }

    /// <summary>
    /// A11(설계 §2.3): 리핏 전용 재장전. Ended에서는 Play()만으로 재시작이 안 되므로
    /// (TogglePlayPause Ended 분기의 실검증 선례) PlayCurrent처럼 미디어를 다시 걸되,
    /// 매 루프마다 나면 소음인 부작용 3건을 뺀 변형이다 — 원형(PlayCurrent)은 고치지 않는다:
    /// ① A12 시작 오버레이(_pendingStartOverlay) 생략 — 같은 파일의 재표시는 소음.
    /// ② LoadSubtitleList 생략 — FillSubtitleFlyout이 _subtitleIndex를 리셋해 사용자의
    ///    자막 선택(끄기 포함)이 매 루프 초기화된다. 대신 _pendingAutoSubtitle만 세워
    ///    Playing 핸들러가 현재 _subtitleIndex를 그대로 재적용하게 한다.
    /// ③ ContentOpened 생략 — 같은 파일이라 셸 동기화(S4 종료·오버레이·아이콘)는 전부 무의미한 재계산.
    /// 이어보기 조회도 생략(_pendingResumeMs = -1 고정) — EndReached가 기록을 이미 지웠고
    /// (97% 정책) 리핏은 항상 0초 시작이다. 배속·Fit 재적용은 기존 Playing 핸들러가 잇는다.
    /// [오디오 동형 이식 완료 — v0.212.0] 오디오는 A12 오버레이·자막이 없어 ①②가 빠져 더 단순하다.
    /// </summary>
    private void ReplayCurrent()
    {
        if (_player is not { } p || _libVlc is not { } lib || _filePath is null) return;

        _durationMs = 0;
        _lastReportedMs = 0;
        _pendingResumeMs = -1;
        _pendingAutoSubtitle = true; // Playing 핸들러가 현재 _subtitleIndex를 재적용(위 ②)

        using var media = new Media(lib, new Uri(_filePath));
        p.Play(media);
    }

    /// <summary>
    /// A11: 현재 파일의 폴더 재생 목록(같은 폴더 스냅샷 — 감시 없음, 이미지 선례와 동일)을
    /// 준비한다. 폴더 스캔은 파일당 속성 읽기가 있어 UI 스레드 금지(§11.1) — 자막 탐지와 같은
    /// Worker.Run 관용구로 돌리고, 결과가 오기 전에 파일이 바뀌었으면 버린다.
    /// 목록 진행(OpenPath)으로 온 파일은 이미 목록의 현재 항목이라 재스캔하지 않는다.
    /// 테스트 클립은 목록 대상이 아니다(Assets 폴더 순회는 무의미 — 이어보기 제외와 같은 성질).
    /// 스캔이 끝나기 전에 EOF가 오면 그 회차는 "목록 없음"으로 판정된다(아주 짧은 클립 +
    /// 느린 네트워크 폴더의 희귀 경합 — 수용, 다음 EOF부터 정상).
    /// [오디오 동형 이식 완료 — v0.212.0] AudioModule.Extensions로 바꾸면 그대로 복제된다.
    /// </summary>
    private async void EnsurePlaylist()
    {
        var file = _filePath!;
        if (IsTestClip(file))
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
            list = await Worker.Run(_ => FolderPlaylist.Create(file, VideoModule.Extensions));
        }
        catch
        {
            return; // 목록 생성 실패가 재생을 방해하면 안 된다(자막 탐지와 같은 규칙)
        }

        if (_tornDown || file != _filePath) return; // 그새 다른 파일로 전환됨
        _playlist = list;
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

    /// <summary>autoAdvance는 PlayCurrent로 중계만 한다(A255 — EOF 자동 진행 표시).</summary>
    private async void OpenPath(string path, bool autoAdvance = false)
    {
        if (!File.Exists(path)) return;

        // 보던 파일이 있으면 위치를 저장하고 전환한다.
        if (_player is { } p && _filePath is not null && !IsTestClip(_filePath) && _durationMs > 0)
        {
            try { _resumeStore.Report(_filePath, p.Time, _durationMs); }
            catch { /* 저장 실패가 전환을 막으면 안 된다 */ }
        }

        _filePath = path;

        // A230 → A249: 빈 상태 → 재생 파일 확보 = Fit 조절기 활성. _filePath가 null이 아니게 되는
        // 지점은 생성자와 여기 둘뿐이고(닫기 경로 없음 — EOF/Ended도 파일은 그대로다) 두 곳 다
        // 갱신한다. 테스트 클립(A207 — 빈 상태 ▶)도 이 경로로 들어오므로 함께 켜진다(재생 중인 콘텐츠다).
        UpdateFitEnabled();

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

        if (_player is not null) PlayCurrent(autoAdvance);
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

        // A306: 정적 이벤트 구독 해제(안 하면 해체된 뷰가 산다)와 억제 해제. _tornDown이 이미
        // 서 있어 UpdateDisplayAwake는 무조건 "해제"로 판정한다 — 걸어 둔 적이 없으면 무동작이다.
        PlaybackSettings.KeepDisplayAwakeChanged -= UpdateDisplayAwake;
        UpdateDisplayAwake();

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
        PlaybackStateChanged?.Invoke(); // A186: 재생 시작 — 셸이 자동 숨김 카운트를 시작한다
        UpdateDisplayAwake();           // A306: 재생 중 = 화면 꺼짐 억제(설정이 켜져 있을 때)

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
        Dispatch(() =>
        {
            PlayButton.Content = "▶";
            PlaybackStateChanged?.Invoke(); // A186: 일시정지 = 바 상시 표시
            UpdateDisplayAwake();           // A306: 일시정지 = 억제 해제
        });

    private void OnPlayerEndReached(object? sender, EventArgs e)
    {
        // 끝까지 봤으면 이어보기 기록을 지운다(97% 정책). (이 콜백 안에서 Stop()을 부르면 교착 — 금지)
        // A11: 이 삭제는 루프 전이와 무관하게 유지한다 — 다 본 파일의 기록 청소는 별개 사실이고,
        // 바로 이 삭제가 목록 진행·리핏 후 이 파일을 다시 열 때 0초 시작을 보장한다(설계 §3.3).
        if (_filePath is not null && !IsTestClip(_filePath) && _durationMs > 0)
            _resumeStore.Report(_filePath, _durationMs, _durationMs);

        // A11(v0.211.0): 구 승계 메모의 예언대로 EOF 전이가 여기 얹혔다. 전이 판정·재생 API는
        // 전부 UI 스레드에서(libvlc 콜백 안 재생 API 직접 호출 금지 — Dispatch 경유),
        // Dispatch 사이에 사용자가 개입했을 수 있어 AdvanceAfterEnd가 파일·상태를 재검사한다.
        // A130의 잔상 정리는 "Ended 상태 + 크기 변경" 조건(ClearEndedFrameOnResize)에 걸려 있어
        // 루프·목록 진행 전이는 Ended에 머물지 않아 자연 비활성이고, 정지 전이만 Ended에 머물러
        // 종전 트레이드오프가 그대로 유효하다(설계 §3.3 전이표의 A130 열). A255 개정으로 정지
        // 전이의 사유가 "목록 루프 끔·끝"에서 "루프 없음(또는 횟수 소진)·목록 끝"으로 바뀌었을
        // 뿐, "Ended에 머무는 것은 정지 전이뿐" 조건 자체는 그대로다.
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
    /// 끝→처음 되감기 1회 = 목록 총 2회 재생. 매 회 0:00 시작(이어보기 무시 — ReplayCurrent와
    /// EndReached의 기록 삭제 규칙 그대로 유지).
    /// 전이 1~4는 Ended에 머물지 않으므로 종전 EndReached UI 갱신
    /// (▶ 표기·시크바 끝·PlaybackStateChanged)을 생략한다 — 곧 Playing이 덮어써 깜빡임만 만들고,
    /// Ended 신호와 Playing 신호의 이중 재평가(A186)도 없앤다. 정지(전이 5)만 종전 갱신 그대로다.
    /// EncounteredError는 전이 트리거가 아니다(실패 파일 자동 스킵은 무한 실패 루프 위험 — 별도
    /// 설계 대상, §3.3). UI 스레드 전용.
    /// A258(v0.258.0): 위 "루프 없음 = 다음 파일"에 설정 게이트가 하나 붙었다 — 설정의
    /// "Auto-play next file"을 끄면 <b>루프 없음일 때만</b> 목록 진행 대신 정지(전이 5)한다.
    /// 루프 모드가 켜져 있으면 옵션과 무관하게 종전 전이 그대로다(아래 게이트 주석 참고).
    /// [오디오 동형 이식 완료 — v0.255.0 / A258 게이트도 동형 이식 — v0.258.0]
    /// 오디오에는 PlaybackStateChanged가 없다 — 그 줄만 빼고 복제.
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
        // OpenPath = 기존 완결 경로 재사용(설계 §2.2 경로 B) — 이어보기 저장·Contain 회귀·
        // PlayCurrent·ContentOpened 셸 동기화(트레이·A174·A186 자동 숨김)까지 전부 따라온다.
        // 신규 셸 배선 0(설계 §5).
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

        // 전이 5: 정지 — 종전 EndReached UI 갱신 그대로(유일하게 Ended에 머무는 경로 —
        // A130 잔상 정리가 여기서만 계속 유효하다). A255: 루프 없음·목록 끝 외에 "횟수 소진"
        // (목록 루프 되감기 예산 소진, 한 파일 소진 후 다음 파일 없음)도 이 경로로 온다.
        // A258: "루프 없음 + Auto-play next file 끔"도 이 경로다 — 목록 중간 파일이어도 여기서
        // 멈추므로 ▶ 표기·시크바 끝 갱신을 반드시 거쳐야 한다(이 블록을 건너뛰는 정지 금지).
        PlayButton.Content = "▶";
        PositionText.Text = TimeText.Format(_durationMs);
        _suppressSeekEvent = true;
        SeekSlider.Value = SeekSlider.Maximum;
        _suppressSeekEvent = false;
        PlaybackStateChanged?.Invoke(); // A186: 재생 종료(Ended) = 바 상시 표시
        UpdateDisplayAwake();           // A306: 정지 = 억제 해제(루프·목록 진행 전이는 곧 Playing이라 여기 안 온다)
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
    /// A11(v0.211.0)부터 Ended에 머무는 것은 정지 전이(AdvanceAfterEnd 전이 5 — A255 개정 후
    /// 사유 = 루프 없음·목록 끝 또는 횟수 소진)뿐이라 이 정리도 그 경로에서만 발동한다.
    /// 루프·목록 진행 전이의 짧은 Ended 구간
    /// (EndReached와 Dispatch 사이)에 SizeChanged가 오면 Clear가 한 번 돌 수 있으나, 이어지는
    /// Playing의 ApplyFitMode가 재도장하므로 무해하다(설계 §3.3 — A130 수리 주석과 같은 논리).
    /// </summary>
    private void ClearEndedFrameOnResize()
    {
        if (_player is { } p && p.State == VLCState.Ended)
            Vlc.Clear();
    }

    private void OnPlayerError(object? sender, EventArgs e)
    {
        ShowMessage($"Playback failed: {Path.GetFileName(_filePath ?? string.Empty)}");
        Dispatch(() =>
        {
            PlaybackStateChanged?.Invoke(); // A186: 재생 실패 = 정지와 동일(바 상시 표시)
            UpdateDisplayAwake();           // A306: 같은 취급 — 억제 해제
        });
    }

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

    // ---------- A306 화면보호기·디스플레이 꺼짐 억제 ----------

    /// <summary>
    /// A306: 이 뷰가 지금 억제를 걸어 두었는가. 훅(<see cref="DisplayAwakeHook"/>)은 개수만 세므로
    /// Acquire/Release를 1:1로 짝지을 책임은 이 플래그에 있다 — 모든 전이는
    /// <see cref="UpdateDisplayAwake"/> 한 곳을 지나며 이 값과 목표 상태를 대조해
    /// <b>필요한 호출만</b> 낸다(재생 → 일시정지 → 재생을 반복해도 카운트가 새지 않는다).
    /// </summary>
    private bool _displayAwakeHeld;

    /// <summary>
    /// A306: "설정이 켜져 있고 + 지금 실제로 재생 중"이면 억제를 걸고, 아니면 푼다.
    /// 호출 지점(전부 UI 스레드 — SetThreadExecutionState가 스레드 단위라 필수 조건이다):
    ///   ① Playing 디스패치      — 재생 시작·일시정지 해제·다음 파일·루프 재시작 전부 여기로 온다
    ///   ② Paused 디스패치       — 일시정지
    ///   ③ EOF 정지 전이(전이 5) — 루프·목록 진행 전이는 곧 Playing이 오므로 건드리지 않는다
    ///                             (여기서 풀었다 곧바로 다시 거는 깜빡임을 만들지 않기 위함)
    ///   ④ EncounteredError      — 실패 = 정지와 같은 취급(A186 규칙과 같은 축)
    ///   ⑤ Unloaded              — 뷰(창) 해체. _tornDown이 이미 서 있어 무조건 해제로 판정된다
    ///   ⑥ 설정 변경 알림        — 재생 중에 꺼도 그 자리에서 풀린다(다음 재생부터가 아니다)
    /// 값은 캐시하지 않고 전이마다 라이브로 읽는다(설정 절의 관례).
    /// 재생 여부는 <see cref="IsPlaying"/>(libvlc IsPlaying) — Playing/Paused/EndReached 디스패치
    /// 시점에는 상태가 이미 반영돼 있다(그 속성 주석의 A186 근거 그대로).
    /// </summary>
    private void UpdateDisplayAwake()
    {
        var want = !_tornDown && IsPlaying &&
            _settings.Get(PlaybackSettings.KeepDisplayAwakeKey,
                PlaybackSettings.KeepDisplayAwakeDefault);
        if (want == _displayAwakeHeld) return;

        _displayAwakeHeld = want;
        if (want) DisplayAwakeHook.Acquire();
        else DisplayAwakeHook.Release();
    }

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

    // ---------- A255 루프 모드·횟수 (본체 클릭 = 모드 순환 · 우클릭 플라이아웃 = 횟수) ----------

    /// <summary>
    /// 루프 플라이아웃 구성 — A255: 본체 클릭이 모드 순환으로 바뀌어 플라이아웃은 우클릭
    /// (ContextFlyout)으로 연다. 구 "Loop list 토글 + Repeat 라디오 4택"을 라디오 7택 한
    /// 그룹(끔 1 + 모드 2 × 횟수 3)으로 바꿔 체크 하나가 곧 현재 상태다(상호 배타 그대로).
    /// 버튼 순환·구 키 이행으로도 상태가 바뀌므로 매 열림(Opening)마다 다시 채운다 —
    /// 오디오 장치 플라이아웃 관례(1회 구성이던 구판과 달라진 점).
    /// [오디오 동형 이식 완료 — v0.255.0] 문자열·구성 동일, 설정 키만 audio.* 로 바꾼다.
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
    /// 라디오 1개 추가 — 자막의 AddSubtitleChoice 관용구. 횟수를 고르면 그 모드로의 전환을
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
    /// 루프 버튼 본체를 상태형으로 갱신 — UpdateFitButton 관용구(아이콘 + 툴팁을 상태에서 만든다).
    /// A255 3상태 표지: 루프 없음 = E8EE 흐림(Opacity 0.4 — 끔 표지 확정값. 빗금 도형 안은
    /// v0.174.1 Geometry 공유 크래시 함정이라 기각) / 목록 루프 = E8EE(RepeatAll) 불투명 /
    /// 한 파일 루프 = E8ED(RepeatOne). 툴팁도 3상태 + 횟수 병기에 우클릭 안내를 덧붙인다
    /// (횟수 플라이아웃 진입이 우클릭뿐이라 이 표기가 유일한 발견 경로다).
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
        // 아무것도 열지 않은 상태의 ▶ = 내장 테스트 클립 재생 (화면 색감 + 스피커 점검).
        // A207: 반드시 _player 가드보다 앞이다 — 빈 모듈 상태(S1 중앙 탐색기)에서는 스왑체인
        // 초기화(OnVlcInitialized → _player 생성)가 아직 안 끝났을 수 있어, 종전 가드 순서로는
        // ▶가 침묵 무동작이었다(회귀 원인). OpenPath는 플레이어가 없으면 _filePath만 걸어 두고
        // OnVlcInitialized 말미의 PlayCurrent()가 잇는다(파일 열기 정상 경로와 동일 훅) —
        // 스왑체인 없이 Play를 직접 부르는 경로는 생기지 않는다.
        if (_filePath is null)
        {
            if (File.Exists(TestClipPath)) OpenPath(TestClipPath);
            else ShowMessage(@"Test clip not found (Assets\test-clip.mp4)");
            return;
        }

        if (_player is not { } p) return;

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
    /// A184: 그 PathIcon 도형을 글자 "1:1" 형상에서 꺾쇠 프레임으로 바꿨다.
    /// A231(3차): 도형 자체를 폐기하고 <b>소형 텍스트 "100%"</b>로 갔다 — 2026-08-25
    /// "무슨 아이콘인지 알 수 없다"는 사용자 재보고 때문이다. 본체는 Button이라 Content에
    /// TextBlock을 넣을 수 있다(문서 모듈 A145의 비활성 표시와 같은 방식 — IconElement만 받는
    /// MenuFlyoutItem.Icon과 다르다).
    /// A253(4차): 옵션 이름을 "100%" → <b>"Original"</b>로 바꾸고(플라이아웃 항목 텍스트),
    /// 본체 상태 표시는 그 약자 "OR" 두 글자로 갔다(2026-08-27 사용자 지시).
    /// A260(5차·확정): 그 "OR"을 <b>테두리 상자 안의 "1:1"</b>로 바꾼다(2026-08-27 사용자 지시 —
    /// 약자보다 배율 기호가 즉시 읽힌다. A143의 "1:1" 글자가 상자를 얻어 돌아온 형태다).
    /// 조립은 <see cref="FitIcons.BuildOriginalRatioBox"/> 한 곳 — 본체는 Button이라 Content에 임의
    /// UIElement를 넣을 수 있다(IconElement만 받는 MenuFlyoutItem.Icon과 다르다).
    /// 툴팁 "Original size"·항목 이름 "Original"·A 키 표기는 A253 그대로 무변경.
    /// A298: 그 조립을 세 모듈이 복제해 갖던 것을 공용 파일 하나로 모았다(Shared/FitIcons.cs —
    /// csproj Compile Link 공유. HotkeySupport와 같은 방식). 디자인을 고칠 곳은 이제 그 한 곳이다.
    /// A299: 비활성일 때 테두리도 글자와 함께 회색이 된다(수리 내용은 그 팩토리 주석).
    /// A310: 플라이아웃 "Original" 항목도 같은 파일의 PathIcon 판본
    /// (<see cref="FitIcons.BuildOriginalRatioIcon"/>)을 쓴다 — 대입은 생성자 한 줄이다.
    /// </summary>
    private void UpdateFitButton()
    {
        (object content, string tip) = _lastFitOption switch
        {
            VideoFitMode.FitWidth =>
                ((object)new FontIcon { Glyph = "\uE8AB", FontSize = 18 }, "Fit width"),
            VideoFitMode.FitHeight =>
                (new FontIcon { Glyph = "\uE8CB", FontSize = 18 }, "Fit height"),
            VideoFitMode.ActualSize =>
                (FitIcons.BuildOriginalRatioBox(), "Original size"),
            _ => (new FontIcon { Glyph = "\uE9A6", FontSize = 18 },
                "Contain - the whole video fits, never enlarged"),
        };
        FitButton.Content = content;
        ToolTipService.SetToolTip(FitButton, FitTip(tip)); // A34: 표기는 키 상수에서
    }

    /// <summary>
    /// 본체 툴팁 = "지금 표시 중인 옵션 (F) · Original (A)" — 1:1 버튼이 사라져도 A 키 표기가
    /// 남게 병합한다(A111). 두 표기 모두 키 상수에서 조립한다(A34 표기 규칙).
    /// A253: 뒤쪽 표기어가 "100%" → "Original"(플라이아웃 항목 이름과 같은 말로 통일).
    /// </summary>
    private static string FitTip(string description) =>
        $"{HotkeySupport.Tip(description, FitKey)} · {HotkeySupport.Tip("Original", ActualSizeKey)}";

    /// <summary>
    /// A230(v0.234.0) → A249(v0.246.0, 정면 반전): Fit 조절기 2개(본체 + 화살표)를 빈 상태에서
    /// 접던 것을 되돌려 <b>표시는 늘 유지하고 활성만</b> 재판정한다(2026-08-27 사용자 지시 —
    /// "현재 모듈에서 쓰는 버튼류는 항상 표시, 사용 불가면 비활성으로만").
    /// 판정 축은 A230 그대로 <see cref="_filePath"/> 하나(이 뷰의 "파일 있음" 정본. EOF/Ended는
    /// 닫힘이 아니라 파일이 그대로 걸려 있어 조절기도 활성으로 남는다). 호출 지점 = 생성자 ·
    /// OpenPath 두 곳이 전부다(그 밖에 _filePath가 바뀌는 자리는 없다).
    /// A·F 키 차단은 가시성이 아니라 여기서 세우는 IsEnabled가 진다 — HotkeySupport의 통과
    /// 게이트가 버튼의 <c>IsEnabled</c>와 <c>Visibility</c>를 <b>둘 다</b> 보기 때문에
    /// (Shared/HotkeySupport.cs:61) 비활성만으로도 키가 새지 않는다(A230이 "부모가 아니라 버튼
    /// 각각을 접는다"고 못 박았던 가시성 게이트 근거를 IsEnabled 게이트가 대체한다).
    /// 자막·루프·▶ 등 빈 상태에도 쓰이는 버튼(A207)은 범위 밖 — 손대지 않는다.
    /// </summary>
    private void UpdateFitEnabled()
    {
        var enabled = _filePath is not null;
        FitButton.IsEnabled = enabled;
        FitOptionsButton.IsEnabled = enabled;
    }

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
    /// A11 루프 키(설계 §4.1) → A255: 플라이아웃 열기에서 모드 순환(본체 클릭과 동일)으로 개정.
    /// 툴팁 표기(UpdateLoopButton)와 액셀러레이터가 이 한 값을 쓴다. 영상 기사용 문자 키
    /// M·S·C·A·F·오디오 M·S·셸 전역 키 어디와도 충돌 없음(설계 대조 — L 선점 0건).
    /// </summary>
    private const VirtualKey LoopKey = VirtualKey.L;

    /// <summary>
    /// A34: 하단 바 버튼에 단독 문자 키를 걸고 툴팁 "(키)" 표기까지 같은 호출에서 만든다.
    /// 텍스트 입력·탐색기 파일 리스트 포커스에서는 HotkeySupport가 키를 통과시킨다(A32/A84 규칙).
    /// M(음소거)은 v0.21.0부터 있던 키를 XAML 액셀러레이터에서 여기로 옮긴 것 — 의미는 그대로다.
    /// 플라이아웃형(S 배속·C 자막)은 누르면 목록이 열리고, Fit(F)·100%(A)·루프(L — A255부터
    /// 모드 순환)는 즉시 적용이다.
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
        // A255: L = 루프 모드 순환(본체 클릭과 동일 — 구 "플라이아웃 열기"에서 개정. 횟수
        // 플라이아웃은 우클릭 전용이 됐다). 툴팁이 상태형이라 Bind가 아닌 Register —
        // 표기는 UpdateLoopButton()이 같은 키 상수(LoopKey)로 조립한다(Fit과 같은 규칙).
        HotkeySupport.Register(this, LoopButton, LoopKey, CycleLoopMode);
        UpdateFitButton();  // Fit 툴팁은 표시 상태를 따라가므로 초기값도 여기서 만든다
        UpdateLoopButton(); // A11: 루프 아이콘·툴팁 초기값 — 같은 이유
    }
}
