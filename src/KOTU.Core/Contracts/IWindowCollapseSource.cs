namespace KOTU.Core.Contracts;

/// <summary>
/// 모듈 뷰가 "하단 바만 남기고 창을 접어 달라"고 셸에 요청하는 계약 (A61, v0.111.0).
/// 지금 유일한 사용처는 정보 모듈의 핀(Always on top, A39) — 접힘은 별도 토글이 아니라
/// **"핀 ON && 전체화면 아님"으로 계산되는 파생 상태**이고, 그 두 상태를 모두 아는 곳이 뷰라서
/// 판단은 뷰가 하고 실행은 셸이 한다.
/// 창 크기·최소 크기 제약(A40 WindowMinSize)은 셸만 만질 수 있어 이 방향(뷰 → 셸)이 된다 —
/// 모듈 프로젝트는 셸을 참조할 수 없다(App → 모듈 단방향, IBottomBarProvider와 같은 이유).
/// 뷰가 내려가면(모듈 전환 등) 뷰가 스스로 false를 한 번 보내 펼침을 보장한다 —
/// A39가 같은 시점에 always on top을 해제하는 것과 같은 이유(끌 수단이 없는 상태 방지).
/// </summary>
public interface IWindowCollapseSource
{
    /// <summary>
    /// true = 하단 바만 남기고 접기, false = 접기 전 크기로 복원.
    /// UI 스레드에서 보내면 셸이 **즉시(동기로)** 처리한다 — 전체화면 전환처럼 순서가 중요한
    /// 호출(먼저 펼치고 나서 SetPresenter)이 성립해야 하기 때문. 다른 스레드면 셸이 디스패치한다.
    /// </summary>
    event Action<bool>? CollapseRequested;
}
