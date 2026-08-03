using WinUtil.Core.Contracts;

namespace WinUtil.Core.Routing;

/// <summary>
/// 확장자 → 담당 모듈 결정. 앱의 중심 라우팅 로직.
/// 등록 순서가 우선순위이며, 사용자 설정으로 확장자별 재정의(override) 가능.
/// </summary>
public sealed class FileTypeRouter
{
    private readonly List<IModule> _modules = [];
    private readonly Dictionary<string, string> _overrides = new(StringComparer.OrdinalIgnoreCase);

    public IReadOnlyList<IModule> Modules => _modules;

    public void Register(IModule module)
    {
        if (_modules.Any(m => m.Id == module.Id))
            throw new InvalidOperationException($"중복 모듈 ID: {module.Id}");
        _modules.Add(module);
    }

    /// <summary>특정 확장자를 특정 모듈이 처리하도록 재정의한다. 예: (".gif", "video")</summary>
    public void SetOverride(string extension, string moduleId) =>
        _overrides[Normalize(extension)] = moduleId;

    /// <summary>파일 경로의 담당 모듈을 찾는다. 없으면 null.</summary>
    public IModule? Resolve(string filePath)
    {
        var ext = Normalize(Path.GetExtension(filePath));
        if (ext.Length == 0) return null;

        if (_overrides.TryGetValue(ext, out var id))
        {
            var overridden = _modules.FirstOrDefault(m => m.Id == id);
            if (overridden is not null) return overridden;
        }

        return _modules.FirstOrDefault(m =>
            m.SupportedExtensions.Contains(ext, StringComparer.OrdinalIgnoreCase));
    }

    private static string Normalize(string ext)
    {
        ext = ext.Trim().ToLowerInvariant();
        return ext.Length > 0 && ext[0] != '.' ? "." + ext : ext;
    }
}
