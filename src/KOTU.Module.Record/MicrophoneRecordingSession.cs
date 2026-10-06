using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace KOTU.Module.Record;

/// <summary>
/// Standard 48 kHz, 16-bit mono PCM WAV. WASAPI performs format conversion in shared mode.
/// The dedicated NAudio capture thread writes bounded audio packets; the UI never handles
/// samples. Stop waits for RecordingStopped through Completion before the WAV is published.
/// </summary>
internal sealed class MicrophoneRecordingSession : IRecordingSession
{
    private const long MaximumDataBytes = 3_500_000_000;
    private readonly MMDevice _device;
    private readonly WasapiCapture _capture;
    private readonly WaveFileWriter _writer;
    private readonly Action<Action> _postToWorker;
    private readonly TaskCompletionSource<RecordingResult> _completion =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
    private long _bytesWritten;
    private string? _writeError;
    private string? _notice;
    private int _ended;

    public Task<RecordingResult> Completion => _completion.Task;
    public bool IsRecording => Volatile.Read(ref _ended) == 0;
    public TimeSpan Elapsed => TimeSpan.FromSeconds(Interlocked.Read(ref _bytesWritten) / 96000d);

    public static IReadOnlyList<MicrophoneDevice> GetDevices(string? preferredId = null)
    {
        using var enumerator = new MMDeviceEnumerator();
        string? defaultId = null;
        try
        {
            using var defaultDevice = enumerator.GetDefaultAudioEndpoint(DataFlow.Capture, Role.Multimedia);
            defaultId = defaultDevice.ID;
        }
        catch { /* An explicitly selected active input can still be used without a default. */ }
        var result = new List<MicrophoneDevice>();
        foreach (var device in enumerator.EnumerateAudioEndPoints(DataFlow.Capture, DeviceState.Active))
        {
            using (device)
            {
                var value = new MicrophoneDevice(device.FriendlyName + (device.ID == defaultId ? " (Default)" : ""), device.ID, device.ID == defaultId);
                if (result.Count < 64) result.Add(value);
                else if (device.ID == preferredId) result[^1] = value;
            }
        }
        return result.OrderByDescending(device => device.Id == defaultId).ToList();
    }

    public MicrophoneRecordingSession(string deviceId, string path, Action<Action> postToWorker)
    {
        _postToWorker = postToWorker;
        using var enumerator = new MMDeviceEnumerator();
        _device = enumerator.GetDevice(deviceId);
        try
        {
            if (_device.State != DeviceState.Active || _device.DataFlow != DataFlow.Capture)
                throw new InvalidOperationException("The selected microphone is no longer available. Choose an active microphone.");
            _capture = new WasapiCapture(_device) { WaveFormat = new WaveFormat(48000, 16, 1) };
        }
        catch { _device.Dispose(); throw; }
        try { _writer = new WaveFileWriter(path, _capture.WaveFormat); }
        catch { _capture.Dispose(); _device.Dispose(); throw; }
        _capture.DataAvailable += OnDataAvailable;
        _capture.RecordingStopped += OnStopped;
        try { _capture.StartRecording(); }
        catch
        {
            Volatile.Write(ref _ended, 1);
            _capture.Dispose();
            _writer.Dispose();
            _device.Dispose();
            throw;
        }
    }

    private void OnDataAvailable(object? sender, WaveInEventArgs args)
    {
        if (_writeError is not null || _notice is not null) return;
        try
        {
            if (_bytesWritten + args.BytesRecorded > MaximumDataBytes)
            {
                _notice = "Recording stopped at the WAV size limit. Start a new recording to continue.";
                RequestStop();
                return;
            }
            _writer.Write(args.Buffer, 0, args.BytesRecorded);
            Interlocked.Add(ref _bytesWritten, args.BytesRecorded);
        }
        catch (Exception ex)
        {
            _writeError = ex.Message;
            RequestStop();
        }
    }

    private void OnStopped(object? sender, StoppedEventArgs args)
    {
        var error = _writeError ?? args.Exception?.Message;
        try { _writer.Dispose(); }
        catch (Exception ex) { error ??= ex.Message; }
        Volatile.Write(ref _ended, 1);
        if (_bytesWritten == 0) error ??= "The microphone returned no audio. Check microphone access in Windows Settings.";
        _completion.TrySetResult(new RecordingResult(error, _notice));
    }

    public void Stop() => _capture.StopRecording();

    private void RequestStop() => _postToWorker(() =>
    {
        // A normal Stop may already have completed before this queued request runs.
        if (IsRecording) Stop();
    });

    public void Dispose()
    {
        _capture.DataAvailable -= OnDataAvailable;
        _capture.RecordingStopped -= OnStopped;
        _capture.Dispose();
        _writer.Dispose();
        _device.Dispose();
    }
}
