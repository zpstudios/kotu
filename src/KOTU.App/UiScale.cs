namespace KOTU.App;

/// <summary>
/// 앱 자체 UI 스케일(DPI) 오버라이드 (v0.24.0, 사용자 요청).
/// 윈도우 디스플레이 설정과 같은 배율 목록에서 고르면, 시스템 DPI(RasterizationScale) 대비
/// 상대 배율을 각 창 루트에 ScaleTransform으로 적용한다(MainWindow.ApplyUiScale).
/// 예: 시스템 150% 모니터에서 100%를 고르면 앱만 2/3 크기로 그려진다.
/// </summary>
public static class UiScale
{
    /// <summary>설정 키. 값은 퍼센트(int), 0 = 시스템 기본(오버라이드 없음).</summary>
    public const string SettingKey = "app.uiScale";

    /// <summary>윈도우 디스플레이 설정이 제공하는 배율 목록(%)과 동일하게 유지한다.</summary>
    public static readonly int[] Percents = [100, 125, 150, 175, 200, 225, 250, 300, 350];

    /// <summary>설정 변경 시 열린 모든 창이 다시 적용하도록 알린다(설정 화면 → 각 MainWindow).</summary>
    public static event Action? Changed;

    public static void NotifyChanged() => Changed?.Invoke();
}
