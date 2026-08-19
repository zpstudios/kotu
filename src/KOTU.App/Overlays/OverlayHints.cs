namespace KOTU.App.Overlays;

/// <summary>
/// A92 안내 문구의 단일 출처 (A107, v0.134.0 신설). 같은 문구가 좌(FileListOverlay)·우
/// (ContentInfoOverlay)·모듈 패널 호스트(SidePanelHost) 세 표면에 흩어져 있어 키 체계가
/// 바뀔 때마다(A86 Z/X → A107 Alt+Z/X → A118 F1/F2 → A158 F11/F12) 한쪽만 고치는 사고
/// 위험이 있었다 — 키 표기·문구 틀은 여기서만 바꾼다.
/// 키 자체의 정본은 MainWindow.LeftPanelKey/RightPanelKey — 키를 바꾸면 여기 표기도 함께 바꾼다.
/// A176: 반투명 축(홀드·2초 홀드 고정) 폐지로 Pinned 안내는 사라졌다 — 남는 문구는
/// 사이드바(불투명 도크) 안내 하나다(F11/F12 단타 토글·핀 버튼이 같은 상태를 오간다).
/// 표시 타이밍(2.5초 + 페이드)은 각 표면의 A92 절이, 다크 반투명 판(A133 — PinnedPlate)은
/// 각 표면의 XAML/조립 코드가 담당한다.
/// </summary>
internal static class OverlayHints
{
    /// <summary>좌(파일 리스트) 패널 키 표기 — A158: F11(구 A118 F1, 구 A107 Alt+Z).</summary>
    internal const string ListKey = "F11";

    /// <summary>우(정보) 패널 키 표기 — A158: F12(구 A118 F2, 구 A107 Alt+X).</summary>
    internal const string InfoKey = "F12";

    /// <summary>사이드바(불투명 도크) 안내 — A108에서 표기를 "Docked"에서 "Sidebar"로 교체
    /// (메서드명 Docked는 구 OverlayMode.OpaqueDocked와 짝이던 이름 — A108 식별자 보존 규칙 유지).
    /// A176: 사이드바가 유일한 열림 상태라 이 안내 하나만 남는다.</summary>
    internal static string Docked(string key) => $"Sidebar - press {key} or the pin button to close";
}
