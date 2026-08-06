using System.Text;
using SharpCompress.Archives;
using SharpCompress.Archives.Zip;
using SharpCompress.Common;
using SharpCompress.Readers;

namespace WinUtil.Module.Archive;

/// <summary>
/// 한글 항목명(zip 한정) 재시도 경로. 7z.dll이 항목명을 깨뜨린 경우
/// SharpCompress에 CP949 인코딩을 지정해 다시 읽는다.
/// </summary>
internal static class Cp949ZipReader
{
    private static ReaderOptions CreateOptions(string? password)
    {
        // System.Text.Encoding.CodePages 없이는 949를 얻을 수 없다.
        var cp949 = CodePagesEncodingProvider.Instance.GetEncoding(949)
            ?? throw new NotSupportedException("Cannot load the CP949 encoding.");
        return new ReaderOptions
        {
            Password = password,
            ArchiveEncoding = new ArchiveEncoding { Default = cp949 },
        };
    }

    /// <summary>CP949로 목록 재시도. 실패하거나 여전히 깨져 있으면 null(호출자가 원래 결과 유지).</summary>
    public static IReadOnlyList<ArchiveEntry>? TryList(string archivePath, string? password)
    {
        try
        {
            using var zip = ZipArchive.Open(archivePath, CreateOptions(password));
            var list = new List<ArchiveEntry>();
            foreach (var entry in zip.Entries)
            {
                if (string.IsNullOrEmpty(entry.Key)) continue;
                list.Add(new ArchiveEntry(
                    ArchiveEntryTree.NormalizePath(entry.Key),
                    entry.IsDirectory,
                    entry.IsDirectory ? 0 : entry.Size,
                    entry.LastModifiedTime ?? default));
            }
            return list.Any(e => MojibakeDetector.LooksBroken(e.Path)) ? null : list;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>CP949 해석으로 해제. 항목명 필터는 목록과 같은 정규화 규칙을 쓴다.</summary>
    public static void Extract(
        string archivePath,
        string targetDirectory,
        IReadOnlyCollection<string>? entryPaths,
        string? password,
        IProgress<double>? progress,
        CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(targetDirectory);
        var wanted = entryPaths is { Count: > 0 }
            ? entryPaths.Select(ArchiveEntryTree.NormalizePath).ToList()
            : null;

        using var zip = ZipArchive.Open(archivePath, CreateOptions(password));
        var targets = zip.Entries
            .Where(e => !e.IsDirectory && !string.IsNullOrEmpty(e.Key))
            .Where(e => wanted is null || Matches(wanted, ArchiveEntryTree.NormalizePath(e.Key!)))
            .ToList();

        var total = Math.Max(1, targets.Sum(e => Math.Max(1, e.Size)));
        long done = 0;
        foreach (var entry in targets)
        {
            cancellationToken.ThrowIfCancellationRequested();
            entry.WriteToDirectory(targetDirectory, new ExtractionOptions
            {
                ExtractFullPath = true,
                Overwrite = true,
                PreserveFileTime = true,
            });
            done += Math.Max(1, entry.Size);
            progress?.Report((double)done / total);
        }
    }

    private static bool Matches(IReadOnlyList<string> wanted, string path) =>
        wanted.Any(w =>
            path.Equals(w, StringComparison.OrdinalIgnoreCase) ||
            path.StartsWith(w + "/", StringComparison.OrdinalIgnoreCase));
}
