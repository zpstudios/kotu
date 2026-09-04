namespace KOTU.Core.Contracts;

/// <summary>
/// A348: 모듈 뷰가 <b>"지금 보여 주려는 파일"이 바뀌었다</b>고 셸에 즉시 알리는 계약 —
/// 로드 완료를 기다리지 않고 <b>항해 시점</b>에 발화한다(뷰 내부 ◀/▶ 탐색, 삭제 후 이웃 이동).
/// 셸은 이 통지에서 <b>가벼운 동기만</b> 한다(좌 리스트의 현재 파일 선택 표시 이동).
/// <para>
/// 이웃한 두 계약과 의미가 다르다:
/// ① <see cref="IContentStateSource.ContentOpened"/> = "열었다" — <b>로드 완료</b> 시점의 통지라
///    무거운 셸 동기(창 제목·정보 패널 캐시 무효·오버레이·드라이브 줄·아이콘)가 그쪽에 달려 있다.
///    같은 파일에 대해 이 통지 <b>뒤에 이어서</b> ContentOpened가 또 온다(로드가 성공한 경우).
/// ② A279 <see cref="IContentPathChangedSource"/> = "보고 있던 그 콘텐츠의 파일이 갈렸다"
///    (문서 'Save as...') — 콘텐츠를 다시 열지 않는 경로 변경이라 항해와는 무관하다.
/// </para>
/// <para>
/// ←/→ 오토리피트처럼 로드보다 항해가 빠른 상황에서, 중간 파일들의 로드는 현재 파일 재검증에
/// 걸려 조용히 폐기된다 — 그래서 ContentOpened는 건너뛰지만 <b>이 통지는 매 스텝 빠짐없이</b>
/// 나간다. 좌 리스트 표시가 리피트를 1:1로 따라가는 근거가 이것이다.
/// </para>
/// </summary>
public interface ICurrentPathSource
{
    /// <summary>
    /// 보여 주려는 파일이 바뀐 즉시 발생한다(UI 스레드 보장 없음 — 셸이 디스패치한다).
    /// </summary>
    event Action<string>? CurrentPathChanged;
}
