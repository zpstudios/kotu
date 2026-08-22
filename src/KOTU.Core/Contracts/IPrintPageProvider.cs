namespace KOTU.Core.Contracts;

/// <summary>
/// 인쇄 한 세션의 페이지 규격 (A211 배치 1). 셸 PrintHost가 OS 인쇄 대화상자의
/// PrintPageDescription(96DPI DIP 좌표계)에서 만들어 공급자에게 넘긴다 — 계약은 UI 프레임워크에
/// 기대지 않으므로(BCL만 — IContentInfoProvider와 같은 규칙) WinRT 구조체 대신 이 record를 쓴다.
/// Page* = 용지 전체 크기, Imageable* = 인쇄 가능 영역(용지 좌상단 기준 오프셋 + 크기) —
/// 페이지 요소는 Page 크기로 만들고 내용은 Imageable 사각형 안에 앉히는 것이 기본이다.
/// DpiX/DpiY = 프린터 실해상도. 비트맵 공급원(PDF·이미지)은 화면 배율이 아니라 이 값 기준으로
/// 렌더 폭을 잡아야 인쇄물이 선명하다(A211 조사 §1-ⓒ Windows.Data.Pdf 항).
/// </summary>
public sealed record PrintPageSpec(
    double PageWidth, double PageHeight,
    double ImageableX, double ImageableY,
    double ImageableWidth, double ImageableHeight,
    uint DpiX, uint DpiY);

/// <summary>
/// 모듈 뷰가 인쇄 페이지를 공급하는 계약 (A211 배치 1, v0.220.0 — 셸 PrintHost가 소비한다).
/// 구현 예정 = 이미지(배치 2)·PDF(배치 3)·문서 텍스트(배치 4)·마크다운(배치 5) — 이 계약을
/// 구현한 뷰에서만 셸 Ctrl+P·모듈 하단 바 인쇄 버튼이 OS 표준 인쇄 대화상자를 띄운다.
/// 미구현 뷰에서는 무동작(진입점 자체가 잠잠하다 — 부록 B 78 확정).
///
/// 스레드 규약: <see cref="GetPrintPageCount"/>·<see cref="CreatePrintPageAsync"/>는 셸이
/// **그 창의 UI 스레드에서만** 부른다(PrintDocument 이벤트가 XAML 스레드로 온다 — PrintHost 참조).
/// <see cref="PrintRequested"/>만 다른 계약들처럼 UI 스레드 보장이 없다(셸이 디스패치한다).
///
/// 페이지 요소 규칙(구현 시 필수):
/// - 반환은 <c>object</c>지만 실체는 새로 조립한 WinUI <c>UIElement</c>여야 한다 — 반환 타입이
///   object인 이유는 ISidePanelProvider·IBottomBarProvider와 같다(Core = UI 비의존, 셸이 캐스팅).
/// - **화면 요소를 그대로 돌려주지 말 것** — WinUI 요소는 부모가 하나뿐이라(v0.174.1 교훈)
///   호출마다 인쇄 전용 요소를 새로 만든다. 넘긴 참조는 셸이 쓰고 버린다(대용량 메모리 규칙).
/// - 색은 명시 지정(검정 글자·흰 배경) — 테마 브러시가 다크 테마에서 흰 글자로 풀리면
///   종이에 아무것도 안 보인다.
/// </summary>
public interface IPrintPageProvider
{
    /// <summary>
    /// 지금 인쇄할 콘텐츠가 실제로 있는가. 계약 구현 여부만으로는 "지금 인쇄 가능한가"를 알 수
    /// 없어서 두는 축이다 — IPlaybackStateSource.HasPlaybackSurface와 같은 이유: 호스트 뷰
    /// (All Readable)는 영상 자식을 얹고 있어도 이 인터페이스를 구현하게 되므로, 인쇄 가능한
    /// 자식(문서·사진)이 전면일 때만 true를 내준다. 빈 상태(파일 없음)도 false.
    /// </summary>
    bool CanPrintNow { get; }

    /// <summary>
    /// OS 인쇄 큐·대화상자에 표시할 작업 이름 — 보통 파일 이름(무제 문서는 표시 제목).
    /// 셸이 세션 시작 시점(UI 스레드)에 1회 읽어 스냅샷한다. 비면 셸이 앱 이름으로 대체한다.
    /// </summary>
    string PrintJobName { get; }

    /// <summary>
    /// 모듈 하단 바의 인쇄 버튼(배치 2~5에서 모듈별 추가)이 셸 인쇄를 요청하는 신호.
    /// 셸(ShowModule)이 구독해 Ctrl+P와 같은 경로(MainWindow.RequestPrint)로 흘린다 —
    /// 버튼 쪽 배선은 이 이벤트 발화 하나로 끝난다. UI 스레드 보장 없음(다른 계약들과 동일).
    /// </summary>
    event Action? PrintRequested;

    /// <summary>
    /// 이 규격일 때의 총 페이지 수. PrintDocument.Paginate 시점에 UI 스레드에서 불린다 —
    /// 무겁게 만들지 말 것(§11 UI 매끄러움). 0 이하를 돌려주면 셸이 안내 페이지 1장으로 대체한다.
    /// </summary>
    int GetPrintPageCount(PrintPageSpec spec);

    /// <summary>
    /// pageNumber(1-base — PrintDocument의 페이지 번호 규약)번째 인쇄 페이지 요소를 만든다.
    /// 미리보기와 본인쇄 양쪽에서 페이지 단위로 불린다(지연 렌더 전제 — PDF는 요청 페이지만
    /// 렌더하면 된다). null 또는 예외면 셸이 안내 페이지로 대체한다(인쇄 파이프는 계속 간다).
    /// </summary>
    Task<object?> CreatePrintPageAsync(int pageNumber, PrintPageSpec spec);
}
