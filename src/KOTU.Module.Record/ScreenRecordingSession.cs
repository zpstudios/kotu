using System.Diagnostics;
using NAudio.CoreAudioApi;
using ScreenRecorderLib;
using KOTU.Core.Threading;

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
    private Recorder? _recorder;
    private readonly ModuleWorker _worker;
    private CameraApartment? _cameraApartment;
    private VideoCaptureRecordingSource? _camera;
    private readonly CameraRecordingGuard? _cameraGuard;
    private System.Threading.Timer? _cameraTimer;
    private int _cameraCheckQueued;
    private int _disposed;
    private int _terminal;
    private int _stopIssued;
    private const string CameraFailure = "The camera could not record. Check Windows camera privacy settings, close other apps using the camera, then refresh and try again. The camera format may not be supported.";
    private readonly TaskCompletionSource<RecordingResult> _completion =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
    private long _started;
    private long _ended;
    private int _recording;

    public Task<RecordingResult> Completion => _completion.Task;
    public bool IsRecording => Volatile.Read(ref _terminal) == 0 && Volatile.Read(ref _disposed) == 0 &&
        Volatile.Read(ref _stopIssued) == 0 && (_cameraGuard?.IsRecording ??
            (Volatile.Read(ref _ended) == 0 && Volatile.Read(ref _recording) != 0));
    public string? PendingStopReason => _cameraGuard?.Snapshot.PendingStopReason;
    public TimeSpan Elapsed
    {
        get
        {
            var cameraState = _cameraGuard?.Snapshot;
            var start = cameraState?.Started ?? Volatile.Read(ref _started);
            var end = cameraState?.Ended ?? Volatile.Read(ref _ended);
            return start == 0 ? TimeSpan.Zero : Stopwatch.GetElapsedTime(start, end == 0 ? Stopwatch.GetTimestamp() : end);
        }
    }

    public static IReadOnlyList<CaptureSource> GetSources(nint ownWindow, CaptureSource? preferred)
    {
        var sources = new List<CaptureSource>();
        foreach (var display in Recorder.GetDisplays())
        {
            var source = new CaptureSource($"Screen: {display.FriendlyName} ({display.DeviceName})", display.DeviceName, 0);
            if (sources.Count < 32) sources.Add(source);
            else if (display.DeviceName == preferred?.DisplayName) sources[^1] = source;
        }
        var windows = new List<CaptureSource>();
        // HWND capture requires Windows 10 1903. Desktop duplication still works on 1809.
        if (OperatingSystem.IsWindowsVersionAtLeast(10, 0, 18362))
            foreach (var window in Recorder.GetWindows().Where(w => w.Handle != ownWindow))
            {
                var source = new CaptureSource($"Window: {window.Title}", null, window.Handle, window.Pid);
                if (windows.Count < 200) windows.Add(source);
                else if (window.Handle == preferred?.WindowHandle && window.Pid == preferred.ProcessId) windows[^1] = source;
            }
        if (preferred is { Kind: CaptureSourceKind.Window } && !windows.Any(w => RecordingSourceCatalog.Key(w) == RecordingSourceCatalog.Key(preferred)) &&
            RecordingSourceDiscovery.WindowStillExists(preferred))
        {
            if (windows.Count == 200) windows[^1] = preferred;
            else windows.Add(preferred);
        }
        sources.AddRange(windows);
        return sources;
    }

    public ScreenRecordingSession(CaptureSource source, string? microphoneId, string? outputId, string path, ModuleWorker worker)
    {
        _worker = worker;
        try
        {
            if (source.Kind == CaptureSourceKind.Camera)
            {
                _cameraApartment = new CameraApartment();
                _cameraGuard = new CameraRecordingGuard(Stopwatch.GetTimestamp());
            }
            // Revalidate immediately before opening the capture. Never silently fall back to
            // another window or to a silent video when the selected source/device disappears.
            RecordingSourceBase video;
            if (source.CameraId is { } cameraId)
            {
                if (!RecordingSourceDiscovery.GetCameras(cameraId, CancellationToken.None).Any(c => c.CameraId == cameraId))
                    throw new CameraStartException("The selected camera is no longer available. Refresh the source list.");
                string nativeCameraId;
                var nativeCameras = Recorder.GetSystemVideoCaptureDevices();
                try
                {
                    nativeCameraId = RecordingSourceCatalog.ResolveNativeCameraId(cameraId, nativeCameras.Select(c => c.DeviceName))
                        ?? throw new CameraStartException("The selected camera could not be identified. Refresh sources and select it again.");
                }
                finally { foreach (var nativeCamera in nativeCameras) nativeCamera.Dispose(); }
                _camera = new VideoCaptureRecordingSource(nativeCameraId)
                {
                    IsVideoFramePreviewEnabled = true,
                    // SRL은 Uniform 축소 후 짝수로 내린다. 1:16부터 16:1까지 두 축이 2 이상이다.
                    VideoFramePreviewSize = new ScreenSize(32, 32),
                };
                _camera.OnFrameRecorded += OnCameraFrame;
                video = _camera;
            }
            else if (source.DisplayName is { } displayName)
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
            // options are built. Explicit endpoint IDs prevent default-device retargeting.
            using var audioDevices = outputId is null && microphoneId is null ? null : new MMDeviceEnumerator();
            var options = RecorderOptions.Default;
            options.SourceOptions.RecordingSources.Add(video);
            options.AudioOptions.AudioSources.Clear();
            options.AudioOptions.IsAudioEnabled = outputId is not null || microphoneId is not null;
            options.AudioOptions.Bitrate = AudioBitrate.bitrate_192kbps;
            LoopbackAudioSource? systemAudio = null;
            if (outputId is not null)
            {
                using var outputDevice = audioDevices!.GetDevice(outputId);
                if (outputDevice.State != DeviceState.Active || outputDevice.DataFlow != DataFlow.Render)
                    throw new InvalidOperationException("The selected system audio output is no longer available. Choose an active output.");
                systemAudio = new LoopbackAudioSource(outputId);
                options.AudioOptions.AudioSources.Add(systemAudio);
            }
            if (microphoneId is not null)
            {
                using var inputDevice = audioDevices!.GetDevice(microphoneId);
                if (inputDevice.State != DeviceState.Active || inputDevice.DataFlow != DataFlow.Capture)
                    throw new InvalidOperationException("The selected microphone is no longer available. Refresh the device list.");
                // Leave headroom when both sources are active; no playback monitoring is added.
                if (systemAudio is not null) systemAudio.Volume = 0.7f;
                options.AudioOptions.AudioSources.Add(new CaptureAudioSource(microphoneId) { Volume = systemAudio is not null ? 0.7f : 1f });
            }
            options.VideoEncoderOptions.Framerate = 30;
            options.VideoEncoderOptions.IsFixedFramerate = true;
            options.VideoEncoderOptions.Encoder = new H264VideoEncoder();
            options.VideoEncoderOptions.IsThrottlingDisabled = false;
            _recorder = Recorder.CreateRecorder(options);
            _recorder.OnStatusChanged += OnStatusChanged;
            _recorder.OnRecordingComplete += OnCompleted;
            _recorder.OnRecordingFailed += OnFailed;
            _recorder.Record(path);
            if (_cameraGuard is not null)
                _cameraTimer = new System.Threading.Timer(_ => QueueCameraCheck(), null, 250, 250);
        }
        catch (Exception error)
        {
            Dispose();
            if (source.Kind == CaptureSourceKind.Camera && error is not CameraStartException)
                throw new CameraStartException(CameraFailure);
            throw;
        }
    }

    private void OnCameraFrame(object? sender, FrameDataRecordedEventArgs args) =>
        _cameraGuard!.Frame(Stopwatch.GetTimestamp());

    private void QueueCameraCheck()
    {
        if (Volatile.Read(ref _disposed) != 0 || _completion.Task.IsCompleted ||
            Interlocked.CompareExchange(ref _cameraCheckQueued, 1, 0) != 0) return;
        _ = CheckCameraAsync();
    }

    private async Task CheckCameraAsync()
    {
        try
        {
            await _worker.Run(_ =>
            {
                // 큐에 들어간 뒤 종료될 수 있으므로 네이티브 호출 직전에 다시 확인한다.
                if (Volatile.Read(ref _disposed) != 0 || _completion.Task.IsCompleted) return;
                if (_cameraGuard!.Check(Stopwatch.GetTimestamp()))
                {
                    MarkEnded();
                    Stop();
                }
            }).ConfigureAwait(false);
        }
        catch
        {
            // 실패가 네이티브 완료를 뜻하지는 않는다. 소유권은 실제 완료까지 유지한다.
        }
        finally { Volatile.Write(ref _cameraCheckQueued, 0); }
    }

    private void OnStatusChanged(object? sender, RecordingStatusEventArgs args)
    {
        if (Volatile.Read(ref _terminal) != 0 || Volatile.Read(ref _disposed) != 0) return;
        if (args.Status == RecorderStatus.Recording)
        {
            var now = Stopwatch.GetTimestamp();
            _cameraGuard?.NativeRecording(now);
            if (Volatile.Read(ref _stopIssued) != 0) return;
            Interlocked.CompareExchange(ref _started, now, 0);
            Volatile.Write(ref _recording, 1);
        }
        else if (args.Status == RecorderStatus.Finishing)
        {
            _cameraGuard?.NativeFinishing(Stopwatch.GetTimestamp());
            MarkEnded();
        }
    }

    private void MarkEnded()
    {
        Interlocked.CompareExchange(ref _ended, Stopwatch.GetTimestamp(), 0);
        Volatile.Write(ref _recording, 0);
    }

    private void OnCompleted(object? sender, RecordingCompleteEventArgs args) => Complete(null);

    private void OnFailed(object? sender, RecordingFailedEventArgs args) =>
        Complete(_cameraGuard is null ? args.Error : CameraFailure);

    private void Complete(string? error)
    {
        if (Interlocked.Exchange(ref _terminal, 1) != 0) return;
        var result = _cameraGuard is { } guard ? guard.Complete(error)! : new RecordingResult(error);
        MarkEnded();
        _completion.TrySetResult(result);
    }

    public void Stop()
    {
        _cameraGuard?.Stop(Stopwatch.GetTimestamp());
        if (Volatile.Read(ref _disposed) != 0 || _completion.Task.IsCompleted ||
            Interlocked.CompareExchange(ref _stopIssued, 1, 0) != 0) return;
        try { _recorder!.Stop(); }
        catch { Volatile.Write(ref _stopIssued, 0); _cameraGuard?.StopFailed(); throw; }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        _cameraGuard?.Stop(Stopwatch.GetTimestamp());
        _cameraTimer?.Dispose();
        _cameraTimer = null;
        if (_recorder is { } recorder)
        {
            recorder.OnStatusChanged -= OnStatusChanged;
            recorder.OnRecordingComplete -= OnCompleted;
            recorder.OnRecordingFailed -= OnFailed;
            recorder.Dispose();
            _recorder = null;
        }
        if (_camera is { } camera)
        {
            camera.OnFrameRecorded -= OnCameraFrame;
            camera.Dispose();
            _camera = null;
        }
        _cameraApartment?.Dispose();
        _cameraApartment = null;
    }
}
