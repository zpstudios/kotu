using System.Diagnostics;

namespace KOTU.Module.Record;

internal sealed record CameraRecordingState(bool IsRecording, long Started, long Ended, string? PendingStopReason);

/// <summary>카메라 프레임과 종료 경합의 실제 세션 경계. 시간 초과는 정지 요청만 허용한다.</summary>
internal sealed class CameraRecordingGuard(long requestedAt)
{
    private readonly object _gate = new();
    private long _firstFrame;
    private long _lastFrame;
    private long _nativeStarted;
    private long _ended;
    private long _frozenStart;
    private bool _stopped;
    private bool _terminal;
    private bool _finishing;
    private string? _error;

    private bool HasStarted => _nativeStarted != 0 && _firstFrame != 0;
    private long StartTime => HasStarted ? Math.Max(_nativeStarted, _firstFrame) : 0;
    internal CameraRecordingState Snapshot
    {
        get
        {
            lock (_gate) return new(!_stopped && !_terminal && HasStarted, _stopped ? _frozenStart : StartTime, _ended,
                !_terminal && _stopped ? _error ?? (_finishing ? "The camera is finishing the recording." : null) : null);
        }
    }

    internal bool IsRecording => Snapshot.IsRecording;

    internal void Frame(long now)
    {
        lock (_gate)
        {
            if (_terminal || _finishing || _error is not null) return;
            Interlocked.CompareExchange(ref _firstFrame, now, 0);
            Interlocked.Exchange(ref _lastFrame, now);
        }
    }

    internal void NativeRecording(long now)
    {
        lock (_gate)
        {
            if (_terminal || _finishing || _error is not null) return;
            Interlocked.CompareExchange(ref _nativeStarted, now, 0);
        }
    }

    internal void Stop(long now)
    {
        lock (_gate)
        {
            if (_stopped || _terminal) return;
            _stopped = true;
            _frozenStart = StartTime;
            _ended = Math.Max(now, _frozenStart);
        }
    }

    internal void StopFailed()
    {
        lock (_gate)
        {
            // 사용자 정지 실패는 실제 녹화 상태로 복귀한다. 가드 실패 사유는 유지한다.
            if (_terminal || _finishing || _error is not null) return;
            _stopped = false;
            _ended = 0;
        }
    }

    internal void NativeFinishing(long now)
    {
        lock (_gate)
        {
            _finishing = true;
            if (_stopped || _terminal) return;
            _stopped = true;
            _frozenStart = StartTime;
            _ended = Math.Max(now, _frozenStart);
        }
    }

    internal bool Check(long now)
    {
        lock (_gate)
        {
            if (_stopped || _terminal) return false;
            if (!HasStarted && Stopwatch.GetElapsedTime(requestedAt, now) >= TimeSpan.FromSeconds(10))
                _error = "The camera did not start within 10 seconds. Check Windows camera privacy settings, close other apps using the camera, then refresh and try again. The camera format may not be supported.";
            else if (HasStarted && Stopwatch.GetElapsedTime(_lastFrame, now) >= TimeSpan.FromSeconds(5))
                _error = "The camera stopped providing frames. Check its connection and Windows camera privacy settings, then refresh and try again.";
            if (_error is null) return false;
            _stopped = true;
            _frozenStart = StartTime;
            _ended = Math.Max(now, _frozenStart);
            return true;
        }
    }

    internal RecordingResult? Complete(string? error, long? now = null)
    {
        lock (_gate)
        {
            if (_terminal) return null;
            _terminal = true;
            if (!_stopped)
            {
                _frozenStart = StartTime;
                _ended = Math.Max(now ?? Stopwatch.GetTimestamp(), _frozenStart);
            }
            _stopped = true;
            return new(_error ?? error);
        }
    }
}
