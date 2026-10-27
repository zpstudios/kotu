using System.Runtime.InteropServices;
using NAudio.CoreAudioApi;
using Windows.Devices.Enumeration;
using Windows.Media.Devices;

namespace KOTU.Module.Record;

internal static class RecordingSourceDiscovery
{
    internal static RecordingSourceSnapshot Read(nint ownWindow, RecordingDiscoverySelection preferred, CancellationToken cancellation)
    {
        IReadOnlyList<CaptureSource>? sources = null;
        IReadOnlyList<MicrophoneDevice>? microphones = null;
        IReadOnlyList<OutputDevice>? outputs = null;
        IReadOnlyList<CaptureSource>? cameras = null;
        string? screenError = null, microphoneError = null, outputError = null, cameraError = null;
        cancellation.ThrowIfCancellationRequested();
        try
        {
            sources = RecordingBackend.GetScreenSources(ownWindow, preferred.VideoSource);
        }
        catch (Exception ex) { screenError = ex.Message; }
        cancellation.ThrowIfCancellationRequested();
        try { microphones = MicrophoneRecordingSession.GetDevices(preferred.MicrophoneId); }
        catch (Exception ex) { microphoneError = ex.Message; }
        cancellation.ThrowIfCancellationRequested();
        try { outputs = GetOutputs(preferred.OutputId); }
        catch (Exception ex) { outputError = ex.Message; }
        cancellation.ThrowIfCancellationRequested();
        try { cameras = GetCameras(preferred.CameraSource?.CameraId, cancellation); }
        catch (OperationCanceledException) { throw; }
        catch { cameraError = "Could not refresh cameras. Refresh sources to try again."; }
        cancellation.ThrowIfCancellationRequested();
        return new(sources, microphones, outputs, screenError, microphoneError, outputError, cameras, cameraError);
    }

    internal static IReadOnlyList<CaptureSource> GetCameras(string? preferredId, CancellationToken cancellation)
    {
        // 장치 인터페이스만 읽는다. 형식 조회나 카메라 활성화는 주기 관측에서 하지 않는다.
        var devices = DeviceInformation.FindAllAsync(MediaDevice.GetVideoCaptureSelector()).AsTask(cancellation).GetAwaiter().GetResult();
        return RecordingSourceCatalog.LimitCameras(devices.Where(d => d.IsEnabled)
            .Select(d => new CaptureSource("Camera: " + d.Name, null, 0, CameraId: d.Id)), preferredId);
    }

    internal static IReadOnlyList<OutputDevice> GetOutputs(string? preferredId)
    {
        using var enumerator = new MMDeviceEnumerator();
        string? defaultId = null;
        try { using var device = enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia); defaultId = device.ID; }
        catch { /* An explicit active output remains selectable without a default. */ }
        var devices = new List<OutputDevice>();
        foreach (var device in enumerator.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active))
        {
            using (device)
            {
                var value = new OutputDevice(device.FriendlyName + (device.ID == defaultId ? " (Default)" : ""), device.ID, device.ID == defaultId);
                if (devices.Count < 64) devices.Add(value);
                else if (device.ID == preferredId) devices[^1] = value;
            }
        }
        return devices.OrderByDescending(d => d.IsDefault).ToArray();
    }

    // GetWindows may omit minimized/hidden windows. Only handle+PID loss establishes disappearance.
    internal static bool WindowStillExists(CaptureSource source) => source.Kind == CaptureSourceKind.Window &&
        source.ProcessId is { } pid && IsWindow(source.WindowHandle) &&
        GetWindowThreadProcessId(source.WindowHandle, out var actualPid) != 0 && actualPid == (uint)pid;

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindow(nint window);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(nint window, out uint processId);
}
