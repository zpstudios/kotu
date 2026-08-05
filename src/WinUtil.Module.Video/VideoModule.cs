using WinUtil.Core.Contracts;
using WinUtil.Core.Settings;

namespace WinUtil.Module.Video;

/// <summary>동영상·음악 플레이어 모듈 (설계 4.3). libvlc(LibVLCSharp) 기반 — 코덱 별도 설치 불필요.</summary>
public sealed class VideoModule : IModule
{
    /// <summary>동영상 확장자(소문자, 점 포함). libvlc가 전부 재생 가능.</summary>
    public static readonly IReadOnlyList<string> VideoExtensions =
    [
        ".mp4", ".mkv", ".avi", ".webm", ".mov", ".wmv", ".m4v",
        ".mpg", ".mpeg", ".ts", ".m2ts", ".flv", ".3gp", ".ogv",
    ];

    /// <summary>음악 확장자 — 같은 플레이어로 재생한다(영상 표면에는 ♪ 오버레이 표시).</summary>
    public static readonly IReadOnlyList<string> AudioExtensions =
    [
        ".mp3", ".flac", ".wav", ".ogg", ".opus", ".m4a", ".aac", ".wma",
    ];

    /// <summary>이 모듈이 담당하는 전체 확장자(라우팅·열기 대화상자·파일 연결용).</summary>
    public static readonly IReadOnlyList<string> Extensions =
        [.. VideoExtensions, .. AudioExtensions];

    /// <summary>음악 파일 여부(확장자 기준). 오디오 전용 표시 판단에 쓴다.</summary>
    public static bool IsAudioFile(string path) =>
        AudioExtensions.Contains(Path.GetExtension(path).ToLowerInvariant());

    private readonly ISettingsService _settings;

    public VideoModule(ISettingsService settings) => _settings = settings;

    public string Id => "video";

    public string DisplayName => "동영상";

    public string IconGlyph => "\uE714"; // Video

    public IReadOnlyList<string> SupportedExtensions => Extensions;

    public object CreateView(OpenContext context) => new VideoPlayerView(context, _settings);
}
