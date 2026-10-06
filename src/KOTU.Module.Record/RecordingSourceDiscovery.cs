using System.Runtime.InteropServices;
using NAudio.CoreAudioApi;

namespace KOTU.Module.Record;

internal static class RecordingSourceDiscovery
{
    internal static RecordingSourceSnapshot Read(nint ownWindow, RecordingSelection preferred, CancellationToken cancellation)
    {
        IReadOnlyList<CaptureSource>? sources = null;
        IReadOnlyList<MicrophoneDevice>? microphones = null;
        IReadOnlyList<OutputDevice>? outputs = null;
        string? screenError = null, microphoneError = null, outputError = null;
        cancellation.ThrowIfCancellationRequested();
        try
        {
            sources = RecordingBackend.GetScreenSources(ownWindow, preferred.Source);
        }
        catch (Exception ex) { screenError = ex.Message; }
        cancellation.ThrowIfCancellationRequested();
        try { microphones = MicrophoneRecordingSession.GetDevices(preferred.MicrophoneId); }
        catch (Exception ex) { microphoneError = ex.Message; }
        cancellation.ThrowIfCancellationRequested();
        try { outputs = GetOutputs(preferred.OutputId); }
        catch (Exception ex) { outputError = ex.Message; }
        cancellation.ThrowIfCancellationRequested();
        return new(sources, microphones, outputs, screenError, microphoneError, outputError);
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
    internal static bool WindowStillExists(CaptureSource source) => source.DisplayName is null &&
        source.ProcessId is { } pid && IsWindow(source.WindowHandle) &&
        GetWindowThreadProcessId(source.WindowHandle, out var actualPid) != 0 && actualPid == (uint)pid;

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindow(nint window);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(nint window, out uint processId);
}
