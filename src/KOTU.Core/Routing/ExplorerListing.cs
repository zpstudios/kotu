namespace KOTU.Core.Routing;

/// <summary>
/// 내장 탐색기(v0.25.0)의 폴더 목록 로직. UI 비의존 — 폴더는 전부, 파일은 담당 확장자만
/// (사용자 확정: "폴더 + 담당 확장자만"). 숨김/시스템 항목은 기본으로 제외하되,
/// A160(v0.169.0)부터 includeHidden 인자로 포함시킬 수 있다(설정 키 explorer.showHidden —
/// 값을 읽는 곳은 UI 쪽 ExplorerPane.ShowHiddenSettingKey 한 곳이고 여기는 인자만 받는다).
/// </summary>
public static class ExplorerListing
{
    /// <summary>
    /// 탐색기 한 항목. 폴더면 Size는 0.
    /// Created(A117, v0.136.0) = 만든 날짜 — 폴더도 파일과 같은 방식으로 채운다
    /// (DirectoryInfo.CreationTime / FileInfo.CreationTime). Modified와 병존하는 별도 정렬 키의 원본.
    /// </summary>
    public sealed record Entry(
        string Path, string Name, bool IsFolder, long Size, DateTime Modified, DateTime Created);

    /// <summary>파일명이 확장자 목록(소문자, 점 포함)에 해당하는지. 대소문자 무시.</summary>
    public static bool MatchesExtension(string fileName, IReadOnlyList<string> extensions) =>
        extensions.Contains(System.IO.Path.GetExtension(fileName).ToLowerInvariant());

    /// <summary>
    /// 폴더 내용을 나열한다: 폴더 먼저, 그다음 확장자 일치 파일, 각각 이름순.
    /// maxItems로 초대형 폴더의 UI 폭주를 막는다.
    /// includeHidden(A160) = 숨김·시스템 항목도 포함할지. 기본 false = 종전 동작 그대로.
    /// 호출부(ExplorerPane)가 설정값을 스캔 시작 시점에 스냅샷해 넘긴다 — 여기서 설정을 읽지 않는다
    /// (KOTU.Core는 UI·설정 주입에 비의존).
    /// </summary>
    public static IReadOnlyList<Entry> List(
        string folder, IReadOnlyList<string> extensions, int maxItems = 2000, bool includeHidden = false)
    {
        var info = new DirectoryInfo(folder);
        var result = new List<Entry>();

        foreach (var d in info.EnumerateDirectories()
                     .Where(d => ShouldShow(d.Attributes, includeHidden))
                     .OrderBy(d => d.Name, StringComparer.OrdinalIgnoreCase))
        {
            if (result.Count >= maxItems) return result;
            result.Add(new Entry(d.FullName, d.Name, true, 0, d.LastWriteTime, d.CreationTime));
        }

        foreach (var f in info.EnumerateFiles()
                     .Where(f => ShouldShow(f.Attributes, includeHidden) && MatchesExtension(f.Name, extensions))
                     .OrderBy(f => f.Name, StringComparer.OrdinalIgnoreCase))
        {
            if (result.Count >= maxItems) return result;
            result.Add(new Entry(f.FullName, f.Name, false, f.Length, f.LastWriteTime, f.CreationTime));
        }

        return result;
    }

    /// <summary>
    /// 우측 리스트 정렬 키 (A5). 설정 저장 문자열은 소문자 이름과 수동 동기.
    /// Created는 A117(v0.136.0) 신설 — Modified와 같은 방식으로 병존한다(날짜 범위 필터 아님).
    /// </summary>
    public enum SortKey
    {
        Name,
        Size,
        Modified,
        Created,
    }

    /// <summary>
    /// 정렬·필터를 적용한 표시용 목록을 만든다 (A5·A7). 폴더 먼저 규칙은 유지.
    /// Name=이름 오름차순, Size=큰 것부터(폴더는 크기가 없어 이름순), Modified=최신부터,
    /// Created=만든 날짜 최신부터(A117 — Modified 규칙 그대로 복제: 폴더도 날짜순). 동률은 이름순.
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
            // A117: 만든 날짜 — 위 Modified 분기와 같은 모양(폴더도 파일도 최신부터, 동률은 이름순).
            SortKey.Created => (
                folders.OrderByDescending(e => e.Created).ThenBy(e => e.Name, byName),
                files.OrderByDescending(e => e.Created).ThenBy(e => e.Name, byName)),
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

    /// <summary>
    /// 표시 대상인지 (A160, v0.169.0) — 숨김/시스템 표시 정책의 <b>단일 원본</b>.
    /// 열거 지점 전부가 이 한 줄을 통과한다: 위 List의 폴더·파일 두 곳 +
    /// 좌 패널 폴더 트리(KOTU.App.Overlays.FileListOverlay.LoadChildrenAsync).
    /// 트리에 있던 인라인 복제(속성 마스크 직접 비교)를 없앤 자리다 — 리스트와 트리가 서로
    /// 다른 집합을 보이는 것이 이 항목의 최악 회귀라, 새 열거 지점도 반드시 여기를 거칠 것.
    /// includeHidden = true면 숨김·시스템을 함께 보여 준다(OS 탐색기는 2개 옵션이지만
    /// KOTU는 1단계에서 하나로 묶는다 — 사용자 확정).
    /// </summary>
    public static bool ShouldShow(FileAttributes attributes, bool includeHidden) =>
        includeHidden || !IsHiddenOrSystem(attributes);

    /// <summary>숨김·시스템 속성 판정. 부르는 곳은 ShouldShow 하나 — 판정식은 여기 한 벌뿐이다.</summary>
    private static bool IsHiddenOrSystem(FileAttributes attributes) =>
        (attributes & (FileAttributes.Hidden | FileAttributes.System)) != 0;
}
