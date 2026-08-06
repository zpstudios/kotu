using Microsoft.UI.Windowing;

namespace WinUtil.App;

/// <summary>
/// 창 헤더(타이틀바)를 앱 브랜드 색(#15072E, 다크 퍼플)으로 칠한다.
/// v0.14.1의 시스템 강조색 추적은 사용자 요청으로 고정 색으로 대체(v0.15.1).
/// 본문 배경(MainWindow.xaml 루트)과 같은 색이라 창 전체가 한 색으로 보인다.
/// 캡션 버튼(최소화/최대화/닫기)까지 같이 칠해야 얼룩져 보이지 않는다.
/// WinAppSDK 1.2+의 AppWindowTitleBar 단순 색 커스터마이즈 — Win10 1809+에서도 동작.
/// </summary>
internal static class TitleBarTheming
{
    /// <summary>브랜드 배경색 #15072E (RGB 21, 7, 46).</summary>
    internal static readonly Windows.UI.Color Background = Rgb(0x15, 0x07, 0x2E);

    private static readonly Windows.UI.Color Foreground = Rgb(0xFF, 0xFF, 0xFF);
    private static readonly Windows.UI.Color HoverBackground = Rgb(0x2B, 0x14, 0x54);   // 밝게
    private static readonly Windows.UI.Color PressedBackground = Rgb(0x3A, 0x1C, 0x70); // 더 밝게
    private static readonly Windows.UI.Color InactiveBackground = Rgb(0x10, 0x05, 0x22); // 어둡게
    private static readonly Windows.UI.Color InactiveForeground = Rgb(0x9E, 0x93, 0xB8); // 연보라 회색

    /// <summary>타이틀바 전체(배경+캡션 버튼)에 브랜드 색을 적용한다. 실패는 조용히 무시(기본색 유지).</summary>
    public static void Apply(AppWindowTitleBar titleBar)
    {
        try
        {
            titleBar.BackgroundColor = Background;
            titleBar.ForegroundColor = Foreground;
            titleBar.InactiveBackgroundColor = InactiveBackground;
            titleBar.InactiveForegroundColor = InactiveForeground;

            titleBar.ButtonBackgroundColor = Background;
            titleBar.ButtonForegroundColor = Foreground;
            titleBar.ButtonHoverBackgroundColor = HoverBackground;
            titleBar.ButtonHoverForegroundColor = Foreground;
            titleBar.ButtonPressedBackgroundColor = PressedBackground;
            titleBar.ButtonPressedForegroundColor = Foreground;
            titleBar.ButtonInactiveBackgroundColor = InactiveBackground;
            titleBar.ButtonInactiveForegroundColor = InactiveForeground;
        }
        catch
        {
            // 다운레벨 OS 등에서 미지원이면 기본 타이틀바 유지
        }
    }

    private static Windows.UI.Color Rgb(byte r, byte g, byte b) =>
        Windows.UI.Color.FromArgb(255, r, g, b);
}
