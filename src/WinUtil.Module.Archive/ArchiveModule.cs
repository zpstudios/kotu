using WinUtil.Core.Contracts;

namespace WinUtil.Module.Archive;

/// <summary>압축 모듈. Core의 IModule 계약 구현.</summary>
public sealed class ArchiveModule : IModule
{
    /// <summary>이 모듈이 담당하는 확장자(소문자, 점 포함).</summary>
    public static readonly IReadOnlyList<string> Extensions =
        [".zip", ".7z", ".rar", ".tar", ".gz", ".tgz", ".bz2", ".xz"];

    public string Id => "archive";

    public string DisplayName => "zip"; // v0.26.0: "Archive"가 압축임을 못 알아보는 사용자 배려 (사용자 요청)

    public string BrandName => "ZP-zip";

    public string IconGlyph => "\uF012"; // ZipFolder

    public IReadOnlyList<string> SupportedExtensions => Extensions;

    public object CreateView(OpenContext context) => new ArchiveView(context);
}
