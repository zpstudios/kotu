using KOTU.Core.Settings;

namespace KOTU.Module.Record;

internal sealed record RecordingSourcePreferences(string VideoKey, string CameraKey)
{
    public static RecordingSourcePreferences Read(ISettingsService settings)
    {
        var legacy = settings.Get("record.sourceKey", "");
        var legacyCamera = legacy.StartsWith("camera:", StringComparison.Ordinal);
        var camera = settings.Get<string?>("record.cameraKey", null);
        return new(legacyCamera ? "" : legacy, camera ?? (legacyCamera ? legacy : ""));
    }

    public void Write(ISettingsService settings)
    {
        settings.Set("record.sourceKey", VideoKey);
        settings.Set("record.cameraKey", CameraKey);
    }
}

internal sealed record RecordingStartInput
{
    public bool Disposed { get; init; }
    public bool Busy { get; init; }
    public bool Starting { get; init; }
    public RecordingPhase Phase { get; init; }
    public bool CloseDialog { get; init; }
    public bool ChoosingFolder { get; init; }
    public bool FolderInitializing { get; init; }
    public bool CatalogInitializing { get; init; }
    public RecordingFolderState? Folder { get; init; }
    public CaptureSource? Video { get; init; }
    public CaptureSource? Camera { get; init; }
    public MicrophoneDevice? Microphone { get; init; }
    public OutputDevice? Output { get; init; }
    public bool IncludeMicrophone { get; init; }
    public bool IncludeOutput { get; init; }
    public bool CamerasObserved { get; init; }
    public int CameraCount { get; init; }
    public string? CameraError { get; init; }
    public RecordingSourcePreferences Preferences { get; init; } = new("", "");
    public string MicrophonePreference { get; init; } = "";
    public string OutputPreference { get; init; } = "";
}

internal sealed record RecordingStartRequest(RecordingKind Kind, CaptureSource? Source,
    string? MicrophoneId, string? OutputId, string Labels, RecordingFolderState Folder,
    bool IncludeMicrophonePreference, bool IncludeOutputPreference, RecordingSourcePreferences Preferences,
    string MicrophonePreference, string OutputPreference)
{
    public bool IsVisual => Kind != RecordingKind.Microphone;
    public string Extension => IsVisual ? ".mp4" : ".wav";
    public RecordingSelection Selection => new(Source, MicrophoneId, OutputId);

    public void SavePreferences(ISettingsService settings)
    {
        settings.Set("record.mode", IsVisual ? "screen" : "microphone");
        settings.Set("record.includeMicrophone", IncludeMicrophonePreference);
        settings.Set("record.includeSystemAudio", IncludeOutputPreference);
        Preferences.Write(settings);
        settings.Set("record.microphoneId", MicrophonePreference);
        settings.Set("record.outputId", OutputPreference);
        settings.Save();
    }
}

internal sealed record RecordingStartDecision(RecordingStartRequest? Request, string Detail)
{
    public bool CanStart => Request is not null;
}

// 표시와 실제 시작이 같은 검증을 사용한다. 호출자는 첫 알림과 대기 전에 뷰를 예약한다.
internal static class RecordingStartPolicy
{
    public static RecordingStartDecision Evaluate(RecordingKind kind, RecordingStartInput input)
    {
        string? reason = input.Disposed ? "This Record view is closed."
            : input.Starting || input.Busy && input.Phase == RecordingPhase.Starting ? "A recording is starting. Wait for it to start or finish."
            : input.Busy ? input.Phase is RecordingPhase.Stopping or RecordingPhase.Saving
                ? "The current recording is stopping or saving. Wait for it to finish."
                : "Stop and save the current recording before starting another."
            : input.CloseDialog ? "Finish the current recording decision first."
            : input.ChoosingFolder ? "Finish choosing the save folder first."
            : input.FolderInitializing ? "Preparing save folder. Please wait."
            : input.CatalogInitializing ? "Finding recording sources. Please wait."
            : input.Folder is not { Ready: true } ? "Choose a valid save folder or use Retry."
            : null;
        if (reason is not null) return new(null, reason);

        var source = kind == RecordingKind.Screen ? input.Video : kind == RecordingKind.Camera ? input.Camera : null;
        if (kind == RecordingKind.Microphone && input.Microphone is null)
            return new(null, "Choose an available microphone in Audio (microphone).");
        if (kind == RecordingKind.Screen && source?.Kind is not (CaptureSourceKind.Display or CaptureSourceKind.Window))
            return new(null, "Choose an available screen or window in Video and camera.");
        if (kind == RecordingKind.Camera && source?.Kind != CaptureSourceKind.Camera)
            return new(null, input.CameraError is not null ? "Could not refresh cameras. Refresh sources and select a camera."
                : input.CamerasObserved && input.CameraCount == 0 ? "No cameras available. Connect a camera, then refresh sources."
                : "Choose an available camera in Video and camera.");
        if (kind is not (RecordingKind.Microphone or RecordingKind.Screen or RecordingKind.Camera))
            return new(null, "Choose a supported recording type.");
        var visual = kind != RecordingKind.Microphone;
        if (visual && input.IncludeOutput && input.Output is null)
            return new(null, "Choose a system audio output or turn off Include system audio.");
        if (visual && input.IncludeMicrophone && input.Microphone is null)
            return new(null, "Choose a microphone or turn off Include the selected microphone.");
        var microphone = !visual || input.IncludeMicrophone ? input.Microphone : null;
        var output = visual && input.IncludeOutput ? input.Output : null;
        var labels = string.Join(" · ", new[]
        {
            source?.Label,
            output is null ? null : "System audio: " + output.Label,
            microphone is null ? null : "Microphone: " + microphone.Label,
        }.Where(label => label is not null));
        var request = new RecordingStartRequest(kind, source, microphone?.Id, output?.Id, labels, input.Folder!,
            input.IncludeMicrophone, input.IncludeOutput, input.Preferences,
            input.MicrophonePreference, input.OutputPreference);
        return new(request, labels + (visual ? " (MP4)" : " (WAV)"));
    }
}
