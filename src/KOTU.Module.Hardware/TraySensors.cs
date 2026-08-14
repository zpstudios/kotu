namespace KOTU.Module.Hardware;

/// <summary>
/// 트레이 아이콘(A18)용 **전역 선택 파사드**. A70부터 선택의 소유자는 창(인스턴스)별
/// <see cref="HardwareInstanceState"/>이고, 여기의 전역 값은 **마지막으로 커밋된 1벌**이다 —
/// 어느 창에서 조작했든 마지막 조작이 이기며(사용자 확정), 저장(`hardware.traySensors`)도 같은 1벌.
///
/// - App의 SensorTray가 소비하는 표면(MaxCount·Selected·Changed·Clear)만 남기고 내부는
///   스토어(HardwareInstanceState) 위임 — App 쪽 수정 최소화.
/// - <see cref="Selected"/>는 불변 스냅샷 배열 — SensorTray의 ComposeKey가 **워커 스레드**에서
///   열거 중일 때 UI 스레드에서 커밋돼도 안전하다(A18 규약 유지 — 깨면 경합).
/// - Toggle/IsSelected는 A70에서 인스턴스(HardwareInstanceState)로 이사 — 뷰가 직접 쓴다.
///   Initialize도 스토어(HardwareInstanceState.Initialize)에 흡수됐다.
/// </summary>
public static class TraySensors
{
    public const int MaxCount = HardwareInstanceState.MaxSelected;

    /// <summary>전역 1벌이 커밋될 때(UI 스레드, 동기). SensorTray의 구독·아이콘 갱신용.</summary>
    public static event Action? Changed;

    /// <summary>전역(마지막 커밋) 선택 — 불변 스냅샷, 어느 스레드에서 읽어도 안전.</summary>
    public static IReadOnlyList<string> Selected => HardwareInstanceState.GlobalSelection;

    /// <summary>
    /// 전부 해제(트레이 메뉴 "Hide tray sensors") = **전역 1벌만 비운다**(A70). 열려 있는 창들의
    /// 런타임 선택은 그대로 남는다(허용된 발산) — 다음에 어느 창에서든 핀을 토글하면 그 창의
    /// 선택 전체가 다시 커밋되어 아이콘이 되살아난다.
    /// </summary>
    public static void Clear() => HardwareInstanceState.ClearGlobalSelection();

    /// <summary>스토어의 선택 커밋 깔때기 전용 — 직접 부르지 말 것(커밋 없이 통지만 나가면 안 된다).</summary>
    internal static void RaiseChanged() => Changed?.Invoke();
}
