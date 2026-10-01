namespace KOTU.App.Integration;

/// <summary>Release-only opt-in smoke path; loads packaged engines without recording or changing OS state.</summary>
internal static class StandaloneSmoke
{
    internal static bool HasLauncherReceipt()
    {
        var nonce = Environment.GetEnvironmentVariable("KOTU_STANDALONE_SMOKE_NONCE");
        var marker = Environment.GetEnvironmentVariable("KOTU_STANDALONE_SMOKE_MARKER");
        if (!Guid.TryParseExact(nonce, "N", out _) || string.IsNullOrEmpty(marker)) return false;
        var scratch = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "scratch"));
        return string.Equals(Path.TrimEndingDirectorySeparator(Path.GetFullPath(Path.GetTempPath())), scratch, StringComparison.OrdinalIgnoreCase)
            && string.Equals(marker, Path.Combine(scratch, "standalone-smoke-" + nonce + ".complete"), StringComparison.OrdinalIgnoreCase)
            && !File.Exists(marker);
    }

    internal static void Complete()
    {
        if (!Program.IsStandaloneSmokeTest || !HasLauncherReceipt())
            throw new InvalidOperationException("Standalone smoke launcher receipt unavailable.");
        var nonce = Environment.GetEnvironmentVariable("KOTU_STANDALONE_SMOKE_NONCE")!;
        var marker = Environment.GetEnvironmentVariable("KOTU_STANDALONE_SMOKE_MARKER")!;
        var pending = marker + ".pending";
        try
        {
            using (var stream = new FileStream(pending, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                var content = System.Text.Encoding.UTF8.GetBytes("KOTU standalone smoke complete:" + nonce);
                stream.Write(content);
                stream.Flush(flushToDisk: true);
            }
            File.Move(pending, marker);
        }
        finally { File.Delete(pending); }
    }

    internal static void VerifyNativePayload()
    {
        if (!KOTU.Core.Integration.DistributionPolicy.IsStandalone)
            throw new InvalidOperationException("Standalone smoke requires the standalone distribution.");

        LibVLCSharp.Shared.Core.Initialize();
        using (var vlc = new LibVLCSharp.Shared.LibVLC("--no-plugins-cache", "--no-video", "--no-audio"))
        {
            if (string.IsNullOrWhiteSpace(vlc.Version)) throw new InvalidOperationException("VLC engine failed to initialize.");
        }
        using (var recorder = ScreenRecorderLib.Recorder.CreateRecorder(ScreenRecorderLib.RecorderOptions.Default))
        {
            if (recorder is null) throw new InvalidOperationException("Recorder engine failed to initialize.");
        }
        var archive = Path.Combine(Path.GetTempPath(), "standalone-smoke.zip");
        using (var zip = System.IO.Compression.ZipFile.Open(archive, System.IO.Compression.ZipArchiveMode.Create))
        using (var writer = new StreamWriter(zip.CreateEntry("test.txt").Open())) writer.Write("KOTU");
        try
        {
            if (new KOTU.Module.Archive.SevenZipBackend().List(archive).Count != 1)
                throw new InvalidOperationException("Archive engine failed to load the smoke fixture.");
        }
        finally { File.Delete(archive); }
    }
}
