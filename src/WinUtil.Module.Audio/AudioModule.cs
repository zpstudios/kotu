using WinUtil.Core.Contracts;
using WinUtil.Core.Settings;

namespace WinUtil.Module.Audio;

/// <summary>
/// 음악 플레이어 모듈 (A10, v0.75.0 — 비디오 모듈에서 분리).
/// libvlc(LibVLCSharp) 기반, 파형 시각화 상시 표시.
/// </summary>
public sealed class AudioModule : IModule
{
    /// <summary>음악 확장자(소문자, 점 포함) — 비디오 모듈에서 이관(A10).</summary>
    public static readonly IReadOnlyList<string> Extensions =
    [
        ".mp3", ".flac", ".wav", ".ogg", ".opus", ".m4a", ".aac", ".wma",
    ];

    private readonly ISettingsService _settings;

    public AudioModule(ISettingsService settings) => _settings = settings;

    public string Id => "audio";

    public string DisplayName => "Music"; // 용도(음악 감상) 중심 — Videos·Images와 같은 결 (사용자 확정)

    public string BrandName => "ZP-audio";

    public string IconGlyph => "\uE8D6"; // MusicInfo (♪)

    public IReadOnlyList<string> SupportedExtensions => Extensions;

    public object CreateView(OpenContext context) => new AudioPlayerView(context, _settings);
}
