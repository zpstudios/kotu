// Compiled with Windows' .NET Framework compiler, independently of the bundled .NET 8 app.
using System;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Reflection;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using System.Threading;
using System.Runtime.InteropServices;

[assembly: AssemblyTitle("KOTU Standalone")]
[assembly: AssemblyCompany("ZP Studios")]
[assembly: AssemblyProduct("KOTU")]
[assembly: AssemblyCopyright("Copyright (c) 2026 ZP Studios")]

internal static class StandaloneLauncher
{
    [STAThread]
    private static int Main(string[] args)
    {
        string root = null;
        bool smoke = args.Length == 1 && args[0] == "--standalone-smoke-test";
        int result = 1;
        try
        {
            var version = Assembly.GetExecutingAssembly().GetName().Version.ToString(3);
            // An elevated restart may inherit the app's scratch TEMP. Keep new sessions as siblings.
            var tempBase = Environment.GetEnvironmentVariable("KOTU_STANDALONE_TEMP_BASE") ?? Path.GetTempPath();
            if (!Path.IsPathRooted(tempBase)) throw new InvalidDataException("Invalid temporary folder.");
            tempBase = Path.GetFullPath(tempBase);
            root = Path.Combine(tempBase, "KOTU-Standalone-" + version + "-" + Guid.NewGuid().ToString("N"));
            var security = new DirectorySecurity();
            security.SetAccessRuleProtection(true, false);
            var user = WindowsIdentity.GetCurrent().User;
            if (user == null) throw new UnauthorizedAccessException("Windows user identity unavailable.");
            security.SetOwner(user);
            security.AddAccessRule(new FileSystemAccessRule(user, FileSystemRights.FullControl,
                InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Allow));
            new DirectoryInfo(root).Create(security);
            Extract(root);
            var scratch = Path.Combine(root, "scratch");
            Directory.CreateDirectory(scratch);
            var start = new ProcessStartInfo(Path.Combine(root, "app", "KOTU.exe"));
            start.UseShellExecute = false;
            start.WorkingDirectory = Environment.CurrentDirectory;
            start.Arguments = JoinArguments(args);
            start.EnvironmentVariables["TEMP"] = scratch;
            start.EnvironmentVariables["TMP"] = scratch;
            start.EnvironmentVariables["WEBVIEW2_USER_DATA_FOLDER"] = Path.Combine(scratch, "WebView2");
            start.EnvironmentVariables["KOTU_STANDALONE_LAUNCHER"] = Assembly.GetExecutingAssembly().Location;
            start.EnvironmentVariables["KOTU_STANDALONE_TEMP_BASE"] = tempBase;
            string smokeNonce = null;
            string smokeMarker = null;
            // Only this launcher can supply the per-run receipt; never inherit a previous test.
            start.EnvironmentVariables.Remove("KOTU_STANDALONE_SMOKE_NONCE");
            start.EnvironmentVariables.Remove("KOTU_STANDALONE_SMOKE_MARKER");
            if (smoke)
            {
                smokeNonce = Guid.NewGuid().ToString("N");
                smokeMarker = Path.Combine(scratch, "standalone-smoke-" + smokeNonce + ".complete");
                start.EnvironmentVariables["KOTU_STANDALONE_SMOKE_NONCE"] = smokeNonce;
                start.EnvironmentVariables["KOTU_STANDALONE_SMOKE_MARKER"] = smokeMarker;
            }
            // No Velopack locator inherited from an installed process may reach this payload.
            foreach (string name in new string[] { "VELOPACK_LOCATOR", "VELOPACK_RESTART", "DOTNET_BUNDLE_EXTRACT_BASE_DIR" })
                start.EnvironmentVariables.Remove(name);
            using (var child = Process.Start(start))
            {
                if (child == null) throw new IOException("Could not start KOTU.");
                child.WaitForExit();
                result = child.ExitCode;
            }
            if (smoke && Directory.Exists(Path.Combine(scratch, "KOTU")) &&
                File.Exists(Path.Combine(scratch, "KOTU", "startup-error.log"))) result = 1;
            if (smoke && !HasSmokeReceipt(smokeMarker, smokeNonce)) result = 1;
        }
        catch (Exception error)
        {
            if (!smoke) MessageBox(IntPtr.Zero, "KOTU Standalone could not start.\n\n" + error.Message, "KOTU Standalone", 0x10);
            result = 1;
        }
        finally
        {
            if (root != null)
            {
                // WebView2/antivirus handles can outlive the app briefly. Never schedule OS deletion.
                for (int attempt = 0; attempt < 30; attempt++)
                {
                    try { DeleteOwnedTree(root); break; }
                    catch (IOException) { Thread.Sleep(1000); }
                    catch (UnauthorizedAccessException) { Thread.Sleep(1000); }
                }
                if (smoke && Directory.Exists(root)) result = 1;
            }
        }
        return result;
    }

