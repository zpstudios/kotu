namespace WinUtil.App;

/// <summary>
/// 앱 전역 브랜드 문구(단일 소스). 설정 About·첫 실행 웰컴 다이얼로그에서 사용하고,
/// 설치 스플래시 이미지(packaging/splash.png, gen_splash.py로 생성)도 같은 내용을 담는다.
/// 문구 수정 시 splash.png도 재생성할 것.
/// 주의: settings.ini·no watch history 항목은 지향점 — 실제 동작 일치화는 차기 작업(사용자 결정, 2026-08-06).
/// </summary>
internal static class Branding
{
    public const string AppName = "KOTU";

    /// <summary>
    /// 모듈별 액센트 색. v0.26.0부터 하단 바 스트립/칩 색 대신 창·트레이의 모듈 색
    /// KOTU 아이콘(packaging/gen_app_icon.py — 색을 여기와 동일하게 유지할 것)이 모드를 구분한다.
    /// 이 메서드는 색의 단일 소스 정의로 유지. 미등록 ID는 null(중립).
    /// </summary>
    public static Windows.UI.Color? ModuleAccent(string? moduleId) => moduleId switch
    {
        "archive" => Windows.UI.Color.FromArgb(0xFF, 0xC7, 0x7E, 0x1F), // amber  — KOTU-archive
        "image" => Windows.UI.Color.FromArgb(0xFF, 0x2E, 0x9E, 0x5B), // green  — KOTU-image
        "video" => Windows.UI.Color.FromArgb(0xFF, 0xD6, 0x49, 0x4F), // red    — KOTU-video
        "audio" => Windows.UI.Color.FromArgb(0xFF, 0x1F, 0xA8, 0xA0), // teal   — KOTU-audio (A10)
        "hardware" => Windows.UI.Color.FromArgb(0xFF, 0x38, 0x74, 0xD8), // blue   — KOTU-info
        "document" => Windows.UI.Color.FromArgb(0xFF, 0x7A, 0x5A, 0xC8), // purple — KOTU-doc (아이콘 생성 시 이 색 사용)
        _ => null,
    };

    public const string MissionStatement =
        "• No bloat. Ever.\n" +
        "• Crucial features only — easy to use.\n" +
        "• Easy to install & uninstall — all files in one folder, all settings in settings.ini beside the app.\n" +
        "• No personal information collected, whatsoever — no watch history, no file history.\n" +
        "• Free forever, for everyone — personal and commercial use alike.\n" +
        "• Our only revenue: Patreon and silent in-app ad."; // v0.27.0 문구 확정 (사용자 요청)
}
