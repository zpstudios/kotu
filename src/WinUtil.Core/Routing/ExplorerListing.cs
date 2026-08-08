namespace WinUtil.Core.Routing;

/// <summary>
/// 내장 탐색기(v0.25.0)의 폴더 목록 로직. UI 비의존 — 폴더는 전부, 파일은 담당 확장자만
/// (사용자 확정: "폴더 + 담당 확장자만"). 숨김/시스템 항목은 제외한다.
/// </summary>
public static class ExplorerListing
{
    /// <summary>탐색기 한 항목. 폴더면 Size는 0.</summary>
    public sealed record Entry(string Path, string Name, bool IsFolder, long Size, DateTime Modified);

    /// <summary>파일명이 확장자 목록(소문자, 점 포함)에 해당하는지. 대소문자 무시.</summary>
    public static bool MatchesExtension(string fileName, IReadOnlyList<string> extensions) =>
        extensions.Contains(System.IO.Path.GetExtension(fileName).ToLowerInvariant());

    /// <summary>
    /// 폴더 내용을 나열한다: 폴더 먼저, 그다음 확장자 일치 파일, 각각 이름순.
    /// maxItems로 초대형 폴더의 UI 폭주를 막는다.
    /// </summary>
    public static IReadOnlyList<Entry> List(string folder, IReadOnlyList<string> extensions, int maxItems = 2000)
    {
        var info = new DirectoryInfo(folder);
        var result = new List<Entry>();

        foreach (var d in info.EnumerateDirectories()
                     .Where(d => !IsHiddenOrSystem(d.Attributes))
                     .OrderBy(d => d.Name, StringComparer.OrdinalIgnoreCase))
        {
            if (result.Count >= maxItems) return result;
            result.Add(new Entry(d.FullName, d.Name, true, 0, d.LastWriteTime));
        }

        foreach (var f in info.EnumerateFiles()
                     .Where(f => !IsHiddenOrSystem(f.Attributes) && MatchesExtension(f.Name, extensions))
                     .OrderBy(f => f.Name, StringComparer.OrdinalIgnoreCase))
        {
            if (result.Count >= maxItems) return result;
            result.Add(new Entry(f.FullName, f.Name, false, f.Length, f.LastWriteTime));
        }

        return result;
    }

    /// <summary>우측 리스트 정렬 키 (A5). 설정 저장 문자열은 소문자 이름과 수동 동기.</summary>
    public enum SortKey
    {
        Name,
        Size,
        Modified,
    }

    /// <summary>
    /// 정렬·필터를 적용한 표시용 목록을 만든다 (A5·A7). 폴더 먼저 규칙은 유지.
    /// Name=이름 오름차순, Size=큰 것부터(폴더는 크기가 없어 이름순), Modified=최신부터. 동률은 이름순.
    /// hiddenExtensions(소문자, 점 포함)에 있는 확장자의 파일은 뺀다 — 폴더는 항상 남긴다.
    /// </summary>
    public static IReadOnlyList<Entry> Arrange(
        IReadOnlyList<Entry> entries, SortKey key, IReadOnlyCollection<string>? hiddenExtensions = null)
    {
        var folders = entries.Where(e => e.IsFolder);
        var files = entries.Where(e => !e.IsFolder);
        if (hiddenExtensions is { Count: > 0 })
            files = files.Where(f =>
                !hiddenExtensions.Contains(System.IO.Path.GetExtension(f.Name).ToLowerInvariant()));

        var byName = StringComparer.OrdinalIgnoreCase;
        (folders, files) = key switch
        {
            SortKey.Size => (
                folders.OrderBy(e => e.Name, byName),
                files.OrderByDescending(e => e.Size).ThenBy(e => e.Name, byName)),
            SortKey.Modified => (
                folders.OrderByDescending(e => e.Modified).ThenBy(e => e.Name, byName),
                files.OrderByDescending(e => e.Modified).ThenBy(e => e.Name, byName)),
            _ => (
                folders.OrderBy(e => e.Name, byName),
                files.OrderBy(e => e.Name, byName)),
        };
        return [.. folders, .. files];
    }

    /// <summary>재생 길이 표시용 텍스트 (A6): 1시간 미만 m:ss, 이상 h:mm:ss. 1초 미만은 빈 문자열.</summary>
    public static string FormatDuration(TimeSpan duration) => duration.TotalSeconds < 1
        ? string.Empty
        : duration.TotalHours >= 1
            ? $"{(int)duration.TotalHours}:{duration.Minutes:00}:{duration.Seconds:00}"
            : $"{duration.Minutes}:{duration.Seconds:00}";

    /// <summary>파일 크기 표시용 텍스트 (B/KB/MB/GB).</summary>
    public static string FormatSize(long bytes) => bytes switch
    {
        < 1024 => $"{bytes} B",
        < 1024 * 1024 => $"{bytes / 1024.0:0.#} KB",
        < 1024L * 1024 * 1024 => $"{bytes / 1024.0 / 1024.0:0.#} MB",
        _ => $"{bytes / 1024.0 / 1024.0 / 1024.0:0.##} GB",
    };

    private static bool IsHiddenOrSystem(FileAttributes attributes) =>
        (attributes & (FileAttributes.Hidden | FileAttributes.System)) != 0;
}
