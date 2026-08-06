using WinUtil.Core.Contracts;

namespace WinUtil.Module.Hardware;

/// <summary>하드웨어 모듈 (Phase 5a: 정보 표시). 파일을 다루지 않으므로 담당 확장자는 없다.</summary>
public sealed class HardwareModule : IModule
{
    public string Id => "hardware";

    public string DisplayName => "H/W Info"; // v0.28.1 사용자 요청 (이전: Hardware-info)

    public string BrandName => "ZP-info";

    public string IconGlyph => "\uE950"; // Component (칩 모양)

    public IReadOnlyList<string> SupportedExtensions => [];

    public object CreateView(OpenContext context) => new HardwareView(context);
}
