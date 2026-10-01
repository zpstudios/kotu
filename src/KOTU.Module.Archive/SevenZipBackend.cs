using System.Buffers.Binary;
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
        catch (SevenZipException ex) when (IsPasswordError(ex, archivePath))
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
        catch (SevenZipException ex) when (IsPasswordError(ex, archivePath))
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
            else
            {
                throw new FileNotFoundException("A selected source no longer exists.", source);
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

    /// <summary>
    /// 7z.dll 예외 메시지 기반 암호 오류 판별. SevenZipSharp의 일반 열기 실패 문구에도
    /// 암호 가능성이 함께 적히므로, 실제 압축 시그니처가 확인된 파일만 암호 재시도로 보낸다.
    /// </summary>
    private static bool IsPasswordError(SevenZipException ex, string archivePath)
    {
        var text = ex.ToString();
        var mentionsPassword = text.Contains("password", StringComparison.OrdinalIgnoreCase) ||
                               text.Contains("encrypted", StringComparison.OrdinalIgnoreCase);
        return mentionsPassword && HasPasswordEvidence(archivePath);
    }

    /// <summary>
    /// ZIP의 암호 플래그와 7z의 AES 코더처럼 파일 안에서 확인되는 암호 증거를 찾는다.
    /// RAR은 시그니처 뒤 암호 메타데이터가 버전별로 달라 기존 암호 흐름을 보존하도록 시그니처를 쓴다.
    /// 확장자만 맞는 손상 파일은 SevenZipSharp의 모호한 암호 문구를 그대로 열기 실패로 처리한다.
    /// </summary>
    internal static bool HasPasswordEvidence(string archivePath)
    {
        Span<byte> header = stackalloc byte[32];
        try
        {
            using var stream = File.OpenRead(archivePath);
            if (ZipCentralDirectoryContainsEncryptedEntry(stream)) return true;

            stream.Position = 0;
            var read = stream.Read(header);
            if (read >= 6 && header[..6].SequenceEqual(new byte[] { 0x37, 0x7A, 0xBC, 0xAF, 0x27, 0x1C }))
                return read >= 32 && SevenZipHeaderContainsAes(stream, header);

            return read >= 7 && header[..7].SequenceEqual(new byte[] { 0x52, 0x61, 0x72, 0x21, 0x1A, 0x07, 0x00 }) ||
                   read >= 8 && header[..8].SequenceEqual(new byte[] { 0x52, 0x61, 0x72, 0x21, 0x1A, 0x07, 0x01, 0x00 });
        }
        catch (IOException)
        {
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>
    /// ZIP 중앙 디렉터리의 모든 항목 플래그를 읽는다. 로컬 헤더 위치에 의존하지 않으므로
    /// 앞에 실행 파일이 붙은 SFX와 평문·암호 항목이 섞인 ZIP도 판별한다.
    /// </summary>
    private static bool ZipCentralDirectoryContainsEncryptedEntry(FileStream stream)
    {
        const int eocdLength = 22;
        const int maxEocdSearch = eocdLength + ushort.MaxValue;
        if (stream.Length < eocdLength) return false;

        var tailLength = (int)Math.Min(stream.Length, maxEocdSearch);
        var tail = new byte[tailLength];
        if (!TryReadAt(stream, stream.Length - tailLength, tail)) return false;

        for (var i = tail.Length - eocdLength; i >= 0; i--)
        {
            if (BinaryPrimitives.ReadUInt32LittleEndian(tail.AsSpan(i, 4)) != 0x06054B50) continue;
            var commentLength = BinaryPrimitives.ReadUInt16LittleEndian(tail.AsSpan(i + 20, 2));
            if (i + eocdLength + commentLength != tail.Length) continue;
            var eocdPosition = stream.Length - tailLength + i;
            if (TryReadZipEocdCandidate(stream, tail.AsSpan(i, eocdLength), eocdPosition, out var encrypted))
                return encrypted;
        }
        return false;
    }

    /// <summary>EOCD 후보 하나의 디스크·ZIP64·중앙 디렉터리 구조를 끝까지 검증한다.</summary>
    private static bool TryReadZipEocdCandidate(
        FileStream stream, ReadOnlySpan<byte> eocd, long eocdPosition, out bool encrypted)
    {
        encrypted = false;
        var disk = BinaryPrimitives.ReadUInt16LittleEndian(eocd[4..6]);
        var centralDisk = BinaryPrimitives.ReadUInt16LittleEndian(eocd[6..8]);
        var entriesOnDisk = BinaryPrimitives.ReadUInt16LittleEndian(eocd[8..10]);
        var totalEntries = BinaryPrimitives.ReadUInt16LittleEndian(eocd[10..12]);
        if (disk != 0 || centralDisk != 0 || entriesOnDisk != totalEntries) return false;

        ulong centralSize = BinaryPrimitives.ReadUInt32LittleEndian(eocd[12..16]);
        ulong centralOffset = BinaryPrimitives.ReadUInt32LittleEndian(eocd[16..20]);
        ulong expectedEntries = totalEntries;
        long centralEnd = eocdPosition;
        if (entriesOnDisk == ushort.MaxValue || totalEntries == ushort.MaxValue ||
            centralSize == uint.MaxValue || centralOffset == uint.MaxValue)
        {
            if (!TryReadZip64Directory(
                    stream, eocdPosition, out centralEnd, out centralSize, out centralOffset, out expectedEntries))
                return false;
        }

        if (centralSize > (ulong)centralEnd || centralOffset > (ulong)centralEnd) return false;
        var centralStart = centralEnd - (long)centralSize;
        // 중앙 디렉터리 오프셋보다 실제 위치가 앞설 수 없다. 차이는 SFX 접두부 길이다.
        if ((ulong)centralStart < centralOffset) return false;
        return TryReadCentralDirectory(stream, centralStart, centralSize, expectedEntries, out encrypted);
    }

    /// <summary>ZIP64 locator와 EOCD를 제한된 후방 탐색으로 찾아 중앙 디렉터리 범위를 돌려준다.</summary>
    private static bool TryReadZip64Directory(
        FileStream stream, long eocdPosition, out long centralEnd, out ulong centralSize,
        out ulong centralOffset, out ulong expectedEntries)
    {
        centralEnd = 0;
        centralSize = 0;
        centralOffset = 0;
        expectedEntries = 0;
        const int locatorLength = 20;
        if (eocdPosition < locatorLength) return false;

        Span<byte> locator = stackalloc byte[locatorLength];
        var locatorPosition = eocdPosition - locatorLength;
        if (!TryReadAt(stream, locatorPosition, locator) ||
            BinaryPrimitives.ReadUInt32LittleEndian(locator[..4]) != 0x07064B50 ||
            BinaryPrimitives.ReadUInt32LittleEndian(locator[4..8]) != 0 ||
            BinaryPrimitives.ReadUInt32LittleEndian(locator[16..20]) != 1)
            return false;

        var recordedPosition = BinaryPrimitives.ReadUInt64LittleEndian(locator[8..16]);
        if (recordedPosition <= (ulong)long.MaxValue &&
            TryReadZip64EocdAt(
                stream, (long)recordedPosition, locatorPosition,
                out centralSize, out centralOffset, out expectedEntries))
        {
            centralEnd = (long)recordedPosition;
            return true;
        }

        // SFX 접두부가 있으면 locator의 기록 오프셋에는 그 길이가 빠져 있다.
        const int maxZip64Search = 1024 * 1024;
        var searchStart = Math.Max(0, locatorPosition - maxZip64Search);
        var searchLength = checked((int)(locatorPosition - searchStart));
        var search = new byte[searchLength];
        if (!TryReadAt(stream, searchStart, search)) return false;
        for (var i = search.Length - 12; i >= 0; i--)
        {
            if (BinaryPrimitives.ReadUInt32LittleEndian(search.AsSpan(i, 4)) != 0x06064B50) continue;
            var position = searchStart + i;
            if (!TryReadZip64EocdAt(
                    stream, position, locatorPosition, out centralSize, out centralOffset, out expectedEntries))
                continue;
            centralEnd = position;
            return true;
        }
        return false;
    }

    private static bool TryReadZip64EocdAt(
        FileStream stream, long position, long expectedEnd, out ulong centralSize,
        out ulong centralOffset, out ulong expectedEntries)
    {
        centralSize = 0;
        centralOffset = 0;
        expectedEntries = 0;
        Span<byte> record = stackalloc byte[56];
        if (position < 0 || position > expectedEnd - record.Length ||
            !TryReadAt(stream, position, record) ||
            BinaryPrimitives.ReadUInt32LittleEndian(record[..4]) != 0x06064B50)
            return false;

        var bodySize = BinaryPrimitives.ReadUInt64LittleEndian(record[4..12]);
        if (bodySize < 44 || bodySize > (ulong)(expectedEnd - position - 12) ||
            (ulong)position + 12 + bodySize != (ulong)expectedEnd)
            return false;
        if (BinaryPrimitives.ReadUInt32LittleEndian(record[16..20]) != 0 ||
            BinaryPrimitives.ReadUInt32LittleEndian(record[20..24]) != 0)
            return false;
        var entriesOnDisk = BinaryPrimitives.ReadUInt64LittleEndian(record[24..32]);
        expectedEntries = BinaryPrimitives.ReadUInt64LittleEndian(record[32..40]);
        if (entriesOnDisk != expectedEntries)
            return false;

        centralSize = BinaryPrimitives.ReadUInt64LittleEndian(record[40..48]);
        centralOffset = BinaryPrimitives.ReadUInt64LittleEndian(record[48..56]);
        return true;
    }

    private static bool TryReadCentralDirectory(
        FileStream stream, long start, ulong size, ulong expectedEntries, out bool encrypted)
    {
        encrypted = false;
        if (size > (ulong)(stream.Length - start)) return false;
        var end = start + (long)size;
        var position = start;
        ulong entries = 0;
        Span<byte> signature = stackalloc byte[4];
        Span<byte> entry = stackalloc byte[46];
        while (position < end)
        {
            if (end - position < signature.Length || !TryReadAt(stream, position, signature)) return false;
            var recordSignature = BinaryPrimitives.ReadUInt32LittleEndian(signature);
            if (recordSignature == 0x05054B50)
            {
                Span<byte> digitalSignature = stackalloc byte[6];
                if (end - position < digitalSignature.Length ||
                    !TryReadAt(stream, position, digitalSignature))
                    return false;
                var signatureLength = BinaryPrimitives.ReadUInt16LittleEndian(digitalSignature[4..6]);
                var next = position + digitalSignature.Length + signatureLength;
                if (next != end) return false;
                position = next;
                break;
            }
            if (recordSignature != 0x02014B50 || end - position < entry.Length ||
                !TryReadAt(stream, position, entry))
                return false;
            if ((BinaryPrimitives.ReadUInt16LittleEndian(entry[8..10]) & 0x0001) != 0) encrypted = true;

            var variableLength = (long)BinaryPrimitives.ReadUInt16LittleEndian(entry[28..30]) +
                                 BinaryPrimitives.ReadUInt16LittleEndian(entry[30..32]) +
                                 BinaryPrimitives.ReadUInt16LittleEndian(entry[32..34]);
            var next = position + entry.Length + variableLength;
            if (next <= position || next > end) return false;
            position = next;
            entries++;
        }
        return position == end && entries == expectedEntries;
    }

    private static bool TryReadAt(FileStream stream, long position, Span<byte> destination)
    {
        if (position < 0 || position > stream.Length || destination.Length > stream.Length - position) return false;
        stream.Position = position;
        var read = 0;
        while (read < destination.Length)
        {
            var count = stream.Read(destination[read..]);
            if (count == 0) return false;
            read += count;
        }
        return true;
    }

    /// <summary>7z 시작 헤더가 가리키는 다음 헤더에서 AES-256 코더 ID를 찾는다.</summary>
    private static bool SevenZipHeaderContainsAes(FileStream stream, ReadOnlySpan<byte> header)
    {
        var nextHeaderOffset = BitConverter.ToUInt64(header[12..20]);
        var nextHeaderSize = BitConverter.ToUInt64(header[20..28]);
        if (nextHeaderSize == 0 || nextHeaderOffset > (ulong)long.MaxValue - 32) return false;

        var absoluteOffset = (long)nextHeaderOffset + 32;
        if (absoluteOffset < 32 || absoluteOffset >= stream.Length) return false;
        var available = (ulong)(stream.Length - absoluteOffset);
        var bytesToRead = (int)Math.Min(Math.Min(nextHeaderSize, available), 1024UL * 1024);
        if (bytesToRead < 4) return false;

        var nextHeader = new byte[bytesToRead];
        stream.Position = absoluteOffset;
        var read = 0;
        while (read < nextHeader.Length)
        {
            var count = stream.Read(nextHeader, read, nextHeader.Length - read);
            if (count == 0) break;
            read += count;
        }
        for (var i = 0; i <= read - 4; i++)
        {
            if (nextHeader[i] == 0x06 && nextHeader[i + 1] == 0xF1 &&
                nextHeader[i + 2] == 0x07 && nextHeader[i + 3] == 0x01)
                return true;
        }
        return false;
    }
}
