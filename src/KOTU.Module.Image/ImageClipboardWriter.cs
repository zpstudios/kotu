using System.ComponentModel;
using System.Runtime.InteropServices;

namespace KOTU.Module.Image;

/// <summary>모든 형식을 먼저 준비하고, 성공한 핸들만 Windows에 넘기는 즉시 렌더링 복사.</summary>
internal sealed class ImageClipboardWriter : IDisposable
{
    internal const int MaximumAttempts = 6;
    private readonly IImageClipboardNative _native;
    private readonly uint[] _formats;
    private readonly nint[] _handles = new nint[3];
    private bool _used;

    private ImageClipboardWriter(IImageClipboardNative native, uint pngFormat)
    {
        _native = native;
        _formats = [pngFormat, 17, 8]; // PNG, CF_DIBV5, CF_DIB
    }

    internal static ImageClipboardWriter Prepare(ImageClipboardPayload payload, IImageClipboardNative? native = null)
    {
        native ??= Win32ImageClipboard.Instance;
        if (payload.Png.Length == 0 || payload.DibV5.Length == 0 || payload.Dib.Length == 0)
            throw new ArgumentException("Empty image payload", nameof(payload));
        var pngFormat = native.RegisterPngFormat();
        if (pngFormat == 0) throw new Win32Exception("Clipboard format registration failed");
        var prepared = new ImageClipboardWriter(native, pngFormat);
        try
        {
            // 이 단계는 워커에서 끝낸다. 실패해도 기존 클립보드에는 손대지 않는다.
            prepared._handles[0] = native.Allocate(payload.Png);
            prepared._handles[1] = native.Allocate(payload.DibV5);
            prepared._handles[2] = native.Allocate(payload.Dib);
            return prepared;
        }
        catch
        {
            prepared.Dispose();
            throw;
        }
    }

    internal async Task<bool> PublishAsync(nint owner, uint expectedSequence, Func<bool> isCurrent,
        Func<Task>? delay = null)
    {
        if (_used) throw new InvalidOperationException("Clipboard payload already consumed");
        _used = true;
        if (owner == 0) throw new ArgumentException("Clipboard owner is required", nameof(owner));
        delay ??= () => Task.Delay(40);
        for (var attempt = 0; attempt < MaximumAttempts; attempt++)
        {
            if (!isCurrent() || !_native.IsWindow(owner) || _native.Sequence != expectedSequence) return false;
            if (_native.Open(owner))
            {
                try
                {
                    // 잠금 안에서 다시 비교해야 다른 앱의 더 늦은 복사를 덮지 않는다.
                    if (!isCurrent() || _native.Sequence != expectedSequence) return false;
                    if (!_native.Empty()) throw new Win32Exception("Clipboard could not be emptied");
                    var failed = false;
                    for (var i = 0; i < _handles.Length; i++)
                    {
                        if (_native.SetData(_formats[i], _handles[i])) _handles[i] = 0;
                        else failed = true;
                    }
                    // 일부 형식의 게시 실패에도 이미 넘긴 유효 형식은 유지한다. 재시도로 비우지 않는다.
                    if (failed) throw new Win32Exception("Clipboard image format publication failed");
                    return true;
                }
                finally { _native.Close(); }
            }
            if (attempt + 1 < MaximumAttempts) await delay();
        }
        throw new Win32Exception("Clipboard is busy");
    }

    public void Dispose()
    {
        _used = true;
        for (var i = 0; i < _handles.Length; i++)
        {
            if (_handles[i] == 0) continue;
            _native.Free(_handles[i]);
            _handles[i] = 0;
        }
    }
}

/// <summary>실제 클립보드를 바꾸지 않고 실패 순서와 네이티브 소유권을 검증하기 위한 경계.</summary>
internal interface IImageClipboardNative
{
    uint RegisterPngFormat();
    nint Allocate(byte[] bytes);
    void Free(nint handle);
    uint Sequence { get; }
    bool IsWindow(nint window);
    bool Open(nint owner);
    void Close();
    bool Empty();
    bool SetData(uint format, nint handle);
}

internal sealed class Win32ImageClipboard : IImageClipboardNative
{
    internal static readonly Win32ImageClipboard Instance = new();
    public uint RegisterPngFormat() => RegisterClipboardFormat("PNG");
    public uint Sequence => GetClipboardSequenceNumber();
    public bool IsWindow(nint window) => IsWindowNative(window);
    public bool Open(nint owner) => OpenClipboard(owner);
    public void Close() => CloseClipboard();
    public bool Empty() => EmptyClipboard();
    public bool SetData(uint format, nint handle) => SetClipboardData(format, handle) != 0;
    public void Free(nint handle) => GlobalFree(handle);

    public nint Allocate(byte[] bytes)
    {
        var handle = GlobalAlloc(0x0002, (nuint)bytes.Length); // GMEM_MOVEABLE
        if (handle == 0) throw new Win32Exception(Marshal.GetLastWin32Error());
        try
        {
            var pointer = GlobalLock(handle);
            if (pointer == 0) throw new Win32Exception(Marshal.GetLastWin32Error());
            try { Marshal.Copy(bytes, 0, pointer, bytes.Length); }
            finally { GlobalUnlock(handle); }
            return handle;
        }
        catch
        {
            GlobalFree(handle);
            throw;
        }
    }

    [DllImport("user32.dll", EntryPoint = "RegisterClipboardFormatW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint RegisterClipboardFormat(string name);

    [DllImport("user32.dll", ExactSpelling = true)]
    private static extern uint GetClipboardSequenceNumber();

    [DllImport("user32.dll", EntryPoint = "IsWindow", ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindowNative(nint window);

    [DllImport("user32.dll", ExactSpelling = true, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool OpenClipboard(nint owner);

    [DllImport("user32.dll", ExactSpelling = true, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseClipboard();

    [DllImport("user32.dll", ExactSpelling = true, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EmptyClipboard();

    [DllImport("user32.dll", ExactSpelling = true, SetLastError = true)]
    private static extern nint SetClipboardData(uint format, nint handle);

    [DllImport("kernel32.dll", ExactSpelling = true, SetLastError = true)]
    private static extern nint GlobalAlloc(uint flags, nuint bytes);

    [DllImport("kernel32.dll", ExactSpelling = true, SetLastError = true)]
    private static extern nint GlobalLock(nint handle);

    [DllImport("kernel32.dll", ExactSpelling = true, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GlobalUnlock(nint handle);

    [DllImport("kernel32.dll", ExactSpelling = true, SetLastError = true)]
    private static extern nint GlobalFree(nint handle);
}
