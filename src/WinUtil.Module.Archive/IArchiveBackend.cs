namespace WinUtil.Module.Archive;

/// <summary>압축 파일 내부 항목 하나. 경로는 '/' 구분자로 정규화된 상대 경로.</summary>
public sealed record ArchiveEntry(string Path, bool IsDirectory, long Size, DateTime Modified);

/// <summary>암호가 필요하거나 틀렸음을 나타낸다. 뷰에서 이 예외를 잡아 암호 입력 후 재시도한다.</summary>
public sealed class ArchivePasswordException : Exception
{
    public ArchivePasswordException(Exception? inner = null)
        : base("A password is required or the password is incorrect.", inner)
    {
    }
}

/// <summary>
/// 압축 백엔드 추상화. 구현은 UI 비의존이며 모든 메서드는 동기(호출자가 Task.Run으로 감싼다).
/// </summary>
public interface IArchiveBackend
{
    /// <summary>압축 내부 항목 목록을 읽는다(풀지 않고 미리보기).</summary>
    IReadOnlyList<ArchiveEntry> List(string archivePath, string? password = null);

    /// <summary>
    /// 해제. <paramref name="entryPaths"/>가 null/빈 목록이면 전체, 아니면 해당 항목(폴더면 하위 포함)만 푼다.
    /// </summary>
    void Extract(
        string archivePath,
        string targetDirectory,
        IReadOnlyCollection<string>? entryPaths = null,
        string? password = null,
        IProgress<double>? progress = null,
        CancellationToken cancellationToken = default);

    /// <summary>zip 생성. 원본이 폴더면 하위 전체 포함.</summary>
    void CreateZip(
        IReadOnlyList<string> sourcePaths,
        string archivePath,
        string? password = null,
        IProgress<double>? progress = null,
        CancellationToken cancellationToken = default);

    /// <summary>7z 생성. 암호 지정 시 항목명까지 암호화(EncryptHeaders).</summary>
    void Create7z(
        IReadOnlyList<string> sourcePaths,
        string archivePath,
        string? password = null,
        IProgress<double>? progress = null,
        CancellationToken cancellationToken = default);
}
