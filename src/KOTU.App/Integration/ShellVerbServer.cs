using System.Runtime.InteropServices;
using KOTU.Core.Cli;

namespace KOTU.App.Integration;

/// <summary>Explorer 선택을 받는 전용 STA local server. XAML과 주 인스턴스 라우팅 전에 실행한다.</summary>
public static class ShellVerbServer
{
    internal const string ServerToken = "--shell-verb-server";
    internal static readonly Guid CompressClass = new("041C5095-1D24-47DD-9B0C-5151269D9F6A");
    internal static readonly Guid ExtractHereClass = new("DD20BBAF-4FF9-49E9-8BB4-D4C361B5AC77");
    internal static readonly Guid ExtractFolderClass = new("DF11F069-F9E7-4D06-86D1-AEA7555D595E");
    private const uint WorkMessage = 0x8001;
    internal static Guid ClassFor(LaunchVerb verb) => verb switch
    {
        LaunchVerb.Compress => CompressClass,
        LaunchVerb.ExtractHere => ExtractHereClass,
        LaunchVerb.ExtractToFolder => ExtractFolderClass,
        _ => throw new ArgumentOutOfRangeException(nameof(verb)),
    };

    internal static bool TryRun(string[] args, out int exitCode)
    {
        exitCode = 0;
        if (!args.Contains(ServerToken)) return false;
        try
        {
            var filtered = args.Where(arg => !arg.Equals("-Embedding", StringComparison.OrdinalIgnoreCase)
                && !arg.Equals("/Embedding", StringComparison.OrdinalIgnoreCase)).ToArray();
            if (filtered.Length != 2 || filtered[0] != ServerToken) throw new InvalidDataException("Invalid shell server arguments.");
            var verb = LaunchRequest.Parse([filtered[1]]).Verb;
            _ = ClassFor(verb);
            Run(verb, ShellSelectionRequest.Launch);
        }
        catch (Exception)
        {
            exitCode = 1;
            ShellSelectionRequest.ShowError();
        }
        return true;
    }

    // Microsoft ExecuteCommandVerb 표본처럼 SINGLEUSE: 한 COM 활성화가 정확히 한 선택을 소유한다.
    internal static void Run(LaunchVerb verb, Action<LaunchRequest> deliver, Action? registered = null)
    {
        Marshal.ThrowExceptionForHR(CoInitializeEx(IntPtr.Zero, 2));
        uint cookie = 0;
        nuint timer = 0;
        try
        {
            _ = PeekMessage(out _, IntPtr.Zero, 0, 0, 0); // PostThreadMessage를 받을 큐를 먼저 만든다.
            var pending = new Queue<Action>();
            var threadId = GetCurrentThreadId();
            Exception? failure = null;
            var completed = false;
            var command = new Command(verb, action =>
            {
                pending.Enqueue(() =>
                {
                    try { action(); }
                    catch (Exception error) { failure = error; }
                    finally { completed = true; }
                });
                if (!PostThreadMessage(threadId, WorkMessage, 0, 0)) throw new IOException("Could not queue shell selection.");
            }, deliver);
            var factory = new Factory(command);
            var clsid = ClassFor(verb);
            Marshal.ThrowExceptionForHR(CoRegisterClassObject(ref clsid, factory, 4, 0, out cookie));
            registered?.Invoke();
            // 취소된 COM 활성화가 프로세스를 남기지 않도록 초기 수신 대기만 제한한다. 선택 합치기가 아니다.
            timer = SetTimer(IntPtr.Zero, 0, 120000, IntPtr.Zero);
            if (timer == 0) throw new IOException("Could not initialize shell server timeout.");
            while (!completed)
            {
                var result = GetMessage(out var message, IntPtr.Zero, 0, 0);
                if (result < 0) throw new IOException("Shell server message loop failed.");
                if (result == 0 || message.Message == 0x113) break;
                if (message.Message == WorkMessage)
                {
                    while (pending.TryDequeue(out var work)) work();
                }
                else
                {
                    _ = TranslateMessage(ref message);
                    _ = DispatchMessage(ref message);
                }
            }
            GC.KeepAlive(factory);
            GC.KeepAlive(command);
            if (failure is not null) throw failure;
        }
        finally
        {
            if (timer != 0) _ = KillTimer(IntPtr.Zero, timer);
            if (cookie != 0) _ = CoRevokeClassObject(cookie);
            CoUninitialize();
        }
    }

