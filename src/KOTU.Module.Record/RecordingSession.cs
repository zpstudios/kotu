namespace KOTU.Module.Record;

internal sealed record CaptureSource(string Label, string? DisplayName, nint WindowHandle, int? ProcessId = null)
{
    public override string ToString() => Label;
}

internal sealed record MicrophoneDevice(string Label, string Id)
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
    void Stop();
}
