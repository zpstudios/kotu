namespace KOTU.Core.Contracts;

/// <summary>
/// A159: 모듈 뷰가 <b>열려 있는 콘텐츠를 닫아 달라</b>고 셸에 요청하는 계약 — 압축 모듈 하단 바
/// Back 버튼의 루트 클릭(내부 백스택이 비어 더 올라갈 층이 없는 상태)이 유일한 발화 지점이다.
/// 뷰는 닫기를 스스로 수행할 수 없다(뷰 교체·제목 복귀·트레이·하단 바 정리가 전부 셸 몫) —
/// 셸은 Esc 말단 층(A202)과 같은 단일 실행부(TryCloseContent — 미저장 가드 포함)로 처리하고,
/// 닫을 콘텐츠가 없으면(빈 상태 S1) 무동작이다. 뷰는 결과를 되돌려받지 않는다.
/// </summary>
public interface IContentCloseRequestSource
{
    /// <summary>열려 있는 콘텐츠를 닫아 달라는 요청이 오면 발생한다(UI 스레드 보장 없음).</summary>
    event Action? ContentCloseRequested;
}
