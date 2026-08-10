namespace KOTU.Module.Video;

/// <summary>
/// 동영상과 같은 폴더에서 자막 파일 후보를 찾는다. UI 비의존 — 단위 테스트 대상.
/// 우선순위: 정확히 같은 이름(movie.srt) → 접미사 변형(movie.ko.srt, 짧은 것 먼저).
/// </summary>
public static class SubtitleFileLocator
{
    /// <summary>지원 자막 확장자(소문자, 점 포함).</summary>
    public static readonly IReadOnlyList<string> SubtitleExtensions =
        [".srt", ".smi", ".ass", ".ssa", ".sub", ".vtt"];

    /// <param name="videoPath">동영상 파일 경로.</param>
    /// <param name="enumerateFiles">디렉터리 → 파일 열거. 테스트에서 가짜 주입 가능(기본: 실제 파일 시스템).</param>
    public static IReadOnlyList<string> Find(
        string videoPath,
        Func<string, IEnumerable<string>>? enumerateFiles = null)
    {
        enumerateFiles ??= Directory.EnumerateFiles;

        var dir = Path.GetDirectoryName(videoPath);
        if (string.IsNullOrEmpty(dir)) dir = ".";
        var baseName = Path.GetFileNameWithoutExtension(videoPath);

        var candidates = new List<(string Path, int Rank, string Name)>();
        foreach (var file in enumerateFiles(dir))
        {
            var ext = Path.GetExtension(file);
            if (!SubtitleExtensions.Contains(ext, StringComparer.OrdinalIgnoreCase)) continue;

            var name = Path.GetFileNameWithoutExtension(file);
            if (string.Equals(name, baseName, StringComparison.OrdinalIgnoreCase))
                candidates.Add((file, 0, name));
            else if (name.StartsWith(baseName + ".", StringComparison.OrdinalIgnoreCase))
                candidates.Add((file, 1, name));
        }

        return candidates
            .OrderBy(c => c.Rank)
            .ThenBy(c => c.Name.Length)
            .ThenBy(c => c.Name, StringComparer.OrdinalIgnoreCase)
            .Select(c => c.Path)
            .ToList();
    }
}
