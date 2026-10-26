namespace KOTU.Module.Record;

internal enum CaptureSourceKind { Display, Window, Camera }
internal enum RecordingKind { Screen, Microphone, Camera }

internal sealed record CaptureSource(string Label, string? DisplayName, nint WindowHandle, int? ProcessId = null, string? CameraId = null)
{
    public CaptureSourceKind Kind => CameraId is not null ? CaptureSourceKind.Camera
        : DisplayName is not null ? CaptureSourceKind.Display : CaptureSourceKind.Window;
    public override string ToString() => Label;
}

internal sealed record MicrophoneDevice(string Label, string Id, bool IsDefault = false)
{
    public override string ToString() => Label;
}

internal sealed record OutputDevice(string Label, string Id, bool IsDefault)
{
    public override string ToString() => Label;
}

internal sealed record RecordingResult(string? Error = null, string? Notice = null);

/// <summary>
/// All device creation, stop and disposal calls belong to the view's ModuleWorker.
/// Native capture callbacks complete the task; none of them touch XAML.
/// Completion means the container has closed, not merely that Stop was requested.
/// </summary>
internal interface IRecordingSession : IDisposable
{
    Task<RecordingResult> Completion { get; }
    TimeSpan Elapsed { get; }
    bool IsRecording { get; }
    string? PendingStopReason => null;
    void Stop();
}
