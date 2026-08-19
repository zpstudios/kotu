namespace KOTU.Core.Contracts;

/// <summary>
/// A189: 모듈 뷰가 <b>경로 없는 콘텐츠</b>(무제 문서 — 문서 모듈 'New text file')로 중앙을
/// 차지하기 시작할 때 셸에 알리는 계약 — <see cref="IContentStateSource.ContentOpened"/>의
/// 경로 없는 판본이다(그 이벤트는 경로가 필수라 재사용할 수 없다 — 6개 구현체의 계약 서명은
/// 불변 유지). 셸은 이 이벤트로 빈 상태 탐색기(S1)를 내리고 드라이브 줄을 숨기고 창 제목을
/// 무제 표기로 바꾼다. 이후 첫 저장이 경로를 확정하면 뷰가 ContentOpened를 쏘아
/// 정상 콘텐츠(경로 기반 축)로 승격된다.
/// </summary>
public interface IUntitledContentSource
{
    /// <summary>무제 콘텐츠로 표시를 시작하면 발생한다(UI 스레드 보장 없음).</summary>
    event Action? UntitledOpened;
}
