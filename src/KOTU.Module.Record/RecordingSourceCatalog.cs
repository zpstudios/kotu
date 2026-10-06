using System.Collections.ObjectModel;

namespace KOTU.Module.Record;

internal sealed record RecordingSelection(CaptureSource? Source, string? MicrophoneId, string? OutputId);

// A null category is a failed observation, not evidence that its sources disappeared.
internal sealed record RecordingSourceSnapshot(
    IReadOnlyList<CaptureSource>? Sources, IReadOnlyList<MicrophoneDevice>? Microphones,
    IReadOnlyList<OutputDevice>? Outputs, string? ScreenError = null, string? MicrophoneError = null, string? OutputError = null);

internal static class RecordingSourceCatalog
{
    internal static string Key(CaptureSource source) => source.DisplayName is { } display
        ? "display:" + display : $"window:{source.WindowHandle}:{source.ProcessId}";

    internal static bool Covers(RecordingSelection active, RecordingSelection observed) =>
        (active.Source is null || observed.Source is { } source && Key(active.Source) == Key(source)) &&
        (active.MicrophoneId is null || active.MicrophoneId == observed.MicrophoneId) &&
        (active.OutputId is null || active.OutputId == observed.OutputId);

    internal static string? Missing(RecordingSelection selection, RecordingSourceSnapshot snapshot)
    {
        if (selection.Source is { } source && snapshot.Sources is { } sources && !sources.Any(s => Key(s) == Key(source)))
            return "The selected screen or window is no longer available.";
        if (selection.OutputId is { } output && snapshot.Outputs is { } outputs && !outputs.Any(s => s.Id == output))
            return "The selected system audio output is no longer available.";
        if (selection.MicrophoneId is { } mic && snapshot.Microphones is { } microphones && !microphones.Any(s => s.Id == mic))
            return "The selected microphone is no longer available.";
        return null;
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
