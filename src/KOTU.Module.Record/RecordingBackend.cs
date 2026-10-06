using System.Runtime.CompilerServices;

namespace KOTU.Module.Record;

internal static class RecordingBackend
{
    // Keep native assembly resolution behind a non-inlined call. A failed C++/CLI load
    // must be caught by the caller without preventing microphone enumeration/recording.
    [MethodImpl(MethodImplOptions.NoInlining)]
    public static IReadOnlyList<CaptureSource> GetScreenSources(nint ownWindow, CaptureSource? preferred = null) =>
        ScreenRecordingSession.GetSources(ownWindow, preferred);

    [MethodImpl(MethodImplOptions.NoInlining)]
    public static IRecordingSession StartScreen(CaptureSource source, string? microphoneId, string? outputId, string path) =>
        new ScreenRecordingSession(source, microphoneId, outputId, path);
}
