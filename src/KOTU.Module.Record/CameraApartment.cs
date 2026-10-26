using System.Runtime.InteropServices;

namespace KOTU.Module.Record;

/// <summary>카메라 생성과 해제를 맡은 같은 전용 워커에서 COM과 MF 참조를 균형 해제한다.</summary>
internal sealed class CameraApartment : IDisposable
{
    private const int MfVersion = 0x00020070;
    private const int MfStartupLite = 1;
    private readonly int _thread = Environment.CurrentManagedThreadId;
    private bool _disposed;

    internal CameraApartment()
    {
        var result = CoInitializeEx(0, 0);
        if (result == unchecked((int)0x80010106))
            throw new CameraStartException("The camera worker could not initialize its required COM apartment.");
        Marshal.ThrowExceptionForHR(result);
        try
        {
            // 정적 장치 열거는 MF를 초기화하지 않는다. 레코더 자체 참조와 별도로 소유한다.
            Marshal.ThrowExceptionForHR(MFStartup(MfVersion, MfStartupLite));
        }
        catch
        {
            CoUninitialize();
            throw;
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        if (Environment.CurrentManagedThreadId != _thread)
            throw new InvalidOperationException("Camera resources must be released on their recording worker.");
        _disposed = true;
        try { Marshal.ThrowExceptionForHR(MFShutdown()); }
        finally { CoUninitialize(); }
    }

    [DllImport("ole32.dll")]
    private static extern int CoInitializeEx(nint reserved, uint flags);
    [DllImport("ole32.dll")]
    private static extern void CoUninitialize();
    [DllImport("mfplat.dll")]
    private static extern int MFStartup(int version, int flags);
    [DllImport("mfplat.dll")]
    private static extern int MFShutdown();
}

internal sealed class CameraStartException(string message) : InvalidOperationException(message);
