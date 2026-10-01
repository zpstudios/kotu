using System.Diagnostics;
using NAudio.CoreAudioApi;
using ScreenRecorderLib;

namespace KOTU.Module.Record;

/// <summary>
/// ScreenRecorderLib 7.0.1 owns D3D capture, WASAPI resampling/mixing and Media Foundation
/// H.264/AAC muxing. GraphicsCapture alone cannot record audio or write an MP4.
/// API evidence and deployment requirements: docs/RECORDING.md.
/// Kept separate from the view and microphone backend so a missing native dependency
/// is caught when screen recording is selected, not while constructing the module.
/// </summary>
internal sealed class ScreenRecordingSession : IRecordingSession
{
    private readonly Recorder _recorder;
    private readonly TaskCompletionSource<RecordingResult> _completion =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
    private long _started;
    private long _ended;
    private int _recording;

    public Task<RecordingResult> Completion => _completion.Task;
    public bool IsRecording => Volatile.Read(ref _recording) != 0;
    public TimeSpan Elapsed
    {
        get
        {
            var start = Volatile.Read(ref _started);
            var end = Volatile.Read(ref _ended);
            return start == 0 ? TimeSpan.Zero : Stopwatch.GetElapsedTime(start, end == 0 ? Stopwatch.GetTimestamp() : end);
        }
    }

    public static IReadOnlyList<CaptureSource> GetSources(nint ownWindow)
    {
        var sources = new List<CaptureSource>();
        foreach (var display in Recorder.GetDisplays().Take(32))
            sources.Add(new CaptureSource($"Screen: {display.FriendlyName} ({display.DeviceName})", display.DeviceName, 0));
        // HWND capture requires Windows 10 1903. Desktop duplication still works on 1809.
        if (OperatingSystem.IsWindowsVersionAtLeast(10, 0, 18362))
            foreach (var window in Recorder.GetWindows().Where(w => w.Handle != ownWindow).Take(200))
                sources.Add(new CaptureSource($"Window: {window.Title}", null, window.Handle, window.Pid));
        return sources;
    }

    public ScreenRecordingSession(CaptureSource source, string? microphoneId, string path)
    {
        // Revalidate immediately before opening the capture. Never silently fall back to
        // another window or to a silent video when the selected source/device disappears.
        RecordingSourceBase video;
        if (source.DisplayName is { } displayName)
        {
            if (!Recorder.GetDisplays().Any(d => d.DeviceName == displayName))
                throw new InvalidOperationException("The selected screen is no longer available. Refresh the source list.");
            video = new DisplayRecordingSource(displayName);
        }
        else
        {
            var window = Recorder.GetWindows().FirstOrDefault(w => w.Handle == source.WindowHandle);
            if (window is null || window.Pid != source.ProcessId || window.IsMinmimized())
                throw new InvalidOperationException("The selected window is closed or minimized. Restore it and refresh the source list.");
            video = new WindowRecordingSource(source.WindowHandle);
        }

        // NAudio's managed COM activation initializes the worker apartment before native
        // options are built. Native LoopbackAudioSource.Default calls CoCreateInstance
        // before the recorder's capture thread initializes COM, so do not rely on it here.
        using var audioDevices = new MMDeviceEnumerator();
        string outputDeviceId;
        try
        {
            using var outputDevice = audioDevices.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
            outputDeviceId = outputDevice.ID;
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException("No default audio output is available. Connect a speaker or headset before recording.", ex);
        }
        var systemAudio = new LoopbackAudioSource(outputDeviceId);
        var options = RecorderOptions.Default;
        options.SourceOptions.RecordingSources.Add(video);
        options.AudioOptions.IsAudioEnabled = true;
        options.AudioOptions.Bitrate = AudioBitrate.bitrate_192kbps;
        options.AudioOptions.AudioSources.Add(systemAudio);
        if (microphoneId is not null)
        {
            using var inputDevice = audioDevices.GetDevice(microphoneId);
            if (inputDevice.State != DeviceState.Active)
                throw new InvalidOperationException("The selected microphone is no longer available. Refresh the device list.");
            // Leave headroom when both sources are active; no playback monitoring is added.
            systemAudio.Volume = 0.7f;
            options.AudioOptions.AudioSources.Add(new CaptureAudioSource(microphoneId) { Volume = 0.7f });
        }
        options.VideoEncoderOptions.Framerate = 30;
        options.VideoEncoderOptions.IsFixedFramerate = true;
        options.VideoEncoderOptions.Encoder = new H264VideoEncoder();
        options.VideoEncoderOptions.IsThrottlingDisabled = false;
        _recorder = Recorder.CreateRecorder(options);
        _recorder.OnStatusChanged += OnStatusChanged;
        _recorder.OnRecordingComplete += OnCompleted;
        _recorder.OnRecordingFailed += OnFailed;
        try { _recorder.Record(path); }
        catch { Dispose(); throw; }
    }

    private void OnStatusChanged(object? sender, RecordingStatusEventArgs args)
    {
        if (args.Status == RecorderStatus.Recording)
        {
            Interlocked.CompareExchange(ref _started, Stopwatch.GetTimestamp(), 0);
            Volatile.Write(ref _recording, 1);
        }
        else if (args.Status == RecorderStatus.Finishing)
            MarkEnded();
    }

    private void MarkEnded()
    {
        Interlocked.CompareExchange(ref _ended, Stopwatch.GetTimestamp(), 0);
        Volatile.Write(ref _recording, 0);
    }

    private void OnCompleted(object? sender, RecordingCompleteEventArgs args)
    {
        MarkEnded();
        _completion.TrySetResult(new RecordingResult());
    }

    private void OnFailed(object? sender, RecordingFailedEventArgs args)
    {
        MarkEnded();
        _completion.TrySetResult(new RecordingResult(args.Error));
    }

    public void Stop() => _recorder.Stop();

    public void Dispose()
    {
        _recorder.OnStatusChanged -= OnStatusChanged;
        _recorder.OnRecordingComplete -= OnCompleted;
        _recorder.OnRecordingFailed -= OnFailed;
        _recorder.Dispose();
    }
}
