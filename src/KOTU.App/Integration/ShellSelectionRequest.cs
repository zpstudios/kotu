using System.Diagnostics;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text.Json;
using KOTU.Core.Cli;

namespace KOTU.App.Integration;

/// <summary>선택 한 번을 임의 nonce 하나로 전달한다. 명령줄에는 경로 목록을 싣지 않는다.</summary>
internal static class ShellSelectionRequest
{
    internal static void ShowError() => MessageBox(IntPtr.Zero, "KOTU could not receive the selected files. Please select the files and try again.", "KOTU", 0x10);
    [System.Runtime.InteropServices.DllImport("user32", EntryPoint = "MessageBoxW", CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
    private static extern int MessageBox(IntPtr window, string text, string title, uint flags);

    internal const string Token = "--shell-selection";
    private const long MaxBytes = 16 * 1024 * 1024;
    private static string RequestDirectory => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "KOTU", "ShellRequests");
    private sealed record Envelope(int Version, string Nonce, DateTimeOffset Created, LaunchVerb Verb, string[] Paths);

    internal static Task<LaunchRequest> ParseAsync(IReadOnlyList<string> args)
    {
        if (!args.Contains(Token)) return Task.FromResult(LaunchRequest.Parse(args));
        if (args.Count != 2 || args[0] != Token) throw new InvalidDataException("Invalid shell selection arguments.");
        return Task.Run(() => Consume(args[1]));
    }

    internal static Task<LaunchRequest> ParseCommandLineAsync(string commandLine)
    {
        var args = LaunchRequest.Tokenize(commandLine);
        if (args.Count > 0 && args[0].EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) args.RemoveAt(0);
        return ParseAsync(args);
    }

    internal static string Write(LaunchRequest request, string? requestDirectory = null)
    {
        Validate(request.Verb, request.Paths);
        var directory = SecureDirectory(requestDirectory);
        var nonce = Guid.NewGuid().ToString("N");
        var path = Path.Combine(directory.FullName, nonce + ".json");
        var bytes = JsonSerializer.SerializeToUtf8Bytes(new Envelope(1, nonce, DateTimeOffset.UtcNow, request.Verb, request.Paths.ToArray()));
        if (bytes.Length > MaxBytes) throw new InvalidDataException("The selected file list is too large.");
        // CreateNew와 비공유 핸들로 다른 실행의 요청을 덮거나 부분 본문을 읽지 못하게 한다.
        var created = false;
        try
        {
            using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
            created = true;
            stream.Write(bytes);
        }
        catch
        {
            if (created)
            {
                try { File.Delete(path); }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }
            throw;
        }
        try { RemoveExpired(directory); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
        return nonce;
    }

    internal static LaunchRequest Consume(string nonce, string? requestDirectory = null)
    {
        if (!Guid.TryParseExact(nonce, "N", out _)) throw new InvalidDataException("Invalid shell selection identifier.");
        var directory = SecureDirectory(requestDirectory);
        var path = Path.Combine(directory.FullName, nonce + ".json");
        if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0) throw new InvalidDataException("Invalid shell selection file.");
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.None, 4096, FileOptions.DeleteOnClose);
        if (stream.Length is <= 0 or > MaxBytes) throw new InvalidDataException("Invalid shell selection size.");
        var envelope = JsonSerializer.Deserialize<Envelope>(stream) ?? throw new InvalidDataException("Empty shell selection.");
        if (envelope.Version != 1 || envelope.Nonce != nonce || envelope.Created > DateTimeOffset.UtcNow.AddMinutes(1)
            || envelope.Created < DateTimeOffset.UtcNow.AddMinutes(-10)) throw new InvalidDataException("Expired or invalid shell selection.");
        Validate(envelope.Verb, envelope.Paths);
        return LaunchRequest.FromPaths(envelope.Verb, envelope.Paths);
    }

    internal static void Launch(LaunchRequest request)
    {
        var nonce = Write(request);
        try
        {
            var start = new ProcessStartInfo(Environment.ProcessPath ?? throw new InvalidOperationException("Application path unavailable.")) { UseShellExecute = false };
            start.ArgumentList.Add(Token);
            start.ArgumentList.Add(nonce);
            using var process = Process.Start(start) ?? throw new IOException("Could not start KOTU.");
        }
        catch
        {
            File.Delete(Path.Combine(RequestDirectory, nonce + ".json"));
            throw;
        }
    }

    private static DirectoryInfo SecureDirectory(string? requestDirectory)
    {
        var directory = new DirectoryInfo(requestDirectory ?? RequestDirectory);
        if (directory.Exists && (directory.Attributes & FileAttributes.ReparsePoint) != 0)
            throw new InvalidDataException("Invalid shell selection directory.");
        var security = new DirectorySecurity();
        security.SetAccessRuleProtection(true, false);
        var user = WindowsIdentity.GetCurrent().User ?? throw new UnauthorizedAccessException();
        security.SetOwner(user);
        security.AddAccessRule(new FileSystemAccessRule(user, FileSystemRights.FullControl,
            InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Allow));
        if (!directory.Exists) directory.Create(security);
        else directory.SetAccessControl(security);
        return directory;
    }

    private static void RemoveExpired(DirectoryInfo directory)
    {
        // 활성 요청은 10분 유효다. 하루 지난 고아 파일만 이름·종류를 검증해 제한된 수로 청소한다.
        // 재귀 탐색/삭제나 요청 본문의 경로를 삭제 대상으로 사용하는 일은 없다.
        foreach (var file in directory.EnumerateFiles("*.json", SearchOption.TopDirectoryOnly).Take(256))
        {
            try
            {
                if (Guid.TryParseExact(Path.GetFileNameWithoutExtension(file.Name), "N", out _)
                    && (file.Attributes & FileAttributes.ReparsePoint) == 0
                    && file.LastWriteTimeUtc < DateTime.UtcNow.AddDays(-1)) file.Delete();
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    private static void Validate(LaunchVerb verb, IReadOnlyList<string>? paths)
    {
        if (verb is not (LaunchVerb.Compress or LaunchVerb.ExtractHere or LaunchVerb.ExtractToFolder)
            || paths is null || paths.Count is 0 or > 100000
            || paths.Any(path => string.IsNullOrWhiteSpace(path) || path.IndexOf('\0') >= 0 || !Path.IsPathFullyQualified(path)))
            throw new InvalidDataException("Invalid shell selection contents.");
    }
}
