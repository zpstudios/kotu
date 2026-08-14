namespace KOTU.App.Overlays;

/// <summary>
/// A92 안내 문구의 단일 출처 (A107, v0.134.0 신설). 같은 문구가 좌(FileListOverlay)·우
/// (ContentInfoOverlay) 두 파일과 XAML 기본값까지 흩어져 있어 키 체계가 바뀔 때마다
/// (A86 Z/X → A107 Alt+Z/X) 한쪽만 고치는 사고 위험이 있었다 — 이제 키 표기·문구 틀은
/// 여기서만 바꾼다(XAML의 Text 기본값은 제거 — 표시 전 ShowHint가 항상 여기 문구를 넣는다).
/// A108(오버레이/사이드바 용어·문구 위치 재정리)이 얹힐 자리도 이 파일이다.
/// 표시 타이밍(2.5초 + 페이드)은 각 오버레이의 A92 절이 그대로 담당한다.
/// </summary>
internal static class OverlayHints
{
    /// <summary>좌(파일 리스트) 오버레이 키 표기 — A107: Alt+Z.</summary>
    internal const string ListKey = "Alt+Z";

    /// <summary>우(정보) 오버레이 키 표기 — A107: Alt+X.</summary>
    internal const string InfoKey = "Alt+X";

    /// <summary>반투명 고정(2초 홀드 승격) 안내 — A92 문구 틀에 키 표기만 끼운다.</summary>
    internal static string Pinned(string key) => $"Pinned - press {key} to close";

    /// <summary>불투명 도크 안내 — A92 문구 틀에 키 표기만 끼운다.</summary>
    internal static string Docked(string key) => $"Docked - press {key} to close";
}
