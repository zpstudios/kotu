namespace KOTU.Module.Record;

/// <summary>Worker-only output transaction. A canceled or failed capture never overwrites the destination.</summary>
internal static class RecordingOutput
{
    // A391: 알려진 폴더 조회와 폴더 생성/쓰기 검사는 호출자의 워커에서만 실행한다.
    public static string PrepareFolder(string? configuredFolder, bool screen, Func<bool, string>? knownFolder = null)
    {
        var folder = configuredFolder;
        if (string.IsNullOrWhiteSpace(folder))
        {
            var root = knownFolder is null
                ? Environment.GetFolderPath(screen ? Environment.SpecialFolder.MyVideos : Environment.SpecialFolder.MyMusic)
                : knownFolder(screen);
            if (string.IsNullOrWhiteSpace(root)) throw new IOException("Windows could not locate the recording library folder.");
            folder = Path.Combine(root, "KOTU");
        }
        if (!Path.IsPathFullyQualified(folder)) throw new IOException("The recording folder must be an absolute path.");
        folder = Path.GetFullPath(folder);
        Directory.CreateDirectory(folder);
        // 최종 파일과 구분되는 탐침을 즉시 삭제한다. 빈 최종 파일은 만들지 않는다.
        var probe = Path.Combine(folder, $".kotu-write-check-{Guid.NewGuid():N}.tmp");
        using (new FileStream(probe, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1, FileOptions.DeleteOnClose)) { }
        return folder;
    }

    public static string CreateDestinationPath(string folder, bool screen, DateTime timestamp)
    {
        var extension = screen ? ".mp4" : ".wav";
        string path;
        do
        {
            path = Path.Combine(folder, $"KOTU-{(screen ? "screen" : "microphone")}-{timestamp:yyyyMMdd-HHmmss}-{Guid.NewGuid():N}{extension}");
        }
        while (File.Exists(path) || Directory.Exists(path));
        return path;
    }

    public static string CreateTemporaryPath(string destination, string extension) =>
        Path.Combine(Path.GetDirectoryName(destination)!, $".kotu-record-{Guid.NewGuid():N}{extension}");

    public static void Publish(string temporaryPath, string destination, bool overwrite = true)
    {
        if (!File.Exists(temporaryPath) || new FileInfo(temporaryPath).Length == 0)
            throw new IOException("The recording produced no data.");
        File.Move(temporaryPath, destination, overwrite);
    }

    public static void Discard(string temporaryPath)
    {
        if (File.Exists(temporaryPath)) File.Delete(temporaryPath);
    }
}
