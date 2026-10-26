using KOTU.Core.Settings;

namespace KOTU.Module.Record;

internal sealed record RecordingFolderSpec(string ConfiguredPath);
internal sealed record RecordingFolderChoice(RecordingFolderSpec Spec, bool NeedsSave);

internal static class RecordingFolderSettings
{
    public const string Key = "record.saveFolder";

    public static string KnownFolder(Environment.SpecialFolder folder) =>
        Environment.GetFolderPath(folder, Environment.SpecialFolderOption.DoNotVerify);

    public static string Normalize(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !Path.IsPathFullyQualified(path))
            throw new IOException("The recording folder must be a valid absolute path.");
        try
        {
            var full = Path.GetFullPath(path);
            var root = Path.GetPathRoot(full)!;
            foreach (var part in full[root.Length..].Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar))
                if (part.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
                    throw new IOException("The recording folder contains invalid path characters.");
            return Path.TrimEndingDirectorySeparator(full);
        }
        catch (ArgumentException ex) { throw new IOException("The recording folder is not a valid path.", ex); }
        catch (NotSupportedException ex) { throw new IOException("The recording folder is not a supported path.", ex); }
    }

    public static RecordingFolderChoice Resolve(ISettingsService settings,
        Func<Environment.SpecialFolder, string>? knownFolder = null)
    {
        var configured = settings.Get<string?>(Key, null);
        if (configured is not null)
            return new(new(configured.Length == 0 ? "" : Normalize(configured)), false);

        knownFolder ??= KnownFolder;
        var audio = ReadLegacy(settings.Get<string?>("record.audioFolder", null), Environment.SpecialFolder.MyMusic, knownFolder);
        var video = ReadLegacy(settings.Get<string?>("record.videoFolder", null), Environment.SpecialFolder.MyVideos, knownFolder);
        if (audio.Default && video.Default) return new(new(""), true);

        // 분류 이름보다 실제 목적지의 동등성을 먼저 비교한다. 두 기본값의 전환만 예외다.
        var audioPath = audio.Path ?? LegacyDefault(Environment.SpecialFolder.MyMusic, knownFolder);
        var videoPath = video.Path ?? LegacyDefault(Environment.SpecialFolder.MyVideos, knownFolder);
        return new(new(string.Equals(audioPath, videoPath, StringComparison.OrdinalIgnoreCase) ? audioPath : ""), true);
    }

    private static (bool Default, string? Path) ReadLegacy(string? value, Environment.SpecialFolder kind,
        Func<Environment.SpecialFolder, string> knownFolder)
    {
        if (string.IsNullOrWhiteSpace(value)) return (true, null);
        var path = Normalize(value);
        string oldDefault;
        try { oldDefault = LegacyDefault(kind, knownFolder); }
        catch { return (false, path); } // 조회 실패로 명시된 절대 경로를 기본값이라고 추측하지 않는다.
        return (string.Equals(path, oldDefault, StringComparison.OrdinalIgnoreCase), path);
    }

    private static string LegacyDefault(Environment.SpecialFolder kind, Func<Environment.SpecialFolder, string> knownFolder)
    {
        try { return Normalize(Path.Combine(Normalize(knownFolder(kind)), "KOTU")); }
        catch (Exception ex) { throw new IOException("Windows could not locate a previous recording folder. Choose a common save folder.", ex); }
    }
}