    [ComVisible(true), ClassInterface(ClassInterfaceType.None)]
    public sealed class Command : IExecuteCommand, IObjectWithSelection
    {
        private readonly LaunchVerb _verb;
        private readonly Action<Action> _post;
        private readonly Action<LaunchRequest> _deliver;
        private IShellItemArray? _selection;
        private bool _executed;
        internal Command(LaunchVerb verb, Action<Action> post, Action<LaunchRequest> deliver)
            => (_verb, _post, _deliver) = (verb, post, deliver);
        public void SetKeyState(uint state) { }
        public void SetParameters(string parameters) { }
        public void SetPosition(NativePoint point) { }
        public void SetShowWindow(int show) { }
        public void SetNoShowUI(bool noUi) { }
        public void SetDirectory(string directory) { }
        public void SetSelection(IShellItemArray selection)
        {
            if (_executed) throw new COMException("Selection already submitted.", unchecked((int)0x80004005));
            _selection = selection;
        }
        public void GetSelection(ref Guid iid, out IntPtr result)
        {
            if (_selection is null) throw new COMException("No selection.", unchecked((int)0x80004005));
            var unknown = Marshal.GetIUnknownForObject(_selection);
            try { Marshal.ThrowExceptionForHR(Marshal.QueryInterface(unknown, ref iid, out result)); }
            finally { Marshal.Release(unknown); }
        }
        public void Execute()
        {
            if (_executed || _selection is null) throw new COMException("Invalid selection invocation.", unchecked((int)0x80004005));
            _executed = true;
            var selection = _selection;
            _selection = null;
            // Explorer 호출에는 즉시 복귀한다. COM 객체 열거와 파일 기록은 별도 broker STA에서 진행한다.
            _post(() =>
            {
                try
                {
                    selection.GetCount(out var count);
                    if (count is 0 or > 100000) throw new InvalidDataException("Invalid selection count.");
                    var paths = new string[count];
                    for (uint index = 0; index < count; index++)
                    {
                        selection.GetItemAt(index, out var item);
                        try
                        {
                            item.GetDisplayName(0x80058000, out var name); // SIGDN_FILESYSPATH
                            try { paths[index] = Marshal.PtrToStringUni(name) ?? throw new InvalidDataException("A selected item has no file path."); }
                            finally { Marshal.FreeCoTaskMem(name); }
                        }
                        finally { if (Marshal.IsComObject(item)) Marshal.ReleaseComObject(item); }
                    }
                    _deliver(LaunchRequest.FromPaths(_verb, paths));
                }
                finally { if (Marshal.IsComObject(selection)) Marshal.ReleaseComObject(selection); }
            });
        }
    }

    [ComVisible(true), ClassInterface(ClassInterfaceType.None)]
    public sealed class Factory : IClassFactory
    {
        private readonly Command _command;
        internal Factory(Command command) => _command = command;
        public int CreateInstance(IntPtr outer, ref Guid iid, out IntPtr result)
        {
            result = IntPtr.Zero;
            if (outer != IntPtr.Zero) return unchecked((int)0x80040110);
            var unknown = Marshal.GetIUnknownForObject(_command);
            try { return Marshal.QueryInterface(unknown, ref iid, out result); }
            finally { Marshal.Release(unknown); }
        }
        public int LockServer(bool locked) => 0;
    }

