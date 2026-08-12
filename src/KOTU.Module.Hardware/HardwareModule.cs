using KOTU.Core.Contracts;
using KOTU.Core.Settings;
using KOTU.Core.Threading;

namespace KOTU.Module.Hardware;

/// <summary>폴러 스냅샷: WMI 스펙 섹션 + 센서 프레임(A17). 섹션은 2초 캐시라 참조가 재사용된다.</summary>
public sealed record HardwareSnapshot(IReadOnlyList<HardwareSection> Sections, SensorFrame Sensors);

/// <summary>하드웨어 모듈 (Phase 5a 정보 표시 + 5b 센서 그래프, A17). 파일을 다루지 않으므로 담당 확장자는 없다.</summary>
public sealed class HardwareModule : IModule
{
    /// <summary>리프레시 주기 선택지(A29, ms). v0.51.0 고정 200ms를 대체 — 기본 300(사용자 확정).</summary>
    internal static readonly int[] RefreshChoices = [100, 300, 1000];

    internal const int DefaultRefreshMs = 300;

    internal const string RefreshSettingKey = "hardware.refreshMs";

    /// <summary>초기 폴러 주기 — 모듈 등록 시 설정값으로 덮어쓴다(A29).</summary>
    internal static readonly TimeSpan AutoRefreshInterval = TimeSpan.FromMilliseconds(DefaultRefreshMs);

    private static ISettingsService? _settings; // 주기 저장용(A29) — 모듈 등록 시 주입

    /// <summary>WMI 스펙 재수집 간격 — 스펙은 거의 안 변하므로(디스크 여유 공간 정도) 매 주기 돌릴 이유가 없다.</summary>
    private static readonly TimeSpan SpecRefreshInterval = TimeSpan.FromSeconds(2);

    // 아래 상태는 전부 폴러 스레드에서만 읽고 쓴다
    // (_snapshotSubscribers만 구독/해지 스레드에서 Interlocked로 증감).
    private static IReadOnlyList<HardwareSection> _sections = [];
    private static DateTime _sectionsAt;
    private static int _snapshotSubscribers;

    /// <summary>트레이 센서 선택(A18)·리프레시 주기(A29)는 설정에서 복원해야 하므로 모듈 등록 시 주입받는다.</summary>
    public HardwareModule(ISettingsService settings)
    {
        TraySensors.Initialize(settings);
        _settings = settings;
        var ms = settings.Get(RefreshSettingKey, DefaultRefreshMs);
        if (!RefreshChoices.Contains(ms)) ms = DefaultRefreshMs; // 구버전 200 등 목록 밖 값 정리
        Poller.Interval = TimeSpan.FromMilliseconds(ms);
    }

    /// <summary>현재 리프레시 주기(ms) — 하단 바 버튼 표기용(A29).</summary>
    internal static int RefreshMs => (int)Poller.Interval.TotalMilliseconds;

    /// <summary>주기 변경(A29): 폴러에 즉시 반영(대기 중 간격 건너뜀) + 설정 저장.</summary>
    internal static void SetRefreshMs(int ms)
    {
        Poller.Interval = TimeSpan.FromMilliseconds(ms);
        if (_settings is { } settings)
        {
            settings.Set(RefreshSettingKey, ms);
            settings.Save();
        }
    }

    /// <summary>
    /// 프로세스 공유 폴러(A42 결정: Hardware만 공유, 창 여러 개여도 수집은 1회).
    /// 매 주기(100/300/1000ms 선택, A29) 센서 한 프레임을 읽고, WMI 스펙은 2초마다만 재수집한다.
    /// 구독이 없으면(뷰도 트레이도) 휴면하므로 그때 비용은 0. BelowNormal 우선순위라
    /// 재생·UI와 CPU를 다투지 않는다. 수집 스레드는 여기 하나다.
    /// </summary>
    internal static PollingWorker<HardwareSnapshot> Poller { get; } =
        new("KOTU hardware poller", AutoRefreshInterval, Poll);

    /// <summary>
    /// 뷰 구독(스펙 + 센서). 이 구독이 하나라도 있어야 WMI 스펙 재수집이 돈다 —
    /// 트레이만 살아 있을 때(A18 상시 표시) 스펙 WMI 질의를 2초마다 낭비하지 않기 위한 구분.
    /// </summary>
    public static IDisposable SubscribeSnapshots(Action<HardwareSnapshot> handler)
    {
        Interlocked.Increment(ref _snapshotSubscribers);
        return new SnapshotSubscription(Poller.Subscribe(handler));
    }

    /// <summary>
    /// 센서 전용 구독(A18 트레이). 폴러는 깨우되 스펙(WMI) 수집은 유발하지 않는다.
    /// 핸들러는 워커 스레드에서 불린다 — UI 반영은 구독자가 디스패치할 책임.
    /// </summary>
    public static IDisposable SubscribeSensors(Action<SensorFrame> handler)
        => Poller.Subscribe(snapshot => handler(snapshot.Sensors));

    private static HardwareSnapshot Poll()
    {
        var now = DateTime.UtcNow;
        // 스펙은 뷰 구독자가 있을 때만 수집한다. 수동 Refresh(RefreshNow)는 A75에서
        // 버튼과 함께 제거 — 주기 폴링이 이미 최신 상태를 유지한다.
        // 트레이 전용 기간엔 마지막 섹션을 그대로 실어 보낸다(트레이는 Sensors만 쓴다).
        var wantSpecs = Volatile.Read(ref _snapshotSubscribers) > 0;
        if (wantSpecs && (_sections.Count == 0 || now - _sectionsAt >= SpecRefreshInterval))
        {
            _sections = HardwareInfoService.Collect();
            _sectionsAt = now;
        }
        return new HardwareSnapshot(_sections, SensorService.Read());
    }

    /// <summary>뷰 구독 해지 토큰 — 스펙 수집 필요 카운트를 내리고 폴러 구독을 끊는다.</summary>
    private sealed class SnapshotSubscription(IDisposable inner) : IDisposable
    {
        private IDisposable? _inner = inner;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _inner, null) is { } subscription)
            {
                subscription.Dispose();
                Interlocked.Decrement(ref _snapshotSubscribers);
            }
        }
    }

    public string Id => "hardware";

    public string DisplayName => "H/W Info"; // v0.28.1 사용자 요청 (이전: Hardware-info)

    public string BrandName => "KOTU-info";

    public string IconGlyph => "\uE950"; // Component (칩 모양)

    public IReadOnlyList<string> SupportedExtensions => [];

    public object CreateView(OpenContext context) => new HardwareView(context);
}
