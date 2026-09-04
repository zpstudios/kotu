namespace KOTU.Core.Contracts;

/// <summary>
/// A346: 셸이 <b>"탐색기 좌 리스트가 지금 표시하고 있는 순서"</b>를 모듈 뷰에 주입하는 계약 —
/// 뷰 내부 ◀/▶ 항해가 그 순서를 그대로 따르게 한다. 종전에는 뷰가 폴더를 스스로 다시 열거해
/// 이름 자연 정렬로 목록을 만들었기 때문에, 좌 리스트가 Modified·Size 같은 다른 정렬 키나
/// 확장자 필터(A7)를 쓰고 있으면 두 순서가 갈렸다(ExplorerListing.Arrange 결과와 독립이었다).
/// <para>
/// 소비자 = 이미지 뷰어(첫 구현). 다른 모듈(영상·오디오)도 같은 계약을 구현하면 그대로 얹힌다.
/// 발화 = ① 뷰 생성 직후 1회(시드) ② 좌 리스트 표시 목록이 다시 그려질 때마다
/// (정렬 변경·필터 변경·감시 재스캔·폴더 이동 — ExplorerPane → FileListOverlay.ViewChanged).
/// 셸이 UI 스레드에서 부른다(디스패치 불필요).
/// </para>
/// <para>
/// 모듈은 셸(KOTU.App)을 참조하지 못하므로 <c>ExplorerListing.Entry</c>를 그대로 넘기지 않고
/// <b>경로 문자열 목록</b>만 넘긴다("컨트롤은 셸에·계약은 Core에·모듈은 슬롯만" —
/// <see cref="ISidePanelProvider"/>·<see cref="IContentPathChangedSource"/>와 같은 규칙).
/// </para>
/// </summary>
public interface IBrowseOrderConsumer
{
    /// <summary>
    /// 좌 리스트의 표시 순서를 주입한다.
    /// </summary>
    /// <param name="folder">그 목록이 속한 폴더의 전체 경로.</param>
    /// <param name="files">
    /// 그 순서 그대로의 <b>파일</b> 경로 전부(폴더 항목 제외 · 확장자 필터·숨김 표시 반영).
    /// 받는 쪽이 자기 모듈이 못 여는 확장자를 다시 거를 수 있다(All Readable 모듈처럼
    /// 여러 종류가 섞인 목록이 온다).
    /// </param>
    void SetBrowseOrder(string folder, IReadOnlyList<string> files);
}