    [ComVisible(true), Guid("00000001-0000-0000-C000-000000000046"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    public interface IClassFactory
    {
        [PreserveSig] int CreateInstance(IntPtr outer, ref Guid iid, out IntPtr result);
        [PreserveSig] int LockServer([MarshalAs(UnmanagedType.Bool)] bool locked);
    }
    [ComVisible(true), Guid("7F9185B0-CB92-43C5-80A9-92277A4F7B54"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    public interface IExecuteCommand
    {
        void SetKeyState(uint state);
        void SetParameters([MarshalAs(UnmanagedType.LPWStr)] string parameters);
        void SetPosition(NativePoint point);
        void SetShowWindow(int show);
        void SetNoShowUI([MarshalAs(UnmanagedType.Bool)] bool noUi);
        void SetDirectory([MarshalAs(UnmanagedType.LPWStr)] string directory);
        void Execute();
    }
    [ComVisible(true), Guid("1C9CD5BB-98E9-4491-A60F-31AACC72B83C"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    public interface IObjectWithSelection
    {
        void SetSelection(IShellItemArray selection);
        void GetSelection(ref Guid iid, out IntPtr result);
    }
    [ComImport, Guid("B63EA76D-1F85-456F-A19C-48159EFA858B"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    public interface IShellItemArray
    {
        void BindToHandler(IntPtr context, ref Guid handler, ref Guid iid, out IntPtr result);
        void GetPropertyStore(int flags, ref Guid iid, out IntPtr result);
        void GetPropertyDescriptionList(IntPtr key, ref Guid iid, out IntPtr result);
        void GetAttributes(uint flags, uint mask, out uint attributes);
        void GetCount(out uint count);
        void GetItemAt(uint index, out IShellItem item);
        void EnumItems(out IntPtr enumerator);
    }
    [ComImport, Guid("43826D1E-E718-42EE-BC55-A1E261C37BFE"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    public interface IShellItem
    {
        void BindToHandler(IntPtr context, ref Guid handler, ref Guid iid, out IntPtr result);
        void GetParent(out IShellItem parent);
        void GetDisplayName(uint kind, out IntPtr name);
        void GetAttributes(uint mask, out uint attributes);
        void Compare(IShellItem other, uint hint, out int order);
    }
    [StructLayout(LayoutKind.Sequential)] public struct NativePoint { public int X; public int Y; }
    [StructLayout(LayoutKind.Sequential)] private struct MessageInfo
    {
        public IntPtr Window;
        public uint Message;
        public nuint WParam;
        public nint LParam;
        public uint Time;
        public NativePoint Point;
        public uint Private;
    }
    [DllImport("ole32")] private static extern int CoInitializeEx(IntPtr reserved, uint flags);
    [DllImport("ole32")] private static extern void CoUninitialize();
    [DllImport("ole32")] private static extern int CoRegisterClassObject(ref Guid clsid, [MarshalAs(UnmanagedType.Interface)] IClassFactory factory, uint context, uint flags, out uint cookie);
    [DllImport("ole32")] private static extern int CoRevokeClassObject(uint cookie);
    [DllImport("kernel32")] private static extern uint GetCurrentThreadId();
    [DllImport("user32", EntryPoint = "GetMessageW")] private static extern int GetMessage(out MessageInfo message, IntPtr window, uint min, uint max);
    [DllImport("user32", EntryPoint = "PeekMessageW")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool PeekMessage(out MessageInfo message, IntPtr window, uint min, uint max, uint remove);
    [DllImport("user32", EntryPoint = "PostThreadMessageW")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool PostThreadMessage(uint thread, uint message, nuint wparam, nint lparam);
    [DllImport("user32")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool TranslateMessage(ref MessageInfo message);
    [DllImport("user32", EntryPoint = "DispatchMessageW")] private static extern nint DispatchMessage(ref MessageInfo message);
    [DllImport("user32")] private static extern nuint SetTimer(IntPtr window, nuint id, uint milliseconds, IntPtr callback);
    [DllImport("user32")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool KillTimer(IntPtr window, nuint id);
}
