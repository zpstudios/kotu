using KOTU.Core.Contracts;
using KOTU.Core.Settings;

namespace KOTU.Module.Document;

/// <summary>
/// 문서 모듈(v0.44.0 뷰어 → A37 편집·저장 승격 → A16 PDF 뷰어 → A190 마크다운 렌더 뷰
/// → A224 편집/뷰 모드 전 형식화).
/// 플레인 텍스트(txt·md·log·ini·html·htm)는 편집·저장, PDF는 보기(Windows.Data.Pdf 렌더).
/// 마크다운(md·markdown)은 편집에 더해 렌더 뷰(자체 최소 렌더러 — MarkdownParser/Renderer)를
/// 하단 바 토글로 오간다. A224: 비md 텍스트 형식의 뷰 모드는 잠금 뷰(에디터 그대로 +
/// IsReadOnly — HTML 렌더는 후속 확인 대기). HWP·오픈오피스는 뷰어가 생기면 확장.
/// </summary>
public sealed class DocumentModule : IModule
{
    /// <summary>담당 확장자(.ini는 A37, .pdf는 A16에서 뷰어와 함께, .html/.htm은 A224에서
    /// 편집/뷰 모드 전 형식화와 함께 추가 — 1차는 텍스트 에디터로 연다).
    /// HWP·오픈오피스는 뷰어가 생기면 추가한다(스텁에 연결하지 않는다).</summary>
    public static readonly string[] Extensions =
        [".txt", ".md", ".markdown", ".log", ".ini", ".html", ".htm", ".pdf"];

    // ---------- A181: 본문 줌 설정 (A171 폭 설정 대체) ----------
    // A171(v0.173.0)의 폭 설정 상수 3종(키 document.editorMaxWidth·기본 900·선택지)은 A181에서
    // 제거됐다 — 본문은 이제 항상 창 폭을 꽉 채우고, 크기 조절은 줌(Ctrl+휠)이 대신한다.
    // 이 키는 설정 화면 UI가 없다(Ctrl+휠로만 바뀌고 즉시 저장된다) — 셸이 참조하지 않지만
    // `document.*` 키의 정의처를 한곳에 모으는 A171의 배치는 유지한다.

    /// <summary>
    /// A181: 본문 배율 설정 키. 값은 퍼센트(int, 기본 100)이고 <b>전역 1벌</b>이다(창·파일 무관).
    /// 범위(A229 — 20~500)·단계(10)·적용(FontSize 배율)은 전부 DocumentView가 정한다.
    /// </summary>
    public const string ZoomSettingKey = "document.zoom";

    /// <summary>A215: 라인 가이드(줄 상/하단 선 — A115 ①) 표시 여부. bool, 기본 true, 전역 1벌.
    /// 하단 바 토글 버튼이 즉시 저장한다(줌과 같은 관용구 — 설정 화면 UI 없음).</summary>
    public const string ShowGuidesSettingKey = "document.showLineGuides";

    /// <summary>A215: 펑츄에이션 마커(¶·EOF — A115 ②) 표시 여부. bool, 기본 true, 전역 1벌.</summary>
    public const string ShowMarksSettingKey = "document.showMarks";

    private readonly ISettingsService _settings;

    /// <summary>A171→A181: 줌 배율 설정을 뷰에 넘기기 위한 주입(선례 = AudioModule).</summary>
    public DocumentModule(ISettingsService settings) => _settings = settings;

    public string Id => "document";

    public string DisplayName => "Document"; // A52: 단수형 확정

    public string BrandName => "KOTU-doc";

    public string IconGlyph => "\uE8A5"; // Document

    public IReadOnlyList<string> SupportedExtensions => Extensions;

    public object CreateView(OpenContext context) => new DocumentView(context, _settings);
}
