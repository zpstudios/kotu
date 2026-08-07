using WinUtil.Core.Contracts;
using WinUtil.Core.Threading;

namespace WinUtil.Module.Hardware;

/// <summary>하드웨어 모듈 (Phase 5a: 정보 표시). 파일을 다루지 않으므로 담당 확장자는 없다.</summary>
public sealed class HardwareModule : IModule
{
    /// <summary>자동 갱신 주기(v0.51.0 사용자 지정) — 하단 바 "200 ms" 표기와 일치해야 한다.</summary>
    internal static readonly TimeSpan AutoRefreshInterval = TimeSpan.FromMilliseconds(200);

    /// <summary>
    /// 프로세스 공유 WMI 폴러(A42 결정: Hardware만 공유, 창 여러 개여도 수집은 1회).
    /// 하드웨어 뷰가 없으면(구독 0) 휴면하므로 상시 비용은 없다. BelowNormal 우선순위라
    /// 재생·UI와 CPU를 다투지 않는다. 뷰는 Subscribe/Poke만 쓰고 수집 스레드는 여기 하나다.
    /// </summary>
    internal static PollingWorker<IReadOnlyList<HardwareSection>> Poller { get; } =
        new("ZP hardware poller", AutoRefreshInterval, HardwareInfoService.Collect);

    public string Id => "hardware";

    public string DisplayName => "H/W Info"; // v0.28.1 사용자 요청 (이전: Hardware-info)

    public string BrandName => "ZP-info";

    public string IconGlyph => "\uE950"; // Component (칩 모양)

    public IReadOnlyList<string> SupportedExtensions => [];

    public object CreateView(OpenContext context) => new HardwareView(context);
}
