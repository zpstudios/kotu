using System.Security.Cryptography;
using System.Text;
using KOTU.Core.Settings;

namespace KOTU.Core.Routing;

// 폴더별 필드를 따로 저장해 다른 창의 보기 변경과 정렬 변경이 서로를 덮지 않는다.
internal static class ExplorerFolderPreferences
{
    internal static string? KeyFor(string? folder)
    {
        var normalized = Normalize(folder);
        return normalized is null ? null : "explorer.folder.v1." +
            Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(normalized)));
    }

    // 실제 파일이나 junction을 열지 않는 Windows 절대 경로의 문자열 정체성이다.
    internal static string? Normalize(string? folder)
    {
        if (string.IsNullOrWhiteSpace(folder)) return null;
        var path = folder.Replace('/', '\\');
        // 기존 파일 작업처럼 일반 확장 drive/UNC 접두어만 받아들인다. 장치 경로는 제외한다.
        if (path.StartsWith(@"\\?\UNC\", StringComparison.OrdinalIgnoreCase)) path = @"\\" + path[8..];
        else if (path.StartsWith(@"\\?\", StringComparison.Ordinal))
        {
            path = path[4..];
            if (path.Length < 3 || !char.IsAsciiLetter(path[0]) || path[1] != ':' || path[2] != '\\') return null;
        }
        if (path.Any(c => c < ' ' || c is '"' or '<' or '>' or '|' or '?' or '*')) return null;
        string root;
        string remainder;
        if (path.Length >= 3 && char.IsAsciiLetter(path[0]) && path[1] == ':' && path[2] == '\\')
        {
            root = path[..3];
            remainder = path[3..];
        }
        else if (path.StartsWith("\\\\", StringComparison.Ordinal))
        {
            var parts = path[2..].Split('\\', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 2 || !ValidSegment(parts[0]) || !ValidSegment(parts[1])) return null;
            root = "\\\\" + parts[0] + "\\" + parts[1] + "\\";
            remainder = string.Join("\\", parts.Skip(2));
        }
        else return null;

        var segments = new List<string>();
        foreach (var part in remainder.Split('\\', StringSplitOptions.RemoveEmptyEntries))
        {
            if (part == ".") continue;
            if (part == "..")
            {
                if (segments.Count > 0) segments.RemoveAt(segments.Count - 1);
                continue;
            }
            if (!ValidSegment(part)) return null;
            segments.Add(part);
        }
        return (root + string.Join("\\", segments)).ToUpperInvariant();
    }

    private static bool ValidSegment(string segment) =>
        segment.Length > 0 && !segment.Contains(':') && !segment.EndsWith('.') && !segment.EndsWith(' ');

    internal static ExplorerListing.SortKey ReadSort(ISettingsService settings, string key,
        ExplorerListing.SortKey fallback) => ReadSort(settings, key) ?? fallback;

    internal static ExplorerListing.SortKey? ReadSort(ISettingsService settings, string key) =>
        settings.Get(key, "") switch
        {
            "name" => ExplorerListing.SortKey.Name,
            "type" => ExplorerListing.SortKey.Type,
            "size" => ExplorerListing.SortKey.Size,
            "modified" => ExplorerListing.SortKey.Modified,
            "created" => ExplorerListing.SortKey.Created,
            _ => null,
        };

    internal static ExplorerPresentationState.ViewMode ReadView(ISettingsService settings, string key) =>
        settings.Get(key, "") == "list" ? ExplorerPresentationState.ViewMode.List : ExplorerPresentationState.ViewMode.Preview;
}
