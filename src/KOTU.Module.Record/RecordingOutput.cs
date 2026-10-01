namespace KOTU.Module.Record;

/// <summary>Worker-only output transaction. A canceled or failed capture never overwrites the destination.</summary>
internal static class RecordingOutput
{
    public static string CreateTemporaryPath(string destination, string extension) =>
        Path.Combine(Path.GetDirectoryName(destination)!, $".kotu-record-{Guid.NewGuid():N}{extension}");

    public static void Publish(string temporaryPath, string destination)
    {
        if (!File.Exists(temporaryPath) || new FileInfo(temporaryPath).Length == 0)
            throw new IOException("The recording produced no data.");
        File.Move(temporaryPath, destination, overwrite: true);
    }

    public static void Discard(string temporaryPath)
    {
        if (File.Exists(temporaryPath)) File.Delete(temporaryPath);
    }
}
