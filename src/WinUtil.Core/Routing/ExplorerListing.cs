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
