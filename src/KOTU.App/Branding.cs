namespace KOTU.App;

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
    /// 저장소 주소 — 설정 About 줄의 링크가 쓴다. 링크용 주소를 코드 여기저기에 흩뿌리지 않으려고
    /// 브랜드 상수로 모아 둔 단일 소스다(A162).
    /// ※ <c>Integration.UpdateService</c>의 동명 상수는 <b>Velopack 업데이트 피드</b> 주소로
    ///   목적이 달라 일부러 합치지 않았다 — 피드를 다른 호스트로 옮겨도 이 링크는 그대로여야 한다.
    /// </summary>
    public const string RepoUrl = "https://github.com/zpstudios/kotu";

    /// <summary>
    /// 사용자 가이드 주소(A162) — 설정 화면의 "Learn more" 링크 목적지.
    /// 가이드는 같은 내용의 문서 <b>두 벌</b>로 존재한다: 원본 <c>docs/USER-GUIDE.md</c>와
    /// 웹 게시본 <c>site/guide.html</c>. 사이트는 아직 어디에도 게시돼 있지 않으므로
    /// (<c>site/README.md</c> "배포" 절 — 워크플로 없음) 앱이 여는 주소는 GitHub가 렌더링해 주는
    /// 마크다운 원본이다. 두 문서의 절 앵커 이름은 같게 맞춰 두었으므로
    /// (guide.html의 <c>id</c> = 마크다운 제목 슬러그) 사이트가 게시되면 이 상수 한 줄만 바꾸면 된다.
    /// </summary>
    public const string UserGuideUrl = RepoUrl + "/blob/master/docs/USER-GUIDE.md";

    /// <summary>가이드의 절 앵커로 바로 가는 주소. 앵커 이름은 소문자와 하이픈만 쓴다.</summary>
    public static Uri GuideLink(string anchor) => new($"{UserGuideUrl}#{anchor}");

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
        // A59(v0.113.0): 기존 6색(amber 37°·green 145°·teal 177°·blue 220°·purple 258°·red 358°)에서
        // 가장 넓게 빈 구간이 red와 purple 사이라 그 한가운데 마젠타 320°를 골랐다 —
        // 어느 색과도 38° 이상 떨어져 16px 아이콘에서도 헷갈리지 않는다.
        KOTU.Module.AllReadable.AllReadableModule.ModuleId =>
            Windows.UI.Color.FromArgb(0xFF, 0xC2, 0x49, 0x9A), // magenta — KOTU-all
        _ => null,
    };

    /// <summary>
    /// 창·트레이 아이콘 테두리 링 색 (A102, v0.130.0) — 모듈 액센트와 같은 색이다.
    /// 링의 목적이 "몇 번째 창인가"(A68 인스턴스 9색)에서 "어느 모듈인가"로 바뀌면서
    /// 색 원천이 <see cref="ModuleAccent"/>로 옮겨왔고, 창 개수 조건도 사라졌다.
    /// null = 링을 그리지 않는다. 두 경우뿐이다:
    ///  · 액센트가 없는 화면(설정·미지원 파일 안내 = 중립 아이콘) — 구분할 모듈이 없다.
    ///  · 정보(하드웨어) 모듈 — 트레이 아이콘이 센서 값을 2줄로 채우는 유일한 모듈이라(A54)
    ///    링이 글자를 갉아먹는다(사용자 확정: 유휴 INF 표기·값 표시 모두 링 없음).
    /// </summary>
    public static Windows.UI.Color? IconRing(string? moduleId)
        => moduleId == "hardware" ? null : ModuleAccent(moduleId);

    /// <summary>
    /// 콘텐츠를 안 연 상태(유휴)의 트레이 아이콘을 전면으로 채울 색 (A140, v0.164.0).
    /// null = 채우지 않는다 = 종전 모습(반투명 다크 판 + 저채도 글자)을 그대로 쓴다.
    /// 값은 <b>모듈 액센트 원색</b>이다(Lighten/Darken 없음 — 사용자 확정).
    ///
    /// null이 되는 두 경우는 결과적으로 <see cref="IconRing"/>과 같지만 <b>판단 근거가 다르므로
    /// 일부러 별도 메서드로 둔다</b>(한쪽 규칙이 바뀌어도 다른 쪽이 딸려 가지 않게 — A169(v0.172.0)가
    /// 실제로 트레이 색 축을 하나 더 늘렸다: 열림 글자색이 모듈 액센트 1색에서 줄별 색으로):
    ///  · 정보(하드웨어) 모듈 — "콘텐츠 열림·닫힘 구분이 없는 모듈"이라 이 색 규칙의 적용 대상이
    ///    아니다(부록 B 67 사용자 확정). 하드웨어는 전용 색 글자 + 링 없음 그대로다.
    ///  · 액센트가 없는 화면(설정·미지원 파일 = 중립 아이콘) — 채울 모듈 색 자체가 없다.
    /// </summary>
    public static Windows.UI.Color? IdleFill(string? moduleId)
        => moduleId == "hardware" ? null : ModuleAccent(moduleId);

    public const string MissionStatement =
        "• No bloat. Ever.\n" +
        "• Crucial features only — easy to use.\n" +
        "• Easy to install & uninstall — all files in one folder, all settings in settings.ini beside the app.\n" +
        "• No personal information collected, whatsoever — no watch history, no file history.\n" +
        "• Free forever, for everyone — personal and commercial use alike.\n" +
        "• Our only revenue: Patreon and silent in-app ad."; // v0.27.0 문구 확정 (사용자 요청)
}
