namespace KOTU.Core.Contracts;

/// <summary>
/// 모듈 뷰가 콘텐츠(파일)를 실제로 열어 표시하기 시작할 때 셸에 알리는 계약 (v0.25.0).
/// 셸은 이 이벤트로 빈 상태 탐색기를 내리고, 좌(폴더 리스트)·우(정보) 오버레이의
/// 기준 경로를 갱신한다. 뷰 내부의 열기 버튼·◀/▶ 탐색처럼 셸을 거치지 않는 열기도
/// 이 이벤트로 셸과 동기화된다.
/// </summary>
public interface IContentStateSource
{
    /// <summary>파일을 열어 표시하기 시작하면 그 경로와 함께 발생한다(UI 스레드 보장 없음).</summary>
    event Action<string>? ContentOpened;
}
