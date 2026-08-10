using SevenZip;

namespace KOTU.Module.Archive;

/// <summary>
/// 7z.dll(LGPL, 동적 로드) 기반 기본 백엔드. 모든 지원 포맷의 해제 + zip/7z 생성 + 암호를 담당한다.
/// zip 항목명이 깨져 보이면(CP949를 다른 코드페이지로 해석) SharpCompress+CP949 재시도 경로로 위임한다.
/// </summary>
public sealed class SevenZipBackend : IArchiveBackend
{
    private static readonly object InitLock = new();
    private static bool _initialized;

    /// <summary>실행 폴더의 7z.dll을 1회 등록한다. 없으면 배치 안내 예외를 던진다.</summary>
    private static void EnsureLibrary()
    {
        if (_initialized) return;
        lock (InitLock)
        {
            if (_initialized) return;
            var dll = Path.Combine(AppContext.BaseDirectory, "7z.dll");
            if (!File.Exists(dll))
            {
                throw new FileNotFoundException(
                    "7z.dll not found. Copy the x64 7z.dll from a 7-Zip installation or '7z extra' next to the app. (See README)",
                    dll);
            }
            SevenZipBase.SetLibraryPath(dll);
            _initialized = true;
        }
    }

    public IReadOnlyList<ArchiveEntry> List(string archivePath, string? password = null)
    {
        EnsureLibrary();
        try
        {
            var entries = ListWithSevenZip(archivePath, password);

            // zip 한정: 항목명 깨짐 감지 시 SharpCompress+CP949로 다시 읽는다.
            if (IsZip(archivePath) && entries.Any(e => MojibakeDetector.LooksBroken(e.Path)))
            {
                var retried = Cp949ZipReader.TryList(archivePath, password);
                if (retried is not null) return retried;
            }
            return entries;
        }
        catch (SevenZipException ex) when (IsPasswordError(ex))
        {
            throw new ArchivePasswordException(ex);
        }
    }

    public void Extract(
        string archivePath,
        string targetDirectory,
        IReadOnlyCollection<string>? entryPaths = null,
        string? password = null,
        IProgress<double>? progress = null,
        CancellationToken cancellationToken = default)
    {
        EnsureLibrary();
        Directory.CreateDirectory(targetDirectory);

        // 목록을 CP949 경로로 읽었다면 해제도 같은 경로를 써야 항목명이 일치한다.
        if (IsZip(archivePath) && HasBrokenZipNames(archivePath, password))
        {
            Cp949ZipReader.Extract(archivePath, targetDirectory, entryPaths, password, progress, cancellationToken);
            return;
        }

        try
        {
            using var extractor = CreateExtractor(archivePath, password);

            // 암호 없이 암호화된 항목을 풀려는 경우를 미리 감지(해제 도중 실패보다 낫다)
            if (string.IsNullOrEmpty(password) && extractor.ArchiveFileData.Any(f => f.Encrypted))
                throw new ArchivePasswordException();

            extractor.Extracting += (_, e) => progress?.Report(e.PercentDone / 100.0);
            extractor.FileExtractionStarted += (_, e) =>
            {
                if (cancellationToken.IsCancellationRequested) e.Cancel = true; // 다음 파일부터 중단
            };

            if (entryPaths is null || entryPaths.Count == 0)
            {
                extractor.ExtractArchive(targetDirectory);
            }
            else
            {
                var indexes = MatchIndexes(extractor, entryPaths);
                if (indexes.Length > 0) extractor.ExtractFiles(targetDirectory, indexes);
            }
            cancellationToken.ThrowIfCancellationRequested();
        }
        catch (SevenZipException ex) when (IsPasswordError(ex))
        {
            throw new ArchivePasswordException(ex);
        }
    }

    public void CreateZip(
        IReadOnlyList<string> sourcePaths,
        string archivePath,
        string? password = null,
        IProgress<double>? progress = null,
        CancellationToken cancellationToken = default)
        => Create(OutArchiveFormat.Zip, sourcePaths, archivePath, password, progress, cancellationToken);

    public void Create7z(
        IReadOnlyList<string> sourcePaths,
        string archivePath,
        string? password = null,
        IProgress<double>? progress = null,
        CancellationToken cancellationToken = default)
        => Create(OutArchiveFormat.SevenZip, sourcePaths, archivePath, password, progress, cancellationToken);

    // ---------- 내부 구현 ----------

