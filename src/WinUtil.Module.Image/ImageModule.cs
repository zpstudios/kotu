using WinUtil.Core.Contracts;

namespace WinUtil.Module.Image;

/// <summary>이미지 뷰어 모듈. Core의 IModule 계약 구현.</summary>
public sealed class ImageModule : IModule
{
    public string Id => "image";

    public string DisplayName => "Images"; // v0.38.0 복수형 (사용자 요청)

    public string BrandName => "ZP-image";

    public string IconGlyph => "\uE8B9"; // Picture

    public IReadOnlyList<string> SupportedExtensions => ImageFolderNavigator.SupportedExtensions;

    public object CreateView(OpenContext context) => new ImageViewerView(context);
}
