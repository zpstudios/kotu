namespace KOTU.App.Overlays;

/// <summary>
/// A92 안내 문구의 단일 출처 (A107, v0.134.0 신설). 같은 문구가 좌(FileListOverlay)·우
/// (ContentInfoOverlay) 두 파일과 XAML 기본값까지 흩어져 있어 키 체계가 바뀔 때마다
/// (A86 Z/X → A107 Alt+Z/X → A118 F1/F2) 한쪽만 고치는 사고 위험이 있었다 — 이제 키 표기·문구
/// 틀은 여기서만 바꾼다(XAML의 Text 기본값은 제거 — 표시 전 ShowHint가 항상 여기 문구를 넣는다).
/// 키 자체의 정본은 MainWindow.LeftPanelKey/RightPanelKey — 키를 바꾸면 여기 표기도 함께 바꾼다.
/// A108(v0.135.0) 용어 확정: **사이드바** = 불투명, 메인 영역을 줄이며 옆에 선다
/// (구 "불투명 도크/밀어내기" — 코드 식별자 OpaqueDocked는 유지) /
/// **오버레이** = 반투명, 메인 위에 덮인다(홀드·2초 홀드 고정 = TranslucentOver/Pinned).
/// 사용자 노출 문구도 이 구분을 따른다: 도크 안내 = "Sidebar", 고정 안내 = "Pinned"(유지).
/// 표시 타이밍(2.5초 + 페이드)은 각 오버레이의 A92 절이, 표시 위치(경계 버튼 옆 — A108)는
/// 각 XAML의 PinnedText 배치가 담당한다.
/// </summary>
internal static class OverlayHints
{
    /// <summary>좌(파일 리스트) 오버레이 키 표기 — A118: F1(구 A107 Alt+Z).</summary>
    internal const string ListKey = "F1";

    /// <summary>우(정보) 오버레이 키 표기 — A118: F2(구 A107 Alt+X).</summary>
    internal const string InfoKey = "F2";

    /// <summary>오버레이 고정(2초 홀드 승격) 안내 — A92 문구 틀에 키 표기만 끼운다.</summary>
    internal static string Pinned(string key) => $"Pinned - press {key} to close";

    /// <summary>사이드바(불투명 도크) 안내 — A108에서 표기를 "Docked"에서 "Sidebar"로 교체
    /// (메서드명 Docked는 OverlayMode.OpaqueDocked와 짝이라 유지 — A108 식별자 보존 규칙).</summary>
    internal static string Docked(string key) => $"Sidebar - press {key} to close";
}
