using WinUtil.Core.Contracts;

namespace WinUtil.Module.Document;

/// <summary>
/// 문서 뷰어 모듈(v0.44.0 — 사용자 요청: 메뉴에서 누를 수 있게 + 설정 탐색기 연결 토글).
/// 1단계는 텍스트·마크다운 원문 표시. PDF·HWP·오픈오피스는 ARCHITECTURE 계획대로 추후 확장.
/// </summary>
public sealed class DocumentModule : IModule
{
    /// <summary>1단계 담당 확장자. PDF·HWP는 뷰어가 생기면 추가한다(스텁에 연결하지 않는다).</summary>
    public static readonly string[] Extensions = [".txt", ".md", ".markdown", ".log"];

    public string Id => "document";

    public string DisplayName => "Documents";

    public string BrandName => "ZP-doc";

    public string IconGlyph => "\uE8A5"; // Document

    public IReadOnlyList<string> SupportedExtensions => Extensions;

    public object CreateView(OpenContext context) => new DocumentView(context);
}
