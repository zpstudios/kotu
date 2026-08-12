namespace KOTU.App.Overlays;

/// <summary>
/// 좌/우 오버레이 표시 모드 (A58).
/// TranslucentOver = 메인 영역 위를 반투명(아크릴, A33)으로 덮음 — 메인 크기 불변
/// (키 홀드·2초 홀드 고정이 이 모드). OpaqueDocked = 불투명으로 튀어나와 메인 영역을
/// 반대쪽으로 축소(키 2연타) — 실제 컬럼 폭 차지는 셸(MainWindow)의 도크 컬럼이 담당하고,
/// 컨트롤은 배경·안내 문구만 바꾼다.
/// </summary>
public enum OverlayMode
{
    TranslucentOver,
    OpaqueDocked,
}
