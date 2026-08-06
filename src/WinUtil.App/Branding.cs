namespace WinUtil.App;

/// <summary>
/// 앱 전역 브랜드 문구(단일 소스). 설정 About·첫 실행 웰컴 다이얼로그에서 사용하고,
/// 설치 스플래시 이미지(packaging/splash.png, gen_splash.py로 생성)도 같은 내용을 담는다.
/// 문구 수정 시 splash.png도 재생성할 것.
/// 주의: settings.ini·no watch history 항목은 지향점 — 실제 동작 일치화는 차기 작업(사용자 결정, 2026-08-06).
/// </summary>
internal static class Branding
{
    public const string AppName = "ZP";

    public const string MissionStatement =
        "• No bloat. Ever.\n" +
        "• Crucial features only — easy to use.\n" +
        "• Easy to install & uninstall — all files in one folder, all settings in settings.ini beside the app.\n" +
        "• No personal information collected, whatsoever — no watch history, no file history.\n" +
        "• Free forever, for everyone — personal and commercial use alike.\n" +
        "• Our only revenue: Patreon and one silent Google ad.";
}
