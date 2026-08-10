using KOTU.Core.Contracts;

namespace KOTU.Module.Image;

/// <summary>이미지 뷰어 모듈. Core의 IModule 계약 구현.</summary>
public sealed class ImageModule : IModule
{
    public string Id => "image";

    public string DisplayName => "Image"; // A52: 단수형 확정 (v0.38.0 복수형 지정을 대체)

    public string BrandName => "KOTU-image";

    public string IconGlyph => "\uE8B9"; // Picture

    public IReadOnlyList<string> SupportedExtensions => ImageFolderNavigator.SupportedExtensions;

    public object CreateView(OpenContext context) => new ImageViewerView(context);
}
