namespace KOTU.Core.Contracts;

/// <summary>
/// A279: 모듈 뷰가 <b>이미 열려 있는 콘텐츠의 대상 경로 자체를 바꿨다</b>고 셸에 알리는 계약 —
/// 문서 모듈의 'Save as...'(저장 성공으로 편집 대상이 새 파일로 갈린 경우)와 무제 문서의 첫 저장이
/// 유일한 발화 지점이다. <see cref="IContentStateSource.ContentOpened"/>와 서명이 같지만 의미가
/// 다르다: 저쪽은 "새 콘텐츠를 열었다"(뷰 내부 ◀/▶ 탐색 포함)이고 이쪽은 "보고 있던 그 콘텐츠의
/// 파일이 갈렸다"다. 셸은 이 이벤트에서만 창 제목을 새 파일 이름으로 다시 만든다 — 뷰 내부 탐색의
/// 제목 갱신은 별개 사안이라 ContentOpened 쪽 동작은 건드리지 않는다.
/// 덮어쓰기 저장은 경로가 그대로라 발화하지 않는다.
/// </summary>
public interface IContentPathChangedSource
{
    /// <summary>열려 있는 콘텐츠의 경로가 새 경로로 바뀌면 발생한다(UI 스레드 보장 없음).</summary>
    event Action<string>? ContentPathChanged;
}
