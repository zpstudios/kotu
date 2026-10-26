using System.Collections.ObjectModel;

namespace KOTU.Module.Record;

internal sealed record RecordingSelection(CaptureSource? Source, string? MicrophoneId, string? OutputId);

// A null category is a failed observation, not evidence that its sources disappeared.
internal sealed record RecordingSourceSnapshot(
    IReadOnlyList<CaptureSource>? Sources, IReadOnlyList<MicrophoneDevice>? Microphones,
    IReadOnlyList<OutputDevice>? Outputs, string? ScreenError = null, string? MicrophoneError = null, string? OutputError = null,
    IReadOnlyList<CaptureSource>? Cameras = null, string? CameraError = null);

internal static class RecordingSourceCatalog
{
    internal static string Key(CaptureSource source) => source.CameraId is { } camera ? "camera:" + camera : source.DisplayName is { } display
        ? "display:" + display : $"window:{source.WindowHandle}:{source.ProcessId}";

    internal static bool Covers(RecordingSelection active, RecordingSelection observed) =>
        (active.Source is null || observed.Source is { } source && Key(active.Source) == Key(source)) &&
        (active.MicrophoneId is null || active.MicrophoneId == observed.MicrophoneId) &&
        (active.OutputId is null || active.OutputId == observed.OutputId);

    internal static string? Missing(RecordingSelection selection, RecordingSourceSnapshot snapshot)
    {
        if (selection.Source is { } source)
        {
            var sources = source.Kind == CaptureSourceKind.Camera ? snapshot.Cameras : snapshot.Sources;
            if (sources is not null && !sources.Any(s => Key(s) == Key(source)))
                return source.Kind == CaptureSourceKind.Camera ? "The selected camera is no longer available."
                    : "The selected screen or window is no longer available.";
        }
        if (selection.OutputId is { } output && snapshot.Outputs is { } outputs && !outputs.Any(s => s.Id == output))
            return "The selected system audio output is no longer available.";
        if (selection.MicrophoneId is { } mic && snapshot.Microphones is { } microphones && !microphones.Any(s => s.Id == mic))
            return "The selected microphone is no longer available.";
        return null;
    }

    // 카메라는 최초 관측에도 기본 선택하지 않는다. 기억한 동일 장치만 복구한다.
    internal static CaptureSource? SelectVideo(IReadOnlyList<CaptureSource> sources, IReadOnlyList<CaptureSource> cameras,
        string? rememberedKey, bool firstScreenObservation) =>
        !string.IsNullOrEmpty(rememberedKey)
            ? sources.Concat(cameras).FirstOrDefault(s => Key(s) == rememberedKey)
            : firstScreenObservation ? sources.FirstOrDefault() : null;

    internal static IReadOnlyList<CaptureSource> LimitCameras(IEnumerable<CaptureSource> cameras, string? preferredId)
    {
        var result = new List<CaptureSource>();
        foreach (var camera in cameras)
        {
            if (result.Count < 64) result.Add(camera);
            else if (camera.CameraId == preferredId) result[^1] = camera;
        }
        return result;
    }

    // 선택 키는 WinRT 원문을 유지하고, 시작할 때만 동일 인터페이스의 네이티브 표기를 찾는다.
    internal static string? ResolveNativeCameraId(string selectedId, IEnumerable<string> nativeIds)
    {
        if (string.IsNullOrEmpty(selectedId)) return null;
        string? match = null;
        foreach (var nativeId in nativeIds)
        {
            if (!string.Equals(selectedId, nativeId, StringComparison.OrdinalIgnoreCase)) continue;
            if (match is not null) return null;
            match = nativeId;
        }
        return match;
    }

    // Keep unchanged rows (and their focus) instead of replacing ItemsSource every poll.
    internal static void Apply<T>(ObservableCollection<T> current, IReadOnlyList<T> desired, Func<T, string> key)
    {
        for (var i = 0; i < desired.Count; i++)
        {
            var wantedKey = key(desired[i]);
            var found = -1;
            for (var j = i; j < current.Count; j++) if (key(current[j]) == wantedKey) { found = j; break; }
            if (found < 0) current.Insert(i, desired[i]);
            else if (found != i) current.Move(found, i);
            if (!EqualityComparer<T>.Default.Equals(current[i], desired[i])) current[i] = desired[i];
        }
        while (current.Count > desired.Count) current.RemoveAt(current.Count - 1);
    }

    internal static T? Select<T>(IReadOnlyList<T> items, string? rememberedKey, Func<T, string> key,
        Func<T, bool> isDefault, bool firstSuccessfulObservation) where T : class
    {
        if (!string.IsNullOrEmpty(rememberedKey)) return items.FirstOrDefault(item => key(item) == rememberedKey);
        return firstSuccessfulObservation ? items.FirstOrDefault(isDefault) ?? items.FirstOrDefault() : null;
    }
}
