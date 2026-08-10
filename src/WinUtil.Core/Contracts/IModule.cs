namespace WinUtil.Core.Contracts;

/// <summary>
/// 기능 모듈(압축/이미지/동영상/하드웨어)의 계약.
/// 모듈은 Core에만 의존하며 서로 직접 참조하지 않는다.
/// </summary>
public interface IModule
{
    /// <summary>고유 ID. 예: "archive", "image", "video".</summary>
    string Id { get; }

    /// <summary>네비게이션에 표시할 이름.</summary>
    string DisplayName { get; }

    /// <summary>
    /// OS에 노출되는 모듈 브랜드명(탐색기 우클릭 메뉴·파일 연결 표시명 등).
    /// 앱 이름 "KOTU"에 기능 접미사를 붙인다. 예: "KOTU-zip".
    /// </summary>
    string BrandName { get; }

    /// <summary>
    /// 네비게이션 아이콘(Segoe Fluent/MDL2 글리프 코드).
    /// 접힌(compact) 네비게이션에서 텍스트 대신 표시된다. 기본값: 문서 아이콘.
    /// </summary>
    string IconGlyph => "\uE7C3";

    /// <summary>담당 확장자 목록(소문자, 점 포함). 예: ".jpg". 파일 라우팅에 사용.</summary>
    IReadOnlyList<string> SupportedExtensions { get; }

    /// <summary>
    /// 셸에 꽂힐 뷰를 생성한다. 반환 타입은 UI 프레임워크 비의존을 위해 object이며,
    /// 셸(WinUI 3)에서 UIElement로 캐스팅한다.
    /// </summary>
    object CreateView(OpenContext context);
}
