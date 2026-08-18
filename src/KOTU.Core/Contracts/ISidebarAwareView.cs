namespace KOTU.Core.Contracts;

/// <summary>
/// 셸이 모듈 뷰에 "좌/우 사이드바(불투명 도크)가 몇 쪽 열려 공간을 차지 중인지"(0~2)를 밀어주는
/// 계약 (A60 3차 신설, v0.138.0 → A119(v0.145.0)에서 bool "양쪽 열림"을 <b>도크 수</b>로 개정 —
/// 소비처가 정보 모듈뿐이라 시그니처 개정이 안전했다). 정보 모듈의 센터 타일 그리드가 열 수
/// (도크 2 = 4열 / 1 = 6열 / 0 = 8열 — A168/v0.165.0이 A119의 2/3/4열을 개정)를 정하는 데 쓴다.
/// 값의 의미(도크 수)는 그대로이므로 이 계약 자체는 A168에서 바뀌지 않았다 —
/// 환산표는 소비처(HardwareView.SetSidebarsState) 한 곳에만 있다.
/// 오버레이(반투명 — 홀드·2초 고정)는
/// 메인 폭을 줄이지 않으므로 셸이 세지 않는다(도크 상태만 계수 — A93 썸네일과 같은 해석).
/// 모듈 프로젝트는 셸을 참조할 수 없으므로(App → 모듈 단방향) 계약은 Core에 두되, 값의 원본
/// (사이드바 상태)이 셸에만 있어 방향은 <b>셸 → 뷰 푸시</b>다 — <see cref="ITrayStatusProvider"/>·
/// <see cref="IWindowCollapseSource"/>(뷰 → 셸)와 같은 이유에서 나온 반대 방향.
/// 호출 지점은 사이드바 상태 변경의 단일 종착점(MainWindow.ApplyOverlayStates) 한 곳이고
/// (F1/F2·2연타·Enter·경계 버튼·모듈 진입 기본(A109)이 전부 그리로 모여 재푸시된다),
/// 미구현 뷰(다른 모듈·설정)는 셸 쪽 캐스트 실패로 no-op이다. UI 스레드에서 호출된다.
/// ※ A119부터 셸이 정보 모듈에도 좌/우 패널(ISidePanelProvider 호스트)을 띄우므로 이 값이
/// 실제로 0~2를 오간다 — A60 3차 당시의 "지금은 항상 8열(상시 false)" 각주는 소멸했다.
/// </summary>
public interface ISidebarAwareView
{
    /// <summary>dockedCount = 공간을 차지 중인 사이드바(불투명 도크) 수(0~2) — 뷰는 밀도를 조절한다.</summary>
    void SetSidebarsState(int dockedCount);
}
