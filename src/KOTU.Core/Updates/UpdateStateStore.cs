namespace KOTU.Core.Updates;

/// <summary>프로세스별 settings 스냅샷과 분리한 불변 거절 표식과 OS 파일 잠금.</summary>
public sealed class UpdateStateStore
{
    private readonly Func<string, FileAttributes> _attributes;
    public string DirectoryPath { get; }
    public UpdateStateStore(string directory) : this(directory, File.GetAttributes) { }
    internal UpdateStateStore(string directory, Func<string, FileAttributes> attributes)
    { DirectoryPath = Path.GetFullPath(directory); _attributes = attributes; }

    private string RefusalPath(UpdateVersion version)
    {
        if (!UpdateVersion.TryParse(version.ToString(), out _)) throw new ArgumentOutOfRangeException(nameof(version));
        return Path.Combine(DirectoryPath, "declined", version + ".declined");
    }

    public bool IsDeclined(UpdateVersion version)
    {
        if (!IsDirectory(DirectoryPath) || !IsDirectory(Path.Combine(DirectoryPath, "declined"))) return false;
        try { _ = _attributes(RefusalPath(version)); return true; }
        catch (FileNotFoundException) { return false; }
        catch (DirectoryNotFoundException) { return false; }
        // 접근 오류를 '거절하지 않음'으로 바꾸지 않는다. 호출자는 안내를 보류한다.
    }

    private bool IsDirectory(string path)
    {
        FileAttributes attributes;
        try { attributes = _attributes(path); }
        catch (FileNotFoundException) { return false; }
        catch (DirectoryNotFoundException) { return false; }
        if ((attributes & FileAttributes.Directory) == 0) throw new IOException("The update state directory is unavailable.");
        return true;
    }

    public void Decline(UpdateVersion version)
    {
        var path = RefusalPath(version);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        try
        {
            using var marker = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.ReadWrite);
            marker.WriteByte(1);
            marker.Flush(flushToDisk: true);
        }
        catch (IOException) when (IsDeclined(version))
        {
            // 동시 생성 또는 이전 flush 실패의 재시도. 표식을 교체하거나 지우지 않는다.
            using var marker = new FileStream(path, FileMode.Open, FileAccess.Write, FileShare.ReadWrite);
            marker.Flush(flushToDisk: true);
        }
    }

    /// <summary>await를 가로질러 보유할 수 있는 프로세스 간 lease. 경합만 null, 기타 오류는 호출자에게 전달.</summary>
    public IDisposable? TryAcquireLease(string name)
    {
        if (name is not ("automatic" or "operation")) throw new ArgumentException("Unknown update lease.", nameof(name));
        Directory.CreateDirectory(DirectoryPath);
        try { return new FileStream(Path.Combine(DirectoryPath, name + ".lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); }
        catch (IOException ex) when ((ex.HResult & 0xffff) is 32 or 33) { return null; }
    }
}