    private static void Extract(string root)
    {
        var app = Path.Combine(root, "app");
        Directory.CreateDirectory(app);
        var prefix = app + Path.DirectorySeparatorChar;
        using (var input = Assembly.GetExecutingAssembly().GetManifestResourceStream("KOTU.Payload.zip"))
        {
            if (input == null) throw new InvalidDataException("Standalone payload missing.");
            using (var zip = new ZipArchive(input, ZipArchiveMode.Read))
            {
                foreach (var entry in zip.Entries)
                {
                    if (Path.IsPathRooted(entry.FullName) || entry.FullName.IndexOf(':') >= 0)
                        throw new InvalidDataException("Invalid payload path.");
                    var target = Path.GetFullPath(Path.Combine(app, entry.FullName));
                    if (!target.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("Invalid payload path.");
                    if (entry.FullName.EndsWith("/") || entry.FullName.EndsWith("\\"))
                    {
                        Directory.CreateDirectory(target);
                        continue;
                    }
                    Directory.CreateDirectory(Path.GetDirectoryName(target));
                    using (var source = entry.Open())
                    using (var file = new FileStream(target, FileMode.CreateNew, FileAccess.Write, FileShare.None)) source.CopyTo(file);
                }
            }
        }
        foreach (var file in new string[] { "KOTU.exe", "resources.pri", "7z.dll", "ScreenRecorderLib.dll", "msvcp140.dll", "concrt140.dll", "vcruntime140.dll", "vcruntime140_1.dll", "LICENSE", "THIRD-PARTY-NOTICES.md", "standalone-policy.txt", "licenses/Microsoft-VC-Runtime-NOTICE.txt" })
            if (!File.Exists(Path.Combine(app, file))) throw new InvalidDataException("Standalone payload missing: " + file);
    }

    private static string JoinArguments(string[] args)
    {
        var result = new StringBuilder();
        foreach (string arg in args)
        {
            if (result.Length > 0) result.Append(' ');
            result.Append('"');
            int slashes = 0;
            foreach (char c in arg)
            {
                if (c == '\\') { slashes++; continue; }
                result.Append('\\', c == '"' ? slashes * 2 + 1 : slashes);
                result.Append(c);
                slashes = 0;
            }
            result.Append('\\', slashes * 2);
            result.Append('"');
        }
        return result.ToString();
    }

    private static bool HasSmokeReceipt(string marker, string nonce)
    {
        return File.Exists(marker) &&
            (File.GetAttributes(marker) & FileAttributes.ReparsePoint) == 0 &&
            new FileInfo(marker).Length <= 256 &&
            File.ReadAllText(marker) == "KOTU standalone smoke complete:" + nonce;
    }

    private static void DeleteOwnedTree(string directory)
    {
        if (!Directory.Exists(directory)) return;
        if ((File.GetAttributes(directory) & FileAttributes.ReparsePoint) != 0)
        {
            Directory.Delete(directory);
            return;
        }
        // Do not follow junctions/symlinks even if a child created one inside its scratch folder.
        foreach (var entry in new DirectoryInfo(directory).EnumerateFileSystemInfos())
        {
            if ((entry.Attributes & FileAttributes.ReparsePoint) != 0)
            {
                if ((entry.Attributes & FileAttributes.Directory) != 0) Directory.Delete(entry.FullName);
                else File.Delete(entry.FullName);
            }
            else if ((entry.Attributes & FileAttributes.Directory) != 0) DeleteOwnedTree(entry.FullName);
            else { entry.Attributes = FileAttributes.Normal; entry.Delete(); }
        }
        Directory.Delete(directory);
    }

    [DllImport("user32.dll", EntryPoint = "MessageBoxW", CharSet = CharSet.Unicode)]
    private static extern int MessageBox(IntPtr window, string text, string title, uint flags);
}