    private static void Create(
        OutArchiveFormat format,
        IReadOnlyList<string> sourcePaths,
        string archivePath,
        string? password,
        IProgress<double>? progress,
        CancellationToken cancellationToken)
    {
        EnsureLibrary();
        var entries = BuildEntryDictionary(sourcePaths); // 압축 내 항목명 → 원본 경로

        var compressor = new SevenZipCompressor
        {
            ArchiveFormat = format,
            CompressionLevel = CompressionLevel.Normal,
            CompressionMode = CompressionMode.Create,
        };
        // 7z는 항목명까지 암호화 가능(zip은 미지원)
        if (format == OutArchiveFormat.SevenZip && !string.IsNullOrEmpty(password))
            compressor.EncryptHeaders = true;

        compressor.Compressing += (_, e) => progress?.Report(e.PercentDone / 100.0);
        compressor.FileCompressionStarted += (_, e) =>
        {
            if (cancellationToken.IsCancellationRequested) e.Cancel = true;
        };

        if (string.IsNullOrEmpty(password))
            compressor.CompressFileDictionary(entries, archivePath);
        else
            compressor.CompressFileDictionary(entries, archivePath, password);

        cancellationToken.ThrowIfCancellationRequested();
    }

    /// <summary>파일/폴더 원본 목록 → (압축 내 항목명, 원본 경로) 사전. 폴더는 하위 전체 포함.</summary>
    private static Dictionary<string, string> BuildEntryDictionary(IReadOnlyList<string> sourcePaths)
    {
        var dict = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var source in sourcePaths)
        {
            if (Directory.Exists(source))
            {
                // 폴더명 자체를 최상위로 유지: baseDir 기준 상대 경로 사용
                var trimmed = Path.TrimEndingDirectorySeparator(source);
                var baseDir = Path.GetDirectoryName(trimmed) ?? trimmed;
                foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
                    AddUnique(dict, Path.GetRelativePath(baseDir, file), file);
            }
            else if (File.Exists(source))
            {
                AddUnique(dict, Path.GetFileName(source), source);
            }
        }
        if (dict.Count == 0) throw new FileNotFoundException("Nothing to compress.");
        return dict;
    }

    /// <summary>항목명이 겹치면 "이름 (2).확장자" 식으로 바꿔 추가한다.</summary>
    private static void AddUnique(Dictionary<string, string> dict, string entryName, string filePath)
    {
        if (dict.TryAdd(entryName, filePath)) return;

        var dir = Path.GetDirectoryName(entryName) ?? string.Empty;
        var name = Path.GetFileNameWithoutExtension(entryName);
        var ext = Path.GetExtension(entryName);
        for (var i = 2; ; i++)
        {
            var candidate = Path.Combine(dir, $"{name} ({i}){ext}");
            if (dict.TryAdd(candidate, filePath)) return;
        }
    }

    private static SevenZipExtractor CreateExtractor(string archivePath, string? password) =>
        string.IsNullOrEmpty(password)
            ? new SevenZipExtractor(archivePath)
            : new SevenZipExtractor(archivePath, password);

    private static List<ArchiveEntry> ListWithSevenZip(string archivePath, string? password)
    {
        using var extractor = CreateExtractor(archivePath, password);
        return extractor.ArchiveFileData
            .Select(f => new ArchiveEntry(
                ArchiveEntryTree.NormalizePath(f.FileName),
                f.IsDirectory,
                f.IsDirectory || f.Size > (ulong)long.MaxValue ? 0L : (long)f.Size,
                f.LastWriteTime))
            .ToList();
    }

    /// <summary>선택 항목(폴더면 하위 포함)을 압축 내 인덱스로 변환한다.</summary>
    private static int[] MatchIndexes(SevenZipExtractor extractor, IReadOnlyCollection<string> entryPaths)
    {
        var wanted = entryPaths.Select(ArchiveEntryTree.NormalizePath).ToList();
        return extractor.ArchiveFileData
            .Where(f =>
            {
                var p = ArchiveEntryTree.NormalizePath(f.FileName);
                return wanted.Any(w =>
                    p.Equals(w, StringComparison.OrdinalIgnoreCase) ||
                    p.StartsWith(w + "/", StringComparison.OrdinalIgnoreCase));
            })
            .Select(f => f.Index)
            .ToArray();
    }

    private static bool HasBrokenZipNames(string archivePath, string? password)
    {
        try
        {
            return ListWithSevenZip(archivePath, password).Any(e => MojibakeDetector.LooksBroken(e.Path));
        }
        catch
        {
            return false; // 판단 불가면 기본(7z.dll) 경로 사용
        }
    }

    private static bool IsZip(string path) =>
        string.Equals(Path.GetExtension(path), ".zip", StringComparison.OrdinalIgnoreCase);

    /// <summary>7z.dll 예외 메시지 기반 암호 오류 판별(보수적 휴리스틱).</summary>
    private static bool IsPasswordError(SevenZipException ex)
    {
        var text = ex.ToString();
        return text.Contains("password", StringComparison.OrdinalIgnoreCase) ||
               text.Contains("encrypted", StringComparison.OrdinalIgnoreCase);
    }
}
