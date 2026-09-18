using System.Runtime.InteropServices;
using KOTU.App.Integration;
using KOTU.Core.Cli;

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        if (args.Length == 3 && args[0] == "--serve")
        {
            ShellVerbServer.Run(Enum.Parse<LaunchVerb>(args[1]), request =>
                File.WriteAllText(args[2], System.Text.Json.JsonSerializer.Serialize(request.Paths)),
                () => Console.WriteLine("READY"));
            return 0;
        }
        var root = Path.Combine(Path.GetTempPath(), "KOTU-ShellSmoke-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var paths = new[] { Path.Combine(root, "한글 a.txt"), Path.Combine(root, "b.txt") };
            foreach (var path in paths) File.WriteAllText(path, "fixture");
            Marshal.ThrowExceptionForHR(CoInitializeEx(IntPtr.Zero, 2));
            try
            {
                foreach (var verb in new[] { LaunchVerb.Compress, LaunchVerb.ExtractHere, LaunchVerb.ExtractToFolder })
                    VerifyInvocation(verb, paths);
            }
            finally { CoUninitialize(); }
            var requestRoot = Path.Combine(root, "Requests");
            var many = Enumerable.Range(0, 500).Select(i => Path.Combine(root, new string('x', 90) + i)).ToArray();
            var nonce = ShellSelectionRequest.Write(LaunchRequest.FromPaths(LaunchVerb.Compress, many), requestRoot);
            var received = ShellSelectionRequest.Consume(nonce, requestRoot);
            Check(received.Paths.SequenceEqual(many), "large exact selection");
            Check(!File.Exists(Path.Combine(requestRoot, nonce + ".json")), "consume deletes request");
            try { ShellSelectionRequest.Consume("../escape", requestRoot); throw new Exception("invalid nonce accepted"); }
            catch (InvalidDataException) { }
            try { ShellSelectionRequest.Consume(nonce, requestRoot); throw new Exception("request replay accepted"); }
            catch (FileNotFoundException) { }
            var invalid = Guid.NewGuid().ToString("N");
            var invalidPath = Path.Combine(requestRoot, invalid + ".json");
            File.WriteAllText(invalidPath, "{invalid json");
            try { ShellSelectionRequest.Consume(invalid, requestRoot); throw new Exception("malformed JSON accepted"); }
            catch (System.Text.Json.JsonException) { }
            Check(!File.Exists(invalidPath), "malformed request deleted");
            var queued = new Queue<Action>();
            var command = new ShellVerbServer.Command(LaunchVerb.Compress, queued.Enqueue, _ => { });
            try { command.Execute(); throw new Exception("missing selection accepted"); }
            catch (COMException) { }
            Console.WriteLine("PASS: actual COM selection for 3 verbs; >32K list; exact paths; one-shot file; invalid nonce/replay rejected. No registry changes.");
            return 0;
        }
        catch (Exception error) { Console.Error.WriteLine(error); return 1; }
        finally
        {
            // 고정 임시 루트 내부의 이 테스트가 만든 파일만 삭제한다. 재귀 삭제는 하지 않는다.
            foreach (var file in Directory.EnumerateFiles(root)) File.Delete(file);
            var requests = Path.Combine(root, "Requests");
            if (Directory.Exists(requests))
            {
                foreach (var file in Directory.EnumerateFiles(requests)) File.Delete(file);
                Directory.Delete(requests);
            }
            Directory.Delete(root);
        }
    }

    private static void VerifyInvocation(LaunchVerb verb, string[] paths)
    {
        var resultPath = Path.Combine(Path.GetDirectoryName(paths[0])!, "result-" + verb + ".json");
        var start = new System.Diagnostics.ProcessStartInfo(Environment.ProcessPath!)
        {
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true,
        };
        start.ArgumentList.Add("--serve");
        start.ArgumentList.Add(verb.ToString());
        start.ArgumentList.Add(resultPath);
        using var server = System.Diagnostics.Process.Start(start)!;
        try
        {
        var ready = server.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(10)).GetAwaiter().GetResult();
        Check(ready == "READY", "server registration");
        var clsid = ShellVerbServer.ClassFor(verb);
        var iid = typeof(ShellVerbServer.IExecuteCommand).GUID;
        Marshal.ThrowExceptionForHR(CoCreateInstance(ref clsid, IntPtr.Zero, 4, ref iid, out var command));
        var pidls = new IntPtr[paths.Length];
        ShellVerbServer.IShellItemArray? selection = null;
        try
        {
            for (var i = 0; i < paths.Length; i++)
                Marshal.ThrowExceptionForHR(SHParseDisplayName(paths[i], IntPtr.Zero, out pidls[i], 0, out _));
            Marshal.ThrowExceptionForHR(SHCreateShellItemArrayFromIDLists((uint)pidls.Length, pidls, out selection));
            ((ShellVerbServer.IObjectWithSelection)command).SetSelection(selection);
            command.Execute();
        }
        finally
        {
            if (selection is not null) Marshal.ReleaseComObject(selection);
            if (Marshal.IsComObject(command)) Marshal.ReleaseComObject(command);
            foreach (var pidl in pidls) if (pidl != IntPtr.Zero) Marshal.FreeCoTaskMem(pidl);
        }
        Check(server.WaitForExit(10000) && server.ExitCode == 0, "server completed");
        var received = System.Text.Json.JsonSerializer.Deserialize<string[]>(File.ReadAllText(resultPath));
        Check(received is not null && received.SequenceEqual(paths), "COM exact selection " + verb);
        Console.WriteLine("PASS COM " + verb);
        }
        finally { if (!server.HasExited) { server.Kill(); server.WaitForExit(); } }
    }
    private static void Check(bool condition, string reason) { if (!condition) throw new Exception(reason); }
    [DllImport("ole32")] private static extern int CoInitializeEx(IntPtr reserved, uint flags);
    [DllImport("ole32")] private static extern void CoUninitialize();
    [DllImport("ole32")] private static extern int CoCreateInstance(ref Guid clsid, IntPtr outer, uint context, ref Guid iid, [MarshalAs(UnmanagedType.Interface)] out ShellVerbServer.IExecuteCommand result);
    [DllImport("shell32", CharSet = CharSet.Unicode)] private static extern int SHParseDisplayName(string name, IntPtr context, out IntPtr pidl, uint requested, out uint attributes);
    [DllImport("shell32")] private static extern int SHCreateShellItemArrayFromIDLists(uint count, [MarshalAs(UnmanagedType.LPArray, SizeParamIndex = 0)] IntPtr[] pidls, out ShellVerbServer.IShellItemArray array);
}
