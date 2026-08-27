namespace KOTU.Core.Contracts;

/// <summary>
/// 모듈 뷰가 "창을 A40 최소 크기(720×540 DIP)로 한 번 줄여 달라"고 셸에 요청하는 계약
/// (A238, v0.253.0 — 구 IWindowCollapseSource(A61 접기/복원, v0.111.0)의 의미 개정·개명.
/// 소비처가 셸 1곳뿐이라 이름·멤버 재설계가 안전했다 — A119 ISidebarAwareView 개정 선례).
/// 유일한 발화처는 정보 모듈의 핀(Always on top, A39) — 핀을 켜는 순간 1회만 쏜다.
/// 핀 해제는 아무것도 요청하지 않고(always on top 해제는 뷰가 프레젠터에 직접 한다),
/// 축소 뒤 사용자가 창을 도로 키우는 것도 자유다(최소 크기는 하한이지 잠금이 아니다).
/// 최소 크기의 DIP → 물리 픽셀 환산(A40 WindowMinSize)과 창 크기 조작은 셸만 할 수 있어
/// 이 방향(뷰 → 셸)이 된다 — 모듈 프로젝트는 셸을 참조할 수 없다(App → 모듈 단방향,
/// IBottomBarProvider와 같은 이유).
/// </summary>
public interface IWindowShrinkSource
{
    /// <summary>
    /// 창을 최소 크기로 1회 줄여 달라는 요청. 전체화면 중이면 셸이 무시한다(창 프레젠터가
    /// 아니라 크기를 만질 수 없고, 복귀 후에도 재축소하지 않는다 — 축소는 핀 순간 1회 액션).
    /// UI 스레드가 아니면 셸이 디스패치한다.
    /// </summary>
    event Action? ShrinkToMinRequested;
}
