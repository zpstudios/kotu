using System.Globalization;

namespace KOTU.Module.Archive;

/// <summary>압축 내부 트리의 노드. 폴더의 Size는 하위 파일 크기의 누적 합.</summary>
public sealed class ArchiveEntryNode
{
    /// <summary>표시 이름(경로 마지막 조각). 루트는 빈 문자열.</summary>
    public required string Name { get; init; }

    /// <summary>'/' 구분 전체 경로. 루트는 빈 문자열.</summary>
    public required string FullPath { get; init; }

    public bool IsDirectory { get; init; }

    /// <summary>파일은 자체 크기, 폴더는 하위 누적 크기(바이트).</summary>
    public long Size { get; set; }

    /// <summary>수정 시각. 정보가 없으면 default.</summary>
    public DateTime Modified { get; set; }

    public List<ArchiveEntryNode> Children { get; } = [];
}

/// <summary>
/// 평평한 항목 경로 목록("a/b/c.txt")을 폴더 트리로 변환하는 순수 로직.
/// UI 비의존 — 단위 테스트 대상.
/// </summary>
public static class ArchiveEntryTree
{
    /// <summary>구분자를 '/'로 통일하고 앞뒤 구분자를 제거한다.</summary>
    public static string NormalizePath(string path) => path.Replace('\\', '/').Trim('/');

    /// <summary>
    /// 항목 목록 → 트리. 중간 폴더는 자동 생성하고, 각 폴더의 누적 크기를 계산하며,
    /// 자식은 폴더 우선 + 이름(대소문자 무시) 순으로 정렬한다. 반환값은 루트(경로 "").
    /// </summary>
    public static ArchiveEntryNode Build(IEnumerable<ArchiveEntry> entries)
    {
        var root = new ArchiveEntryNode { Name = string.Empty, FullPath = string.Empty, IsDirectory = true };
        var folders = new Dictionary<string, ArchiveEntryNode>(StringComparer.OrdinalIgnoreCase)
        {
            [string.Empty] = root,
        };

        foreach (var entry in entries)
        {
            var path = NormalizePath(entry.Path);
            if (path.Length == 0) continue;

            if (entry.IsDirectory)
            {
                var folder = GetOrCreateFolder(folders, path);
                if (entry.Modified != default) folder.Modified = entry.Modified;
                continue;
            }

            var slash = path.LastIndexOf('/');
            var parent = GetOrCreateFolder(folders, slash < 0 ? string.Empty : path[..slash]);
            parent.Children.Add(new ArchiveEntryNode
            {
                Name = path[(slash + 1)..],
                FullPath = path,
                IsDirectory = false,
                Size = entry.Size,
                Modified = entry.Modified,
            });

            // 누적 크기: 루트까지의 모든 조상 폴더에 더한다.
            for (var node = parent; ; node = folders[ParentPath(node.FullPath)])
            {
                node.Size += entry.Size;
                if (node.FullPath.Length == 0) break;
            }
        }

        SortRecursive(root);
        return root;
    }

    /// <summary>사람이 읽는 크기 문자열. 1024 단위, B/KB/MB/GB(GB 초과도 GB로 표기).</summary>
    public static string FormatSize(long bytes)
    {
        if (bytes < 0) bytes = 0;
        if (bytes < 1024) return bytes + " B";

        string[] units = ["KB", "MB", "GB"];
        var value = (double)bytes;
        var unit = -1;
        do
        {
            value /= 1024;
            unit++;
        }
        while (value >= 1024 && unit < units.Length - 1);

        return value.ToString("0.#", CultureInfo.InvariantCulture) + " " + units[unit];
    }

    private static ArchiveEntryNode GetOrCreateFolder(Dictionary<string, ArchiveEntryNode> folders, string path)
    {
        if (folders.TryGetValue(path, out var existing)) return existing;

        var parent = GetOrCreateFolder(folders, ParentPath(path));
        var slash = path.LastIndexOf('/');
        var node = new ArchiveEntryNode
        {
            Name = path[(slash + 1)..],
            FullPath = path,
            IsDirectory = true,
        };
        parent.Children.Add(node);
        folders[path] = node;
        return node;
    }

    private static string ParentPath(string path)
    {
        var slash = path.LastIndexOf('/');
        return slash < 0 ? string.Empty : path[..slash];
    }

    private static void SortRecursive(ArchiveEntryNode node)
    {
        node.Children.Sort(static (a, b) =>
        {
            if (a.IsDirectory != b.IsDirectory) return a.IsDirectory ? -1 : 1; // 폴더 우선
            return string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase);
        });
        foreach (var child in node.Children) SortRecursive(child);
    }
}
