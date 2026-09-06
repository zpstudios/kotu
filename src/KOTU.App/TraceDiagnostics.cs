namespace KOTU.App;

/// <summary>
/// A352 배치 1: 트레이스 로그 토글의 설정 키·변경 알림 — ShellDiagnostics(A234)·
/// NavDiagnostics 관용구 복제(SettingKey + Changed + NotifyChanged).
/// 설정 화면(SettingsView)이 저장 후 NotifyChanged를 부르고, 열린 모든 창(MainWindow)이
/// Changed를 구독해 <see cref="KOTU.Core.Diagnostics.DiagTrace"/>의 게이트를 즉시 켜고 끈다.
/// <para>
/// <b>왜 래퍼가 따로 있나</b>: 기록 시설(DiagTrace)은 모듈에서도 불러야 해서 Core에 있는데,
/// 설정(ISettingsService)과 창 배선은 App의 것이다. 그래서 "설정 키를 아는 쪽"은 여기 남기고
/// Core는 <c>SetEnabled(bool)</c>만 받는다 — 다른 진단 토글들과 같은 층 분리다.
/// </para>
/// </summary>
public static class TraceDiagnostics
{
    /// <summary>설정 키. 값은 bool, 기본 false — 진단 전용이라 일반 사용자에게는 꺼져 있다.
    /// 파일(settings.json)에 저장되므로 재시작 후에도 유지된다 — 크래시 재현이 목적이라
    /// "켜 두고 앱을 다시 띄워 재현"이 정상 사용법이다(다른 진단 토글과 같은 결정).</summary>
    public const string SettingKey = "diag.trace";

    /// <summary>설정 변경 시 열린 모든 창이 게이트를 다시 적용하도록 알린다(설정 화면 → 각 MainWindow).</summary>
    public static event Action? Changed;

    public static void NotifyChanged() => Changed?.Invoke();
}
