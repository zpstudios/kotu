using WinUtil.Core.Contracts;

namespace WinUtil.Module.Document;

/// <summary>
/// 문서 모듈(v0.44.0 뷰어 → A37 편집·저장 승격).
/// 플레인 텍스트(txt·md·log·ini)는 편집·저장까지 지원. PDF·HWP·오픈오피스는 뷰어가 생기면 확장.
/// </summary>
public sealed class DocumentModule : IModule
{
    /// <summary>담당 확장자(.ini는 A37에서 추가 — A36 설정 파일 열기의 선행).
    /// HWP·오픈오피스는 뷰어가 생기면 추가한다(스텁에 연결하지 않는다).</summary>
    public static readonly string[] Extensions = [".txt", ".md", ".markdown", ".log", ".ini"];

    public string Id => "document";

    public string DisplayName => "Documents";

    public string BrandName => "ZP-doc";

    public string IconGlyph => "\uE8A5"; // Document

    public IReadOnlyList<string> SupportedExtensions => Extensions;

    public object CreateView(OpenContext context) => new DocumentView(context);
}
