using Microsoft.UI.Windowing;
using Windows.UI.ViewManagement;

namespace WinUtil.App;

/// <summary>
/// 창 헤더(타이틀바)를 Windows 시스템 강조색(개인 설정 > 색 > 테마 컬러)으로 칠한다.
/// WinAppSDK 1.2+의 AppWindowTitleBar 단순 색 커스터마이즈 — Win10 1809+에서도 동작.
/// 캡션 버튼(최소화/최대화/닫기)까지 같이 칠해야 얼룩져 보이지 않는다.
/// </summary>
internal static class TitleBarTheming
{
    /// <summary>현재 시스템 강조색을 읽어 타이틀바 전체에 적용한다. 실패는 조용히 무시(기본색 유지).</summary>
    public static void ApplyAccent(AppWindowTitleBar titleBar)
    {
        try
        {
            var ui = new UISettings();
            var accent = ui.GetColorValue(UIColorType.Accent);
            var hover = ui.GetColorValue(UIColorType.AccentLight1);   // 버튼 호버
            var pressed = ui.GetColorValue(UIColorType.AccentDark1);  // 버튼 누름
            var inactive = ui.GetColorValue(UIColorType.AccentLight2); // 비활성 창: 연한 강조색
            var white = Windows.UI.Color.FromArgb(255, 255, 255, 255);
            var inactiveText = Windows.UI.Color.FromArgb(255, 0x40, 0x40, 0x40);

            titleBar.BackgroundColor = accent;
            titleBar.ForegroundColor = white;
            titleBar.InactiveBackgroundColor = inactive;
            titleBar.InactiveForegroundColor = inactiveText;

            titleBar.ButtonBackgroundColor = accent;
            titleBar.ButtonForegroundColor = white;
            titleBar.ButtonHoverBackgroundColor = hover;
            titleBar.ButtonHoverForegroundColor = white;
            titleBar.ButtonPressedBackgroundColor = pressed;
            titleBar.ButtonPressedForegroundColor = white;
            titleBar.ButtonInactiveBackgroundColor = inactive;
            titleBar.ButtonInactiveForegroundColor = inactiveText;
        }
        catch
        {
            // 다운레벨 OS 등에서 미지원이면 기본 타이틀바 유지
        }
    }
}
