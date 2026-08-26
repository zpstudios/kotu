namespace KOTU.App;

/// <summary>
/// A234 배치 1: 셸 키(F11/F12) 진단 오버레이 토글의 설정 키·변경 알림 — UiScale 관용구 복제
/// (SettingKey + Changed + NotifyChanged). 설정 화면(SettingsView)이 저장 후 NotifyChanged를
/// 부르고, 열린 모든 창(MainWindow)이 Changed를 구독해 진단 스트립(DiagStrip)을 즉시 켜고 끈다.
/// 계측 목적: F11/F12 불통 수리 3연속 실패(A209·A212·A226) 뒤라 블라인드 수리를 멈추고,
/// 실기기 스크린샷 1장으로 "클릭 후 포커스가 null인가 / 살아 있되 RootLayout 밖인가 /
/// 키는 도달하는데 게이트에서 죽는가"를 실측 확정하기 위한 시설이다(원인 확정 후 배치 2가 수리).
/// </summary>
public static class ShellDiagnostics
{
    /// <summary>설정 키. 값은 bool, 기본 false — 일반 사용자에게는 보이지 않는 진단 전용 오버레이다.
    /// 파일(settings.json)에 저장되므로 재시작 후에도 유지된다(구현 결정).</summary>
    public const string SettingKey = "diag.shellKeyOverlay";

    /// <summary>설정 변경 시 열린 모든 창이 스트립 표시를 다시 적용하도록 알린다(설정 화면 → 각 MainWindow).</summary>
    public static event Action? Changed;

    public static void NotifyChanged() => Changed?.Invoke();
}
