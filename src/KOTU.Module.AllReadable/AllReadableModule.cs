using KOTU.Core.Contracts;
using KOTU.Core.Routing;

namespace KOTU.Module.AllReadable;

/// <summary>
/// All Readable 통합 모듈 (A59, v0.113.0) — 지원하는 모든 형식을 한 창에서 연다.
/// 파일을 열면 <b>센터와 하단 바만</b> 그 확장자를 담당하는 모듈 뷰로 갈아 끼우고(중첩 호스팅),
/// 좌/우 오버레이·시작 메뉴·창 아이덴티티는 이 모듈이 계속 소유한다.
/// 담당 확장자는 자식 모듈들의 <b>합집합</b>이라 오버레이·빈 상태 탐색기 필터가 자동으로
/// "전 모듈 지원 확장자"가 된다(A57 ③의 주입 지점을 그대로 쓴다).
/// 라우팅 우선순위상 <b>맨 마지막에 등록</b>해야 한다 — 그래야 탐색기 더블클릭 등 파일 인자
/// 진입이 종전처럼 전용 모듈로 간다(<see cref="FileTypeRouter.Resolve"/>는 등록 순서가 우선순위).
/// </summary>
public sealed class AllReadableModule : IModule
{
    /// <summary>모듈 ID. 셸의 번호 키(7)·시작 메뉴 위치·창 아이콘 매핑이 이 값을 쓴다.</summary>
    public const string ModuleId = "allreadable";

    /// <summary>자식 후보(파일 모듈만, 자기 자신·정보 모듈 제외) — 뷰가 확장자로 골라 쓴다.</summary>
    private readonly IReadOnlyList<IModule> _children;

    /// <param name="modules">이미 등록된 모듈들(셸이 넘긴다). 자식 후보와 확장자 합집합을 여기서 뽑는다.</param>
    public AllReadableModule(IEnumerable<IModule> modules)
    {
        _children = AllReadableRouting.ChildModules(modules, ModuleId);
        SupportedExtensions = AllReadableRouting.UnionExtensions(_children);
    }

    public string Id => ModuleId;

    public string DisplayName => "All Readable"; // A59 확정 표시명

    public string BrandName => "KOTU-all"; // 다른 모듈과 같은 KOTU-* 형식 (KOTU-doc·KOTU-info와 같은 축약)

    public string IconGlyph => "\uE71D"; // AllApps — "모든 형식"이라는 뜻이 그대로 맞는 글리프

    /// <summary>자식 모듈 담당 확장자의 합집합(생성자에서 1회 계산 — 등록 후 바뀌지 않는다).</summary>
    public IReadOnlyList<string> SupportedExtensions { get; }

    /// <summary>
    /// 확장자 연결(A25·A38)과 설정 연결 섹션(A35)에서 제외한다 — 담당 확장자가 자식 모듈들과
    /// 전부 겹쳐 ProgID·UserChoice·Capabilities를 서로 덮어쓰기 때문이다. 파일 아이콘도 없다.
    /// 그래서 탐색기 더블클릭은 종전대로 전용 모듈로 열린다(A59 확정).
    /// </summary>
    public bool RegistersFileAssociations => false;

    public object CreateView(OpenContext context) => new AllReadableView(context, _children);
}
