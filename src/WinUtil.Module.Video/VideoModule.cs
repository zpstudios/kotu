using WinUtil.Core.Contracts;
using WinUtil.Core.Settings;

namespace WinUtil.Module.Video;

/// <summary>동영상 플레이어 모듈 (설계 4.3). libvlc(LibVLCSharp) 기반 — 코덱 별도 설치 불필요.</summary>
public sealed class VideoModule : IModule
{
    /// <summary>
    /// 동영상 확장자(소문자, 점 포함). libvlc가 전부 재생 가능.
    /// 음악 확장자는 오디오 모듈(KOTU-audio)로 이관(A10, v0.75.0).
    /// </summary>
    public static readonly IReadOnlyList<string> Extensions =
    [
        ".mp4", ".mkv", ".avi", ".webm", ".mov", ".wmv", ".m4v",
        ".mpg", ".mpeg", ".ts", ".m2ts", ".flv", ".3gp", ".ogv",
    ];

    private readonly ISettingsService _settings;

    public VideoModule(ISettingsService settings) => _settings = settings;

    public string Id => "video";

    public string DisplayName => "Video"; // A52: 단수형 확정 (v0.38.0 복수형 지정을 대체)

    public string BrandName => "KOTU-video";

    public string IconGlyph => "\uE714"; // Video

    public IReadOnlyList<string> SupportedExtensions => Extensions;

    public object CreateView(OpenContext context) => new VideoPlayerView(context, _settings);
}
