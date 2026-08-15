namespace KOTU.Core.Contracts;

/// <summary>
/// 모듈 뷰가 셸의 좌/우 패널(사이드바/오버레이) 자리에 얹을 <b>모듈 고유 콘텐츠</b>를 제공할 때
/// 구현한다(A119, v0.145.0 — 첫 사용처는 정보 모듈: 좌 = 큰 그래프 / 우 = 스펙 텍스트).
/// 파일 모듈의 좌(파일 리스트)/우(정보)는 셸 공용 컨트롤(FileListOverlay·ContentInfoOverlay)이
/// 종전대로 담당하고, 이 계약을 구현한 뷰에서만 셸이 그 자리에 모듈 콘텐츠 호스트(SidePanelHost)를
/// 대신 띄운다 — 상태 머신(F1/F2 홀드·2연타·Enter 일괄 토글·경계 버튼)·반투명/불투명 배경·안내
/// 문구는 셸의 기존 경로를 그대로 공유한다. "컨트롤은 셸에·계약은 Core에·모듈은 슬롯만" 패턴
/// (A57 ②·<see cref="IBottomBarProvider"/>·A22 드라이브 줄과 같은 계열)이다.
/// 반환 타입은 UI 프레임워크 비의존을 위해 object이며, 셸(WinUI 3)에서 UIElement로 캐스팅한다.
/// 요소의 생성·소유·갱신은 뷰 몫이다 — 매 호출 같은 인스턴스를 돌려줘도 된다(셸은 참조가 바뀔
/// 때만 다시 얹는다). 요소는 뷰 자신의 트리에 부착하지 말 것(셸 호스트가 유일한 부모여야
/// reparent 함정이 없다). 셸은 모듈 전환 시 호스트를 비워 요소를 트리에서 내린다.
/// </summary>
public interface ISidePanelProvider
{
    /// <summary>좌 패널에 얹을 요소. null이면 그 쪽 패널은 뜨지 않는다.</summary>
    object? GetLeftPanel();

    /// <summary>우 패널에 얹을 요소. null이면 그 쪽 패널은 뜨지 않는다.</summary>
    object? GetRightPanel();
}
