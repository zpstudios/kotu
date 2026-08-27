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

    /// <summary>
    /// A247: "이 창은 그대로 두고 새 창에서 무제를 열어 달라"는 요청(UI 스레드 보장 없음) —
    /// New 버튼 미저장 분기의 "Open in new instance" 선택. 소비자 = 셸(ShowModule 배선) →
    /// WindowManager.OpenUntitledDocumentInNewWindow. 발화한 뷰의 편집 상태는 완전 무변경이다.
    /// </summary>
    event Action? UntitledWindowRequested;

    /// <summary>
    /// A247: 무제 콘텐츠 표시를 시작한다 — 셸→뷰 무제 개시 진입로(새 창 경로:
    /// MainWindow.OpenUntitledDocument가 문서 모듈을 띄운 직후 부른다). 미저장 가드는 걸지
    /// 않으므로 호출자 책임 = 빈 상태 뷰(새 창)에서만 부를 것. 성공하면 UntitledOpened가 난다.
    /// </summary>
    void OpenUntitled();
}
