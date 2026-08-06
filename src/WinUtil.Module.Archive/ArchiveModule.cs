using WinUtil.Core.Contracts;
using WinUtil.Core.Settings;

namespace WinUtil.Module.Archive;

/// <summary>압축 모듈. Core의 IModule 계약 구현. 설정은 마지막 풀기 위치 저장에 쓴다(v0.55.0).</summary>
public sealed class ArchiveModule : IModule
{
    private readonly ISettingsService _settings;

    public ArchiveModule(ISettingsService settings) => _settings = settings;

    /// <summary>이 모듈이 담당하는 확장자(소문자, 점 포함).</summary>
    public static readonly IReadOnlyList<string> Extensions =
        [".zip", ".7z", ".rar", ".tar", ".gz", ".tgz", ".bz2", ".xz"];

    public string Id => "archive";

    public string DisplayName => "ZIP"; // v0.38.0 대문자 (사용자 요청; v0.26.0 "Archive"→"zip"에서 변경)

    public string BrandName => "ZP-zip";

    public string IconGlyph => "\uF012"; // ZipFolder

    public IReadOnlyList<string> SupportedExtensions => Extensions;

    public object CreateView(OpenContext context) => new ArchiveView(context, _settings);
}
