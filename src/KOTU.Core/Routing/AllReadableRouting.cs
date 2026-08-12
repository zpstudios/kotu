using KOTU.Core.Contracts;

namespace KOTU.Core.Routing;

/// <summary>
/// All Readable 통합 모듈(A59)의 순수 계산 — 자식 후보 목록 · 지원 확장자 합집합 ·
/// 확장자 → 자식 모듈 선택. UI에 의존하지 않아 Core에 두고 단위 테스트한다.
/// <see cref="FileTypeRouter"/>와 달리 상태를 갖지 않는다 — 셸의 라우팅 우선순위·사용자 재정의는
/// 전혀 건드리지 않고, 이 모듈 안에서만 쓰는 선택 규칙이다.
/// </summary>
public static class AllReadableRouting
{
    /// <summary>
    /// 자식이 될 수 있는 모듈: 담당 확장자가 있는 파일 모듈만.
    /// 자기 자신(hostId)을 빼서 중첩 재귀(자기가 자기를 자식으로 얹는 것)를 원천 차단하고,
    /// 파일을 다루지 않는 정보(H/W) 모듈은 확장자가 0개라 자연히 빠진다.
    /// 순서는 넘겨받은 순서 그대로 — 그게 곧 <see cref="ResolveChild"/>의 우선순위다.
    /// </summary>
    public static IReadOnlyList<IModule> ChildModules(IEnumerable<IModule> modules, string hostId) =>
        modules
            .Where(m => m.SupportedExtensions.Count > 0
                        && !string.Equals(m.Id, hostId, StringComparison.OrdinalIgnoreCase))
            .ToList();

    /// <summary>
    /// 자식 모듈들이 담당하는 확장자의 합집합(소문자·점 포함으로 정규화, 중복 제거, 첫 등장 순서 유지).
    /// 좌측 파일 리스트 오버레이(A57 ③)와 중앙 빈 상태 탐색기의 필터가 되는 값이다 —
    /// 모듈이 바뀌어도 필터는 "전 모듈 지원 확장자"라는 A59 요구가 이 한 값으로 성립한다.
    /// </summary>
    public static IReadOnlyList<string> UnionExtensions(IEnumerable<IModule> modules)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var result = new List<string>();
        foreach (var module in modules)
        {
            foreach (var ext in module.SupportedExtensions)
            {
                var normalized = Normalize(ext);
                if (normalized.Length > 0 && seen.Add(normalized)) result.Add(normalized);
            }
        }
        return result;
    }

    /// <summary>
    /// 파일 경로의 담당 자식 모듈을 찾는다. 없으면 null(= 셸이 평소 라우팅으로 처리).
    /// 목록 순서가 우선순위인 것도 <see cref="FileTypeRouter.Resolve"/>와 같은 규칙이라,
    /// 같은 확장자를 두 모듈이 주장해도 셸과 이 모듈의 선택이 어긋나지 않는다.
    /// </summary>
    public static IModule? ResolveChild(IEnumerable<IModule> children, string filePath)
    {
        var ext = Normalize(Path.GetExtension(filePath));
        if (ext.Length == 0) return null;
        return children.FirstOrDefault(m =>
            m.SupportedExtensions.Contains(ext, StringComparer.OrdinalIgnoreCase));
    }

    /// <summary>확장자 정규화(소문자 + 앞의 점 보장) — <see cref="FileTypeRouter"/>와 같은 규칙.</summary>
    private static string Normalize(string ext)
    {
        ext = ext.Trim().ToLowerInvariant();
        return ext.Length > 0 && ext[0] != '.' ? "." + ext : ext;
    }
}
