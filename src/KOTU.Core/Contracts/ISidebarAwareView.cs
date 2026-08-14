namespace KOTU.Core.Contracts;

/// <summary>
/// 셸이 모듈 뷰에 "좌·우 사이드바(불투명 도크)가 둘 다 열려 공간을 차지 중인지"를 밀어주는
/// 계약 (A60 3차, v0.138.0). 정보 모듈의 센터 그래프 그리드가 열 수(양쪽 열림 4 / 하나라도
/// 닫힘 8 — A93 썸네일 열 수와 같은 판정)를 정하는 데 쓴다.
/// 모듈 프로젝트는 셸을 참조할 수 없으므로(App → 모듈 단방향) 계약은 Core에 두되, 값의 원본
/// (사이드바 상태)이 셸에만 있어 방향은 <b>셸 → 뷰 푸시</b>다 — <see cref="ITrayStatusProvider"/>·
/// <see cref="IWindowCollapseSource"/>(뷰 → 셸)와 같은 이유에서 나온 반대 방향.
/// 호출 지점은 사이드바 상태 변경의 단일 종착점(MainWindow.ApplyOverlayStates) 한 곳이고,
/// 미구현 뷰(다른 모듈·설정)는 셸 쪽 캐스트 실패로 no-op이다. UI 스레드에서 호출된다.
/// ※ 현행 셸은 파일 컨텍스트가 없는 정보 모듈에 사이드바를 띄우지 않으므로(hasFile·emptyModule
/// 게이트) 지금은 항상 false(= 8열)가 온다 — 셸 정책이 바뀌면 4열이 저절로 살아나는 배선이다.
/// </summary>
public interface ISidebarAwareView
{
    /// <summary>true = 좌·우 사이드바가 둘 다 공간을 차지 중(메인 폭 절반) — 뷰는 밀도를 낮춘다.</summary>
    void SetSidebarsState(bool bothOpen);
}
