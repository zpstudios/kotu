using KOTU.Core.Contracts;
using KOTU.Core.Settings;

namespace KOTU.Module.Document;

/// <summary>
/// 문서 모듈(v0.44.0 뷰어 → A37 편집·저장 승격 → A16 PDF 뷰어).
/// 플레인 텍스트(txt·md·log·ini)는 편집·저장, PDF는 보기(Windows.Data.Pdf 렌더).
/// HWP·오픈오피스는 뷰어가 생기면 확장.
/// </summary>
public sealed class DocumentModule : IModule
{
    /// <summary>담당 확장자(.ini는 A37, .pdf는 A16에서 뷰어와 함께 추가).
    /// HWP·오픈오피스는 뷰어가 생기면 추가한다(스텁에 연결하지 않는다).</summary>
    public static readonly string[] Extensions = [".txt", ".md", ".markdown", ".log", ".ini", ".pdf"];

    // ---------- A171: 본문 컬럼 최대 폭 설정 (v0.173.0) ----------
    // 설정 화면(셸)이 쓰고 DocumentView가 읽는 값이라 양쪽이 함께 볼 수 있는 모듈에 둔다.
    // 셸이 모듈의 public static을 참조하는 선례 = ExplorerPane.xaml.cs:787(AudioModule.Extensions).
    // 이 배치가 `document.*` 설정 키의 첫 사례다(그전까지 문서 모듈에는 설정 키가 없었다).

    /// <summary>A171: 본문 컬럼 최대 폭 설정 키. 값은 px(int)이고 <b>0 = 제한 없음</b>이다.</summary>
    public const string EditorMaxWidthSettingKey = "document.editorMaxWidth";

    /// <summary>
    /// A171 기본값(px) = 현행 하드코딩 폭 그대로. <b>DocumentView.xaml의 MaxWidth 900 두 곳과
    /// 반드시 같은 값</b>이어야 한다 — 설정이 없을 때의 코드 경로와 XAML 초기값이 갈리지 않게.
    /// </summary>
    public const int DefaultEditorMaxWidth = 900;

    /// <summary>
    /// A171 선택지(px, 설정 화면 콤보 순서). "Unlimited"는 이 목록에 없다 — 저장값 0으로 표현하고
    /// 적용 시 <c>double.PositiveInfinity</c>가 된다(DocumentView.ApplyEditorMaxWidth).
    /// </summary>
    public static readonly int[] EditorMaxWidths = [700, 900, 1200];

    private readonly ISettingsService _settings;

    /// <summary>A171: 편집기 폭 설정을 뷰에 넘기기 위한 주입(선례 = AudioModule).</summary>
    public DocumentModule(ISettingsService settings) => _settings = settings;

    public string Id => "document";

    public string DisplayName => "Document"; // A52: 단수형 확정

    public string BrandName => "KOTU-doc";

    public string IconGlyph => "\uE8A5"; // Document

    public IReadOnlyList<string> SupportedExtensions => Extensions;

    public object CreateView(OpenContext context) => new DocumentView(context, _settings);
}
